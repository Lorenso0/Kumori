using Kumori.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kumori.Storage.Tests;

public class AnalyticsRepositoryTests : IDisposable
{
    private readonly string _dbPath;

    public AnalyticsRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"kumori-analytics-{Guid.NewGuid():N}.sqlite3");
        using var con = new SqliteConnection($"Data Source={_dbPath}");
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE attempts(
                id INTEGER PRIMARY KEY,
                started_at TEXT NOT NULL,
                outcome TEXT NOT NULL,
                accuracy REAL NOT NULL DEFAULT 0,
                pp REAL NOT NULL DEFAULT 0,
                score INTEGER NOT NULL DEFAULT 0
            );
            INSERT INTO attempts VALUES
                (1, '2026-07-06T10:00:00', 'completed', 98.0, 100.0, 1000),
                (2, '2026-07-06T11:00:00', 'failed', 50.0, 20.0, 500),
                (3, '2026-07-07T10:00:00', 'completed', 99.0, 120.0, 2000);
            """;
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void GetSummary_ReturnsTotalsAndDailyRows()
    {
        var summary = new AnalyticsRepository(
            new SqliteConnectionFactory(_dbPath, readOnly: true)).GetSummary();

        Assert.Equal(3, summary.Attempts);
        Assert.Equal(2, summary.Completed);
        Assert.Equal(1, summary.Failed);
        Assert.Equal(98.5, summary.AverageAccuracy);
        Assert.Equal(120.0, summary.BestPp);
        Assert.Equal(3500, summary.TotalScore);
        Assert.Equal(2, summary.Daily.Count);
        Assert.Equal("2026-07-07", summary.Daily[0].Day);
    }

    [Fact]
    public void GetSummary_UsesAccountChangeFromLatestSessionOnly()
    {
        using var con = new SqliteConnection($"Data Source={_dbPath}");
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            ALTER TABLE attempts ADD COLUMN session_id INTEGER;
            UPDATE attempts SET session_id = CASE id WHEN 1 THEN 10 ELSE 20 END;
            CREATE TABLE sessions(id INTEGER PRIMARY KEY);
            INSERT INTO sessions VALUES(10), (20);
            CREATE TABLE attempt_profile_changes(
                attempt_id INTEGER NOT NULL, captured_at TEXT NOT NULL,
                old_total_pp REAL, new_total_pp REAL,
                old_global_rank INTEGER, new_global_rank INTEGER,
                old_accuracy REAL, new_accuracy REAL,
                old_play_count INTEGER, new_play_count INTEGER
            );
            INSERT INTO attempt_profile_changes(attempt_id, captured_at, old_total_pp, new_total_pp)
            VALUES(1, '2026-07-06T10:10:00', 100, 140),
                  (2, '2026-07-07T10:10:00', 140, 145);
            """;
        cmd.ExecuteNonQuery();

        var summary = new AnalyticsRepository(
            new SqliteConnectionFactory(_dbPath, readOnly: true)).GetSummary();

        Assert.NotNull(summary.LatestAccountChange);
        Assert.Equal(140, summary.LatestAccountChange!.OldTotalPp);
        Assert.Equal(145, summary.LatestAccountChange.NewTotalPp);
    }

    [Fact]
    public void GetSummary_UsesFirstAndLatestSnapshotsForActiveProfile()
    {
        using var con = new SqliteConnection($"Data Source={_dbPath}");
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE profile_snapshots(
                id INTEGER PRIMARY KEY, captured_at TEXT NOT NULL, player_id INTEGER,
                total_pp REAL, global_rank INTEGER, accuracy REAL, play_count INTEGER);
            INSERT INTO profile_snapshots VALUES
                (1, '2026-07-07T18:00:00+02:00', 1, 6250.82, 55747, 99.50, 79433),
                (2, '2026-07-07T19:00:00+02:00', 1, 6276.18, 55135, 99.48, 79573),
                (3, '2026-07-13T07:49:00+00:00', 1, 6291.26, 52590, 99.44, 79730);
            """;
        cmd.ExecuteNonQuery();

        var summary = new AnalyticsRepository(
            new SqliteConnectionFactory(_dbPath, readOnly: true)).GetSummary();

        Assert.Equal(6250.82, summary.LatestAccountChange!.OldTotalPp);
        Assert.Equal(6291.26, summary.LatestAccountChange.NewTotalPp);
        Assert.Equal(55747, summary.LatestAccountChange.OldGlobalRank);
        Assert.Equal(52590, summary.LatestAccountChange.NewGlobalRank);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { }
    }
}
