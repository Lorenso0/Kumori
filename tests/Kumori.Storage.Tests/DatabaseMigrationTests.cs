using Kumori.Storage;
using Xunit;

namespace Kumori.Storage.Tests;

public sealed class DatabaseMigrationTests : IDisposable
{
    private readonly string path = Path.Combine(Path.GetTempPath(), $"kumori-migration-{Guid.NewGuid():N}.db");

    [Fact]
    public void Migration_WritesVersionAfterUtcColumnsExist()
    {
        var factory = new SqliteConnectionFactory(path, readOnly: false);
        _ = new AttemptSqliteSink(factory);
        using var connection = factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM metadata WHERE key='schema_version'";
        Assert.Equal("4", command.ExecuteScalar());
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('attempts') WHERE name='started_at_utc_ms'";
        Assert.Equal(1L, command.ExecuteScalar());
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('score_webhook_deliveries') WHERE name='api_failure_attempts'";
        Assert.Equal(1L, command.ExecuteScalar());
    }

    [Fact]
    public void Migration_UpgradesVersionOneDatabaseThroughOrderedSteps()
    {
        var factory = new SqliteConnectionFactory(path, readOnly: false);
        _ = new AttemptSqliteSink(factory);
        using (var connection = factory.Open())
        {
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE metadata SET value = '1' WHERE key = 'schema_version'";
            command.ExecuteNonQuery();
        }

        _ = new AttemptSqliteSink(factory);

        using var migrated = factory.Open();
        using var check = migrated.CreateCommand();
        check.CommandText = "SELECT value FROM metadata WHERE key = 'schema_version'";
        Assert.Equal("4", check.ExecuteScalar());
        check.CommandText = "SELECT COUNT(*) FROM pragma_table_info('sessions') WHERE name='ended_at_utc_ms'";
        Assert.Equal(1L, check.ExecuteScalar());
        check.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='score_webhook_deliveries'";
        Assert.Equal(1L, check.ExecuteScalar());
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(path); } catch { }
    }
}
