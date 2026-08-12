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

    [Fact]
    public void RepeatedUnchangedProfile_IsPersistedOnlyOnce()
    {
        var store = new ProfileTelemetryStore(new SqliteConnectionFactory(_path, readOnly: false));
        var snapshot = Snapshot(1, "First", 100, 1_000, 10);

        for (var i = 0; i < 100; i++)
        {
            store.Ingest(snapshot with { WallTime = snapshot.WallTime + i / 60.0 });
        }

        using var verify = new SqliteConnection($"Data Source={_path}");
        verify.Open();
        using var query = verify.CreateCommand();
        query.CommandText = "SELECT COUNT(*) FROM profile_snapshots";
        Assert.Equal(1L, query.ExecuteScalar());
    }

    [Fact]
    public void CountryRank_IsMigratedAndStoredWithLatestProfileSnapshot()
    {
        var store = new ProfileTelemetryStore(new SqliteConnectionFactory(_path, readOnly: false));
        store.Ingest(Snapshot(1, "First", 100, 1_000, 10));

        Assert.True(store.RecordCountryRank(
            1, 25, "nl", DateTimeOffset.FromUnixTimeSeconds(1_700_000_100)));

        using var verify = new SqliteConnection($"Data Source={_path}");
        verify.Open();
        using var query = verify.CreateCommand();
        query.CommandText = "SELECT country_rank, country_code FROM profile_snapshots ORDER BY id DESC LIMIT 1";
        using var reader = query.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(25, reader.GetInt64(0));
        Assert.Equal("NL", reader.GetString(1));
    }

    [Fact]
    public void ExistingProfileSchema_GainsCountryRankWithoutLosingHistory()
    {
        using (var con = new SqliteConnection($"Data Source={_path}"))
        {
            con.Open();
            using var command = con.CreateCommand();
            command.CommandText = """
                CREATE TABLE profile_snapshots(
                    id INTEGER PRIMARY KEY, captured_at TEXT NOT NULL,
                    session_id INTEGER, player_id INTEGER, player_name TEXT,
                    total_pp REAL, global_rank INTEGER, accuracy REAL,
                    play_count INTEGER, level REAL, ranked_score INTEGER,
                    country_code TEXT, fingerprint TEXT NOT NULL);
                INSERT INTO profile_snapshots(
                    captured_at, player_id, player_name, total_pp, global_rank,
                    play_count, country_code, fingerprint)
                VALUES('2026-08-10T20:00:00+00:00', 1, 'First', 100, 1000, 10, 'NL', '{}');
                """;
            command.ExecuteNonQuery();
        }

        var store = new ProfileTelemetryStore(new SqliteConnectionFactory(_path, readOnly: false));

        Assert.Equal(1, store.GetCurrentIdentity()!.PlayerId);
        Assert.True(store.RecordCountryRank(1, 25, "NL", DateTimeOffset.UtcNow));

        using var verify = new SqliteConnection($"Data Source={_path}");
        verify.Open();
        using var query = verify.CreateCommand();
        query.CommandText = "SELECT COUNT(*), MAX(country_rank) FROM profile_snapshots";
        using var reader = query.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(2, reader.GetInt64(0));
        Assert.Equal(25, reader.GetInt64(1));
    }

    [Fact]
    public async Task DeferredPersistence_DoesNotTouchSqliteOnIngestThread()
    {
        Func<CancellationToken, Task>? deferred = null;
        var store = new ProfileTelemetryStore(
            new SqliteConnectionFactory(_path, readOnly: false),
            (_, work) =>
            {
                deferred = work;
                return Task.CompletedTask;
            });

        store.Ingest(Snapshot(1, "First", 100, 1_000, 10));

        Assert.False(File.Exists(_path));
        Assert.NotNull(deferred);
        await deferred!(CancellationToken.None);

        using var verify = new SqliteConnection($"Data Source={_path}");
        verify.Open();
        using var query = verify.CreateCommand();
        query.CommandText = "SELECT COUNT(*) FROM profile_snapshots";
        Assert.Equal(1L, query.ExecuteScalar());
    }

    [Fact]
    public async Task DeferredPersistence_FailureCanBeRescheduledByRepeatedPacket()
    {
        var parent = Path.Combine(Path.GetTempPath(), $"kumori-profile-parent-{Guid.NewGuid():N}");
        var path = Path.Combine(parent, "profile.sqlite3");
        var queued = new List<Func<CancellationToken, Task>>();
        var store = new ProfileTelemetryStore(
            new SqliteConnectionFactory(path, readOnly: false),
            (_, work) =>
            {
                queued.Add(work);
                return Task.CompletedTask;
            });
        var snapshot = Snapshot(1, "First", 100, 1_000, 10);

        try
        {
            store.Ingest(snapshot);
            await Assert.ThrowsAnyAsync<Exception>(() => queued[0](CancellationToken.None));

            Directory.CreateDirectory(parent);
            store.Ingest(snapshot);
            Assert.Equal(2, queued.Count);
            await queued[1](CancellationToken.None);

            using var verify = new SqliteConnection($"Data Source={path}");
            verify.Open();
            using var query = verify.CreateCommand();
            query.CommandText = "SELECT COUNT(*) FROM profile_snapshots";
            Assert.Equal(1L, query.ExecuteScalar());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(parent, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task DeferredPersistence_CoalescesNewReadingWithoutLosingPendingAttempt()
    {
        using (var con = new SqliteConnection($"Data Source={_path}"))
        {
            con.Open();
            using var command = con.CreateCommand();
            command.CommandText = "CREATE TABLE attempts(id INTEGER PRIMARY KEY); INSERT INTO attempts VALUES(1);";
            command.ExecuteNonQuery();
        }

        var queued = new List<Func<CancellationToken, Task>>();
        var store = new ProfileTelemetryStore(
            new SqliteConnectionFactory(_path, readOnly: false),
            (_, work) =>
            {
                queued.Add(work);
                return Task.CompletedTask;
            });

        store.Ingest(Snapshot(1, "First", 100, 1_000, 10));
        store.BeginAttempt(1);
        store.CompleteAttempt(1, "completed");
        store.Ingest(Snapshot(1, "First", 105, 990, 11));

        Assert.Single(queued);
        await queued[0](CancellationToken.None);

        using var verify = new SqliteConnection($"Data Source={_path}");
        verify.Open();
        using var query = verify.CreateCommand();
        query.CommandText = "SELECT old_total_pp, new_total_pp FROM attempt_profile_changes WHERE attempt_id=1";
        using var reader = query.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(100, reader.GetDouble(0));
        Assert.Equal(105, reader.GetDouble(1));
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
