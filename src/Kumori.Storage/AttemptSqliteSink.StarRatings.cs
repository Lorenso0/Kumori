using System.Text.Json;
using System.Text.Json.Nodes;
using Kumori.Gameplay;
using Kumori.Tracking;
using Microsoft.Data.Sqlite;
using Serilog;

namespace Kumori.Storage;

public sealed partial class AttemptSqliteSink
{
    private const string calculated_star_marker = "kumori_calculated";

    private static AttemptStart EnrichBpmStarRatings(AttemptStart start)
    {
        if (!HasBpmAdjust(start.Mods))
            return start;

        string? beatmapPath = ResolveBeatmapPath(start);
        if (string.IsNullOrWhiteSpace(beatmapPath))
            return start;

        try
        {
            BeatmapDifficultyResult result = BeatmapDifficultyCalculator.Calculate(
                beatmapPath,
                ToCapturedMods(start.Mods));
            return start with { BeatmapStats = WithCalculatedStars(start.BeatmapStats, result) };
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "BPM Adjust star calculation failed for beatmap {BeatmapPath}", beatmapPath);
            return start;
        }
    }

    private static AttemptSnapshot RestoreCalculatedBpmStars(
        SqliteConnection con,
        SqliteTransaction tx,
        long attemptId,
        AttemptSnapshot snapshot)
    {
        using var cmd = con.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT a.base_stars, a.adjusted_stars, c.beatmap_json
            FROM attempts a
            JOIN attempt_context c ON c.attempt_id = a.id
            WHERE a.id = @id
              AND EXISTS(
                  SELECT 1 FROM attempt_mods m
                  WHERE m.attempt_id = a.id AND UPPER(m.acronym) = 'BPM')
            """;
        cmd.Parameters.AddWithValue("@id", attemptId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()
            || reader.IsDBNull(0)
            || reader.IsDBNull(1)
            || !HasCalculatedStarMarker(reader.GetString(2)))
        {
            return snapshot;
        }

        var result = new BeatmapDifficultyResult(reader.GetDouble(0), reader.GetDouble(1));
        return snapshot with { BeatmapStats = WithCalculatedStars(snapshot.BeatmapStats, result) };
    }

    private static string? ResolveBeatmapPath(AttemptStart start)
    {
        string? stable = ResolveStableBeatmapPath(start);
        if (!string.IsNullOrWhiteSpace(stable))
            return stable;

        return LazerStorage.ResolveBeatmapAssets(
            start.BeatmapId,
            start.BeatmapSetId,
            start.Difficulty,
            start.GameFolder)?.BeatmapPath;
    }

    private static bool HasBpmAdjust(IReadOnlyList<AttemptMod> mods) =>
        mods.Any(mod => mod.Acronym.Equals("BPM", StringComparison.OrdinalIgnoreCase));

    private static CapturedMod[] ToCapturedMods(IReadOnlyList<AttemptMod> mods) =>
        mods.Select(mod => new CapturedMod(mod.Acronym, mod.SettingsJson)).ToArray();

    private static BeatmapStats WithCalculatedStars(BeatmapStats stats, BeatmapDifficultyResult result)
    {
        JsonObject raw = ParseObject(stats.RawJson);
        SetCalculatedStars(raw, result);
        return stats with
        {
            BaseStars = result.BaseStars,
            Stars = result.AdjustedStars,
            RawJson = raw.ToJsonString(),
        };
    }

    private static void BackfillBpmAttemptStars(SqliteConnection con)
    {
        var candidates = new List<BpmStarBackfillCandidate>();
        using (var select = con.CreateCommand())
        {
            select.CommandText = """
                SELECT a.id, b.beatmap_id, b.set_id, b.difficulty,
                       COALESCE(c.source_json, '{}'), COALESCE(c.beatmap_json, '{}')
                FROM attempts a
                JOIN beatmaps b ON b.id = a.beatmap_id
                LEFT JOIN attempt_context c ON c.attempt_id = a.id
                WHERE EXISTS(
                    SELECT 1 FROM attempt_mods m
                    WHERE m.attempt_id = a.id AND UPPER(m.acronym) = 'BPM')
                """;
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                string beatmapJson = reader.GetString(5);
                if (HasCalculatedStarMarker(beatmapJson))
                    continue;

                candidates.Add(new BpmStarBackfillCandidate(
                    reader.GetInt64(0),
                    reader.IsDBNull(1) ? null : reader.GetInt64(1),
                    reader.IsDBNull(2) ? null : reader.GetInt64(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.GetString(4),
                    beatmapJson));
            }
        }

        foreach (BpmStarBackfillCandidate candidate in candidates)
        {
            string? beatmapPath = ResolveBackfillBeatmapPath(candidate);
            if (string.IsNullOrWhiteSpace(beatmapPath))
                continue;

            try
            {
                CapturedMod[] mods = ReadCapturedMods(con, candidate.AttemptId);
                BeatmapDifficultyResult result = BeatmapDifficultyCalculator.Calculate(beatmapPath, mods);
                using var tx = con.BeginTransaction();
                using var update = con.CreateCommand();
                update.Transaction = tx;
                update.CommandText = """
                    UPDATE attempts
                    SET base_stars = @base_stars,
                        adjusted_stars = @adjusted_stars
                    WHERE id = @id;
                    UPDATE attempt_context
                    SET beatmap_json = @beatmap_json
                    WHERE attempt_id = @id;
                    """;
                update.Parameters.AddWithValue("@base_stars", result.BaseStars);
                update.Parameters.AddWithValue("@adjusted_stars", result.AdjustedStars);
                update.Parameters.AddWithValue("@beatmap_json", PatchBeatmapContext(candidate.BeatmapJson, result));
                update.Parameters.AddWithValue("@id", candidate.AttemptId);
                update.ExecuteNonQuery();
                tx.Commit();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Historical BPM Adjust star backfill failed for attempt {AttemptId}", candidate.AttemptId);
            }
        }
    }

    private static string? ResolveBackfillBeatmapPath(BpmStarBackfillCandidate candidate)
    {
        try
        {
            using var source = JsonDocument.Parse(candidate.SourceJson);
            if (source.RootElement.TryGetProperty("beatmap_path", out JsonElement pathElement)
                && pathElement.ValueKind == JsonValueKind.String
                && pathElement.GetString() is { Length: > 0 } path
                && File.Exists(path))
            {
                return path;
            }

            string? gameFolder = source.RootElement.TryGetProperty("game_folder", out JsonElement gameElement)
                                 && gameElement.ValueKind == JsonValueKind.String
                ? gameElement.GetString()
                : null;
            return LazerStorage.ResolveBeatmapAssets(
                candidate.BeatmapId,
                candidate.BeatmapSetId,
                candidate.Difficulty,
                gameFolder)?.BeatmapPath;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static CapturedMod[] ReadCapturedMods(SqliteConnection con, long attemptId)
    {
        var mods = new List<CapturedMod>();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            SELECT acronym, settings_json
            FROM attempt_mods
            WHERE attempt_id = @id
            ORDER BY position
            """;
        cmd.Parameters.AddWithValue("@id", attemptId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            mods.Add(new CapturedMod(reader.GetString(0), reader.GetString(1)));
        return mods.ToArray();
    }

    private static string PatchBeatmapContext(string json, BeatmapDifficultyResult result)
    {
        JsonObject context = ParseObject(json);
        JsonObject stats = context["stats"] as JsonObject ?? [];
        SetCalculatedStars(stats, result);
        context["stats"] = stats;
        return context.ToJsonString();
    }

    private static void SetCalculatedStars(JsonObject stats, BeatmapDifficultyResult result)
    {
        JsonObject stars = stats["stars"] as JsonObject ?? [];
        stars["original"] = result.BaseStars;
        stars["total"] = result.AdjustedStars;
        stars["converted"] = result.AdjustedStars;
        stars[calculated_star_marker] = true;
        stats["stars"] = stars;
    }

    private static bool HasCalculatedStarMarker(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            JsonElement stats = root.TryGetProperty("stats", out JsonElement wrappedStats)
                ? wrappedStats
                : root;
            return stats.TryGetProperty("stars", out JsonElement stars)
                   && stars.TryGetProperty(calculated_star_marker, out JsonElement marker)
                   && marker.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static JsonObject ParseObject(string? json)
    {
        try
        {
            return JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json) as JsonObject ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private sealed record BpmStarBackfillCandidate(
        long AttemptId,
        long? BeatmapId,
        long? BeatmapSetId,
        string? Difficulty,
        string SourceJson,
        string BeatmapJson);
}
