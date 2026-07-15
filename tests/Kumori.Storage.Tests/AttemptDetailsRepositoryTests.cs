using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Kumori.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kumori.Storage.Tests;

public class AttemptDetailsRepositoryTests : IDisposable
{
    private readonly string _dbPath;

    public AttemptDetailsRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"kumori-details-{Guid.NewGuid():N}.sqlite3");
        using var con = new SqliteConnection($"Data Source={_dbPath}");
        con.Open();
        using (var cmd = con.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE sessions(id INTEGER PRIMARY KEY, started_at TEXT NOT NULL);
                CREATE TABLE beatmaps(
                    id INTEGER PRIMARY KEY, identity TEXT NOT NULL UNIQUE,
                    artist TEXT, title TEXT, difficulty TEXT, stars REAL);
                CREATE TABLE attempts(
                    id INTEGER PRIMARY KEY, session_id INTEGER NOT NULL,
                    beatmap_id INTEGER NOT NULL, started_at TEXT NOT NULL,
                    ended_at TEXT, outcome TEXT NOT NULL DEFAULT 'active',
                    termination_evidence TEXT,
                    progress REAL NOT NULL DEFAULT 0,
                    duration_seconds REAL NOT NULL DEFAULT 0,
                    score INTEGER NOT NULL DEFAULT 0,
                    accuracy REAL NOT NULL DEFAULT 0, grade TEXT,
                    pp REAL NOT NULL DEFAULT 0, fc_pp REAL NOT NULL DEFAULT 0,
                    max_pp REAL NOT NULL DEFAULT 0,
                    combo INTEGER NOT NULL DEFAULT 0,
                    n300 INTEGER NOT NULL DEFAULT 0, n100 INTEGER NOT NULL DEFAULT 0,
                    n50 INTEGER NOT NULL DEFAULT 0, misses INTEGER NOT NULL DEFAULT 0,
                    geki INTEGER NOT NULL DEFAULT 0, katu INTEGER NOT NULL DEFAULT 0,
                    slider_breaks INTEGER NOT NULL DEFAULT 0,
                    unstable_rate REAL NOT NULL DEFAULT 0,
                    z_count INTEGER NOT NULL DEFAULT 0, x_count INTEGER NOT NULL DEFAULT 0,
                    key1_binding TEXT NOT NULL DEFAULT 'Z',
                    key2_binding TEXT NOT NULL DEFAULT 'X',
                    mods_key TEXT NOT NULL DEFAULT 'NM');
                CREATE TABLE attempt_mods(
                    attempt_id INTEGER NOT NULL, position INTEGER NOT NULL,
                    acronym TEXT NOT NULL, settings_json TEXT NOT NULL DEFAULT '{}');
                CREATE TABLE attempt_timing(
                    attempt_id INTEGER PRIMARY KEY, offsets_zlib BLOB NOT NULL,
                    hit_count INTEGER NOT NULL, early_count INTEGER NOT NULL,
                    late_count INTEGER NOT NULL, mean REAL NOT NULL,
                    median REAL NOT NULL, deviation REAL NOT NULL);
                CREATE TABLE attempt_events(
                    id INTEGER PRIMARY KEY, attempt_id INTEGER NOT NULL,
                    captured_at TEXT NOT NULL, map_time_ms INTEGER,
                    event_type TEXT NOT NULL, value REAL,
                    data_json TEXT NOT NULL DEFAULT '{}');
                CREATE TABLE attempt_input_summary(
                    attempt_id INTEGER PRIMARY KEY,
                    key1_presses INTEGER NOT NULL DEFAULT 0,
                    key2_presses INTEGER NOT NULL DEFAULT 0,
                    alternations INTEGER NOT NULL DEFAULT 0,
                    same_key_repeats INTEGER NOT NULL DEFAULT 0,
                    simultaneous_presses INTEGER NOT NULL DEFAULT 0,
                    key1_hold_ms REAL NOT NULL DEFAULT 0,
                    key2_hold_ms REAL NOT NULL DEFAULT 0,
                    peak_kps INTEGER NOT NULL DEFAULT 0,
                    average_kps REAL NOT NULL DEFAULT 0);

                INSERT INTO sessions VALUES (1, '2026-07-07T10:00:00');
                INSERT INTO beatmaps VALUES (1, 'x|y|z', 'Artist', 'Song', 'Extra', 6.1);
                INSERT INTO attempts(id, session_id, beatmap_id, started_at, outcome,
                    accuracy, score, grade, pp, fc_pp, max_pp, combo,
                    n300, n100, n50, misses, slider_breaks, unstable_rate,
                    duration_seconds, progress, mods_key)
                VALUES (1, 1, 1, '2026-07-07T10:05:00', 'completed',
                    97.2, 900000, 'S', 150.5, 160.0, 200.0, 450,
                    500, 20, 3, 2, 1, 95.5,
                    123.4, 1.0, 'HD');
                INSERT INTO attempt_mods VALUES (1, 0, 'HD', '{}');
                INSERT INTO attempt_events(attempt_id, captured_at, map_time_ms, event_type, value, data_json)
                    VALUES (1, '2026-07-07T10:06:00', 5000, 'miss', 1, '{}'),
                           (1, '2026-07-07T10:06:30', 15000, 'hit_100', 5, '{"delta": 2}'),
                           (1, '2026-07-07T10:07:00', 30000, 'slider_break', 1, '{}');
                INSERT INTO attempt_input_summary VALUES (1, 300, 280, 500, 40, 2, 45.5, 44.2, 14, 9.8);
                """;
            cmd.ExecuteNonQuery();
        }

        // Timing blob exactly as Python writes it: zlib.compress(json.dumps(offsets).encode())
        var offsets = new double[] { -5.5, 3.0, 12.25, -1.0 };
        using (var cmd = con.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO attempt_timing VALUES (1, @blob, 4, 2, 2, 2.19, 1.0, 6.8)
                """;
            cmd.Parameters.AddWithValue("@blob", ZlibCompress(JsonSerializer.Serialize(offsets)));
            cmd.ExecuteNonQuery();
        }
    }

    private static byte[] ZlibCompress(string json)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(Encoding.UTF8.GetBytes(json));
        }
        return output.ToArray();
    }

    private AttemptDetailsRepository CreateRepository() =>
        new(new SqliteConnectionFactory(_dbPath, readOnly: true));

    [Fact]
    public void GetDetails_LoadsFullModel()
    {
        var d = CreateRepository().GetDetails(1);

        Assert.NotNull(d);
        Assert.Equal("Song", d!.Summary.Title);
        Assert.Equal(500, d.N300);
        Assert.Equal(20, d.N100);
        Assert.Equal(3, d.N50);
        Assert.Equal(2, d.Summary.Misses);
        Assert.Equal(95.5, d.UnstableRate);
        Assert.Equal(160.0, d.FcPp);
        Assert.Single(d.Mods);
        Assert.Equal("HD", d.Mods[0].Acronym);
        Assert.Equal(3, d.Events.Count);
        Assert.Equal("miss", d.Events[0].EventType);
        Assert.NotNull(d.Input);
        Assert.Equal(14, d.Input!.PeakKps);
    }

    [Fact]
    public void GetDetails_DecodesTimingBlob()
    {
        var d = CreateRepository().GetDetails(1);
        Assert.NotNull(d?.Timing);
        Assert.Equal(new[] { -5.5, 3.0, 12.25, -1.0 }, d!.Timing!.Offsets);
        Assert.Equal(4, d.Timing.HitCount);
        Assert.Equal(2, d.Timing.EarlyCount);
    }

    [Fact]
    public void GetDetails_OversizedSqliteTimingBlobKeepsSummaryWithoutLoadingOffsets()
    {
        using (var con = new SqliteConnection($"Data Source={_dbPath}"))
        {
            con.Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = "UPDATE attempt_timing SET offsets_zlib = zeroblob(@size) WHERE attempt_id = 1";
            cmd.Parameters.AddWithValue("@size", BlobCodec.MaxCompressedBytes + 1);
            cmd.ExecuteNonQuery();
        }

        var details = CreateRepository().GetDetails(1);

        Assert.NotNull(details?.Timing);
        Assert.Empty(details!.Timing!.Offsets);
        Assert.Equal(4, details.Timing.HitCount);
        Assert.Equal(2.19, details.Timing.Mean);
    }

    [Fact]
    public void GetDetails_UnknownAttempt_ReturnsNull()
    {
        Assert.Null(CreateRepository().GetDetails(9999));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { /* best effort */ }
    }
}
