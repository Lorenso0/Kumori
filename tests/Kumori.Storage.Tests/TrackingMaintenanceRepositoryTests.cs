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

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(root, recursive: true); } catch { }
    }
}
