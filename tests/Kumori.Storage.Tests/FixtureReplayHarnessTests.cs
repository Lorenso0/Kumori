using System.Text.Json;
using Kumori.Storage;
using Kumori.Tracking;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kumori.Storage.Tests;

public class FixtureReplayHarnessTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"kumori-replay-{Guid.NewGuid():N}.sqlite3");
    private readonly string _fixturePath = Path.Combine(Path.GetTempPath(), $"kumori-fixture-{Guid.NewGuid():N}.jsonl");

    [Fact]
    public async Task SyntheticCompletedFixture_ReplaysIntoSqliteRows()
    {
        await WriteFixtureAsync(
            Packet(0, "Play", "mapA", live: 0),
            Packet(1.2, "Play", "mapA", live: 1200, score: 10_000, n300: 80, progress: 0.4),
            Packet(3.5, "Play", "mapA", live: 3500, score: 80_000, n300: 300, miss: 1, progress: 1),
            Packet(3.7, "ResultScreen", "mapA", grade: "A", score: 80_000, n300: 300, miss: 1, progress: 1));

        await ReplayAsync();

        using var con = Open();
        Assert.Equal("completed", Scalar<string>(con, "SELECT outcome FROM attempts"));
        Assert.Equal(80_000, Scalar<long>(con, "SELECT score FROM attempts"));
        Assert.Equal(1, Scalar<long>(con, "SELECT COUNT(*) FROM attempt_timing"));
        Assert.True(Scalar<long>(con, "SELECT COUNT(*) FROM attempt_events") > 0);
        Assert.False(string.IsNullOrWhiteSpace(Scalar<string>(con, "SELECT ended_at FROM sessions")));
    }

    [Fact]
    public async Task SyntheticRetryFixture_DiscardsEmptyPulseAndKeepsRetriedRow()
    {
        await WriteFixtureAsync(
            Packet(0, "Play", "mapA", live: 0),
            Packet(0.1, "SongSelect", "mapA"),
            Packet(0.2, "Play", "mapA", live: 0),
            Packet(1.0, "Play", "mapA", live: 1000, score: 10_000, n300: 50, progress: 0.2),
            Packet(4.2, "Play", "mapA", live: 4200, score: 40_000, n300: 160, progress: 0.7),
            Packet(4.3, "SongSelect", "mapA"),
            Packet(4.5, "Play", "mapA", live: 0));

        await ReplayAsync();

        using var con = Open();
        Assert.Equal(2, Scalar<long>(con, "SELECT COUNT(*) FROM attempts"));
        Assert.Equal(1, Scalar<long>(con, "SELECT COUNT(*) FROM attempts WHERE outcome='retried'"));
        Assert.Equal(1, Scalar<long>(con, "SELECT COUNT(*) FROM attempts WHERE outcome='active'"));
        Assert.Equal(40_000, Scalar<long>(con, "SELECT score FROM attempts WHERE outcome='retried'"));
    }

    [Fact]
    public async Task SyntheticMapSwitchFixture_FinalizesAbandonedAndStartsNext()
    {
        await WriteFixtureAsync(
            Packet(0, "Play", "mapA", live: 0),
            Packet(4.0, "Play", "mapA", live: 4000, score: 20_000, n300: 100, progress: 0.5),
            Packet(4.1, "Play", "mapB", live: 0));

        await ReplayAsync();

        using var con = Open();
        Assert.Equal(2, Scalar<long>(con, "SELECT COUNT(*) FROM attempts"));
        Assert.Equal("abandoned", Scalar<string>(con, "SELECT outcome FROM attempts ORDER BY id LIMIT 1"));
        Assert.Equal("mapB", Scalar<string>(con, """
            SELECT b.identity FROM attempts a
            JOIN beatmaps b ON b.id = a.beatmap_id
            WHERE a.outcome = 'active'
            """));
    }

    private async Task ReplayAsync()
    {
        var sink = new AttemptSqliteSink(
            new SqliteConnectionFactory(_dbPath, readOnly: false),
            (_, work) => work(CancellationToken.None));
        var runner = new TrackingReplayRunner(new AttemptTracker(sink), new SessionTracker(sink));
        await runner.RunAsync(new FixturePacketSource(_fixturePath));
    }

    private async Task WriteFixtureAsync(params string[] rawPackets)
    {
        await using var stream = File.CreateText(_fixturePath);
        for (var i = 0; i < rawPackets.Length; i++)
        {
            await stream.WriteLineAsync(JsonSerializer.Serialize(new
            {
                wall = 1_788_000_000 + i,
                mono = ExtractMono(rawPackets[i]),
                raw = rawPackets[i],
            }));
        }
    }

    private static double ExtractMono(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        return doc.RootElement.GetProperty("mono").GetDouble();
    }

    private static string Packet(
        double mono,
        string state,
        string identity,
        long live = 0,
        string grade = "",
        int score = 0,
        double n300 = 0,
        double n100 = 0,
        double n50 = 0,
        double miss = 0,
        double progress = 0) =>
        JsonSerializer.Serialize(new
        {
            mono,
            state = new { name = state },
            play = new
            {
                mode = new { number = 0, name = "osu" },
                score,
                grade,
                accuracy = n300 + n100 + n50 + miss == 0
                    ? 0
                    : (300 * n300 + 100 * n100 + 50 * n50) / (300 * (n300 + n100 + n50 + miss)),
                progress,
                hits = new Dictionary<string, double>
                {
                    ["300"] = n300,
                    ["100"] = n100,
                    ["50"] = n50,
                    ["0"] = miss,
                    ["sliderBreaks"] = 0,
                },
                combo = new { max = n300 + n100 + n50 },
                pp = new { current = 42.0, maxAchievedThisPlay = 42.0 },
                healthBar = new { normal = grade == "F" ? 0 : 1 },
                mods = new[] { new { acronym = "HD", settings = new { } } },
            },
            beatmap = new
            {
                checksum = identity,
                artist = "Artist",
                title = identity == "mapA" ? "Song A" : "Song B",
                mapper = "Mapper",
                version = "Insane",
                time = new { live },
            },
        });

    private SqliteConnection Open()
    {
        var con = new SqliteConnection($"Data Source={_dbPath}");
        con.Open();
        return con;
    }

    private static T Scalar<T>(SqliteConnection con, string sql)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        return (T)Convert.ChangeType(cmd.ExecuteScalar()!, typeof(T));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { }
        try { File.Delete(_dbPath + "-wal"); } catch { }
        try { File.Delete(_dbPath + "-shm"); } catch { }
        try { File.Delete(_fixturePath); } catch { }
    }
}
