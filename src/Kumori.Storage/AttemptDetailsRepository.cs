using System.Globalization;
using System.Text.Json;
using Kumori.Core.Models;
using Microsoft.Data.Sqlite;
using Serilog;

namespace Kumori.Storage;

/// <summary>
/// Loads the full inspector model for one attempt. Called on selection only,
/// always off the UI thread.
/// </summary>
public sealed class AttemptDetailsRepository
{
    private readonly SqliteConnectionFactory _factory;

    public AttemptDetailsRepository(SqliteConnectionFactory factory)
    {
        _factory = factory;
    }

    public AttemptDetails? GetDetails(long attemptId)
    {
        if (!_factory.DatabaseExists)
        {
            return null;
        }
        using var con = _factory.Open();

        var details = ReadAttemptRow(con, attemptId);
        if (details is null)
        {
            return null;
        }
        var source = ReadSourceContext(con, attemptId);
        return details with
        {
            Mods = ReadMods(con, attemptId),
            Timing = ReadTiming(con, attemptId),
            Events = ReadEvents(con, attemptId),
            Input = ReadInput(con, attemptId),
            Movement = ReadMovement(con, attemptId),
            CapturedDifficulty = ReadCapturedDifficulty(con, attemptId),
            LocalBeatmapPath = source.BeatmapPath,
            LocalMediaDirectory = source.MediaDirectory,
            ClientKind = source.ClientKind,
            ResultRecoveredFromReplay = source.ResultRecovered,
            ResultRecoverySource = source.RecoverySource,
            ResultRecoverySimulationCompleted = source.SimulationCompleted,
        };
    }

    private static (string? BeatmapPath, string? MediaDirectory, string ClientKind,
        bool ResultRecovered, string? RecoverySource, bool SimulationCompleted) ReadSourceContext(
        SqliteConnection con,
        long attemptId)
    {
        if (!TableExists(con, "attempt_context"))
            return (null, null, "unknown", false, null, false);
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT source_json FROM attempt_context WHERE attempt_id = @id";
        cmd.Parameters.AddWithValue("@id", attemptId);
        string? json = cmd.ExecuteScalar() as string;
        if (string.IsNullOrWhiteSpace(json))
            return (null, null, "unknown", false, null, false);
        try
        {
            using var document = JsonDocument.Parse(json);
            string? beatmap = document.RootElement.TryGetProperty("beatmap_path", out var beatmapElement)
                ? beatmapElement.GetString()
                : null;
            string? media = document.RootElement.TryGetProperty("media_directory", out var mediaElement)
                ? mediaElement.GetString()
                : null;
            string clientKind = document.RootElement.TryGetProperty("client_kind", out var clientElement)
                ? clientElement.GetString() ?? "unknown"
                : "unknown";
            bool recovered = document.RootElement.TryGetProperty("result_recovery", out var recovery)
                             && recovery.ValueKind == JsonValueKind.Object;
            string? recoverySource = recovered && recovery.TryGetProperty("source", out var sourceElement)
                ? sourceElement.GetString()
                : null;
            bool simulationCompleted = recovered
                                       && recovery.TryGetProperty("simulation", out var simulation)
                                       && simulation.GetString()?.Equals("completed", StringComparison.OrdinalIgnoreCase) == true;
            return (beatmap, media, clientKind, recovered, recoverySource, simulationCompleted);
        }
        catch (JsonException)
        {
            return (null, null, "unknown", false, null, false);
        }
    }

    public IReadOnlyList<AttemptTrendSummary> GetRecentSameMapAttempts(long attemptId, int limit = 6)
    {
        if (!_factory.DatabaseExists)
            return [];
        using var con = _factory.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            SELECT a.id, a.accuracy, a.n100, a.n50, a.misses, a.slider_breaks, t.mean
            FROM attempts a
            LEFT JOIN attempt_timing t ON t.attempt_id = a.id
            WHERE a.beatmap_id = (SELECT beatmap_id FROM attempts WHERE id = @id)
              AND a.id < @id AND a.outcome <> 'active'
            ORDER BY CASE a.outcome
                         WHEN 'completed' THEN 0
                         WHEN 'failed' THEN 1
                         ELSE 2
                     END,
                     a.id DESC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@id", attemptId);
        cmd.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 20));
        using var reader = cmd.ExecuteReader();
        var result = new List<AttemptTrendSummary>();
        while (reader.Read())
        {
            result.Add(new AttemptTrendSummary
            {
                Id = reader.GetInt64(0),
                Accuracy = reader.GetDouble(1),
                N100 = (int)reader.GetInt64(2),
                N50 = (int)reader.GetInt64(3),
                Misses = (int)reader.GetInt64(4),
                SliderBreaks = (int)reader.GetInt64(5),
                MeanOffset = reader.IsDBNull(6) ? null : reader.GetDouble(6),
            });
        }
        return result;
    }

    /// <summary>
    /// Returns finished attempts which can be overlaid on <paramref name="attemptId"/>.
    /// Matching the internal beatmap row guarantees the same map and difficulty.
    /// Candidates must additionally have movement and an identical normalized
    /// mod/settings signature so their timeline, geometry, judgements and score
    /// can be compared without mixing different gameplay configurations.
    /// </summary>
    public IReadOnlyList<ReplayComparisonSummary> GetComparableAttempts(long attemptId, int limit = 20)
    {
        if (!_factory.DatabaseExists)
            return [];

        using var con = _factory.Open();
        string primaryModsKey;
        using (var primary = con.CreateCommand())
        {
            primary.CommandText = "SELECT COALESCE(mods_key, 'NM') FROM attempts WHERE id = @id";
            primary.Parameters.AddWithValue("@id", attemptId);
            primaryModsKey = primary.ExecuteScalar() as string ?? "NM";
        }
        string primarySignature = ReplayComparisonCompatibility.Signature(
            primaryModsKey,
            ReadMods(con, attemptId));

        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            SELECT a.id, a.started_at, a.outcome, COALESCE(a.mods_key, 'NM'),
                   a.accuracy, a.score, a.pp, a.combo,
                   a.n300, a.n100, a.n50, a.misses
            FROM attempts a
            WHERE a.beatmap_id = (SELECT beatmap_id FROM attempts WHERE id = @id)
              AND a.id <> @id
              AND a.outcome <> 'active'
              AND EXISTS (
                  SELECT 1 FROM attempt_movement_chunks movement
                  WHERE movement.attempt_id = a.id
              )
            ORDER BY a.id DESC
            """;
        cmd.Parameters.AddWithValue("@id", attemptId);

        using var reader = cmd.ExecuteReader();
        var candidates = new List<ReplayComparisonSummary>();
        while (reader.Read())
        {
            candidates.Add(new ReplayComparisonSummary(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetDouble(4),
                reader.GetInt64(5),
                reader.GetDouble(6),
                (int)reader.GetInt64(7),
                (int)reader.GetInt64(8),
                (int)reader.GetInt64(9),
                (int)reader.GetInt64(10),
                (int)reader.GetInt64(11)));
        }

        reader.Close();
        int clampedLimit = Math.Clamp(limit, 1, 25);
        var result = new List<ReplayComparisonSummary>(clampedLimit);
        foreach (ReplayComparisonSummary candidate in candidates)
        {
            string candidateSignature = ReplayComparisonCompatibility.Signature(
                candidate.ModsKey,
                ReadMods(con, candidate.Id));
            if (!string.Equals(primarySignature, candidateSignature, StringComparison.Ordinal))
                continue;

            result.Add(candidate);
            if (result.Count == clampedLimit)
                break;
        }
        return result;
    }

    public IReadOnlyList<JudgementEvent> GetJudgementEvents(long attemptId)
    {
        if (!_factory.DatabaseExists)
            return [];

        using var con = _factory.Open();
        return ReadEvents(con, attemptId);
    }

    private static AttemptDetails? ReadAttemptRow(SqliteConnection con, long attemptId)
    {
        using var cmd = con.CreateCommand();
        var hasExternalBeatmapId = HasColumn(con, "beatmaps", "beatmap_id");
        var hasSetId = HasColumn(con, "beatmaps", "set_id");
        var hasChecksum = HasColumn(con, "beatmaps", "checksum");
        var hasTicks = HasColumn(con, "attempts", "large_tick_hits");
        var hasTails = HasColumn(con, "attempts", "slider_tail_hits");
        var hasBaseStars = HasColumn(con, "attempts", "base_stars");
        var hasMapper = HasColumn(con, "beatmaps", "mapper");
        var hasBeatmapDiff = HasColumn(con, "beatmaps", "ar");
        var hasBpm = HasColumn(con, "beatmaps", "bpm");
        var hasMaxCombo = HasColumn(con, "beatmaps", "max_combo");
        cmd.CommandText = $"""
            SELECT a.id, a.session_id, a.started_at, a.ended_at, a.outcome,
                   a.grade, a.accuracy, a.score, a.pp, a.combo, a.misses,
                   a.mods_key, a.progress,
                   COALESCE(b.artist, ''), COALESCE(b.title, ''),
                   COALESCE(b.difficulty, ''), {(hasBaseStars ? "COALESCE(a.base_stars, b.stars)" : "b.stars")},
                   {(hasExternalBeatmapId ? "b.beatmap_id" : "NULL")},
                   {(hasSetId ? "b.set_id" : "NULL")},
                   {(hasChecksum ? "b.checksum" : "b.identity")},
                   a.n300, a.n100, a.n50, a.geki, a.katu, a.slider_breaks,
                   a.unstable_rate, a.fc_pp, a.max_pp, a.duration_seconds,
                   a.termination_evidence, a.z_count, a.x_count,
                   a.key1_binding, a.key2_binding,
                   {(hasTicks ? "a.large_tick_hits" : "0")}, {(hasTicks ? "a.large_tick_misses" : "0")},
                   {(hasTicks ? "a.small_tick_hits" : "0")}, {(hasTicks ? "a.small_tick_misses" : "0")},
                   {(hasTails ? "a.slider_tail_hits" : "0")}, {(hasTails ? "a.slider_tail_misses" : "0")},
                   {(hasBaseStars ? "a.base_stars" : "NULL")}, {(hasBaseStars ? "a.adjusted_stars" : "NULL")},
                   {(hasMapper ? "COALESCE(b.mapper, '')" : "''")},
                   {(hasBeatmapDiff ? "b.ar" : "NULL")}, {(hasBeatmapDiff ? "b.cs" : "NULL")},
                   {(hasBeatmapDiff ? "b.od" : "NULL")}, {(hasBeatmapDiff ? "b.hp" : "NULL")},
                   {(hasBpm ? "b.bpm" : "NULL")},
                   {(hasMaxCombo ? "COALESCE(b.max_combo, 0)" : "0")}
            FROM attempts a
            JOIN beatmaps b ON b.id = a.beatmap_id
            WHERE a.id = @id
            """;
        cmd.Parameters.AddWithValue("@id", attemptId);
        using var r = cmd.ExecuteReader();
        if (!r.Read())
        {
            return null;
        }
        var summary = new AttemptSummary
        {
            Id = r.GetInt64(0),
            SessionId = r.GetInt64(1),
            StartedAt = r.GetString(2),
            EndedAt = r.IsDBNull(3) ? null : r.GetString(3),
            Outcome = r.GetString(4),
            Grade = r.IsDBNull(5) ? null : r.GetString(5),
            Accuracy = r.GetDouble(6),
            Score = r.GetInt64(7),
            Pp = r.GetDouble(8),
            Combo = (int)r.GetInt64(9),
            Misses = (int)r.GetInt64(10),
            ModsKey = r.GetString(11),
            Progress = r.GetDouble(12),
            Artist = r.GetString(13),
            Title = r.GetString(14),
            Difficulty = r.GetString(15),
            Stars = r.IsDBNull(16) ? null : r.GetDouble(16),
            OsuBeatmapId = r.IsDBNull(17) ? null : r.GetInt64(17),
            BeatmapSetId = r.IsDBNull(18) ? null : r.GetInt64(18),
            Checksum = r.IsDBNull(19) ? null : r.GetString(19),
            Key1Count = (int)r.GetInt64(31),
            Key2Count = (int)r.GetInt64(32),
        };
        return new AttemptDetails
        {
            Summary = summary,
            N300 = (int)r.GetInt64(20),
            N100 = (int)r.GetInt64(21),
            N50 = (int)r.GetInt64(22),
            Geki = (int)r.GetInt64(23),
            Katu = (int)r.GetInt64(24),
            SliderBreaks = (int)r.GetInt64(25),
            UnstableRate = r.GetDouble(26),
            FcPp = r.GetDouble(27),
            MaxPp = r.GetDouble(28),
            DurationSeconds = r.GetDouble(29),
            TerminationEvidence = r.IsDBNull(30) ? null : r.GetString(30),
            Key1Count = (int)r.GetInt64(31),
            Key2Count = (int)r.GetInt64(32),
            Key1Binding = r.GetString(33),
            Key2Binding = r.GetString(34),
            LargeTickHits = (int)r.GetInt64(35),
            LargeTickMisses = (int)r.GetInt64(36),
            SmallTickHits = (int)r.GetInt64(37),
            SmallTickMisses = (int)r.GetInt64(38),
            SliderTailHits = (int)r.GetInt64(39),
            SliderTailMisses = (int)r.GetInt64(40),
            BaseStars = r.IsDBNull(41) ? null : r.GetDouble(41),
            AdjustedStars = r.IsDBNull(42) ? null : r.GetDouble(42),
            Mapper = r.GetString(43),
            BeatmapAr = r.IsDBNull(44) ? null : r.GetDouble(44),
            BeatmapCs = r.IsDBNull(45) ? null : r.GetDouble(45),
            BeatmapOd = r.IsDBNull(46) ? null : r.GetDouble(46),
            BeatmapHp = r.IsDBNull(47) ? null : r.GetDouble(47),
            Bpm = r.IsDBNull(48) ? null : r.GetDouble(48),
            BeatmapMaxCombo = (int)r.GetInt64(49),
        };
    }

    private static bool HasColumn(SqliteConnection con, string table, string column)
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

    private static IReadOnlyList<ModEntry> ReadMods(SqliteConnection con, long attemptId)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            SELECT acronym, settings_json FROM attempt_mods
            WHERE attempt_id = @id ORDER BY position
            """;
        cmd.Parameters.AddWithValue("@id", attemptId);
        var mods = new List<ModEntry>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            mods.Add(new ModEntry(r.GetString(0), r.GetString(1)));
        }
        return mods;
    }

    private static TimingSummary? ReadTiming(SqliteConnection con, long attemptId)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            SELECT offsets_zlib, hit_count, early_count, late_count,
                   mean, median, deviation
            FROM attempt_timing WHERE attempt_id = @id
            """;
        cmd.Parameters.AddWithValue("@id", attemptId);
        using var r = cmd.ExecuteReader();
        if (!r.Read())
        {
            return null;
        }
        double[] offsets;
        try
        {
            offsets = BlobCodec.DecodeOffsets((byte[])r.GetValue(0));
        }
        catch
        {
            offsets = Array.Empty<double>(); // corrupt blob: keep the summary stats
        }
        return new TimingSummary
        {
            Offsets = offsets,
            HitCount = (int)r.GetInt64(1),
            EarlyCount = (int)r.GetInt64(2),
            LateCount = (int)r.GetInt64(3),
            Mean = r.GetDouble(4),
            Median = r.GetDouble(5),
            Deviation = r.GetDouble(6),
        };
    }

    private static IReadOnlyList<JudgementEvent> ReadEvents(SqliteConnection con, long attemptId)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            SELECT id, event_type, map_time_ms, value, data_json
            FROM attempt_events
            WHERE attempt_id = @id
            ORDER BY map_time_ms, id
            """;
        cmd.Parameters.AddWithValue("@id", attemptId);
        var events = new List<JudgementEvent>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            events.Add(new JudgementEvent
            {
                Id = r.GetInt64(0),
                EventType = r.GetString(1),
                MapTimeMs = r.IsDBNull(2) ? null : r.GetInt64(2),
                Value = r.IsDBNull(3) ? null : r.GetDouble(3),
                DataJson = r.GetString(4),
            });
        }
        return events;
    }

    private static InputSummary? ReadInput(SqliteConnection con, long attemptId)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            SELECT key1_presses, key2_presses, alternations,
                   simultaneous_presses, key1_hold_ms, key2_hold_ms,
                   peak_kps, average_kps
            FROM attempt_input_summary WHERE attempt_id = @id
            """;
        cmd.Parameters.AddWithValue("@id", attemptId);
        using var r = cmd.ExecuteReader();
        if (!r.Read())
        {
            return null;
        }
        return new InputSummary
        {
            Key1Presses = (int)r.GetInt64(0),
            Key2Presses = (int)r.GetInt64(1),
            Alternations = (int)r.GetInt64(2),
            SimultaneousPresses = (int)r.GetInt64(3),
            Key1HoldMs = r.GetDouble(4),
            Key2HoldMs = r.GetDouble(5),
            PeakKps = (int)r.GetInt64(6),
            AverageKps = r.GetDouble(7),
        };
    }

    private static MovementSummary? ReadMovement(SqliteConnection con, long attemptId)
    {
        if (!TableExists(con, "attempt_movement"))
        {
            return null;
        }
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            SELECT source, sample_rate, sample_count, dropped_samples
            FROM attempt_movement WHERE attempt_id = @id
            """;
        cmd.Parameters.AddWithValue("@id", attemptId);
        using var r = cmd.ExecuteReader();
        if (!r.Read())
        {
            return null;
        }
        var count = (int)r.GetInt64(2);
        return new MovementSummary
        {
            Source = r.IsDBNull(0) ? null : r.GetString(0),
            SampleRate = r.IsDBNull(1) ? 0 : r.GetDouble(1),
            SampleCount = count,
            DroppedSamples = r.IsDBNull(3) ? 0 : (int)r.GetInt64(3),
            Available = count > 0,
        };
    }

    private static IReadOnlyDictionary<string, DifficultyPair> ReadCapturedDifficulty(
        SqliteConnection con, long attemptId)
    {
        var result = new Dictionary<string, DifficultyPair>();
        if (!TableExists(con, "attempt_context"))
        {
            return result;
        }
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT beatmap_json FROM attempt_context WHERE attempt_id = @id";
        cmd.Parameters.AddWithValue("@id", attemptId);
        string json;
        using (var r = cmd.ExecuteReader())
        {
            if (!r.Read() || r.IsDBNull(0))
            {
                return result;
            }
            json = r.GetString(0);
        }
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("stats", out var stats)
                || stats.ValueKind != JsonValueKind.Object)
            {
                return result;
            }
            foreach (var name in new[] { "ar", "cs", "od", "hp" })
            {
                if (stats.TryGetProperty(name, out var element))
                {
                    result[name] = ParsePair(element, "original", "converted");
                }
            }
            if (stats.TryGetProperty("stars", out var starsElement))
            {
                result["stars"] = ParsePair(starsElement, "original", "total");
            }
            if (stats.TryGetProperty("bpm", out var bpmElement))
            {
                result["bpm"] = ParsePair(bpmElement, "common", "realtime");
            }
        }
        catch (JsonException ex)
        {
            Log.Warning(ex, "Corrupt attempt_context beatmap_json for attempt {AttemptId}", attemptId);
        }
        return result;
    }

    private static DifficultyPair ParsePair(JsonElement element, string originalKey, string convertedKey)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return new DifficultyPair(null, TryNumber(element));
        }
        double? original = element.TryGetProperty(originalKey, out var o) ? TryNumber(o) : null;
        double? converted = element.TryGetProperty(convertedKey, out var c) ? TryNumber(c) : null;
        return new DifficultyPair(original, converted);
    }

    private static double? TryNumber(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Number => element.TryGetDouble(out var d) ? d : null,
        JsonValueKind.String => double.TryParse(
            element.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var s) ? s : null,
        _ => null,
    };

    private static bool TableExists(SqliteConnection con, string table)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=@name LIMIT 1";
        cmd.Parameters.AddWithValue("@name", table);
        using var reader = cmd.ExecuteReader();
        return reader.Read();
    }
}

public sealed record ReplayComparisonSummary(
    long Id,
    string StartedAt,
    string Outcome,
    string ModsKey,
    double Accuracy,
    long Score,
    double Pp,
    int Combo,
    int N300,
    int N100,
    int N50,
    int Misses);
