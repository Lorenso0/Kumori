using Kumori.Storage;
using Kumori.Tracking;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kumori.Storage.Tests;

public sealed class ProfileTelemetryStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"kumori-profile-{Guid.NewGuid():N}.sqlite3");

    [Fact]
    public void CompletedAttempt_OnlyUsesUpdateFromSameLoggedInProfile()
    {
        using (var con = new SqliteConnection($"Data Source={_path}"))
        {
            con.Open();
            using var command = con.CreateCommand();
            command.CommandText = "CREATE TABLE attempts(id INTEGER PRIMARY KEY); INSERT INTO attempts VALUES(1);";
            command.ExecuteNonQuery();
        }

        var store = new ProfileTelemetryStore(new SqliteConnectionFactory(_path, readOnly: false));
        store.Ingest(Snapshot(1, "First", 100, 1_000, 10));
        store.BeginAttempt(1);
        store.CompleteAttempt(1, "completed");

        // A different login must create its own snapshot, not complete First's attempt.
        store.Ingest(Snapshot(2, "Second", 200, 500, 20));
        store.Ingest(Snapshot(1, "First", 105, 990, 11));

        using var verify = new SqliteConnection($"Data Source={_path}");
        verify.Open();
        using var query = verify.CreateCommand();
        query.CommandText = "SELECT old_total_pp, new_total_pp FROM attempt_profile_changes WHERE attempt_id=1";
        using var reader = query.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(100, reader.GetDouble(0));
        Assert.Equal(105, reader.GetDouble(1));
    }

    [Fact]
    public void FailedAndDiscardedAttempts_CannotBecomePendingProfileChanges()
    {
        using (var con = new SqliteConnection($"Data Source={_path}"))
        {
            con.Open();
            using var command = con.CreateCommand();
            command.CommandText = "CREATE TABLE attempts(id INTEGER PRIMARY KEY); INSERT INTO attempts VALUES(1), (2);";
            command.ExecuteNonQuery();
        }

        var store = new ProfileTelemetryStore(new SqliteConnectionFactory(_path, readOnly: false));
        store.Ingest(Snapshot(1, "First", 100, 1_000, 10));

        store.BeginAttempt(1);
        store.CompleteAttempt(1, "failed");
        // A duplicate or delayed completion signal must not resurrect a failed attempt.
        store.CompleteAttempt(1, "completed");

        store.BeginAttempt(2);
        store.DiscardAttempt(2);
        store.CompleteAttempt(2, "completed");

        store.Ingest(Snapshot(1, "First", 105, 990, 11));

        using var verify = new SqliteConnection($"Data Source={_path}");
        verify.Open();
        using var query = verify.CreateCommand();
        query.CommandText = "SELECT COUNT(*) FROM attempt_profile_changes";
        Assert.Equal(0L, query.ExecuteScalar());
    }

    private static TosuSnapshot Snapshot(long id, string name, double pp, long rank, long playCount) => new()
    {
        WallTime = 1_700_000_000 + playCount,
        Profile = new TosuProfile { Id = id, Name = name, TotalPp = pp, GlobalRank = rank, PlayCount = playCount },
    };

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(_path); } catch { }
    }
}
