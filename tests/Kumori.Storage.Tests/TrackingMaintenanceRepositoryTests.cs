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
                INSERT INTO sessions(id, started_at) VALUES(1, '2026-06-01T12:00:00Z');
                INSERT INTO sessions(id, started_at) VALUES(2, '2026-07-10T12:00:00Z');
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

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(root, recursive: true); } catch { }
    }
}
