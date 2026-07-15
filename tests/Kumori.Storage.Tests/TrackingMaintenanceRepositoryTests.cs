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

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(root, recursive: true); } catch { }
    }
}
