using Kumori.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kumori.Storage.Tests;

/// <summary>
/// Builds a temporary SQLite database with the Python tracker's schema subset
/// (sessions/beatmaps/attempts from the legacy tracker) and verifies
/// the read-only repository against it.
/// </summary>
public class AttemptRepositoryTests : IDisposable
{
    private readonly string _dbPath;

    public AttemptRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"kumori-test-{Guid.NewGuid():N}.sqlite3");
        using var con = new SqliteConnection($"Data Source={_dbPath}");
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE sessions(
                id INTEGER PRIMARY KEY,
                started_at TEXT NOT NULL,
                ended_at TEXT
            );
            CREATE TABLE beatmaps(
                id INTEGER PRIMARY KEY,
                identity TEXT NOT NULL UNIQUE,
                artist TEXT, title TEXT, difficulty TEXT, stars REAL
            );
            CREATE TABLE attempts(
                id INTEGER PRIMARY KEY,
                session_id INTEGER NOT NULL,
                beatmap_id INTEGER NOT NULL,
                started_at TEXT NOT NULL,
                ended_at TEXT,
                outcome TEXT NOT NULL DEFAULT 'active',
                progress REAL NOT NULL DEFAULT 0,
                score INTEGER NOT NULL DEFAULT 0,
                accuracy REAL NOT NULL DEFAULT 0,
                grade TEXT,
                pp REAL NOT NULL DEFAULT 0,
                combo INTEGER NOT NULL DEFAULT 0,
                misses INTEGER NOT NULL DEFAULT 0,
                mods_key TEXT NOT NULL DEFAULT 'NM'
            );
            INSERT INTO sessions(id, started_at) VALUES (1, '2026-07-07T10:00:00');
            INSERT INTO beatmaps(id, identity, artist, title, difficulty, stars)
                VALUES (1, 'a|b|c', 'Artist', 'Song', 'Insane', 5.25);
            """;
        cmd.ExecuteNonQuery();

        using var insert = con.CreateCommand();
        insert.CommandText = """
            INSERT INTO attempts(id, session_id, beatmap_id, started_at, outcome,
                                 accuracy, score, grade, pp, combo, misses, mods_key, progress)
            VALUES (@id, 1, 1, @startedAt, 'completed', 98.5, 1000000, 'S', 123.4, 500, 0, 'HDDT', 1.0)
            """;
        var idParam = insert.Parameters.Add("@id", SqliteType.Integer);
        var startedParam = insert.Parameters.Add("@startedAt", SqliteType.Text);
        for (var i = 1; i <= 250; i++)
        {
            idParam.Value = i;
            startedParam.Value = $"2026-07-07T10:{i % 60:00}:00";
            insert.ExecuteNonQuery();
        }
    }

    private AttemptRepository CreateRepository() =>
        new(new SqliteConnectionFactory(_dbPath, readOnly: true));

    [Fact]
    public void GetRecentAttempts_ReturnsNewestFirst()
    {
        var page = CreateRepository().GetRecentAttempts(null, 100);
        Assert.Equal(100, page.Count);
        Assert.Equal(250, page[0].Id);
        Assert.Equal(151, page[^1].Id);
        Assert.Equal("Artist", page[0].Artist);
        Assert.Equal("HDDT", page[0].ModsKey);
        Assert.Equal(5.25, page[0].Stars);
    }

    [Fact]
    public void GetRecentAttempts_KeysetPagesWithoutOverlap()
    {
        var repo = CreateRepository();
        var first = repo.GetRecentAttempts(null, 100);
        var second = repo.GetRecentAttempts(first[^1].Id, 100);
        var third = repo.GetRecentAttempts(second[^1].Id, 100);

        Assert.Equal(100, second.Count);
        Assert.Equal(50, third.Count);
        var all = first.Concat(second).Concat(third).Select(a => a.Id).ToList();
        Assert.Equal(250, all.Distinct().Count());
    }

    [Fact]
    public void GetAttemptsForSession_ReturnsEveryPlayInTheSession()
    {
        var plays = CreateRepository().GetAttemptsForSession(1);
        Assert.Equal(250, plays.Count);
        Assert.All(plays, play => Assert.Equal(1, play.SessionId));
    }

    [Fact]
    public void MissingDatabase_ReturnsEmptyInsteadOfThrowing()
    {
        var repo = new AttemptRepository(
            new SqliteConnectionFactory(Path.Combine(Path.GetTempPath(), "does-not-exist.sqlite3")));
        Assert.Empty(repo.GetRecentAttempts());
        Assert.Equal(0, repo.CountAttempts());
    }

    [Fact]
    public void GetRecentAttempts_SearchFiltersByTitleCaseInsensitive()
    {
        var repo = CreateRepository();
        Assert.Equal(100, repo.GetRecentAttempts(null, 100, "song").Count);
        Assert.Equal(100, repo.GetRecentAttempts(null, 100, "HDDT").Count);
        Assert.Empty(repo.GetRecentAttempts(null, 100, "nonexistent"));
    }

    [Fact]
    public void GetRecentAttempts_SearchEscapesLikeWildcards()
    {
        // "%" alone must not match everything once escaped.
        Assert.Empty(CreateRepository().GetRecentAttempts(null, 100, "100%"));
    }

    [Fact]
    public void CountAttempts_CountsAll()
    {
        Assert.Equal(250, CreateRepository().CountAttempts());
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { /* best effort */ }
    }
}
