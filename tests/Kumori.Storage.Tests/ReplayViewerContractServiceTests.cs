using System.Text.Json;
using Kumori.Core.Models;
using Kumori.Core.Settings;
using Kumori.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kumori.Storage.Tests;

public class ReplayViewerContractServiceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _beatmapPath;
    private readonly string _contractDirectory;

    public ReplayViewerContractServiceTests()
    {
        var root = Path.Combine(Path.GetTempPath(), $"kumori-viewer-contract-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        _dbPath = Path.Combine(root, "tracking.sqlite3");
        _beatmapPath = Path.Combine(root, "map.osu");
        _contractDirectory = Path.Combine(root, "runtime", "viewer-contracts");
        File.WriteAllText(_beatmapPath, "osu file format v14\n");
        SeedDatabase();
    }

    [Fact]
    public void WriteContract_ProducesViewerPayload()
    {
        var service = CreateService();

        var path = service.WriteContract(1, _beatmapPath);

        Assert.StartsWith(_contractDirectory, path, StringComparison.OrdinalIgnoreCase);
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("contract_version").GetInt32());
        Assert.Equal(_beatmapPath, root.GetProperty("beatmap_path").GetString());
        Assert.Equal("Song", root.GetProperty("attempt").GetProperty("title").GetString());
        Assert.Equal("live", root.GetProperty("attempt").GetProperty("movement_source").GetString());
        Assert.Equal("completed", root.GetProperty("attempt").GetProperty("outcome").GetString());
        Assert.Equal(1, root.GetProperty("attempt").GetProperty("progress").GetDouble());
        Assert.Equal(2, root.GetProperty("samples").GetArrayLength());
        Assert.Equal(2, root.GetProperty("judgement_events").GetArrayLength());
        Assert.Equal(500, root.GetProperty("final_hits").GetProperty("n300").GetInt32());
        Assert.Equal(0.8, root.GetProperty("settings").GetProperty("osu_replay_master_volume").GetDouble());
    }

    [Fact]
    public void WriteContract_PreservesCustomRateAdjustSpeed()
    {
        using var con = Open();
        using var tx = con.BeginTransaction();
        Execute(con, tx, "UPDATE attempts SET mods_key = 'DT' WHERE id = 1");
        Execute(con, tx, "DELETE FROM attempt_mods WHERE attempt_id = 1");
        Execute(con, tx,
            "INSERT INTO attempt_mods(attempt_id, position, acronym, settings_json) VALUES(1, 0, 'DT', '{\"speed_change\":2}')");
        tx.Commit();

        var path = CreateService().WriteContract(1, _beatmapPath);

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var attempt = doc.RootElement.GetProperty("attempt");
        Assert.Equal(2, attempt.GetProperty("clock_rate").GetDouble());
        Assert.Equal(2, attempt.GetProperty("mods")[0].GetProperty("settings").GetProperty("speed_change").GetDouble());
    }

    [Fact]
    public void WriteContract_IncludesRecentSameMapAttempts()
    {
        using (var con = Open())
        {
            using var cmd = con.CreateCommand();
            cmd.CommandText = """
                INSERT INTO attempts(id, session_id, beatmap_id, started_at, outcome,
                    accuracy, n100, n50, misses, slider_breaks, mods_key)
                VALUES (0, 1, 1, '2026-07-07T09:30:00', 'quit',
                    96.0, 20, 4, 3, 1, 'HD');
                INSERT INTO attempts(id, session_id, beatmap_id, started_at, outcome,
                    accuracy, n100, n50, misses, slider_breaks, mods_key)
                VALUES (-1, 1, 1, '2026-07-07T09:00:00', 'completed',
                    95.5, 24, 5, 4, 2, 'HD');
                INSERT INTO attempt_timing VALUES (0, @blob, 1, 0, 1, 7.0, 7.0, 0);
                INSERT INTO attempt_timing VALUES (-1, @blob, 1, 0, 1, 8.5, 8.5, 0);
                """;
            cmd.Parameters.AddWithValue("@blob", BlobCodec.EncodeOffsets([8.5]));
            cmd.ExecuteNonQuery();
        }

        var path = CreateService().WriteContract(1, _beatmapPath);

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var recent = doc.RootElement.GetProperty("recent_attempts");
        Assert.Equal(2, recent.GetArrayLength());
        Assert.Equal(-1, recent[0].GetProperty("id").GetInt64());
        Assert.Equal(95.5, recent[0].GetProperty("accuracy").GetDouble());
        Assert.Equal(8.5, recent[0].GetProperty("mean_offset").GetDouble());
    }

    [Fact]
    public void WriteContract_RequiresMovementSamples()
    {
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "DELETE FROM attempt_movement_chunks";
        cmd.ExecuteNonQuery();

        Assert.Throws<InvalidOperationException>(() => CreateService().WriteContract(1, _beatmapPath));
    }

    [Fact]
    public void ResolveViewerExecutable_FindsDistViewerFromAppDebugOutput()
    {
        var root = Path.Combine(Path.GetTempPath(), $"kumori-viewer-resolve-{Guid.NewGuid():N}");
        var appBase = Path.Combine(root, "src", "Kumori.App", "bin", "Debug", "net8.0-windows");
        var viewer = Path.Combine(root, "dist", "app", "Kumori.ReplayViewer", "Kumori.ReplayViewer.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(viewer)!);
        Directory.CreateDirectory(appBase);
        File.WriteAllText(viewer, "");

        try
        {
            Assert.Equal(viewer, ReplayViewerContractService.ResolveViewerExecutable(appBase));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private ReplayViewerContractService CreateService()
    {
        var factory = new SqliteConnectionFactory(_dbPath, readOnly: false);
        return new ReplayViewerContractService(
            new AttemptDetailsRepository(factory),
            new MovementRepository(factory),
            new KumoriSettings(),
            _contractDirectory);
    }

    private void SeedDatabase()
    {
        using var con = Open();
        using var cmd = con.CreateCommand();
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
            CREATE TABLE attempt_movement(
                attempt_id INTEGER PRIMARY KEY,
                source TEXT NOT NULL DEFAULT 'live',
                sample_rate REAL NOT NULL DEFAULT 0,
                sample_count INTEGER NOT NULL DEFAULT 0,
                dropped_samples INTEGER NOT NULL DEFAULT 0,
                replay_status TEXT NOT NULL DEFAULT 'not_checked',
                calibration_json TEXT NOT NULL DEFAULT '{}',
                captured_at TEXT NOT NULL);
            CREATE TABLE attempt_movement_chunks(
                attempt_id INTEGER NOT NULL,
                position INTEGER NOT NULL,
                first_map_time_ms INTEGER NOT NULL,
                last_map_time_ms INTEGER NOT NULL,
                sample_count INTEGER NOT NULL,
                payload_zlib BLOB NOT NULL);

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
            INSERT INTO attempt_events(attempt_id, captured_at, map_time_ms, event_type, value, data_json)
                VALUES (1, '2026-07-07T10:06:00', 5000, 'miss', 1, '{}'),
                       (1, '2026-07-07T10:06:30', 15000, 'hit_100', 5, '{"delta": 2}');
            INSERT INTO attempt_input_summary(attempt_id) VALUES (1);
            INSERT INTO attempt_movement VALUES (1, 'live', 1000, 2, 0, 'not_checked', '{}', '2026-07-07T10:06:00');
            """;
        cmd.ExecuteNonQuery();

        using var timing = con.CreateCommand();
        timing.CommandText = "INSERT INTO attempt_timing VALUES (1, @blob, 0, 0, 0, 0, 0, 0)";
        timing.Parameters.AddWithValue("@blob", BlobCodec.EncodeOffsets(Array.Empty<double>()));
        timing.ExecuteNonQuery();

        using var movement = con.CreateCommand();
        movement.CommandText = "INSERT INTO attempt_movement_chunks VALUES (1, 0, 0, 1000, 2, @blob)";
        movement.Parameters.AddWithValue("@blob", MovementRepository.EncodeSamples([
            new MovementSample { MapTimeMs = 0, MonotonicMs = 0, X = 256, Y = 192, RawX = 10, RawY = 20 },
            new MovementSample { MapTimeMs = 1000, MonotonicMs = 1000, X = 260, Y = 196, RawX = 11, RawY = 21, Buttons = 1 },
        ]));
        movement.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var con = new SqliteConnection($"Data Source={_dbPath}");
        con.Open();
        return con;
    }

    private static void Execute(SqliteConnection con, SqliteTransaction tx, string sql)
    {
        using var cmd = con.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(Path.GetDirectoryName(_dbPath)!, recursive: true); } catch { }
    }
}
