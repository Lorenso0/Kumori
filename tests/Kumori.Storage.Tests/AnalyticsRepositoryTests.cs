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
                (2, '2026-07-06T11:00:00', 'failed', 50.0, 999.0, 500),
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
        Assert.Equal(120.0, summary.Daily[0].BestPp);
        Assert.Equal(100.0, summary.Daily[1].BestPp);
    }

    [Fact]
    public void GetSummary_ReturnsOverallAndDailyActivityMetrics()
    {
        using (var con = new SqliteConnection($"Data Source={_dbPath}"))
        {
            con.Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = """
                ALTER TABLE attempts ADD COLUMN duration_seconds REAL NOT NULL DEFAULT 0;
                ALTER TABLE attempts ADD COLUMN z_count INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE attempts ADD COLUMN x_count INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE attempts ADD COLUMN misses INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE attempts ADD COLUMN n300 INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE attempts ADD COLUMN n100 INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE attempts ADD COLUMN n50 INTEGER NOT NULL DEFAULT 0;
                UPDATE attempts SET
                    duration_seconds = CASE id WHEN 1 THEN 120 WHEN 2 THEN 60 ELSE 180 END,
                    z_count = id * 100,
                    x_count = id * 50,
                    misses = id;
                """;
            cmd.ExecuteNonQuery();
        }

        var summary = new AnalyticsRepository(
            new SqliteConnectionFactory(_dbPath, readOnly: true)).GetSummary();

        Assert.Equal(360, summary.TotalDurationSeconds);
        Assert.Equal(900, summary.ZTotal + summary.XTotal);
        Assert.Equal(6, summary.TotalMisses);
        Assert.Equal(180, summary.Daily[0].TotalDurationSeconds);
        Assert.Equal(450, summary.Daily[0].ZTotal + summary.Daily[0].XTotal);
        Assert.Equal(3, summary.Daily[0].TotalMisses);
    }

    [Fact]
    public void GetDailyProgress_ReturnsAccountAndMapHighlights()
    {
        using (var con = new SqliteConnection($"Data Source={_dbPath}"))
        {
            con.Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = """
                ALTER TABLE attempts ADD COLUMN beatmap_id INTEGER;
                ALTER TABLE attempts ADD COLUMN duration_seconds REAL NOT NULL DEFAULT 0;
                ALTER TABLE attempts ADD COLUMN z_count INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE attempts ADD COLUMN x_count INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE attempts ADD COLUMN misses INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE attempts ADD COLUMN n300 INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE attempts ADD COLUMN n100 INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE attempts ADD COLUMN n50 INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE attempts ADD COLUMN combo INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE attempts ADD COLUMN slider_breaks INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE attempts ADD COLUMN mods_key TEXT NOT NULL DEFAULT 'NM';
                ALTER TABLE attempts ADD COLUMN player_name TEXT;
                CREATE TABLE beatmaps(
                    id INTEGER PRIMARY KEY, beatmap_id INTEGER, set_id INTEGER,
                    artist TEXT, title TEXT, difficulty TEXT, bpm REAL,
                    stars REAL, ar REAL, od REAL, cs REAL,
                    max_combo INTEGER NOT NULL DEFAULT 0);
                INSERT INTO beatmaps VALUES
                    (10, 1010, 100, 'Artist A', 'Song A', 'Hard', 160, 4.2, 9, 8, 4, 800),
                    (20, 2020, 200, 'Artist B', 'Song B', 'Insane', 180, 5.8, 9.5, 8.7, 4, 1200);
                UPDATE attempts SET beatmap_id = CASE id WHEN 3 THEN 20 ELSE 10 END,
                    duration_seconds = id * 60,
                    z_count = id * 100,
                    x_count = id * 50,
                    misses = id,
                    n300 = id * 100,
                    n100 = id * 10,
                    n50 = id,
                    combo = id * 200,
                    slider_breaks = id - 1,
                    mods_key = CASE id WHEN 3 THEN 'HD,DA,BPM' ELSE 'NM' END,
                    player_name = 'Lorenzo';
                CREATE TABLE attempt_mods(
                    attempt_id INTEGER NOT NULL, acronym TEXT NOT NULL, settings_json TEXT NOT NULL);
                INSERT INTO attempt_mods VALUES(3, 'BPM', '{"target_bpm":180}');
                CREATE TABLE profile_snapshots(
                    id INTEGER PRIMARY KEY, captured_at TEXT NOT NULL, player_id INTEGER,
                    player_name TEXT, country_code TEXT, total_pp REAL,
                    global_rank INTEGER, play_count INTEGER, country_rank INTEGER);
                INSERT INTO profile_snapshots VALUES
                    (1, '2026-07-06T20:00:00+00:00', 99, 'Lorenzo', 'NL', 1000, 500, 10, 30),
                    (2, '2026-07-07T10:00:00+00:00', 99, 'Lorenzo', 'NL', 1005, 490, 11, 29),
                    (3, '2026-07-07T20:00:00+00:00', 99, 'Lorenzo', 'NL', 1010, 480, 12, 27);
                """;
            cmd.ExecuteNonQuery();
        }

        var report = new AnalyticsRepository(
            new SqliteConnectionFactory(_dbPath, readOnly: true))
            .GetDailyProgress("2026-07-07");

        Assert.NotNull(report);
        Assert.Equal("Lorenzo", report!.PlayerName);
        Assert.Equal(1, report.Summary.Attempts);
        Assert.Equal(1, report.Summary.DistinctMaps);
        Assert.Equal(2_000, report.Summary.TotalScore);
        Assert.Equal(180, report.Summary.TotalDurationSeconds);
        Assert.Equal("Song B", report.MostPlayedMap!.Title);
        Assert.Equal(1, report.MostPlayedMap.Plays);
        Assert.Equal(2020, report.MostPlayedMap.BeatmapId);
        Assert.Equal(200, report.MostPlayedMap.BeatmapSetId);
        Assert.Equal(5.8, report.MostPlayedMap.Stars);
        Assert.Equal(180, report.MostPlayedMap.Bpm);
        Assert.Equal(120, report.BestPlay!.Pp);
        Assert.Equal(600, report.BestPlay.Combo);
        Assert.Equal(1200, report.BestPlay.MaxCombo);
        Assert.Equal(30, report.BestPlay.N100);
        Assert.Equal(3, report.BestPlay.N50);
        Assert.Equal(2, report.BestPlay.SliderBreaks);
        Assert.Equal(5.8, report.BestPlay.BaseStars);
        Assert.Equal(9.5, report.BestPlay.BaseAr);
        Assert.Equal(2020, report.BestPlay.BeatmapId);
        Assert.Equal(200, report.BestPlay.BeatmapSetId);
        Assert.Equal("HD,DA,BPM", report.BestPlay.ModsKey);
        Assert.Equal(180, report.BestPlay.Bpm);
        Assert.False(report.BestPlay.UsedBpmAdjust);
        Assert.Collection(
            report.MostUsedModCombinations,
            combination =>
            {
                Assert.Equal("HD,DA", combination.ModsKey);
                Assert.Equal(1, combination.Plays);
            });
        Assert.Equal(99, report.Account!.PlayerId);
        Assert.Equal(10, report.Account.OldPlayCount);
        Assert.Equal(12, report.Account.NewPlayCount);
        Assert.Equal(500, report.Account.OldGlobalRank);
        Assert.Equal(480, report.Account.NewGlobalRank);
        Assert.Equal(30, report.Account.OldCountryRank);
        Assert.Equal(27, report.Account.NewCountryRank);
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
        Assert.Equal(25.36, summary.Daily[0].PpChange!.Value, precision: 2);
        Assert.Equal(612, summary.Daily[0].RankChange);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { }
    }
}
