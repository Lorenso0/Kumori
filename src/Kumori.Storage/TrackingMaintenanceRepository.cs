using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kumori.Core;
using Microsoft.Data.Sqlite;

namespace Kumori.Storage;

public sealed class TrackingMaintenanceRepository
{
    internal const int MaxRawSnapshotCompressedBytes = 4 * 1024 * 1024;
    internal const int MaxRawSnapshotDecompressedBytes = 16 * 1024 * 1024;
    internal const int MaxRawSnapshotMods = 64;

    private readonly SqliteConnectionFactory _factory;

    public TrackingMaintenanceRepository(SqliteConnectionFactory factory)
    {
        _factory = factory;
    }

    public int EndOpenSessions()
    {
        if (!_factory.DatabaseExists)
        {
            return 0;
        }

        using var con = OpenWriteConnection();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            UPDATE sessions
            SET ended_at = @ended_at, ended_at_utc_ms = @ended_at_utc_ms, interrupted = 0
            WHERE ended_at IS NULL
            """;
        var endedAt = DateTimeOffset.UtcNow;
        cmd.Parameters.AddWithValue("@ended_at", endedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@ended_at_utc_ms", endedAt.ToUnixTimeMilliseconds());
        return cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Finalizes tracking rows left open by a previous interrupted app process.
    /// This must run during startup before the live tracking runtime is created.
    /// </summary>
    public (int Attempts, int Sessions) RecoverInterruptedTracking()
    {
        if (!_factory.DatabaseExists)
        {
            return (0, 0);
        }

        using var con = OpenWriteConnection();
        using var tx = con.BeginTransaction();

        using var attempts = con.CreateCommand();
        attempts.Transaction = tx;
        attempts.CommandText = """
            UPDATE attempts
            SET ended_at = COALESCE(ended_at, started_at),
                ended_at_utc_ms = COALESCE(ended_at_utc_ms, started_at_utc_ms),
                outcome = 'abandoned',
                termination_evidence = CASE
                    WHEN termination_evidence IS NULL OR termination_evidence = ''
                        THEN 'startup_recovery'
                    ELSE termination_evidence || ':startup_recovery'
                END
            WHERE outcome = 'active'
            """;
        var recoveredAttempts = attempts.ExecuteNonQuery();

        using var sessions = con.CreateCommand();
        sessions.Transaction = tx;
        sessions.CommandText = """
            UPDATE sessions
            SET ended_at = COALESCE(
                    (SELECT MAX(COALESCE(a.ended_at, a.started_at))
                     FROM attempts a
                     WHERE a.session_id = sessions.id),
                    started_at),
                ended_at_utc_ms = COALESCE(
                    (SELECT MAX(COALESCE(a.ended_at_utc_ms, a.started_at_utc_ms))
                     FROM attempts a
                     WHERE a.session_id = sessions.id),
                    started_at_utc_ms),
                interrupted = 1
            WHERE ended_at IS NULL
            """;
        var recoveredSessions = sessions.ExecuteNonQuery();

        tx.Commit();
        return (recoveredAttempts, recoveredSessions);
    }

    public int RepairMissingTosuResults()
    {
        if (!_factory.DatabaseExists)
        {
            return 0;
        }

        using var con = OpenWriteConnection();
        using var tx = con.BeginTransaction();
        int restoredFromCheckpoints = RestoreMissingResultsFromCheckpoints(con, tx);
        using var repair = con.CreateCommand();
        repair.Transaction = tx;
        repair.CommandText = """
            UPDATE attempts
            SET accuracy = 0,
                grade = NULL,
                termination_evidence = CASE
                    WHEN instr(COALESCE(termination_evidence, ''), 'tosu_result_missing') > 0
                        THEN termination_evidence
                    WHEN termination_evidence IS NULL OR termination_evidence = ''
                        THEN 'tosu_result_missing'
                    ELSE termination_evidence || ':tosu_result_missing'
                END
            WHERE outcome <> 'active'
              AND score = 0
              AND n300 + n100 + n50 + misses = 0
              AND EXISTS (
                  SELECT 1 FROM attempt_timing t
                  WHERE t.attempt_id = attempts.id AND t.hit_count > 0
                  UNION ALL
                  SELECT 1 FROM attempt_events e
                  WHERE e.attempt_id = attempts.id
                    AND e.event_type = 'checkpoint'
              )
              AND (
                  ABS(accuracy) > 0.000001
                  OR grade IS NOT NULL
                  OR instr(COALESCE(termination_evidence, ''), 'tosu_result_missing') = 0
              )
            """;
        var neutralized = repair.ExecuteNonQuery();
        int repaired = restoredFromCheckpoints + neutralized;
        if (repaired > 0)
        {
            RebuildPersonalBests(con, tx);
        }
        tx.Commit();
        return repaired;
    }

    private static int RestoreMissingResultsFromCheckpoints(
        SqliteConnection con,
        SqliteTransaction tx)
    {
        var candidates = new List<(long AttemptId, string SourceJson, string ScoreJson)>();
        using (var read = con.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = """
                SELECT a.id, COALESCE(c.source_json, '{}'), COALESCE(c.score_json, '{}')
                FROM attempts a
                LEFT JOIN attempt_context c ON c.attempt_id=a.id
                WHERE a.outcome <> 'active'
                  AND a.score = 0
                  AND a.n300 + a.n100 + a.n50 + a.misses = 0
                  AND EXISTS (
                      SELECT 1 FROM attempt_events e
                      WHERE e.attempt_id=a.id AND e.event_type='checkpoint'
                  )
                """;
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                candidates.Add((
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2)));
            }
        }

        int restored = 0;
        foreach ((long attemptId, string sourceJson, string scoreJson) in candidates)
        {
            List<RecoveryCheckpoint> checkpoints = ReadRecoveryCheckpoints(con, tx, attemptId);
            RecoveryCheckpoint? final = checkpoints.LastOrDefault(checkpoint => checkpoint.CoreTotal > 0);
            if (final is null)
                continue;

            using (var update = con.CreateCommand())
            {
                update.Transaction = tx;
                update.CommandText = """
                    UPDATE attempts
                    SET outcome=CASE
                            WHEN outcome='completed' AND @progress < 0.98 THEN 'quit'
                            ELSE outcome
                        END,
                        grade=CASE
                            WHEN outcome='completed' AND @progress < 0.98 THEN NULL
                            ELSE grade
                        END,
                        n300=@n300, n100=@n100, n50=@n50, misses=@misses,
                        accuracy=@accuracy, combo=@combo, pp=@pp,
                        progress=@progress, unstable_rate=@unstable_rate,
                        termination_evidence=CASE
                            WHEN instr(COALESCE(termination_evidence, ''), 'tosu_result_missing') > 0
                                THEN termination_evidence
                            WHEN termination_evidence IS NULL OR termination_evidence = ''
                                THEN 'tosu_result_missing'
                            ELSE termination_evidence || ':tosu_result_missing'
                        END
                    WHERE id=@id AND score=0 AND n300+n100+n50+misses=0
                    """;
                update.Parameters.AddWithValue("@n300", final.N300);
                update.Parameters.AddWithValue("@n100", final.N100);
                update.Parameters.AddWithValue("@n50", final.N50);
                update.Parameters.AddWithValue("@misses", final.Misses);
                update.Parameters.AddWithValue("@accuracy", final.Accuracy);
                update.Parameters.AddWithValue("@combo", final.Combo);
                update.Parameters.AddWithValue("@pp", final.Pp);
                update.Parameters.AddWithValue("@progress", final.Progress);
                update.Parameters.AddWithValue("@unstable_rate", final.UnstableRate);
                update.Parameters.AddWithValue("@id", attemptId);
                if (update.ExecuteNonQuery() == 0)
                    continue;
            }

            string repairedSourceJson = MarkMissingResultCheckpointRepair(sourceJson);
            string repairedScoreJson = RestoreScoreJsonCore(scoreJson, final);
            using (var context = con.CreateCommand())
            {
                context.Transaction = tx;
                context.CommandText = """
                    INSERT INTO attempt_context(
                        attempt_id, source_json, pp_json, beatmap_json,
                        score_json, session_json, multiplayer_json)
                    VALUES(@id, @source, '{}', '{}', @score, '{}', '{}')
                    ON CONFLICT(attempt_id) DO UPDATE SET
                        source_json=excluded.source_json,
                        score_json=excluded.score_json
                    """;
                context.Parameters.AddWithValue("@id", attemptId);
                context.Parameters.AddWithValue("@source", repairedSourceJson);
                context.Parameters.AddWithValue("@score", repairedScoreJson);
                context.ExecuteNonQuery();
            }

            RebuildCheckpointJudgementEvents(con, tx, attemptId, checkpoints);
            restored++;
        }
        return restored;
    }

    /// <summary>
    /// Repairs partial plays whose valid tosu hit totals were overwritten by
    /// the short-lived partial-simulation authority regression. The periodic
    /// checkpoint stream is retained independently and is the authority used
    /// to restore both totals and judgement events.
    /// </summary>
    public int RepairPartialSimulationCoreResults()
    {
        if (!_factory.DatabaseExists)
            return 0;

        using var con = OpenWriteConnection();
        using var tx = con.BeginTransaction();
        var candidates = new List<(long AttemptId, string SourceJson, string ScoreJson)>();
        using (var read = con.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = """
                SELECT a.id, c.source_json, c.score_json
                FROM attempts a
                JOIN attempt_context c ON c.attempt_id=a.id
                WHERE a.outcome IN ('failed', 'retried', 'quit', 'abandoned')
                  AND EXISTS (
                      SELECT 1 FROM attempt_events e
                      WHERE e.attempt_id=a.id AND e.event_type='checkpoint'
                  )
                """;
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                string sourceJson = reader.IsDBNull(1) ? "{}" : reader.GetString(1);
                if (!WasCoreOverwrittenByNormalPartialSimulation(sourceJson))
                    continue;
                candidates.Add((
                    reader.GetInt64(0),
                    sourceJson,
                    reader.IsDBNull(2) ? "{}" : reader.GetString(2)));
            }
        }

        int repaired = 0;
        foreach (var candidate in candidates)
        {
            List<RecoveryCheckpoint> checkpoints = ReadRecoveryCheckpoints(con, tx, candidate.AttemptId);
            if (checkpoints.Count == 0 || checkpoints[^1].CoreTotal <= 0)
                continue;
            RecoveryCheckpoint final = checkpoints[^1];

            using (var update = con.CreateCommand())
            {
                update.Transaction = tx;
                update.CommandText = """
                    UPDATE attempts
                    SET n300=@n300, n100=@n100, n50=@n50, misses=@misses
                    WHERE id=@id AND outcome IN ('failed', 'retried', 'quit', 'abandoned')
                    """;
                update.Parameters.AddWithValue("@n300", final.N300);
                update.Parameters.AddWithValue("@n100", final.N100);
                update.Parameters.AddWithValue("@n50", final.N50);
                update.Parameters.AddWithValue("@misses", final.Misses);
                update.Parameters.AddWithValue("@id", candidate.AttemptId);
                if (update.ExecuteNonQuery() == 0)
                    continue;
            }

            string sourceJson = MarkPartialCoreCheckpointRepair(candidate.SourceJson);
            string scoreJson = RestoreScoreJsonCore(candidate.ScoreJson, final);
            using (var context = con.CreateCommand())
            {
                context.Transaction = tx;
                context.CommandText = """
                    UPDATE attempt_context
                    SET source_json=@source, score_json=@score
                    WHERE attempt_id=@id
                    """;
                context.Parameters.AddWithValue("@source", sourceJson);
                context.Parameters.AddWithValue("@score", scoreJson);
                context.Parameters.AddWithValue("@id", candidate.AttemptId);
                context.ExecuteNonQuery();
            }

            RebuildCheckpointJudgementEvents(con, tx, candidate.AttemptId, checkpoints);
            repaired++;
        }

        tx.Commit();
        return repaired;
    }

    private static bool WasCoreOverwrittenByNormalPartialSimulation(string sourceJson)
    {
        try
        {
            using var document = JsonDocument.Parse(sourceJson);
            if (!document.RootElement.TryGetProperty("result_recovery", out var recovery)
                || recovery.ValueKind != JsonValueKind.Object
                || recovery.TryGetProperty("reason", out _)
                || !recovery.TryGetProperty("simulated_fields", out var fields)
                || fields.ValueKind != JsonValueKind.Array)
                return false;
            return fields.EnumerateArray().Any(field =>
                field.ValueKind == JsonValueKind.String && IsCoreSimulationField(field.GetString()));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsCoreSimulationField(string? field)
        => field is "300" or "100" or "50" or "misses";

    private static List<RecoveryCheckpoint> ReadRecoveryCheckpoints(
        SqliteConnection con,
        SqliteTransaction tx,
        long attemptId)
    {
        using var read = con.CreateCommand();
        read.Transaction = tx;
        read.CommandText = """
            SELECT captured_at, map_time_ms, data_json
            FROM attempt_events
            WHERE attempt_id=@id AND event_type='checkpoint'
            ORDER BY id
            """;
        read.Parameters.AddWithValue("@id", attemptId);
        using var reader = read.ExecuteReader();
        var checkpoints = new List<RecoveryCheckpoint>();
        while (reader.Read())
        {
            try
            {
                using var document = JsonDocument.Parse(reader.GetString(2));
                JsonElement root = document.RootElement;
                checkpoints.Add(new RecoveryCheckpoint(
                    reader.GetString(0),
                    reader.IsDBNull(1) ? 0 : reader.GetInt64(1),
                    Count(root, "n300"),
                    Count(root, "n100"),
                    Count(root, "n50"),
                    Count(root, "misses"),
                    Count(root, "slider_breaks"),
                    Number(root, "accuracy"),
                    Count(root, "combo"),
                    Number(root, "pp"),
                    Math.Clamp(Number(root, "progress"), 0, 1),
                    Number(root, "unstable_rate")));
            }
            catch (JsonException)
            {
                // A malformed checkpoint cannot be used as recovery evidence.
            }
        }
        return checkpoints;

        static int Count(JsonElement root, string name)
            => TryReadNumber(root, name, out double number)
                ? Math.Max(0, (int)Math.Round(number))
                : 0;

        static double Number(JsonElement root, string name)
            => TryReadNumber(root, name, out double number)
                ? Math.Max(0, number)
                : 0;

        static bool TryReadNumber(JsonElement root, string name, out double number)
        {
            number = 0;
            if (!root.TryGetProperty(name, out JsonElement value))
                return false;

            return value.ValueKind switch
            {
                JsonValueKind.Number => value.TryGetDouble(out number),
                JsonValueKind.String => double.TryParse(
                    value.GetString(),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out number),
                _ => false,
            };
        }
    }

    private static string RestoreScoreJsonCore(string scoreJson, RecoveryCheckpoint checkpoint)
    {
        JsonObject root;
        try { root = JsonNode.Parse(scoreJson) as JsonObject ?? []; }
        catch (JsonException) { root = []; }
        var hits = root["hits"] as JsonObject ?? [];
        hits["_300"] = checkpoint.N300;
        hits["_100"] = checkpoint.N100;
        hits["_50"] = checkpoint.N50;
        hits["_0"] = checkpoint.Misses;
        root["hits"] = hits;
        root.Remove("recovered_from_replay");
        return root.ToJsonString();
    }

    private static string MarkPartialCoreCheckpointRepair(string sourceJson)
    {
        JsonObject root;
        try { root = JsonNode.Parse(sourceJson) as JsonObject ?? []; }
        catch (JsonException) { root = []; }
        var recovery = root["result_recovery"] as JsonObject ?? [];
        if (recovery["simulated_fields"] is JsonArray fields)
        {
            var retained = new JsonArray();
            foreach (JsonNode? field in fields)
            {
                string? name = field?.GetValue<string>();
                if (!IsCoreSimulationField(name))
                    retained.Add(name);
            }
            recovery["simulated_fields"] = retained;
        }
        recovery["core_result_source"] = "tosu_checkpoint";
        recovery["core_result_repaired_at_utc"] = DateTimeOffset.UtcNow.ToString("O");
        root["result_recovery"] = recovery;
        return root.ToJsonString();
    }

    private static string MarkMissingResultCheckpointRepair(string sourceJson)
    {
        JsonObject root;
        try { root = JsonNode.Parse(sourceJson) as JsonObject ?? []; }
        catch (JsonException) { root = []; }
        var recovery = root["result_recovery"] as JsonObject ?? [];
        recovery["reason"] = "tosu_gameplay_values_missing";
        recovery["core_result_source"] = "tosu_checkpoint";
        recovery["checkpoint_repaired_at_utc"] = DateTimeOffset.UtcNow.ToString("O");
        root["result_recovery"] = recovery;
        return root.ToJsonString();
    }

    private static void RebuildCheckpointJudgementEvents(
        SqliteConnection con,
        SqliteTransaction tx,
        long attemptId,
        IReadOnlyList<RecoveryCheckpoint> checkpoints)
    {
        using (var delete = con.CreateCommand())
        {
            delete.Transaction = tx;
            delete.CommandText = """
                DELETE FROM attempt_events
                WHERE attempt_id=@id
                  AND event_type IN ('miss', 'hit_50', 'hit_100', 'slider_break')
                """;
            delete.Parameters.AddWithValue("@id", attemptId);
            delete.ExecuteNonQuery();
        }

        if (checkpoints.Count < 2)
            return;
        RecoveryCheckpoint previous = checkpoints[0];
        for (int index = 1; index < checkpoints.Count; index++)
        {
            RecoveryCheckpoint current = checkpoints[index];
            AddCumulative("hit_100", current.N100, previous.N100);
            AddCumulative("hit_50", current.N50, previous.N50);
            AddPerIncrement("miss", current.Misses, previous.Misses);
            AddPerIncrement("slider_break", current.SliderBreaks, previous.SliderBreaks);
            previous = current;

            void AddCumulative(string eventType, int value, int prior)
            {
                if (value <= prior) return;
                Insert(eventType, value, JsonSerializer.Serialize(new { delta = value - prior }));
            }

            void AddPerIncrement(string eventType, int value, int prior)
            {
                for (int count = 0; count < value - prior; count++)
                    Insert(eventType, prior + count + 1, "{}");
            }

            void Insert(string eventType, int value, string dataJson)
            {
                using var insert = con.CreateCommand();
                insert.Transaction = tx;
                insert.CommandText = """
                    INSERT INTO attempt_events(
                        attempt_id, captured_at, map_time_ms, event_type, value, data_json)
                    VALUES(@id, @captured, @time, @type, @value, @data)
                    """;
                insert.Parameters.AddWithValue("@id", attemptId);
                insert.Parameters.AddWithValue("@captured", current.CapturedAt);
                insert.Parameters.AddWithValue("@time", current.MapTimeMs);
                insert.Parameters.AddWithValue("@type", eventType);
                insert.Parameters.AddWithValue("@value", value);
                insert.Parameters.AddWithValue("@data", dataJson);
                insert.ExecuteNonQuery();
            }
        }
    }

    private sealed record RecoveryCheckpoint(
        string CapturedAt,
        long MapTimeMs,
        int N300,
        int N100,
        int N50,
        int Misses,
        int SliderBreaks,
        double Accuracy,
        int Combo,
        double Pp,
        double Progress,
        double UnstableRate)
    {
        public int CoreTotal => N300 + N100 + N50 + Misses;
    }

    public int DeleteAttempt(long attemptId)
    {
        using var con = OpenWriteConnection();
        EnsureNoActiveTracking(con);
        if (ScalarCount(con, null, """
                SELECT COUNT(*)
                FROM attempts a
                JOIN sessions s ON s.id=a.session_id
                WHERE a.id=@id AND s.ended_at IS NULL
                """, ("@id", attemptId)) > 0)
            throw new InvalidOperationException("An attempt in the active session cannot be deleted.");
        using var tx = con.BeginTransaction();
        using var cmd = con.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM attempts WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", attemptId);
        var deleted = cmd.ExecuteNonQuery();
        RebuildPersonalBests(con, tx);
        PruneUnusedBeatmaps(con, tx);
        tx.Commit();
        return deleted;
    }

    public int DeleteSession(long sessionId)
    {
        using var con = OpenWriteConnection();
        EnsureNoActiveTracking(con);
        if (ScalarCount(con, null,
                "SELECT COUNT(*) FROM sessions WHERE id=@id AND ended_at IS NULL",
                ("@id", sessionId)) > 0)
            throw new InvalidOperationException("The active session cannot be deleted.");
        using var tx = con.BeginTransaction();
        using var cmd = con.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM sessions WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", sessionId);
        var deleted = cmd.ExecuteNonQuery();
        RebuildPersonalBests(con, tx);
        PruneUnusedBeatmaps(con, tx);
        tx.Commit();
        return deleted;
    }

    public int DeleteBefore(string isoTimestamp)
    {
        using var con = OpenWriteConnection();
        EnsureNoActiveTracking(con);
        using var tx = con.BeginTransaction();
        using var cmd = con.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM sessions WHERE ended_at IS NOT NULL AND started_at < @before";
        cmd.Parameters.AddWithValue("@before", isoTimestamp);
        var deleted = cmd.ExecuteNonQuery();
        DeleteIfExists(con, tx, "profile_snapshots", "captured_at < @before", ("@before", isoTimestamp));
        RebuildPersonalBests(con, tx);
        PruneUnusedBeatmaps(con, tx);
        tx.Commit();
        return deleted;
    }

    /// <summary>
    /// Deletes individual plays and account snapshots older than a local calendar cutoff,
    /// then removes ended sessions that no longer contain any plays.
    /// </summary>
    public (int Attempts, int Sessions) DeleteTrackingBefore(string isoTimestamp)
    {
        if (!_factory.DatabaseExists)
        {
            return (0, 0);
        }

        using var con = OpenWriteConnection();
        EnsureNoActiveTracking(con);
        using var tx = con.BeginTransaction();
        using var attempts = con.CreateCommand();
        attempts.Transaction = tx;
        attempts.CommandText = "DELETE FROM attempts WHERE started_at < @before";
        attempts.Parameters.AddWithValue("@before", isoTimestamp);
        var deletedAttempts = attempts.ExecuteNonQuery();
        DeleteIfExists(con, tx, "profile_snapshots", "captured_at < @before", ("@before", isoTimestamp));

        using var sessions = con.CreateCommand();
        sessions.Transaction = tx;
        sessions.CommandText = """
            DELETE FROM sessions
            WHERE ended_at IS NOT NULL
              AND NOT EXISTS (SELECT 1 FROM attempts WHERE attempts.session_id = sessions.id)
            """;
        var deletedSessions = sessions.ExecuteNonQuery();
        RebuildPersonalBests(con, tx);
        PruneUnusedBeatmaps(con, tx);
        tx.Commit();
        return (deletedAttempts, deletedSessions);
    }

    public int DeleteAll()
    {
        using var con = OpenWriteConnection();
        EnsureNoActiveTracking(con);
        using var tx = con.BeginTransaction();
        var deleted = DeleteAllFrom(con, tx, "sessions");
        DeleteAllFrom(con, tx, "profile_snapshots");
        DeleteAllFrom(con, tx, "attempts");
        DeleteAllFrom(con, tx, "personal_bests");
        DeleteAllFrom(con, tx, "attempt_improvements");
        DeleteAllFrom(con, tx, "beatmaps");
        tx.Commit();
        return deleted;
    }

    public void ClearBeatmapCache()
    {
        var path = AppPaths.BeatmapMediaDir;
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
        Directory.CreateDirectory(path);
    }

    public (int InvalidAttempts, int EmptySessions, int ReclassifiedCompleted) CleanupInvalidAttempts(double minimumSeconds = 3.0)
    {
        using var con = OpenWriteConnection();
        EnsureNoActiveTracking(con);
        using var tx = con.BeginTransaction();
        var reclassified = ScalarCount(con, tx, """
            SELECT COUNT(*) FROM attempts
            WHERE outcome IN ('retried','quit','abandoned')
              AND progress >= 0.99
              AND (score > 0 OR n300 > 0 OR n100 > 0 OR n50 > 0 OR misses > 0)
            """);
        Execute(con, tx, """
            UPDATE attempts
            SET outcome='completed',
                termination_evidence=COALESCE(termination_evidence,'') || ':complete_progress',
                progress=1
            WHERE outcome IN ('retried','quit','abandoned')
              AND progress >= 0.99
              AND (score > 0 OR n300 > 0 OR n100 > 0 OR n50 > 0 OR misses > 0)
            """);
        var invalid = ScalarCount(con, tx, """
            SELECT COUNT(*) FROM attempts
            WHERE outcome != 'active'
              AND (
                duration_seconds < @minimum
                OR (score=0 AND n300=0 AND n100=0 AND n50=0 AND misses=0)
              )
            """, ("@minimum", minimumSeconds));
        Execute(con, tx, """
            DELETE FROM attempts
            WHERE outcome != 'active'
              AND (
                duration_seconds < @minimum
                OR (score=0 AND n300=0 AND n100=0 AND n50=0 AND misses=0)
              )
            """, ("@minimum", minimumSeconds));
        var emptySessions = ScalarCount(con, tx, """
            SELECT COUNT(*) FROM sessions s
            WHERE s.ended_at IS NOT NULL
              AND NOT EXISTS (SELECT 1 FROM attempts a WHERE a.session_id=s.id)
            """);
        Execute(con, tx, """
            DELETE FROM sessions
            WHERE ended_at IS NOT NULL
              AND NOT EXISTS (SELECT 1 FROM attempts a WHERE a.session_id=sessions.id)
            """);
        RebuildPersonalBests(con, tx);
        PruneUnusedBeatmaps(con, tx);
        tx.Commit();
        return (invalid, emptySessions, reclassified);
    }

    public int PreviewAttemptsShorterThan(int seconds)
    {
        if (!_factory.DatabaseExists)
            return 0;
        seconds = Math.Clamp(seconds, 1, 300);
        using var con = _factory.Open();
        return ScalarCount(con, null, """
            SELECT COUNT(*)
            FROM attempts a
            JOIN sessions s ON s.id=a.session_id
            WHERE a.outcome <> 'active'
              AND s.ended_at IS NOT NULL
              AND a.duration_seconds < @seconds
            """, ("@seconds", seconds));
    }

    public (int Attempts, int EmptySessions) DeleteAttemptsShorterThan(int seconds)
    {
        seconds = Math.Clamp(seconds, 1, 300);
        using var con = OpenWriteConnection();
        EnsureNoActiveTracking(con);
        using var tx = con.BeginTransaction();
        var attempts = ScalarCount(con, tx, """
            SELECT COUNT(*)
            FROM attempts a
            JOIN sessions s ON s.id=a.session_id
            WHERE a.outcome <> 'active'
              AND s.ended_at IS NOT NULL
              AND a.duration_seconds < @seconds
            """, ("@seconds", seconds));
        Execute(con, tx, """
            DELETE FROM attempts
            WHERE id IN (
                SELECT a.id
                FROM attempts a
                JOIN sessions s ON s.id=a.session_id
                WHERE a.outcome <> 'active'
                  AND s.ended_at IS NOT NULL
                  AND a.duration_seconds < @seconds
            )
            """, ("@seconds", seconds));
        var emptySessions = ScalarCount(con, tx, """
            SELECT COUNT(*) FROM sessions s
            WHERE s.ended_at IS NOT NULL
              AND NOT EXISTS (SELECT 1 FROM attempts a WHERE a.session_id=s.id)
            """);
        Execute(con, tx, """
            DELETE FROM sessions
            WHERE ended_at IS NOT NULL
              AND NOT EXISTS (SELECT 1 FROM attempts a WHERE a.session_id=sessions.id)
            """);
        RebuildPersonalBests(con, tx);
        PruneUnusedBeatmaps(con, tx);
        tx.Commit();
        return (attempts, emptySessions);
    }

    public (int InvalidAttempts, int EmptySessions, int ReclassifiableCompleted, int ModBackfillCandidates) PreviewCleanup(double minimumSeconds = 3.0)
    {
        if (!_factory.DatabaseExists)
        {
            return (0, 0, 0, 0);
        }

        using var con = _factory.Open();
        var reclassifiable = ScalarCount(con, null, """
            SELECT COUNT(*) FROM attempts
            WHERE outcome IN ('retried','quit','abandoned')
              AND progress >= 0.99
              AND (score > 0 OR n300 > 0 OR n100 > 0 OR n50 > 0 OR misses > 0)
            """);
        var invalid = ScalarCount(con, null, """
            SELECT COUNT(*) FROM attempts
            WHERE outcome != 'active'
              AND (
                duration_seconds < @minimum
                OR (score=0 AND n300=0 AND n100=0 AND n50=0 AND misses=0)
              )
            """, ("@minimum", minimumSeconds));
        var emptySessions = ScalarCount(con, null, """
            SELECT COUNT(*) FROM sessions s
            WHERE s.ended_at IS NOT NULL
              AND NOT EXISTS (SELECT 1 FROM attempts a WHERE a.session_id=s.id)
            """);
        var modBackfill = TableExists(con, "attempt_raw_snapshots")
            ? ScalarCount(con, null, """
                SELECT COUNT(DISTINCT a.id)
                FROM attempts a
                JOIN attempt_raw_snapshots r ON r.attempt_id=a.id
                WHERE r.kind='start'
                """)
            : 0;
        return (invalid, emptySessions, reclassifiable, modBackfill);
    }

    public IReadOnlyList<string> PreviewCleanupDetails(double minimumSeconds = 3.0, int limit = 30)
    {
        var rows = new List<string>();
        if (!_factory.DatabaseExists)
        {
            return rows;
        }

        using var con = _factory.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            SELECT a.id, a.outcome, a.duration_seconds, a.progress,
                   COALESCE(b.artist, ''), COALESCE(b.title, ''), COALESCE(b.difficulty, '')
            FROM attempts a
            JOIN beatmaps b ON b.id = a.beatmap_id
            WHERE outcome != 'active'
              AND (
                duration_seconds < @minimum
                OR (score=0 AND n300=0 AND n100=0 AND n50=0 AND misses=0)
                OR (outcome IN ('retried','quit','abandoned') AND progress >= 0.99)
              )
            ORDER BY a.id DESC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@minimum", minimumSeconds);
        cmd.Parameters.AddWithValue("@limit", limit);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetInt64(0);
            var outcome = reader.GetString(1);
            var duration = reader.GetDouble(2);
            var progress = reader.GetDouble(3);
            var map = $"{reader.GetString(4)} - {reader.GetString(5)} [{reader.GetString(6)}]";
            var action = outcome is "retried" or "quit" or "abandoned" && progress >= 0.99
                ? "reclassify"
                : "delete";
            rows.Add($"#{id} {action}: {outcome}, {duration:0.0}s, {progress:P0}, {map}");
        }
        return rows;
    }

    public int BackfillModSettingsFromSnapshots()
    {
        using var con = OpenWriteConnection();
        if (!TableExists(con, "attempt_raw_snapshots"))
        {
            return 0;
        }
        var updated = 0;
        using var select = con.CreateCommand();
        select.CommandText = """
            SELECT a.id, length(r.payload_zlib), r.payload_zlib
            FROM attempts a
            JOIN attempt_raw_snapshots r ON r.attempt_id=a.id
            WHERE r.kind='start'
            ORDER BY a.id
            """;
        using var reader = select.ExecuteReader();
        var rows = new List<(long Id, IReadOnlyList<(string Acronym, string SettingsJson)> Mods)>();
        while (reader.Read())
        {
            if (reader.IsDBNull(1))
            {
                continue;
            }
            var compressedLength = reader.GetInt64(1);
            if (compressedLength < 0 || compressedLength > MaxRawSnapshotCompressedBytes)
            {
                continue;
            }

            var payload = (byte[])reader.GetValue(2);
            if (payload.LongLength != compressedLength)
            {
                continue;
            }

            var mods = TryExtractMods(payload);
            if (mods.Count > 0)
            {
                rows.Add((reader.GetInt64(0), mods));
            }
        }
        reader.Close();

        if (rows.Count == 0)
        {
            return 0;
        }

        using var tx = con.BeginTransaction();
        foreach (var (id, mods) in rows)
        {
            var modsKey = string.Join(",", mods.Select(m => m.Acronym));
            Execute(con, tx, "UPDATE attempts SET mods_key=@mods WHERE id=@id", ("@mods", (object)modsKey), ("@id", id));
            Execute(con, tx, "DELETE FROM attempt_mods WHERE attempt_id=@id", ("@id", id));
            for (var i = 0; i < mods.Count; i++)
            {
                Execute(con, tx,
                    "INSERT INTO attempt_mods(attempt_id,position,acronym,settings_json) VALUES(@id,@pos,@acronym,@settings)",
                    ("@id", id), ("@pos", i), ("@acronym", (object)mods[i].Acronym), ("@settings", mods[i].SettingsJson));
            }
            updated++;
        }
        RebuildPersonalBests(con, tx);
        tx.Commit();
        return updated;
    }

    public void ExportJson(string destination)
    {
        var payload = new
        {
            schema_version = 2,
            exported_at = DateTimeOffset.Now.ToString("O"),
            sessions = QueryExportRows()
                .GroupBy(r => new { r.SessionId, r.SessionStarted, r.SessionEnded })
                .Select(g => new
                {
                    id = g.Key.SessionId,
                    started_at = g.Key.SessionStarted,
                    ended_at = g.Key.SessionEnded,
                    attempts = g.Select(r => new
                    {
                        id = r.AttemptId,
                        started_at = r.StartedAt,
                        ended_at = r.EndedAt,
                        outcome = r.Outcome,
                        artist = r.Artist,
                        title = r.Title,
                        difficulty = r.Difficulty,
                        mods_key = r.ModsKey,
                        score = r.Score,
                        accuracy = r.Accuracy,
                        grade = r.Grade,
                        pp = r.Pp,
                        fc_pp = r.FcPp,
                        combo = r.Combo,
                        n300 = r.N300,
                        n100 = r.N100,
                        n50 = r.N50,
                        misses = r.Misses,
                        unstable_rate = r.UnstableRate,
                        z_count = r.ZCount,
                        x_count = r.XCount,
                        progress = r.Progress,
                    }).ToArray(),
                }).ToArray(),
        };
        File.WriteAllText(destination, JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8);
    }

    public void ExportCsv(string destination)
    {
        var fields = new[]
        {
            "session_id", "session_started", "attempt_id", "started_at", "outcome",
            "artist", "title", "difficulty", "mods_key", "score", "accuracy", "grade",
            "pp", "fc_pp", "combo", "n300", "n100", "n50", "misses", "unstable_rate",
            "z_count", "x_count", "progress",
        };
        using var writer = new StreamWriter(destination, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        writer.WriteLine(string.Join(",", fields));
        foreach (var row in QueryExportRows())
        {
            writer.WriteLine(string.Join(",", new[]
            {
                row.SessionId.ToString(CultureInfo.InvariantCulture),
                Csv(row.SessionStarted),
                row.AttemptId.ToString(CultureInfo.InvariantCulture),
                Csv(row.StartedAt),
                Csv(row.Outcome),
                Csv(row.Artist),
                Csv(row.Title),
                Csv(row.Difficulty),
                Csv(row.ModsKey),
                row.Score.ToString(CultureInfo.InvariantCulture),
                row.Accuracy.ToString(CultureInfo.InvariantCulture),
                Csv(row.Grade),
                row.Pp.ToString(CultureInfo.InvariantCulture),
                row.FcPp.ToString(CultureInfo.InvariantCulture),
                row.Combo.ToString(CultureInfo.InvariantCulture),
                row.N300.ToString(CultureInfo.InvariantCulture),
                row.N100.ToString(CultureInfo.InvariantCulture),
                row.N50.ToString(CultureInfo.InvariantCulture),
                row.Misses.ToString(CultureInfo.InvariantCulture),
                row.UnstableRate.ToString(CultureInfo.InvariantCulture),
                row.ZCount.ToString(CultureInfo.InvariantCulture),
                row.XCount.ToString(CultureInfo.InvariantCulture),
                row.Progress.ToString(CultureInfo.InvariantCulture),
            }));
        }
    }

    private IReadOnlyList<ExportRow> QueryExportRows()
    {
        var rows = new List<ExportRow>();
        if (!_factory.DatabaseExists)
        {
            return rows;
        }

        using var con = _factory.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            SELECT s.id, s.started_at, s.ended_at,
                   a.id, a.started_at, a.ended_at, a.outcome,
                   COALESCE(b.artist, ''), COALESCE(b.title, ''), COALESCE(b.difficulty, ''),
                   a.mods_key, a.score, a.accuracy, a.grade, a.pp, a.fc_pp, a.combo,
                   a.n300, a.n100, a.n50, a.misses, a.unstable_rate, a.z_count, a.x_count,
                   a.progress
            FROM sessions s
            JOIN attempts a ON a.session_id = s.id
            JOIN beatmaps b ON b.id = a.beatmap_id
            ORDER BY s.started_at DESC, a.started_at DESC, a.id DESC
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new ExportRow(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetInt64(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8),
                reader.GetString(9),
                reader.GetString(10),
                reader.GetInt64(11),
                reader.GetDouble(12),
                reader.IsDBNull(13) ? "" : reader.GetString(13),
                reader.GetDouble(14),
                reader.GetDouble(15),
                (int)reader.GetInt64(16),
                (int)reader.GetInt64(17),
                (int)reader.GetInt64(18),
                (int)reader.GetInt64(19),
                (int)reader.GetInt64(20),
                reader.GetDouble(21),
                (int)reader.GetInt64(22),
                (int)reader.GetInt64(23),
                reader.GetDouble(24)));
        }
        return rows;
    }

    private SqliteConnection OpenWriteConnection()
    {
        var con = _factory.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        cmd.ExecuteNonQuery();
        return con;
    }

    private static void EnsureNoActiveTracking(SqliteConnection con)
    {
        if (ScalarCount(con, null, "SELECT COUNT(*) FROM sessions WHERE ended_at IS NULL") > 0
            || ScalarCount(con, null, "SELECT COUNT(*) FROM attempts WHERE outcome='active'") > 0)
            throw new InvalidOperationException("Tracking data cannot be changed while a session is active.");
    }

    private static void RebuildPersonalBests(
        SqliteConnection con,
        SqliteTransaction? transaction = null)
    {
        using var ownedTransaction = transaction is null ? con.BeginTransaction() : null;
        var tx = transaction ?? ownedTransaction!;
        DeleteAllFrom(con, tx, "personal_bests");
        DeleteAllFrom(con, tx, "attempt_improvements");

        foreach (var metric in PersonalBestMetrics)
        {
            using (var improvements = con.CreateCommand())
            {
                improvements.Transaction = tx;
                improvements.CommandText = $"""
                    WITH candidates AS (
                        SELECT a.id,
                               a.beatmap_id,
                               a.mods_key,
                               a.{metric.ColumnName} AS new_value,
                               (
                                   SELECT {metric.Aggregate}(previous.{metric.ColumnName})
                                   FROM attempts previous
                                   WHERE previous.beatmap_id = a.beatmap_id
                                     AND previous.mods_key = a.mods_key
                                     AND previous.outcome IN ('completed', 'failed')
                                     AND previous.n300 + previous.n100 + previous.n50 + previous.misses > 0
                                     AND previous.id < a.id
                               ) AS previous_value
                        FROM attempts a
                        WHERE a.outcome IN ('completed', 'failed')
                          AND a.n300 + a.n100 + a.n50 + a.misses > 0
                    )
                    INSERT INTO attempt_improvements(attempt_id, metric, previous_value, new_value, delta)
                    SELECT id, @metric, previous_value, new_value,
                           CASE WHEN previous_value IS NULL THEN NULL ELSE new_value - previous_value END
                    FROM candidates
                    WHERE previous_value IS NULL OR new_value {metric.Comparison} previous_value
                    """;
                improvements.Parameters.AddWithValue("@metric", metric.Name);
                improvements.ExecuteNonQuery();
            }

            using var best = con.CreateCommand();
            best.Transaction = tx;
            best.CommandText = $"""
                INSERT INTO personal_bests(beatmap_id, mods_key, metric, attempt_id, value)
                SELECT beatmap_id, mods_key, @metric, id, {metric.ColumnName}
                FROM (
                    SELECT a.*,
                           ROW_NUMBER() OVER (
                               PARTITION BY beatmap_id, mods_key
                               ORDER BY {metric.ColumnName} {metric.SortDirection}, id ASC
                           ) AS row_number
                    FROM attempts a
                    WHERE outcome IN ('completed', 'failed')
                      AND n300 + n100 + n50 + misses > 0
                )
                WHERE row_number = 1
                """;
            best.Parameters.AddWithValue("@metric", metric.Name);
            best.ExecuteNonQuery();
        }

        ownedTransaction?.Commit();
    }

    private static readonly PersonalBestMetric[] PersonalBestMetrics =
    [
        new("score", "score", "MAX", ">", "DESC"),
        new("accuracy", "accuracy", "MAX", ">", "DESC"),
        new("pp", "pp", "MAX", ">", "DESC"),
        new("combo", "combo", "MAX", ">", "DESC"),
        new("fewest_misses", "misses", "MIN", "<", "ASC"),
    ];

    private sealed record PersonalBestMetric(
        string Name,
        string ColumnName,
        string Aggregate,
        string Comparison,
        string SortDirection);

    private static void PruneUnusedBeatmaps(
        SqliteConnection con,
        SqliteTransaction? transaction = null)
    {
        using var cmd = con.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "DELETE FROM beatmaps WHERE id NOT IN (SELECT beatmap_id FROM attempts)";
        cmd.ExecuteNonQuery();
    }

    private static int DeleteAllFrom(SqliteConnection con, SqliteTransaction? tx, string table)
    {
        if (!TableExists(con, table))
        {
            return 0;
        }

        using var cmd = con.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"DELETE FROM {table}";
        return cmd.ExecuteNonQuery();
    }

    private static void Execute(SqliteConnection con, SqliteTransaction? tx, string sql, params (string Name, object Value)[] parameters)
    {
        using var cmd = con.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }
        cmd.ExecuteNonQuery();
    }

    private static int ScalarCount(SqliteConnection con, SqliteTransaction? tx, string sql, params (string Name, object Value)[] parameters)
    {
        using var cmd = con.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static IReadOnlyList<(string Acronym, string SettingsJson)> TryExtractMods(byte[] zlibPayload)
    {
        try
        {
            if (zlibPayload.LongLength > MaxRawSnapshotCompressedBytes)
            {
                return Array.Empty<(string, string)>();
            }

            using var input = new MemoryStream(zlibPayload);
            using var zlib = new System.IO.Compression.ZLibStream(input, System.IO.Compression.CompressionMode.Decompress);
            using var json = new MemoryStream();
            var buffer = new byte[81920];
            var decompressedBytes = 0;
            while (true)
            {
                var read = zlib.Read(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    break;
                }
                if (read > MaxRawSnapshotDecompressedBytes - decompressedBytes)
                {
                    return Array.Empty<(string, string)>();
                }
                json.Write(buffer, 0, read);
                decompressedBytes += read;
            }

            json.Position = 0;
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
            var root = doc.RootElement.TryGetProperty("payload", out var payload) ? payload : doc.RootElement;
            if (!TryFindProperty(root, "mods", out var modsElement) || modsElement.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<(string, string)>();
            }
            var mods = new List<(string, string)>();
            var modCount = 0;
            foreach (var mod in modsElement.EnumerateArray())
            {
                modCount++;
                if (modCount > MaxRawSnapshotMods)
                {
                    return Array.Empty<(string, string)>();
                }

                var acronym = mod.TryGetProperty("acronym", out var acronymElement)
                    ? acronymElement.GetString()
                    : null;
                if (string.IsNullOrWhiteSpace(acronym))
                {
                    continue;
                }
                var settings = mod.TryGetProperty("settings", out var settingsElement)
                    ? settingsElement.GetRawText()
                    : "{}";
                mods.Add((acronym, settings));
            }
            return mods;
        }
        catch
        {
            return Array.Empty<(string, string)>();
        }
    }

    private static bool TryFindProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty(name, out value))
            {
                return true;
            }
            foreach (var property in element.EnumerateObject())
            {
                if (TryFindProperty(property.Value, name, out value))
                {
                    return true;
                }
            }
        }
        value = default;
        return false;
    }

    private static void DeleteIfExists(
        SqliteConnection con,
        SqliteTransaction tx,
        string table,
        string where,
        (string Name, object Value) parameter)
    {
        if (!TableExists(con, table))
        {
            return;
        }

        using var cmd = con.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"DELETE FROM {table} WHERE {where}";
        cmd.Parameters.AddWithValue(parameter.Name, parameter.Value);
        cmd.ExecuteNonQuery();
    }

    private static bool TableExists(SqliteConnection con, string table)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @name";
        cmd.Parameters.AddWithValue("@name", table);
        return (long)(cmd.ExecuteScalar() ?? 0L) > 0;
    }

    private static string Csv(string? value)
    {
        value ??= "";
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private sealed record ExportRow(
        long SessionId,
        string SessionStarted,
        string? SessionEnded,
        long AttemptId,
        string StartedAt,
        string? EndedAt,
        string Outcome,
        string Artist,
        string Title,
        string Difficulty,
        string ModsKey,
        long Score,
        double Accuracy,
        string Grade,
        double Pp,
        double FcPp,
        int Combo,
        int N300,
        int N100,
        int N50,
        int Misses,
        double UnstableRate,
        int ZCount,
        int XCount,
        double Progress);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };
}
