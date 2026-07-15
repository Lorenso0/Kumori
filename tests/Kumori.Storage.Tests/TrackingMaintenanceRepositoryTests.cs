using System.IO.Compression;
using Kumori.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kumori.Storage.Tests;

public sealed class TrackingMaintenanceRepositoryTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        $"kumori-tracking-maintenance-{Guid.NewGuid():N}");
    private readonly string databasePath;

    public TrackingMaintenanceRepositoryTests()
    {
        Directory.CreateDirectory(root);
        databasePath = Path.Combine(root, "tracking.sqlite3");
    }

    [Fact]
    public void DeleteBeforeRemovesOnlySessionsOlderThanCutoff()
    {
        var factory = new SqliteConnectionFactory(databasePath, readOnly: false);
        _ = new AttemptSqliteSink(factory, (_, work) => work(CancellationToken.None));
        using (var connection = factory.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO sessions(id, started_at, ended_at) VALUES(1, '2026-06-01T12:00:00Z', '2026-06-01T13:00:00Z');
                INSERT INTO sessions(id, started_at, ended_at) VALUES(2, '2026-07-10T12:00:00Z', '2026-07-10T13:00:00Z');
                """;
            command.ExecuteNonQuery();
        }

        var deleted = new TrackingMaintenanceRepository(factory).DeleteBefore("2026-07-01");

        Assert.Equal(1, deleted);
        using var verification = factory.Open();
        using var count = verification.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM sessions WHERE id = 2";
        Assert.Equal(1L, (long)count.ExecuteScalar()!);
    }

    [Fact]
    public void MutationsAreRejectedWhileAnySessionIsActive()
    {
        var factory = new SqliteConnectionFactory(databasePath, readOnly: false);
        _ = new AttemptSqliteSink(factory, (_, work) => work(CancellationToken.None));
        using (var connection = factory.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "INSERT INTO sessions(id, started_at) VALUES(1, '2026-07-15T12:00:00Z')";
            command.ExecuteNonQuery();
        }

        var repository = new TrackingMaintenanceRepository(factory);

        Assert.Throws<InvalidOperationException>(() => repository.DeleteAll());
        Assert.Throws<InvalidOperationException>(() => repository.DeleteBefore("2026-07-16"));
        Assert.Throws<InvalidOperationException>(() => repository.CleanupInvalidAttempts());
        Assert.Throws<InvalidOperationException>(() => repository.DeleteAttemptsShorterThan(3));
    }

    [Fact]
    public void StartupRecoveryFinalizesOnlyInterruptedOpenTrackingRows()
    {
        var factory = new SqliteConnectionFactory(databasePath, readOnly: false);
        _ = new AttemptSqliteSink(factory, (_, work) => work(CancellationToken.None));
        using (var connection = factory.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO sessions(id, started_at, started_at_utc_ms, ended_at, ended_at_utc_ms, interrupted)
                VALUES
                    (1, '2026-07-14T10:00:00Z', 1784023200000, NULL, NULL, 0),
                    (2, '2026-07-14T11:00:00Z', 1784026800000, NULL, NULL, 0),
                    (3, '2026-07-14T12:00:00Z', 1784030400000, '2026-07-14T12:10:00Z', 1784031000000, 0);
                INSERT INTO beatmaps(id, identity) VALUES(1, 'map-a');
                INSERT INTO attempts(
                    id, session_id, beatmap_id, started_at, started_at_utc_ms,
                    ended_at, ended_at_utc_ms, outcome, termination_evidence)
                VALUES
                    (1, 1, 1, '2026-07-14T10:01:00Z', 1784023260000,
                     NULL, NULL, 'active', NULL),
                    (2, 2, 1, '2026-07-14T11:01:00Z', 1784026860000,
                     '2026-07-14T11:05:00Z', 1784027100000, 'completed', 'results'),
                    (3, 3, 1, '2026-07-14T12:01:00Z', 1784030460000,
                     '2026-07-14T12:05:00Z', 1784030700000, 'completed', 'results');
                """;
            command.ExecuteNonQuery();
        }

        var repository = new TrackingMaintenanceRepository(factory);
        var recovered = repository.RecoverInterruptedTracking();

        Assert.Equal((1, 2), recovered);
        using var verification = factory.Open();
        using var rows = verification.CreateCommand();
        rows.CommandText = """
            SELECT s.id, s.ended_at, s.ended_at_utc_ms, s.interrupted,
                   a.outcome, a.termination_evidence
            FROM sessions s
            LEFT JOIN attempts a ON a.session_id=s.id
            ORDER BY s.id
            """;
        using var reader = rows.ExecuteReader();

        Assert.True(reader.Read());
        Assert.Equal(1, reader.GetInt64(0));
        Assert.Equal("2026-07-14T10:01:00Z", reader.GetString(1));
        Assert.Equal(1784023260000, reader.GetInt64(2));
        Assert.Equal(1, reader.GetInt64(3));
        Assert.Equal("abandoned", reader.GetString(4));
        Assert.Equal("startup_recovery", reader.GetString(5));

        Assert.True(reader.Read());
        Assert.Equal(2, reader.GetInt64(0));
        Assert.Equal("2026-07-14T11:05:00Z", reader.GetString(1));
        Assert.Equal(1784027100000, reader.GetInt64(2));
        Assert.Equal(1, reader.GetInt64(3));
        Assert.Equal("completed", reader.GetString(4));
        Assert.Equal("results", reader.GetString(5));

        Assert.True(reader.Read());
        Assert.Equal(3, reader.GetInt64(0));
        Assert.Equal("2026-07-14T12:10:00Z", reader.GetString(1));
        Assert.Equal(1784031000000, reader.GetInt64(2));
        Assert.Equal(0, reader.GetInt64(3));
        Assert.Equal("completed", reader.GetString(4));
        Assert.Equal("results", reader.GetString(5));
        Assert.False(reader.Read());
        reader.Close();

        Assert.Equal((0, 0), repository.RecoverInterruptedTracking());
        Assert.Equal(1, repository.DeleteSession(1));
    }

    [Fact]
    public void StartupRepairNeutralizesMissingTosuResultAndRemovesFalsePersonalBests()
    {
        var factory = new SqliteConnectionFactory(databasePath, readOnly: false);
        _ = new AttemptSqliteSink(factory, (_, work) => work(CancellationToken.None));
        using (var connection = factory.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO sessions(id, started_at, ended_at)
                VALUES(1, '2026-07-15T10:00:00Z', '2026-07-15T10:10:00Z');
                INSERT INTO beatmaps(id, identity) VALUES(1, 'map-a');
                INSERT INTO attempts(
                    id, session_id, beatmap_id, started_at, ended_at, outcome,
                    termination_evidence, score, accuracy, grade, combo,
                    n300, n100, n50, misses)
                VALUES(1, 1, 1, '2026-07-15T10:01:00Z', '2026-07-15T10:01:10Z',
                       'failed', 'state_transition', 0, 100, 'F', 0, 0, 0, 0, 0);
                INSERT INTO attempt_timing(
                    attempt_id, offsets_zlib, hit_count, early_count, late_count, mean, median, deviation)
                VALUES(1, X'', 25, 10, 15, 0, 0, 0);
                INSERT INTO personal_bests(beatmap_id, mods_key, metric, attempt_id, value)
                VALUES(1, 'NM', 'accuracy', 1, 100);
                INSERT INTO attempt_improvements(attempt_id, metric, new_value)
                VALUES(1, 'accuracy', 100);
                """;
            command.ExecuteNonQuery();
        }

        var repository = new TrackingMaintenanceRepository(factory);

        Assert.Equal(1, repository.RepairMissingTosuResults());
        Assert.Equal(0, repository.RepairMissingTosuResults());
        using var verification = factory.Open();
        Assert.Equal(0d, Scalar<double>(verification, "SELECT accuracy FROM attempts WHERE id=1"));
        Assert.Equal(0L, Scalar<long>(verification, "SELECT COUNT(*) FROM attempts WHERE grade IS NOT NULL"));
        Assert.Contains("tosu_result_missing", Scalar<string>(verification, "SELECT termination_evidence FROM attempts WHERE id=1"));
        Assert.Equal(0L, Scalar<long>(verification, "SELECT COUNT(*) FROM personal_bests"));
        Assert.Equal(0L, Scalar<long>(verification, "SELECT COUNT(*) FROM attempt_improvements"));
    }

    [Fact]
    public void StartupRepairRestoresPartialTosuCountsAndEventsOverwrittenBySimulation()
    {
        var factory = new SqliteConnectionFactory(databasePath, readOnly: false);
        _ = new AttemptSqliteSink(factory, (_, work) => work(CancellationToken.None));
        using (var connection = factory.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO sessions(id, started_at, ended_at)
                VALUES(1, '2026-07-15T18:39:00Z', '2026-07-15T18:41:00Z');
                INSERT INTO beatmaps(id, identity) VALUES(1, 'map-a');
                INSERT INTO attempts(
                    id, session_id, beatmap_id, started_at, ended_at, outcome,
                    score, accuracy, combo, n300, n100, n50, misses)
                VALUES(1, 1, 1, '2026-07-15T18:39:49Z', '2026-07-15T18:40:08Z',
                       'quit', 140420, 94.3389, 98, 10, 1, 0, 0);
                INSERT INTO attempt_context(attempt_id, source_json, score_json)
                VALUES(1,
                    '{"result_recovery":{"simulation":"completed","simulation_schema":2,"simulated_fields":["300","100","misses","max pp","judgement events"]}}',
                    '{"hits":{"_300":10,"_100":1,"_50":0,"_0":0},"recovered_from_replay":true}');
                INSERT INTO attempt_events(attempt_id, captured_at, map_time_ms, event_type, value, data_json)
                VALUES
                    (1, '2026-07-15T18:39:50Z', 8, 'checkpoint', 0,
                     '{"n300":0,"n100":0,"n50":0,"misses":0,"slider_breaks":0}'),
                    (1, '2026-07-15T18:39:56Z', 6000, 'checkpoint', 0,
                     '{"n300":50,"n100":2,"n50":0,"misses":1,"slider_breaks":0}'),
                    (1, '2026-07-15T18:40:08Z', 17031, 'checkpoint', 0,
                     '{"n300":82,"n100":4,"n50":0,"misses":3,"slider_breaks":1}'),
                    (1, '2026-07-15T18:40:09Z', 2121, 'hit_100', 1,
                     '{"source":"replay_simulation"}');
                """;
            command.ExecuteNonQuery();
        }

        var repository = new TrackingMaintenanceRepository(factory);
        Assert.Equal(1, repository.RepairPartialSimulationCoreResults());
        Assert.Equal(0, repository.RepairPartialSimulationCoreResults());

        using var verification = factory.Open();
        using (var values = verification.CreateCommand())
        {
            values.CommandText = "SELECT n300, n100, n50, misses FROM attempts WHERE id=1";
            using var reader = values.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal(82, reader.GetInt32(0));
            Assert.Equal(4, reader.GetInt32(1));
            Assert.Equal(0, reader.GetInt32(2));
            Assert.Equal(3, reader.GetInt32(3));
        }
        Assert.Equal(3L, Scalar<long>(verification,
            "SELECT COUNT(*) FROM attempt_events WHERE attempt_id=1 AND event_type='miss'"));
        Assert.Equal(2L, Scalar<long>(verification,
            "SELECT COUNT(*) FROM attempt_events WHERE attempt_id=1 AND event_type='hit_100'"));
        Assert.Equal(1L, Scalar<long>(verification,
            "SELECT COUNT(*) FROM attempt_events WHERE attempt_id=1 AND event_type='slider_break'"));
        string sourceJson = Scalar<string>(verification,
            "SELECT source_json FROM attempt_context WHERE attempt_id=1");
        string scoreJson = Scalar<string>(verification,
            "SELECT score_json FROM attempt_context WHERE attempt_id=1");
        Assert.Contains("tosu_checkpoint", sourceJson);
        Assert.DoesNotContain("\"300\"", sourceJson);
        Assert.Contains("\"_300\":82", scoreJson);
        Assert.Contains("\"_0\":3", scoreJson);
        Assert.DoesNotContain("recovered_from_replay", scoreJson);
    }

    [Fact]
    public void DeleteShortPlaysUsesStrictBoundaryAndRemovesNewlyEmptyEndedSessions()
    {
        var factory = new SqliteConnectionFactory(databasePath, readOnly: false);
        _ = new AttemptSqliteSink(factory, (_, work) => work(CancellationToken.None));
        using (var connection = factory.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO sessions(id, started_at, ended_at) VALUES
                    (1, '2026-07-15T10:00:00Z', '2026-07-15T10:10:00Z'),
                    (2, '2026-07-15T11:00:00Z', '2026-07-15T11:10:00Z');
                INSERT INTO beatmaps(id, identity) VALUES (1, 'map-a'), (2, 'map-b');
                INSERT INTO attempts(id, session_id, beatmap_id, started_at, ended_at, outcome, duration_seconds, score, n300)
                VALUES
                    (1, 1, 1, '2026-07-15T10:00:00Z', '2026-07-15T10:00:02Z', 'quit', 2.99, 100, 1),
                    (2, 2, 2, '2026-07-15T11:00:00Z', '2026-07-15T11:00:03Z', 'completed', 3.0, 100, 1);
                """;
            command.ExecuteNonQuery();
        }

        var repository = new TrackingMaintenanceRepository(factory);
        Assert.Equal(1, repository.PreviewAttemptsShorterThan(3));

        var deleted = repository.DeleteAttemptsShorterThan(3);

        Assert.Equal((1, 1), deleted);
        using var verification = factory.Open();
        using var counts = verification.CreateCommand();
        counts.CommandText = "SELECT (SELECT COUNT(*) FROM attempts), (SELECT COUNT(*) FROM sessions), (SELECT COUNT(*) FROM beatmaps)";
        using var reader = counts.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(1, reader.GetInt32(0));
        Assert.Equal(1, reader.GetInt32(1));
        Assert.Equal(1, reader.GetInt32(2));
    }

    [Fact]
    public void MaintenanceRebuildsEveryPersonalBestAndImprovementMetric()
    {
        var factory = new SqliteConnectionFactory(databasePath, readOnly: false);
        _ = new AttemptSqliteSink(factory, (_, work) => work(CancellationToken.None));
        using (var connection = factory.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO sessions(id, started_at, ended_at) VALUES
                    (1, '2026-07-15T10:00:00Z', '2026-07-15T10:10:00Z');
                INSERT INTO beatmaps(id, identity) VALUES (1, 'map-a');
                INSERT INTO attempts(
                    id, session_id, beatmap_id, started_at, ended_at, outcome, mods_key,
                    duration_seconds, score, accuracy, pp, combo, misses)
                VALUES
                    (1, 1, 1, '2026-07-15T10:00:00Z', '2026-07-15T10:04:00Z', 'completed', 'HD',
                     240, 100000, 0.99, 200, 500, 3),
                    (2, 1, 1, '2026-07-15T10:05:00Z', '2026-07-15T10:09:00Z', 'completed', 'HD',
                     240, 200000, 0.98, 190, 600, 2);
                """;
            command.ExecuteNonQuery();
        }

        Assert.Equal(0, new TrackingMaintenanceRepository(factory).DeleteAttempt(999));

        using var verification = factory.Open();
        using var bests = verification.CreateCommand();
        bests.CommandText = "SELECT metric, attempt_id, value FROM personal_bests ORDER BY metric";
        using var rows = bests.ExecuteReader();
        var values = new Dictionary<string, (long AttemptId, double Value)>();
        while (rows.Read())
        {
            values.Add(rows.GetString(0), (rows.GetInt64(1), rows.GetDouble(2)));
        }
        Assert.Equal(5, values.Count);
        Assert.Equal((1L, 0.99), values["accuracy"]);
        Assert.Equal((2L, 600d), values["combo"]);
        Assert.Equal((2L, 2d), values["fewest_misses"]);
        Assert.Equal((1L, 200d), values["pp"]);
        Assert.Equal((2L, 200000d), values["score"]);

        using var improvements = verification.CreateCommand();
        improvements.CommandText = "SELECT COUNT(*) FROM attempt_improvements";
        Assert.Equal(8L, (long)improvements.ExecuteScalar()!);
    }

    [Fact]
    public void DeleteAndPersonalBestRebuildRollBackTogether()
    {
        var factory = new SqliteConnectionFactory(databasePath, readOnly: false);
        _ = new AttemptSqliteSink(factory, (_, work) => work(CancellationToken.None));
        using (var connection = factory.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO sessions(id, started_at, ended_at)
                VALUES(1, '2026-07-15T10:00:00Z', '2026-07-15T10:10:00Z');
                INSERT INTO beatmaps(id, identity) VALUES(1, 'map-a');
                INSERT INTO attempts(
                    id, session_id, beatmap_id, started_at, ended_at, outcome,
                    duration_seconds, score, accuracy, pp, combo, misses)
                VALUES
                    (1, 1, 1, '2026-07-15T10:00:00Z', '2026-07-15T10:04:00Z', 'completed',
                     240, 100000, 0.98, 100, 400, 2),
                    (2, 1, 1, '2026-07-15T10:05:00Z', '2026-07-15T10:09:00Z', 'completed',
                     240, 200000, 0.99, 200, 500, 1);
                INSERT INTO personal_bests(beatmap_id, mods_key, metric, attempt_id, value)
                VALUES(1, 'NM', 'score', 2, 200000);
                CREATE TRIGGER reject_personal_best_rebuild
                BEFORE INSERT ON personal_bests
                BEGIN
                    SELECT RAISE(ABORT, 'forced rebuild failure');
                END;
                """;
            command.ExecuteNonQuery();
        }

        var repository = new TrackingMaintenanceRepository(factory);

        Assert.Throws<SqliteException>(() => repository.DeleteAttempt(2));
        using var verification = factory.Open();
        using var count = verification.CreateCommand();
        count.CommandText = """
            SELECT (SELECT COUNT(*) FROM attempts WHERE id = 2),
                   (SELECT COUNT(*) FROM personal_bests WHERE attempt_id = 2)
            """;
        using var reader = count.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(1, reader.GetInt32(0));
        Assert.Equal(1, reader.GetInt32(1));
    }

    [Fact]
    public void SnapshotModBackfillSkipsOversizedCompressedAndDecompressedPayloads()
    {
        var factory = new SqliteConnectionFactory(databasePath, readOnly: false);
        _ = new AttemptSqliteSink(factory, (_, work) => work(CancellationToken.None));
        var decompressionBomb = Compress(new byte[
            TrackingMaintenanceRepository.MaxRawSnapshotDecompressedBytes + 1]);
        Assert.True(
            decompressionBomb.Length < TrackingMaintenanceRepository.MaxRawSnapshotCompressedBytes);
        using (var connection = factory.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE attempt_raw_snapshots(
                    attempt_id INTEGER NOT NULL,
                    kind TEXT NOT NULL,
                    payload_zlib BLOB NOT NULL,
                    PRIMARY KEY(attempt_id, kind));
                INSERT INTO sessions(id, started_at, ended_at)
                VALUES(1, '2026-07-15T10:00:00Z', '2026-07-15T10:10:00Z');
                INSERT INTO beatmaps(id, identity) VALUES(1, 'map-a');
                INSERT INTO attempts(id, session_id, beatmap_id, started_at, ended_at, outcome, mods_key)
                VALUES
                    (1, 1, 1, '2026-07-15T10:00:00Z', '2026-07-15T10:04:00Z', 'completed', 'NM'),
                    (2, 1, 1, '2026-07-15T10:05:00Z', '2026-07-15T10:09:00Z', 'completed', 'NM');
                INSERT INTO attempt_raw_snapshots(attempt_id, kind, payload_zlib)
                VALUES(1, 'start', zeroblob(@oversized));
                INSERT INTO attempt_raw_snapshots(attempt_id, kind, payload_zlib)
                VALUES(2, 'start', @bomb);
                """;
            command.Parameters.AddWithValue(
                "@oversized",
                TrackingMaintenanceRepository.MaxRawSnapshotCompressedBytes + 1);
            command.Parameters.Add("@bomb", SqliteType.Blob).Value = decompressionBomb;
            command.ExecuteNonQuery();
        }

        Assert.Equal(0, new TrackingMaintenanceRepository(factory).BackfillModSettingsFromSnapshots());
        using var verification = factory.Open();
        using var mods = verification.CreateCommand();
        mods.CommandText = "SELECT COUNT(*) FROM attempt_mods";
        Assert.Equal(0L, (long)mods.ExecuteScalar()!);
    }

    private static byte[] Compress(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            zlib.Write(bytes);
        }
        return output.ToArray();
    }

    private static T Scalar<T>(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(root, recursive: true); } catch { }
    }
}
