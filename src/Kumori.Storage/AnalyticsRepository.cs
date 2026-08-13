using Kumori.Core.Models;
using System.Text.Json;

namespace Kumori.Storage;

public sealed class AnalyticsRepository
{
    private readonly SqliteConnectionFactory _factory;

    public AnalyticsRepository(SqliteConnectionFactory factory)
    {
        _factory = factory;
    }

    public AnalyticsSummary GetSummary(int days = int.MaxValue)
    {
        if (!_factory.DatabaseExists)
        {
            return new AnalyticsSummary();
        }

        using var con = _factory.Open();
        var hasUtc = HasColumn(con, "attempts", "started_at_utc_ms");
        var hasDuration = HasColumn(con, "attempts", "duration_seconds");
        var hasKeys = HasColumn(con, "attempts", "z_count")
                      && HasColumn(con, "attempts", "x_count");
        var hasMisses = HasColumn(con, "attempts", "misses");
        var hasBeatmapId = HasColumn(con, "attempts", "beatmap_id");
        using var totals = con.CreateCommand();
        totals.CommandText = $"""
            SELECT COUNT(*),
                   SUM(CASE WHEN outcome='completed' THEN 1 ELSE 0 END),
                   SUM(CASE WHEN outcome='failed' THEN 1 ELSE 0 END),
                   COALESCE(AVG(CASE WHEN outcome='completed' THEN accuracy END), 0),
                   COALESCE(MAX(CASE WHEN outcome='completed' THEN pp END), 0),
                   COALESCE(SUM(score), 0),
                   {(hasDuration ? "COALESCE(SUM(a.duration_seconds), 0)" : "0")},
                   {(hasKeys ? "COALESCE(SUM(a.z_count), 0)" : "0")},
                   {(hasKeys ? "COALESCE(SUM(a.x_count), 0)" : "0")},
                   {(hasMisses ? "COALESCE(SUM(a.misses), 0)" : "0")}
            FROM attempts a
            """;
        using var r = totals.ExecuteReader();
        r.Read();
        var summary = new AnalyticsSummary
        {
            Attempts = r.GetInt64(0),
            Completed = r.IsDBNull(1) ? 0 : r.GetInt64(1),
            Failed = r.IsDBNull(2) ? 0 : r.GetInt64(2),
            AverageAccuracy = r.GetDouble(3),
            BestPp = r.GetDouble(4),
            TotalScore = r.GetInt64(5),
            TotalDurationSeconds = Convert.ToDouble(r.GetValue(6), System.Globalization.CultureInfo.InvariantCulture),
            ZTotal = Convert.ToInt64(r.GetValue(7), System.Globalization.CultureInfo.InvariantCulture),
            XTotal = Convert.ToInt64(r.GetValue(8), System.Globalization.CultureInfo.InvariantCulture),
            TotalMisses = Convert.ToInt64(r.GetValue(9), System.Globalization.CultureInfo.InvariantCulture),
        };
        r.Close();

        var dailyAccountChanges = ReadDailyProfileChanges(con);
        using var daily = con.CreateCommand();
        var dayExpression = hasUtc ? "date(a.started_at_utc_ms / 1000, 'unixepoch', 'localtime')" : "substr(a.started_at, 1, 10)";
        daily.CommandText = $"""
            SELECT {dayExpression} day,
                   COUNT(*) attempts,
                   SUM(CASE WHEN outcome='completed' THEN 1 ELSE 0 END) completed,
                   COALESCE(AVG(CASE WHEN outcome='completed' THEN accuracy END), 0) average_accuracy,
                   COALESCE(MAX(CASE WHEN outcome='completed' THEN pp END), 0) best_pp,
                   {(hasDuration ? "COALESCE(SUM(a.duration_seconds), 0)" : "0")} duration_seconds,
                   {(hasKeys ? "COALESCE(SUM(a.z_count), 0)" : "0")} z_count,
                   {(hasKeys ? "COALESCE(SUM(a.x_count), 0)" : "0")} x_count,
                   {(hasMisses ? "COALESCE(SUM(a.misses), 0)" : "0")} misses,
                   {(hasBeatmapId ? "COUNT(DISTINCT a.beatmap_id)" : "0")} distinct_maps,
                   COALESCE(SUM(a.score), 0) total_score
            FROM attempts a
            GROUP BY {dayExpression}
            ORDER BY day DESC
            LIMIT @days
            """;
        daily.Parameters.AddWithValue("@days", Math.Clamp(days, 1, int.MaxValue));
        using var dailyReader = daily.ExecuteReader();
        var rows = new List<DailyAttemptTrend>();
        while (dailyReader.Read())
        {
            var day = dailyReader.GetString(0);
            dailyAccountChanges.TryGetValue(day, out var accountChange);
            rows.Add(new DailyAttemptTrend
            {
                Day = day,
                Attempts = dailyReader.GetInt64(1),
                Completed = dailyReader.IsDBNull(2) ? 0 : dailyReader.GetInt64(2),
                AverageAccuracy = dailyReader.GetDouble(3),
                BestPp = dailyReader.GetDouble(4),
                TotalDurationSeconds = Convert.ToDouble(dailyReader.GetValue(5), System.Globalization.CultureInfo.InvariantCulture),
                ZTotal = Convert.ToInt64(dailyReader.GetValue(6), System.Globalization.CultureInfo.InvariantCulture),
                XTotal = Convert.ToInt64(dailyReader.GetValue(7), System.Globalization.CultureInfo.InvariantCulture),
                TotalMisses = Convert.ToInt64(dailyReader.GetValue(8), System.Globalization.CultureInfo.InvariantCulture),
                DistinctMaps = Convert.ToInt64(dailyReader.GetValue(9), System.Globalization.CultureInfo.InvariantCulture),
                TotalScore = Convert.ToInt64(dailyReader.GetValue(10), System.Globalization.CultureInfo.InvariantCulture),
                PpChange = accountChange?.PpChange,
                RankChange = accountChange?.RankChange,
            });
        }
        dailyReader.Close();
        var keyBindings = ReadLatestKeyBindings(con);
        return summary with
        {
            Daily = rows,
            LatestAccountChange = ReadLatestProfileChange(con),
            Key1Binding = keyBindings.Key1,
            Key2Binding = keyBindings.Key2,
            LastSyncedAt = ReadLastSynced(con),
        };
    }

    public DailyProgressReport? GetDailyProgress(string day)
    {
        if (!_factory.DatabaseExists
            || !DateOnly.TryParseExact(day, "yyyy-MM-dd", out _))
        {
            return null;
        }

        using var con = _factory.Open();
        var hasUtc = HasColumn(con, "attempts", "started_at_utc_ms");
        var hasDuration = HasColumn(con, "attempts", "duration_seconds");
        var hasKeys = HasColumn(con, "attempts", "z_count")
                      && HasColumn(con, "attempts", "x_count");
        var hasMisses = HasColumn(con, "attempts", "misses");
        var hasPlayerName = HasColumn(con, "attempts", "player_name");
        var hasBeatmapId = HasColumn(con, "attempts", "beatmap_id");
        var dayExpression = hasUtc
            ? "date(a.started_at_utc_ms / 1000, 'unixepoch', 'localtime')"
            : "substr(a.started_at, 1, 10)";

        using var totals = con.CreateCommand();
        totals.CommandText = $"""
            SELECT COUNT(*),
                   SUM(CASE WHEN outcome='completed' THEN 1 ELSE 0 END),
                   COALESCE(AVG(CASE WHEN outcome='completed' THEN accuracy END), 0),
                   COALESCE(MAX(CASE WHEN outcome='completed' THEN pp END), 0),
                   {(hasDuration ? "COALESCE(SUM(a.duration_seconds), 0)" : "0")},
                   {(hasKeys ? "COALESCE(SUM(a.z_count), 0)" : "0")},
                   {(hasKeys ? "COALESCE(SUM(a.x_count), 0)" : "0")},
                   {(hasMisses ? "COALESCE(SUM(a.misses), 0)" : "0")},
                   {(hasPlayerName ? "COALESCE(MAX(NULLIF(a.player_name, '')), '')" : "''")},
                   {(hasBeatmapId ? "COUNT(DISTINCT a.beatmap_id)" : "0")},
                   COALESCE(SUM(a.score), 0)
            FROM attempts a
            WHERE {dayExpression} = @day
            """;
        totals.Parameters.AddWithValue("@day", day);
        using var reader = totals.ExecuteReader();
        reader.Read();
        var attempts = reader.GetInt64(0);
        if (attempts == 0)
            return null;
        var completed = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
        var averageAccuracy = reader.GetDouble(2);
        var bestPp = reader.GetDouble(3);
        var durationSeconds = Convert.ToDouble(reader.GetValue(4), System.Globalization.CultureInfo.InvariantCulture);
        var zTotal = Convert.ToInt64(reader.GetValue(5), System.Globalization.CultureInfo.InvariantCulture);
        var xTotal = Convert.ToInt64(reader.GetValue(6), System.Globalization.CultureInfo.InvariantCulture);
        var totalMisses = Convert.ToInt64(reader.GetValue(7), System.Globalization.CultureInfo.InvariantCulture);
        var playerName = reader.GetString(8);
        var distinctMaps = reader.GetInt64(9);
        var totalScore = reader.GetInt64(10);
        reader.Close();
        var accountChanges = ReadDailyProfileChanges(con);
        accountChanges.TryGetValue(day, out var accountChange);
        var summary = new DailyAttemptTrend
        {
            Day = day,
            Attempts = attempts,
            Completed = completed,
            AverageAccuracy = averageAccuracy,
            BestPp = bestPp,
            TotalDurationSeconds = durationSeconds,
            ZTotal = zTotal,
            XTotal = xTotal,
            TotalMisses = totalMisses,
            DistinctMaps = distinctMaps,
            TotalScore = totalScore,
            PpChange = accountChange?.PpChange,
            RankChange = accountChange?.RankChange,
        };

        return new DailyProgressReport
        {
            Summary = summary,
            PlayerName = playerName,
            Account = ReadDailyAccountProgress(con, day),
            MostPlayedMap = ReadMostPlayedMap(con, dayExpression, day),
            BestPlay = ReadBestPlay(con, dayExpression, day),
            MostUsedModCombinations = ReadMostUsedModCombinations(con, dayExpression, day),
        };
    }

    private static IReadOnlyList<DailyModCombinationUsage> ReadMostUsedModCombinations(
        Microsoft.Data.Sqlite.SqliteConnection con,
        string dayExpression,
        string day)
    {
        if (!HasColumn(con, "attempts", "mods_key"))
            return Array.Empty<DailyModCombinationUsage>();

        bool hasBeatmaps = HasTable(con, "beatmaps");
        bool hasBpm = hasBeatmaps && HasColumn(con, "beatmaps", "bpm");
        bool hasAttemptMods = HasTable(con, "attempt_mods");
        bool hasContext = HasTable(con, "attempt_context");
        using var command = con.CreateCommand();
        command.CommandText = $"""
            SELECT CASE
                       WHEN TRIM(COALESCE(a.mods_key, '')) = '' THEN 'NM'
                       ELSE UPPER(TRIM(a.mods_key))
                   END AS mods_key,
                   a.id,
                   {(hasBpm ? "b.bpm" : "NULL")},
                   {(hasAttemptMods ? "(SELECT m.settings_json FROM attempt_mods m WHERE m.attempt_id = a.id AND UPPER(m.acronym) = 'BPM' LIMIT 1)" : "NULL")},
                   {(hasContext ? "c.score_json" : "NULL")}
            FROM attempts a
            {(hasBeatmaps ? "LEFT JOIN beatmaps b ON b.id = a.beatmap_id" : "")}
            {(hasContext ? "LEFT JOIN attempt_context c ON c.attempt_id = a.id" : "")}
            WHERE {dayExpression} = @day
            ORDER BY a.id DESC
            """;
        command.Parameters.AddWithValue("@day", day);
        using var reader = command.ExecuteReader();
        var totals = new Dictionary<DailyModCombinationKey, (long Plays, long LatestAttempt)>();
        while (reader.Read())
        {
            string modsKey = reader.GetString(0);
            double? baseBpm = reader.IsDBNull(2) ? null : reader.GetDouble(2);
            double? targetBpm = ReadTargetBpm(reader.IsDBNull(3) ? null : reader.GetString(3));
            bool activeBpmAdjust = targetBpm is > 0
                                   && baseBpm is > 0
                                   && Math.Abs(targetBpm.Value - baseBpm.Value) > 0.05
                                   && HasAuthoritativeResultMods(reader.IsDBNull(4) ? null : reader.GetString(4));
            if (!activeBpmAdjust)
                modsKey = RemoveBpmMarker(modsKey);

            long attemptId = reader.GetInt64(1);
            var key = new DailyModCombinationKey(
                modsKey,
                activeBpmAdjust ? targetBpm : null);
            if (totals.TryGetValue(key, out var current))
                totals[key] = (current.Plays + 1, Math.Max(current.LatestAttempt, attemptId));
            else
                totals[key] = (1, attemptId);
        }

        return totals
            .OrderByDescending(pair => pair.Value.Plays)
            .ThenByDescending(pair => pair.Value.LatestAttempt)
            .Take(3)
            .Select(pair => new DailyModCombinationUsage
            {
                ModsKey = pair.Key.ModsKey,
                Bpm = pair.Key.Bpm,
                Plays = pair.Value.Plays,
            })
            .ToArray();
    }

    private readonly record struct DailyModCombinationKey(string ModsKey, double? Bpm);

    private static string RemoveBpmMarker(string modsKey)
    {
        string normalized = modsKey.Trim().ToUpperInvariant();
        if (normalized.Contains(','))
        {
            normalized = string.Join(",", normalized
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(mod => !mod.Equals("BPM", StringComparison.OrdinalIgnoreCase)));
        }
        else
        {
            normalized = normalized.Replace("BPM", "", StringComparison.OrdinalIgnoreCase);
        }

        return string.IsNullOrWhiteSpace(normalized) ? "NM" : normalized;
    }

    private static DailyMapHighlight? ReadMostPlayedMap(
        Microsoft.Data.Sqlite.SqliteConnection con,
        string dayExpression,
        string day)
    {
        if (!HasTable(con, "beatmaps")) return null;
        bool hasBeatmapId = HasColumn(con, "beatmaps", "beatmap_id");
        bool hasSetId = HasColumn(con, "beatmaps", "set_id");
        bool hasStars = HasColumn(con, "beatmaps", "stars");
        bool hasAr = HasColumn(con, "beatmaps", "ar");
        bool hasOd = HasColumn(con, "beatmaps", "od");
        bool hasCs = HasColumn(con, "beatmaps", "cs");
        bool hasBpm = HasColumn(con, "beatmaps", "bpm");
        using var command = con.CreateCommand();
        command.CommandText = $"""
            SELECT COALESCE(b.artist, ''), COALESCE(b.title, ''),
                   COALESCE(b.difficulty, ''), COUNT(*),
                   {(hasBeatmapId ? "COALESCE(b.beatmap_id, 0)" : "0")},
                   {(hasSetId ? "COALESCE(b.set_id, 0)" : "0")},
                   {(hasStars ? "b.stars" : "NULL")},
                   {(hasAr ? "b.ar" : "NULL")},
                   {(hasOd ? "b.od" : "NULL")},
                   {(hasCs ? "b.cs" : "NULL")},
                   {(hasBpm ? "b.bpm" : "NULL")}
            FROM attempts a
            JOIN beatmaps b ON b.id = a.beatmap_id
            WHERE {dayExpression} = @day
            GROUP BY b.id, b.artist, b.title, b.difficulty
            ORDER BY COUNT(*) DESC, MAX(a.pp) DESC, b.id
            LIMIT 1
            """;
        command.Parameters.AddWithValue("@day", day);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new DailyMapHighlight
            {
                Artist = reader.GetString(0),
                Title = reader.GetString(1),
                Difficulty = reader.GetString(2),
                Plays = reader.GetInt64(3),
                BeatmapId = reader.GetInt64(4),
                BeatmapSetId = reader.GetInt64(5),
                Stars = reader.IsDBNull(6) ? null : reader.GetDouble(6),
                Ar = reader.IsDBNull(7) ? null : reader.GetDouble(7),
                Od = reader.IsDBNull(8) ? null : reader.GetDouble(8),
                Cs = reader.IsDBNull(9) ? null : reader.GetDouble(9),
                Bpm = reader.IsDBNull(10) ? null : reader.GetDouble(10),
            }
            : null;
    }

    private static DailyPlayHighlight? ReadBestPlay(
        Microsoft.Data.Sqlite.SqliteConnection con,
        string dayExpression,
        string day)
    {
        if (!HasTable(con, "beatmaps")) return null;
        bool hasBpm = HasColumn(con, "beatmaps", "bpm");
        bool hasBeatmapId = HasColumn(con, "beatmaps", "beatmap_id");
        bool hasSetId = HasColumn(con, "beatmaps", "set_id");
        bool hasCombo = HasColumn(con, "attempts", "combo");
        bool hasMaxCombo = HasColumn(con, "beatmaps", "max_combo");
        bool hasN100 = HasColumn(con, "attempts", "n100");
        bool hasN50 = HasColumn(con, "attempts", "n50");
        bool hasSliderBreaks = HasColumn(con, "attempts", "slider_breaks");
        bool hasAttemptMods = HasTable(con, "attempt_mods");
        bool hasBaseStars = HasColumn(con, "attempts", "base_stars");
        bool hasAdjustedStars = HasColumn(con, "attempts", "adjusted_stars");
        bool hasBeatmapStars = HasColumn(con, "beatmaps", "stars");
        bool hasAr = HasColumn(con, "beatmaps", "ar");
        bool hasOd = HasColumn(con, "beatmaps", "od");
        bool hasCs = HasColumn(con, "beatmaps", "cs");
        bool hasContext = HasTable(con, "attempt_context");
        using var command = con.CreateCommand();
        command.CommandText = $"""
            SELECT COALESCE(b.artist, ''), COALESCE(b.title, ''),
                   COALESCE(b.difficulty, ''), a.pp, a.accuracy,
                   COALESCE(a.misses, 0), COALESCE(a.mods_key, 'NM'),
                   {(hasBpm ? "b.bpm" : "NULL")},
                   {(hasContext ? "c.beatmap_json" : "NULL")},
                   {(hasBeatmapId ? "COALESCE(b.beatmap_id, 0)" : "0")},
                   {(hasSetId ? "COALESCE(b.set_id, 0)" : "0")},
                   {(hasCombo ? "COALESCE(a.combo, 0)" : "0")},
                   {(hasMaxCombo ? "COALESCE(b.max_combo, 0)" : "0")},
                   {(hasN100 ? "COALESCE(a.n100, 0)" : "0")},
                   {(hasN50 ? "COALESCE(a.n50, 0)" : "0")},
                   {(hasSliderBreaks ? "COALESCE(a.slider_breaks, 0)" : "0")},
                   {(hasAttemptMods ? "(SELECT m.settings_json FROM attempt_mods m WHERE m.attempt_id = a.id AND UPPER(m.acronym) = 'BPM' LIMIT 1)" : "NULL")},
                   {(hasBaseStars ? "a.base_stars" : "NULL")},
                   {(hasAdjustedStars ? "a.adjusted_stars" : "NULL")},
                   {(hasBeatmapStars ? "b.stars" : "NULL")},
                   {(hasAr ? "b.ar" : "NULL")},
                   {(hasOd ? "b.od" : "NULL")},
                   {(hasCs ? "b.cs" : "NULL")},
                   {(hasContext ? "c.score_json" : "NULL")}
            FROM attempts a
            JOIN beatmaps b ON b.id = a.beatmap_id
            {(hasContext ? "LEFT JOIN attempt_context c ON c.attempt_id = a.id" : "")}
            WHERE {dayExpression} = @day AND a.outcome = 'completed'
            ORDER BY a.pp DESC, a.id DESC
            LIMIT 1
            """;
        command.Parameters.AddWithValue("@day", day);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;

        double? baseBpm = reader.IsDBNull(7) ? null : reader.GetDouble(7);
        string modsKey = reader.GetString(6);
        string? bpmSettings = reader.IsDBNull(16) ? null : reader.GetString(16);
        double? targetBpm = ReadTargetBpm(bpmSettings);
        bool usedBpmAdjust = targetBpm is > 0
                             && baseBpm is > 0
                             && Math.Abs(targetBpm.Value - baseBpm.Value) > 0.05
                             && HasAuthoritativeResultMods(reader.IsDBNull(23) ? null : reader.GetString(23));
        string? beatmapJson = reader.IsDBNull(8) ? null : reader.GetString(8);
        (double? Base, double? Adjusted) ar = ResolveDailyStat(
            beatmapJson,
            "ar",
            reader.IsDBNull(20) ? null : reader.GetDouble(20));
        (double? Base, double? Adjusted) od = ResolveDailyStat(
            beatmapJson,
            "od",
            reader.IsDBNull(21) ? null : reader.GetDouble(21));
        (double? Base, double? Adjusted) cs = ResolveDailyStat(
            beatmapJson,
            "cs",
            reader.IsDBNull(22) ? null : reader.GetDouble(22));
        double? baseStars = !reader.IsDBNull(17)
            ? reader.GetDouble(17)
            : !reader.IsDBNull(19) ? reader.GetDouble(19) : null;
        double? adjustedStars = !reader.IsDBNull(18) ? reader.GetDouble(18) : baseStars;
        return new DailyPlayHighlight
        {
            Artist = reader.GetString(0),
            Title = reader.GetString(1),
            Difficulty = reader.GetString(2),
            Pp = reader.GetDouble(3),
            Accuracy = reader.GetDouble(4),
            Misses = reader.GetInt64(5),
            ModsKey = modsKey,
            BaseStars = baseStars,
            AdjustedStars = adjustedStars,
            BaseAr = ar.Base,
            AdjustedAr = ar.Adjusted,
            BaseOd = od.Base,
            AdjustedOd = od.Adjusted,
            BaseCs = cs.Base,
            AdjustedCs = cs.Adjusted,
            BaseBpm = baseBpm,
            Bpm = usedBpmAdjust
                ? targetBpm
                : ResolveDailyBpm(
                    baseBpm,
                    modsKey,
                    beatmapJson),
            BeatmapId = reader.GetInt64(9),
            BeatmapSetId = reader.GetInt64(10),
            Combo = reader.GetInt64(11),
            MaxCombo = reader.GetInt64(12),
            N100 = reader.GetInt64(13),
            N50 = reader.GetInt64(14),
            SliderBreaks = reader.GetInt64(15),
            UsedBpmAdjust = usedBpmAdjust,
        };
    }

    private static (double? Base, double? Adjusted) ResolveDailyStat(
        string? beatmapJson,
        string name,
        double? fallback)
    {
        if (string.IsNullOrWhiteSpace(beatmapJson))
            return (fallback, fallback);
        try
        {
            using var document = JsonDocument.Parse(beatmapJson);
            if (!document.RootElement.TryGetProperty("stats", out JsonElement stats)
                || !stats.TryGetProperty(name, out JsonElement value))
            {
                return (fallback, fallback);
            }

            double? original = value.TryGetProperty("original", out JsonElement originalValue)
                               && originalValue.TryGetDouble(out double parsedOriginal)
                ? parsedOriginal
                : fallback;
            double? adjusted = value.TryGetProperty("converted", out JsonElement convertedValue)
                               && convertedValue.TryGetDouble(out double parsedAdjusted)
                ? parsedAdjusted
                : original;
            return (original, adjusted);
        }
        catch (JsonException)
        {
            return (fallback, fallback);
        }
    }

    private static double? ReadTargetBpm(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson))
            return null;
        try
        {
            using var document = JsonDocument.Parse(settingsJson);
            if (!document.RootElement.TryGetProperty("target_bpm", out JsonElement target)
                || !target.TryGetDouble(out double targetBpm)
                || !double.IsFinite(targetBpm)
                || targetBpm <= 0)
            {
                return null;
            }

            return targetBpm;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool HasAuthoritativeResultMods(string? scoreJson)
    {
        if (string.IsNullOrWhiteSpace(scoreJson))
            return false;
        try
        {
            using var document = JsonDocument.Parse(scoreJson);
            return document.RootElement.TryGetProperty("mods_authoritative_result", out JsonElement value)
                   && value.ValueKind is JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static double? ResolveDailyBpm(double? baseBpm, string modsKey, string? beatmapJson)
    {
        if (!string.IsNullOrWhiteSpace(beatmapJson))
        {
            try
            {
                using var document = JsonDocument.Parse(beatmapJson);
                if (document.RootElement.TryGetProperty("stats", out JsonElement stats)
                    && stats.TryGetProperty("bpm", out JsonElement bpm)
                    && bpm.TryGetProperty("realtime", out JsonElement realtime)
                    && realtime.TryGetDouble(out double captured)
                    && captured > 0)
                    return captured;
            }
            catch (JsonException)
            {
            }
        }

        if (baseBpm is not > 0)
            return null;
        string normalized = modsKey.Replace(",", "", StringComparison.Ordinal).ToUpperInvariant();
        if (normalized.Contains("DT", StringComparison.Ordinal)
            || normalized.Contains("NC", StringComparison.Ordinal))
            return baseBpm * 1.5;
        if (normalized.Contains("HT", StringComparison.Ordinal)
            || normalized.Contains("DC", StringComparison.Ordinal))
            return baseBpm * 0.75;
        return baseBpm;
    }

    private static DailyAccountProgress? ReadDailyAccountProgress(
        Microsoft.Data.Sqlite.SqliteConnection con,
        string day)
    {
        if (!HasTable(con, "profile_snapshots")) return null;
        var countryRankColumn = HasColumn(con, "profile_snapshots", "country_rank")
            ? "country_rank"
            : "NULL";
        using var command = con.CreateCommand();
        command.CommandText = $"""
            SELECT captured_at, player_id, COALESCE(player_name, ''), country_code,
                   total_pp, global_rank, play_count, {countryRankColumn}
            FROM profile_snapshots
            WHERE player_id = (
                SELECT player_id FROM profile_snapshots
                WHERE player_id IS NOT NULL ORDER BY id DESC LIMIT 1)
            ORDER BY captured_at ASC, id ASC
            """;
        using var reader = command.ExecuteReader();
        var readings = new List<DailyAccountReading>();
        while (reader.Read())
        {
            if (!DateTimeOffset.TryParse(
                    reader.GetString(0),
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AllowWhiteSpaces,
                    out var capturedAt))
                continue;
            readings.Add(new DailyAccountReading(
                capturedAt.ToLocalTime().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetDouble(4),
                reader.IsDBNull(5) ? null : reader.GetInt64(5),
                reader.IsDBNull(6) ? null : reader.GetInt64(6),
                reader.IsDBNull(7) ? null : reader.GetInt64(7)));
        }
        var groups = readings.GroupBy(reading => reading.Day, StringComparer.Ordinal).ToList();
        var index = groups.FindIndex(group => string.Equals(group.Key, day, StringComparison.Ordinal));
        if (index < 0) return null;
        var first = groups[index].First();
        var latest = groups[index].Last();
        var baseline = index > 0 ? groups[index - 1].Last() : first;
        return new DailyAccountProgress
        {
            PlayerId = latest.PlayerId,
            PlayerName = latest.PlayerName,
            CountryCode = latest.CountryCode,
            OldTotalPp = baseline.TotalPp,
            NewTotalPp = latest.TotalPp,
            OldGlobalRank = baseline.GlobalRank,
            NewGlobalRank = latest.GlobalRank,
            OldCountryRank = index > 0
                ? LastCountryRank(groups[index - 1])
                : FirstCountryRank(groups[index]),
            NewCountryRank = LastCountryRank(groups[index]),
            OldPlayCount = baseline.PlayCount,
            NewPlayCount = latest.PlayCount,
        };
    }

    private sealed record DailyAccountReading(
        string Day,
        long PlayerId,
        string PlayerName,
        string? CountryCode,
        double? TotalPp,
        long? GlobalRank,
        long? PlayCount,
        long? CountryRank);

    private static long? LastCountryRank(IEnumerable<DailyAccountReading> readings) =>
        readings.Reverse().FirstOrDefault(reading => reading.CountryRank is not null)?.CountryRank;

    private static long? FirstCountryRank(IEnumerable<DailyAccountReading> readings) =>
        readings.FirstOrDefault(reading => reading.CountryRank is not null)?.CountryRank;

    private static IReadOnlyDictionary<string, DailyAccountChange> ReadDailyProfileChanges(
        Microsoft.Data.Sqlite.SqliteConnection con)
    {
        var changes = new Dictionary<string, DailyAccountChange>(StringComparer.Ordinal);
        if (!HasTable(con, "profile_snapshots"))
        {
            return changes;
        }

        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            SELECT captured_at, total_pp, global_rank
            FROM profile_snapshots
            WHERE player_id = (
                SELECT player_id
                FROM profile_snapshots
                WHERE player_id IS NOT NULL
                ORDER BY id DESC
                LIMIT 1)
            ORDER BY captured_at ASC, id ASC
            """;
        using var reader = cmd.ExecuteReader();
        var readings = new List<DailyProfileReading>();
        while (reader.Read())
        {
            if (!DateTimeOffset.TryParse(
                    reader.GetString(0),
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AllowWhiteSpaces,
                    out var capturedAt))
            {
                continue;
            }

            readings.Add(new DailyProfileReading(
                capturedAt.ToLocalTime().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                reader.IsDBNull(1) ? null : reader.GetDouble(1),
                reader.IsDBNull(2) ? null : reader.GetInt64(2)));
        }

        DailyProfileReading? previousDayLast = null;
        foreach (var group in readings.GroupBy(reading => reading.Day, StringComparer.Ordinal))
        {
            var first = group.First();
            var latest = group.Last();
            var baseline = previousDayLast ?? first;
            changes[group.Key] = new DailyAccountChange(
                baseline.TotalPp is { } oldPp && latest.TotalPp is { } newPp ? newPp - oldPp : null,
                baseline.GlobalRank is { } oldRank && latest.GlobalRank is { } newRank ? oldRank - newRank : null);
            previousDayLast = latest;
        }

        return changes;
    }

    private sealed record DailyProfileReading(string Day, double? TotalPp, long? GlobalRank);
    private sealed record DailyAccountChange(double? PpChange, long? RankChange);

    private static (string Key1, string Key2) ReadLatestKeyBindings(Microsoft.Data.Sqlite.SqliteConnection con)
    {
        if (!HasColumn(con, "attempts", "key1_binding")) return ("Z", "X");
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT key1_binding, key2_binding FROM attempts ORDER BY id DESC LIMIT 1";
        using var reader = cmd.ExecuteReader();
        return reader.Read()
            ? (reader.IsDBNull(0) ? "Z" : reader.GetString(0), reader.IsDBNull(1) ? "X" : reader.GetString(1))
            : ("Z", "X");
    }

    private static string? ReadLastSynced(Microsoft.Data.Sqlite.SqliteConnection con)
    {
        if (!HasTable(con, "profile_snapshots"))
        {
            return null;
        }
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT captured_at FROM profile_snapshots ORDER BY id DESC LIMIT 1";
        return cmd.ExecuteScalar() is string value && value.Length > 0 ? value : null;
    }

    private static bool HasColumn(Microsoft.Data.Sqlite.SqliteConnection con, string table, string column)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static AccountChangeSummary? ReadLatestProfileChange(Microsoft.Data.Sqlite.SqliteConnection con)
    {
        if (HasTable(con, "profile_snapshots"))
        {
            using var snapshots = con.CreateCommand();
            snapshots.CommandText = """
                SELECT total_pp, global_rank, accuracy, play_count
                FROM profile_snapshots
                WHERE player_id = (
                    SELECT player_id FROM profile_snapshots
                    WHERE player_id IS NOT NULL
                    ORDER BY id DESC
                    LIMIT 1)
                ORDER BY id ASC
                LIMIT 1;
                SELECT total_pp, global_rank, accuracy, play_count
                FROM profile_snapshots
                WHERE player_id = (
                    SELECT player_id FROM profile_snapshots
                    WHERE player_id IS NOT NULL
                    ORDER BY id DESC
                    LIMIT 1)
                ORDER BY id DESC
                LIMIT 1
                """;
            using var reader = snapshots.ExecuteReader();
            if (!reader.Read()) return null;
            var first = ReadAccountChangeRow(reader);
            if (!reader.NextResult() || !reader.Read()) return null;
            var latest = ReadAccountChangeRow(reader);
            return new AccountChangeSummary
            {
                OldTotalPp = first.TotalPp,
                NewTotalPp = latest.TotalPp,
                OldGlobalRank = first.GlobalRank,
                NewGlobalRank = latest.GlobalRank,
                OldAccuracy = first.Accuracy,
                NewAccuracy = latest.Accuracy,
                OldPlayCount = first.PlayCount,
                NewPlayCount = latest.PlayCount,
            };
        }

        return ReadLatestAttemptProfileChange(con);
    }

    // Kept solely for databases created by older versions that do not contain
    // profile snapshots yet. New dashboard values compare the first and latest
    // snapshots for the active player profile.
    private static AccountChangeSummary? ReadLatestAttemptProfileChange(Microsoft.Data.Sqlite.SqliteConnection con)
    {
        if (!HasTable(con, "attempt_profile_changes") || !HasTable(con, "sessions")) return null;

        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            SELECT old_total_pp, new_total_pp,
                   old_global_rank, new_global_rank,
                   old_accuracy, new_accuracy,
                   old_play_count, new_play_count
            FROM attempt_profile_changes
            WHERE attempt_id IN (
                SELECT id
                FROM attempts
                WHERE session_id = (SELECT id FROM sessions ORDER BY id DESC LIMIT 1)
            )
            ORDER BY captured_at DESC, attempt_id DESC
            LIMIT 1
            """;
        using var r = cmd.ExecuteReader();
        if (!r.Read())
        {
            return null;
        }

        return new AccountChangeSummary
        {
            OldTotalPp = r.IsDBNull(0) ? null : r.GetDouble(0),
            NewTotalPp = r.IsDBNull(1) ? null : r.GetDouble(1),
            OldGlobalRank = r.IsDBNull(2) ? null : r.GetInt64(2),
            NewGlobalRank = r.IsDBNull(3) ? null : r.GetInt64(3),
            OldAccuracy = r.IsDBNull(4) ? null : r.GetDouble(4),
            NewAccuracy = r.IsDBNull(5) ? null : r.GetDouble(5),
            OldPlayCount = r.IsDBNull(6) ? null : r.GetInt64(6),
            NewPlayCount = r.IsDBNull(7) ? null : r.GetInt64(7),
        };
    }

    private static (double? TotalPp, long? GlobalRank, double? Accuracy, long? PlayCount) ReadAccountChangeRow(Microsoft.Data.Sqlite.SqliteDataReader reader) =>
        (reader.IsDBNull(0) ? null : reader.GetDouble(0),
         reader.IsDBNull(1) ? null : reader.GetInt64(1),
         reader.IsDBNull(2) ? null : reader.GetDouble(2),
         reader.IsDBNull(3) ? null : reader.GetInt64(3));

    private static bool HasTable(Microsoft.Data.Sqlite.SqliteConnection con, string table)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @table";
        cmd.Parameters.AddWithValue("@table", table);
        return (long)(cmd.ExecuteScalar() ?? 0L) > 0;
    }
}
