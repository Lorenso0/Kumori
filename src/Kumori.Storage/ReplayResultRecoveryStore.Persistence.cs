using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Kumori.Storage;

public sealed partial class ReplayResultRecoveryStore
{
    private static bool IsReplayRecovery(string sourceJson)
    {
        try
        {
            using var document = JsonDocument.Parse(sourceJson);
            return document.RootElement.TryGetProperty("result_recovery", out var recovery)
                   && recovery.TryGetProperty("reason", out var reason)
                   && string.Equals(reason.GetString(), "tosu_gameplay_values_missing", StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool ReplayMustOwnAccuracy(CurrentResult current, ReplayResultData replay)
    {
        int currentCoreTotal = current.N300 + current.N100 + current.N50 + current.Misses;
        int replayCoreTotal = replay.N300 + replay.N100 + replay.N50 + replay.Misses;

        // tosu can briefly report 100% while omitting the entire final result.
        // Once a checksum-matched replay supplies those judgements, its header
        // is the authority for accuracy as well as the other missing fields.
        if (currentCoreTotal == 0 && replayCoreTotal > 0)
            return true;

        try
        {
            using var document = JsonDocument.Parse(current.SourceJson);
            return NeedsAccuracyAuthorityRepair(
                document.RootElement,
                current.Accuracy,
                current.N100,
                current.N50,
                current.Misses);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static bool NeedsAccuracyAuthorityRepair(JsonElement source)
    {
        if (!source.TryGetProperty("result_recovery", out var recovery)
            || recovery.ValueKind != JsonValueKind.Object)
            return false;

        if (recovery.TryGetProperty("accuracy_source", out var accuracySource)
            && accuracySource.ValueKind == JsonValueKind.String
            && string.Equals(accuracySource.GetString(), "replay_or_tosu", StringComparison.Ordinal))
            return false;

        if (!recovery.TryGetProperty("simulated_fields", out var fields)
            || fields.ValueKind != JsonValueKind.Array)
            return false;

        return fields.EnumerateArray().Any(field =>
            field.ValueKind == JsonValueKind.String
            && string.Equals(field.GetString(), "accuracy", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Also detects results produced by the previous recovery path, which kept
    /// a placeholder 100% after replay judgements had proved the play imperfect.
    /// </summary>
    public static bool NeedsAccuracyAuthorityRepair(
        JsonElement source,
        double accuracy,
        int n100,
        int n50,
        int misses)
    {
        if (NeedsAccuracyAuthorityRepair(source))
            return true;

        if (accuracy < 99.999999 || (n100 == 0 && n50 == 0 && misses == 0))
            return false;

        // A perfect value cannot coexist with an explicitly imperfect core
        // judgement. A checksum-matched replay is the safe authority regardless
        // of which older persistence path wrote the row.
        return true;
    }

    private static void ReplaceRecoveredJudgementEvents(
        SqliteConnection con,
        SqliteTransaction tx,
        long attemptId,
        IReadOnlyList<ReplaySimulationJudgement> judgements,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using (var delete = con.CreateCommand())
        {
            delete.Transaction = tx;
            delete.CommandText = "DELETE FROM attempt_events WHERE attempt_id=@id AND event_type IN ('miss', 'hit_50', 'hit_100', 'slider_break')";
            delete.Parameters.AddWithValue("@id", attemptId);
            delete.ExecuteNonQuery();
        }

        for (var index = 0; index < judgements.Count; index++)
        {
            if ((index & 127) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            var judgement = judgements[index];
            string? eventType = judgement.Kind switch
            {
                0 => "miss",
                1 => "hit_50",
                2 => "hit_100",
                3 => "slider_break",
                _ => null,
            };
            if (eventType is null) continue;

            double mapTime = judgement.Kind == 3 ? judgement.ObjectStartTime : judgement.RootStartTime;
            var data = new JsonObject
            {
                ["source"] = "replay_simulation",
                ["time_offset_ms"] = judgement.TimeOffset,
                ["result_time_ms"] = judgement.EventTime,
            };
            using var insert = con.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = """
                INSERT INTO attempt_events(attempt_id, captured_at, map_time_ms, event_type, value, data_json)
                VALUES(@id, @captured, @time, @type, 1, @data)
                """;
            insert.Parameters.AddWithValue("@id", attemptId);
            insert.Parameters.AddWithValue("@captured", DateTimeOffset.UtcNow.ToString("O"));
            insert.Parameters.AddWithValue("@time", Math.Max(0, (long)Math.Round(mapTime)));
            insert.Parameters.AddWithValue("@type", eventType);
            insert.Parameters.AddWithValue("@data", data.ToJsonString());
            insert.ExecuteNonQuery();
        }
    }

    private static CurrentResult? Read(
        SqliteConnection con,
        SqliteTransaction tx,
        long attemptId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var cmd = con.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT a.outcome, a.score, a.accuracy, a.grade, a.combo, a.n300, a.n100, a.n50,
                   a.misses, a.geki, a.katu, a.large_tick_hits, a.large_tick_misses,
                   a.small_tick_hits, a.small_tick_misses, a.slider_tail_hits,
                   a.slider_tail_misses, COALESCE(c.source_json, '{}')
            FROM attempts a
            LEFT JOIN attempt_context c ON c.attempt_id=a.id
            WHERE a.id=@id
            """;
        cmd.Parameters.AddWithValue("@id", attemptId);
        using var reader = cmd.ExecuteReader();
        cancellationToken.ThrowIfCancellationRequested();
        return reader.Read()
            ? new CurrentResult(
                reader.GetString(0), reader.GetInt64(1), reader.GetDouble(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetInt32(4),
                reader.GetInt32(5), reader.GetInt32(6), reader.GetInt32(7),
                reader.GetInt32(8), reader.GetInt32(9), reader.GetInt32(10),
                reader.GetInt32(11), reader.GetInt32(12), reader.GetInt32(13),
                reader.GetInt32(14), reader.GetInt32(15), reader.GetInt32(16),
                reader.GetString(17))
            : null;
    }

    private static void UpsertScoreContext(
        SqliteConnection con, SqliteTransaction tx, long attemptId, long score, string? grade,
        int n300, int n100, int n50, int misses, int geki, int katu,
        int largeTickHits, int largeTickMisses, int smallTickHits, int smallTickMisses,
        int sliderTailHits, int sliderTailMisses)
    {
        string existingJson = "{}";
        using (var read = con.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = "SELECT score_json FROM attempt_context WHERE attempt_id=@id";
            read.Parameters.AddWithValue("@id", attemptId);
            existingJson = read.ExecuteScalar() as string ?? "{}";
        }

        JsonObject root;
        try { root = JsonNode.Parse(existingJson) as JsonObject ?? []; }
        catch (JsonException) { root = []; }
        var hits = root["hits"] as JsonObject ?? [];
        root["score"] = score;
        root["grade"] = grade ?? "";
        hits["_300"] = n300;
        hits["_100"] = n100;
        hits["_50"] = n50;
        hits["_0"] = misses;
        hits["geki"] = geki;
        hits["katu"] = katu;
        hits["largeTickHits"] = largeTickHits;
        hits["largeTickMisses"] = largeTickMisses;
        hits["smallTickHits"] = smallTickHits;
        hits["smallTickMisses"] = smallTickMisses;
        hits["sliderTailHits"] = sliderTailHits;
        hits["sliderTailMisses"] = sliderTailMisses;
        root["hits"] = hits;
        root["recovered_from_replay"] = true;

        using var cmd = con.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO attempt_context(attempt_id, source_json, pp_json, beatmap_json,
                                        score_json, session_json, multiplayer_json)
            VALUES(@id, '{}', '{}', '{}', @score_json, '{}', '{}')
            ON CONFLICT(attempt_id) DO UPDATE SET score_json=excluded.score_json
            """;
        cmd.Parameters.AddWithValue("@id", attemptId);
        cmd.Parameters.AddWithValue("@score_json", root.ToJsonString());
        cmd.ExecuteNonQuery();
    }

    private static void RecordRecoverySource(
        SqliteConnection con, SqliteTransaction tx, long attemptId, string source, IReadOnlyList<string> fields)
    {
        string sourceJson = "{}";
        using (var read = con.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = "SELECT source_json FROM attempt_context WHERE attempt_id=@id";
            read.Parameters.AddWithValue("@id", attemptId);
            sourceJson = read.ExecuteScalar() as string ?? "{}";
        }

        JsonObject root;
        try { root = JsonNode.Parse(sourceJson) as JsonObject ?? []; }
        catch (JsonException) { root = []; }
        var recoveredFields = new JsonArray();
        foreach (var field in fields)
            recoveredFields.Add(field);
        var recovery = root["result_recovery"] as JsonObject ?? [];
        recovery["source"] = source;
        if (fields.Any(IsCoreResultField))
            recovery["reason"] = "tosu_gameplay_values_missing";
        recovery["recovered_at_utc"] = DateTimeOffset.UtcNow.ToString("O");
        recovery["fields"] = recoveredFields;
        if (fields.Contains("accuracy", StringComparer.OrdinalIgnoreCase))
            recovery["accuracy_source"] = "replay_or_tosu";
        root["result_recovery"] = recovery;

        using var update = con.CreateCommand();
        update.Transaction = tx;
        update.CommandText = "UPDATE attempt_context SET source_json=@json WHERE attempt_id=@id";
        update.Parameters.AddWithValue("@json", root.ToJsonString());
        update.Parameters.AddWithValue("@id", attemptId);
        update.ExecuteNonQuery();
    }

    private static bool IsCoreResultField(string field)
        => field is "score" or "accuracy" or "grade" or "combo"
            or "300" or "100" or "50" or "misses";

    private static void RebuildRecoveredPersonalBests(
        SqliteConnection con,
        SqliteTransaction tx,
        long attemptId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        long beatmapId;
        string modsKey;
        using (var group = con.CreateCommand())
        {
            group.Transaction = tx;
            group.CommandText = "SELECT beatmap_id, mods_key FROM attempts WHERE id=@id";
            group.Parameters.AddWithValue("@id", attemptId);
            using var reader = group.ExecuteReader();
            if (!reader.Read()) return;
            beatmapId = reader.GetInt64(0);
            modsKey = reader.GetString(1);
        }

        foreach (var (metric, column, order, aggregate, comparison) in new[]
        {
            ("score", "score", "DESC", "MAX", ">"),
            ("accuracy", "accuracy", "DESC", "MAX", ">"),
            ("pp", "pp", "DESC", "MAX", ">"),
            ("combo", "combo", "DESC", "MAX", ">"),
            ("fewest_misses", "misses", "ASC", "MIN", "<"),
        })
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (var delete = con.CreateCommand())
            {
                delete.Transaction = tx;
                delete.CommandText = "DELETE FROM personal_bests WHERE beatmap_id=@beatmap AND mods_key=@mods AND metric=@metric";
                delete.Parameters.AddWithValue("@beatmap", beatmapId);
                delete.Parameters.AddWithValue("@mods", modsKey);
                delete.Parameters.AddWithValue("@metric", metric);
                delete.ExecuteNonQuery();
            }

            using (var deleteImprovements = con.CreateCommand())
            {
                deleteImprovements.Transaction = tx;
                deleteImprovements.CommandText = """
                    DELETE FROM attempt_improvements
                    WHERE metric=@metric
                      AND attempt_id IN (
                          SELECT id FROM attempts
                          WHERE beatmap_id=@beatmap AND mods_key=@mods
                      )
                    """;
                deleteImprovements.Parameters.AddWithValue("@beatmap", beatmapId);
                deleteImprovements.Parameters.AddWithValue("@mods", modsKey);
                deleteImprovements.Parameters.AddWithValue("@metric", metric);
                deleteImprovements.ExecuteNonQuery();
            }

            using (var improvements = con.CreateCommand())
            {
                improvements.Transaction = tx;
                // column/aggregate/comparison are fixed constants above, never external input.
                improvements.CommandText = $"""
                    WITH candidates AS (
                        SELECT a.id,
                               a.{column} AS new_value,
                               (
                                   SELECT {aggregate}(previous.{column})
                                   FROM attempts previous
                                   WHERE previous.beatmap_id=@beatmap
                                     AND previous.mods_key=@mods
                                     AND previous.outcome IN ('completed', 'failed')
                                     AND (previous.score > 0 OR previous.n300 + previous.n100 + previous.n50 + previous.misses > 0)
                                     AND previous.id < a.id
                               ) AS previous_value
                        FROM attempts a
                        WHERE a.beatmap_id=@beatmap AND a.mods_key=@mods
                          AND a.outcome IN ('completed', 'failed')
                          AND (a.score > 0 OR a.n300 + a.n100 + a.n50 + a.misses > 0)
                    )
                    INSERT INTO attempt_improvements(attempt_id, metric, previous_value, new_value, delta)
                    SELECT id, @metric, previous_value, new_value,
                           CASE WHEN previous_value IS NULL THEN NULL ELSE new_value - previous_value END
                    FROM candidates
                    WHERE previous_value IS NULL OR new_value {comparison} previous_value
                    """;
                improvements.Parameters.AddWithValue("@beatmap", beatmapId);
                improvements.Parameters.AddWithValue("@mods", modsKey);
                improvements.Parameters.AddWithValue("@metric", metric);
                improvements.ExecuteNonQuery();
            }

            using var best = con.CreateCommand();
            best.Transaction = tx;
            // column/order are fixed constants above, never external input.
            best.CommandText = $"""
                INSERT INTO personal_bests(beatmap_id, mods_key, metric, attempt_id, value)
                SELECT @beatmap, @mods, @metric, id, {column}
                FROM attempts
                WHERE beatmap_id=@beatmap AND mods_key=@mods
                  AND outcome IN ('completed', 'failed')
                  AND (score > 0 OR n300 + n100 + n50 + misses > 0)
                ORDER BY {column} {order}, id ASC
                LIMIT 1
                """;
            best.Parameters.AddWithValue("@beatmap", beatmapId);
            best.Parameters.AddWithValue("@mods", modsKey);
            best.Parameters.AddWithValue("@metric", metric);
            best.ExecuteNonQuery();
        }
    }

    private static void UpsertSimulationTiming(
        SqliteConnection con,
        SqliteTransaction tx,
        long attemptId,
        IReadOnlyList<double> offsets,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var values = new double[offsets.Count];
        for (var index = 0; index < offsets.Count; index++)
        {
            if ((index & 1023) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            values[index] = offsets[index];
        }
        var sorted = (double[])values.Clone();
        Array.Sort(sorted);
        cancellationToken.ThrowIfCancellationRequested();
        double mean = values.Average();
        double median = sorted.Length % 2 == 1
            ? sorted[sorted.Length / 2]
            : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
        double deviation = Math.Sqrt(values.Average(value => Math.Pow(value - mean, 2)));
        using var cmd = con.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO attempt_timing(attempt_id, offsets_zlib, hit_count, early_count,
                                       late_count, mean, median, deviation)
            VALUES(@id, @offsets, @count, @early, @late, @mean, @median, @deviation)
            ON CONFLICT(attempt_id) DO UPDATE SET
                offsets_zlib=excluded.offsets_zlib, hit_count=excluded.hit_count,
                early_count=excluded.early_count, late_count=excluded.late_count,
                mean=excluded.mean, median=excluded.median, deviation=excluded.deviation
            """;
        cmd.Parameters.AddWithValue("@id", attemptId);
        cmd.Parameters.Add("@offsets", SqliteType.Blob).Value = BlobCodec.EncodeOffsets(values);
        cmd.Parameters.AddWithValue("@count", values.Length);
        cmd.Parameters.AddWithValue("@early", values.Count(value => value < 0));
        cmd.Parameters.AddWithValue("@late", values.Count(value => value > 0));
        cmd.Parameters.AddWithValue("@mean", mean);
        cmd.Parameters.AddWithValue("@median", median);
        cmd.Parameters.AddWithValue("@deviation", deviation);
        cmd.ExecuteNonQuery();
    }

    private static void UpsertSimulationContext(
        SqliteConnection con,
        SqliteTransaction tx,
        long attemptId,
        ReplaySimulationResult simulation)
    {
        string ppJson = "{}";
        string beatmapJson = "{}";
        using (var read = con.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = "SELECT pp_json, beatmap_json FROM attempt_context WHERE attempt_id=@id";
            read.Parameters.AddWithValue("@id", attemptId);
            using var reader = read.ExecuteReader();
            if (reader.Read())
            {
                ppJson = reader.IsDBNull(0) ? "{}" : reader.GetString(0);
                beatmapJson = reader.IsDBNull(1) ? "{}" : reader.GetString(1);
            }
        }

        JsonObject pp = ParseObject(ppJson);
        SetNumberIfMissing(pp, "pp", simulation.Pp);
        SetNumberIfMissing(pp, "fc_pp", simulation.FcPp);
        SetNumberIfMissing(pp, "max_pp", simulation.MaxPp);
        pp["recovered_from_replay_simulation"] = true;

        JsonObject beatmap = ParseObject(beatmapJson);
        JsonObject stats = beatmap["stats"] as JsonObject ?? [];
        JsonObject stars = stats["stars"] as JsonObject ?? [];
        SetNumberIfMissing(stars, "original", simulation.BaseStars);
        SetNumberIfMissing(stars, "total", simulation.AdjustedStars);
        SetNumberIfMissing(stars, "converted", simulation.AdjustedStars);
        stats["stars"] = stars;
        SetPair(stats, "ar", simulation.ApproachRate, simulation.AdjustedApproachRate);
        SetPair(stats, "cs", simulation.CircleSize, simulation.AdjustedCircleSize);
        SetPair(stats, "od", simulation.OverallDifficulty, simulation.AdjustedOverallDifficulty);
        SetPair(stats, "hp", simulation.DrainRate, simulation.AdjustedDrainRate);
        JsonObject bpm = stats["bpm"] as JsonObject ?? [];
        SetNumberIfMissing(bpm, "common", simulation.Bpm);
        SetNumberIfMissing(bpm, "realtime", simulation.AdjustedBpm);
        stats["bpm"] = bpm;
        SetNumberIfMissing(stats, "clockRate", simulation.ClockRate);
        SetNumberIfMissing(stats, "maxCombo", simulation.MaxCombo);
        JsonObject objects = stats["objects"] as JsonObject ?? [];
        SetNumberIfMissing(objects, "circles", simulation.CircleCount);
        SetNumberIfMissing(objects, "sliders", simulation.SliderCount);
        SetNumberIfMissing(objects, "spinners", simulation.SpinnerCount);
        stats["objects"] = objects;
        beatmap["stats"] = stats;
        beatmap["recovered_from_replay_simulation"] = true;

        using var update = con.CreateCommand();
        update.Transaction = tx;
        update.CommandText = """
            INSERT INTO attempt_context(attempt_id, source_json, pp_json, beatmap_json,
                                        score_json, session_json, multiplayer_json)
            VALUES(@id, '{}', @pp, @beatmap, '{}', '{}', '{}')
            ON CONFLICT(attempt_id) DO UPDATE SET
                pp_json=excluded.pp_json,
                beatmap_json=excluded.beatmap_json
            """;
        update.Parameters.AddWithValue("@id", attemptId);
        update.Parameters.AddWithValue("@pp", pp.ToJsonString());
        update.Parameters.AddWithValue("@beatmap", beatmap.ToJsonString());
        update.ExecuteNonQuery();

        static JsonObject ParseObject(string json)
        {
            try { return JsonNode.Parse(json) as JsonObject ?? []; }
            catch (JsonException) { return []; }
        }

        static void SetPair(JsonObject stats, string name, double original, double converted)
        {
            JsonObject pair = stats[name] as JsonObject ?? [];
            SetNumberIfMissing(pair, "original", original, allowZero: true);
            SetNumberIfMissing(pair, "converted", converted, allowZero: true);
            stats[name] = pair;
        }

        static void SetNumberIfMissing(JsonObject parent, string name, double value, bool allowZero = false)
        {
            if ((!allowZero && value <= 0) || value < 0 || HasPositiveNumber(parent[name])) return;
            parent[name] = value;
        }

        static bool HasPositiveNumber(JsonNode? node)
            => node is JsonValue value
               && value.TryGetValue<double>(out double number)
               && number > 0.000001;
    }

    private static void RecordSimulation(
        SqliteConnection con,
        SqliteTransaction tx,
        long attemptId,
        IReadOnlyList<string> fields,
        bool tosuResultWasMissing)
    {
        string sourceJson = "{}";
        using (var read = con.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = "SELECT source_json FROM attempt_context WHERE attempt_id=@id";
            read.Parameters.AddWithValue("@id", attemptId);
            sourceJson = read.ExecuteScalar() as string ?? "{}";
        }
        JsonObject root;
        try { root = JsonNode.Parse(sourceJson) as JsonObject ?? []; }
        catch (JsonException) { root = []; }
        var recovery = root["result_recovery"] as JsonObject ?? [];
        var simulatedFields = new JsonArray();
        foreach (string field in fields) simulatedFields.Add(field);
        recovery["simulation"] = "completed";
        recovery["simulation_schema"] = CurrentSimulationSchema;
        recovery["simulation_completed_at_utc"] = DateTimeOffset.UtcNow.ToString("O");
        recovery["simulated_fields"] = simulatedFields;
        if (tosuResultWasMissing)
            recovery["reason"] = "tosu_gameplay_values_missing";
        root["result_recovery"] = recovery;
        using var update = con.CreateCommand();
        update.Transaction = tx;
        update.CommandText = "UPDATE attempt_context SET source_json=@json WHERE attempt_id=@id";
        update.Parameters.AddWithValue("@json", root.ToJsonString());
        update.Parameters.AddWithValue("@id", attemptId);
        update.ExecuteNonQuery();
    }

    private sealed record CurrentResult(
        string Outcome, long Score, double Accuracy, string? Grade, int Combo,
        int N300, int N100, int N50, int Misses, int Geki, int Katu,
        int LargeTickHits, int LargeTickMisses, int SmallTickHits, int SmallTickMisses,
        int SliderTailHits, int SliderTailMisses, string SourceJson);
}

