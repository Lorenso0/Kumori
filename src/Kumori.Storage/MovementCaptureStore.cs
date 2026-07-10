using System.Text.Json;
using Kumori.Core.Models;
using Microsoft.Data.Sqlite;
using Serilog;

namespace Kumori.Storage;

public sealed class MovementCaptureStore
{
    private const int key1_button = 0x10;
    private const int key2_button = 0x20;

    private readonly SqliteConnectionFactory _factory;
    private readonly List<double> _pressTimes = new();
    private long? _attemptId;
    private int _position;
    private int _sampleCount;
    private int _key1Presses;
    private int _key2Presses;
    private int _alternations;
    private int _sameKeyRepeats;
    private int _simultaneousPresses;
    private int _lastButtons;
    private int _lastPressedKey;
    private double? _firstMonotonic;
    private double? _lastMonotonic;
    private double? _key1DownAt;
    private double? _key2DownAt;
    private double _key1HoldMs;
    private double _key2HoldMs;

    public MovementCaptureStore(SqliteConnectionFactory factory)
    {
        _factory = factory;
        EnsureSchema();
    }

    public void Start(long attemptId)
    {
        _attemptId = attemptId;
        _position = 0;
        _sampleCount = 0;
        _key1Presses = 0;
        _key2Presses = 0;
        _alternations = 0;
        _sameKeyRepeats = 0;
        _simultaneousPresses = 0;
        _lastButtons = 0;
        _lastPressedKey = 0;
        _firstMonotonic = null;
        _lastMonotonic = null;
        _key1DownAt = null;
        _key2DownAt = null;
        _key1HoldMs = 0;
        _key2HoldMs = 0;
        _pressTimes.Clear();

        using var con = _factory.Open();
        using var tx = con.BeginTransaction();
        Execute(con, tx, "DELETE FROM attempt_movement_chunks WHERE attempt_id = @id", ("@id", attemptId));
        Execute(con, tx, "DELETE FROM attempt_movement WHERE attempt_id = @id", ("@id", attemptId));
        Execute(con, tx, "DELETE FROM attempt_input_summary WHERE attempt_id = @id", ("@id", attemptId));
        tx.Commit();
    }

    public void AddSamples(IReadOnlyList<MovementSample> samples)
    {
        if (_attemptId is not { } attemptId || samples.Count == 0)
        {
            return;
        }

        foreach (var sample in samples)
        {
            ObserveKeys(sample);
        }

        using var con = _factory.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            INSERT INTO attempt_movement_chunks(attempt_id, position, first_map_time_ms,
                                                last_map_time_ms, sample_count, payload_zlib)
            VALUES(@attempt_id, @position, @first, @last, @count, @payload)
            """;
        cmd.Parameters.AddWithValue("@attempt_id", attemptId);
        cmd.Parameters.AddWithValue("@position", _position++);
        cmd.Parameters.AddWithValue("@first", (long)samples.First().MapTimeMs);
        cmd.Parameters.AddWithValue("@last", (long)samples.Last().MapTimeMs);
        cmd.Parameters.AddWithValue("@count", samples.Count);
        cmd.Parameters.AddWithValue("@payload", MovementRepository.EncodeSamples(samples));
        cmd.ExecuteNonQuery();
        _sampleCount += samples.Count;
    }

    public void Complete(int droppedSamples, string? source, string? calibrationJson)
    {
        if (_attemptId is not { } attemptId)
        {
            return;
        }

        var durationMs = Math.Max(0, (_lastMonotonic ?? 0) - (_firstMonotonic ?? 0));
        if (_key1DownAt is { } zDown)
        {
            _key1HoldMs += Math.Max(0, (_lastMonotonic ?? zDown) - zDown);
            _key1DownAt = null;
        }
        if (_key2DownAt is { } xDown)
        {
            _key2HoldMs += Math.Max(0, (_lastMonotonic ?? xDown) - xDown);
            _key2DownAt = null;
        }

        var totalPresses = _key1Presses + _key2Presses;
        var sampleRate = durationMs > 0 ? _sampleCount * 1000.0 / durationMs : 0;
        var averageKps = durationMs > 0 ? totalPresses * 1000.0 / durationMs : 0;
        var peakKps = PeakKps();

        using var con = _factory.Open();
        using var tx = con.BeginTransaction();
        using var movement = con.CreateCommand();
        movement.Transaction = tx;
        movement.CommandText = """
            INSERT INTO attempt_movement(attempt_id, source, sample_rate, sample_count,
                                         dropped_samples, replay_status, calibration_json, captured_at)
            VALUES(@attempt_id, @source, @sample_rate, @sample_count,
                   @dropped, 'not_checked', @calibration, @captured_at)
            ON CONFLICT(attempt_id) DO UPDATE SET
                source = excluded.source,
                sample_rate = excluded.sample_rate,
                sample_count = excluded.sample_count,
                dropped_samples = excluded.dropped_samples,
                replay_status = excluded.replay_status,
                calibration_json = excluded.calibration_json,
                captured_at = excluded.captured_at
            """;
        movement.Parameters.AddWithValue("@attempt_id", attemptId);
        movement.Parameters.AddWithValue("@source", string.IsNullOrWhiteSpace(source) ? "live" : source);
        movement.Parameters.AddWithValue("@sample_rate", sampleRate);
        movement.Parameters.AddWithValue("@sample_count", _sampleCount);
        movement.Parameters.AddWithValue("@dropped", droppedSamples);
        movement.Parameters.AddWithValue("@calibration", string.IsNullOrWhiteSpace(calibrationJson) ? "{}" : calibrationJson);
        movement.Parameters.AddWithValue("@captured_at", DateTimeOffset.Now.ToString("O"));
        movement.ExecuteNonQuery();

        using var input = con.CreateCommand();
        input.Transaction = tx;
        input.CommandText = """
            INSERT INTO attempt_input_summary(attempt_id, key1_presses, key2_presses,
                                              alternations, same_key_repeats,
                                              simultaneous_presses, key1_hold_ms,
                                              key2_hold_ms, peak_kps, average_kps)
            VALUES(@attempt_id, @key1, @key2, @alternations, @same,
                   @simultaneous, @key1_hold, @key2_hold, @peak, @average)
            ON CONFLICT(attempt_id) DO UPDATE SET
                key1_presses = excluded.key1_presses,
                key2_presses = excluded.key2_presses,
                alternations = excluded.alternations,
                same_key_repeats = excluded.same_key_repeats,
                simultaneous_presses = excluded.simultaneous_presses,
                key1_hold_ms = excluded.key1_hold_ms,
                key2_hold_ms = excluded.key2_hold_ms,
                peak_kps = excluded.peak_kps,
                average_kps = excluded.average_kps
            """;
        input.Parameters.AddWithValue("@attempt_id", attemptId);
        input.Parameters.AddWithValue("@key1", _key1Presses);
        input.Parameters.AddWithValue("@key2", _key2Presses);
        input.Parameters.AddWithValue("@alternations", _alternations);
        input.Parameters.AddWithValue("@same", _sameKeyRepeats);
        input.Parameters.AddWithValue("@simultaneous", _simultaneousPresses);
        input.Parameters.AddWithValue("@key1_hold", _key1HoldMs);
        input.Parameters.AddWithValue("@key2_hold", _key2HoldMs);
        input.Parameters.AddWithValue("@peak", peakKps);
        input.Parameters.AddWithValue("@average", averageKps);
        input.ExecuteNonQuery();

        Execute(con, tx,
            "UPDATE attempts SET z_count = @z, x_count = @x WHERE id = @id",
            ("@z", _key1Presses), ("@x", _key2Presses), ("@id", attemptId));
        Execute(con, tx,
            """
            UPDATE sessions
            SET z_count = z_count + @z, x_count = x_count + @x
            WHERE id = (SELECT session_id FROM attempts WHERE id = @id)
            """,
            ("@z", _key1Presses), ("@x", _key2Presses), ("@id", attemptId));

        tx.Commit();
        Log.Debug("Stored movement capture for attempt {AttemptId}: {Samples} samples, Z {Z}, X {X}",
            attemptId, _sampleCount, _key1Presses, _key2Presses);
        _attemptId = null;
    }

    private void ObserveKeys(MovementSample sample)
    {
        var now = sample.MonotonicMs;
        _firstMonotonic ??= now;
        _lastMonotonic = now;

        var buttons = sample.Buttons;
        var pressed = buttons & ~_lastButtons;
        var released = _lastButtons & ~buttons;
        if ((pressed & key1_button) != 0)
        {
            CountPress(1, buttons);
            _key1DownAt = now;
        }
        if ((pressed & key2_button) != 0)
        {
            CountPress(2, buttons);
            _key2DownAt = now;
        }
        if ((released & key1_button) != 0 && _key1DownAt is { } zDown)
        {
            _key1HoldMs += Math.Max(0, now - zDown);
            _key1DownAt = null;
        }
        if ((released & key2_button) != 0 && _key2DownAt is { } xDown)
        {
            _key2HoldMs += Math.Max(0, now - xDown);
            _key2DownAt = null;
        }
        _lastButtons = buttons;
    }

    private void CountPress(int key, int buttons)
    {
        if (key == 1)
        {
            _key1Presses++;
        }
        else
        {
            _key2Presses++;
        }

        if (_lastPressedKey != 0)
        {
            if (_lastPressedKey == key)
            {
                _sameKeyRepeats++;
            }
            else
            {
                _alternations++;
            }
        }
        _lastPressedKey = key;
        if ((buttons & (key1_button | key2_button)) == (key1_button | key2_button))
        {
            _simultaneousPresses++;
        }
        if (_lastMonotonic is { } t)
        {
            _pressTimes.Add(t);
        }
    }

    private int PeakKps()
    {
        var peak = 0;
        var left = 0;
        for (var right = 0; right < _pressTimes.Count; right++)
        {
            while (_pressTimes[right] - _pressTimes[left] > 1000)
            {
                left++;
            }
            peak = Math.Max(peak, right - left + 1);
        }
        return peak;
    }

    private void EnsureSchema()
    {
        using var con = _factory.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = MovementSchema.Sql;
        cmd.ExecuteNonQuery();
    }

    private static void Execute(
        SqliteConnection con,
        SqliteTransaction tx,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        using var cmd = con.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }
        cmd.ExecuteNonQuery();
    }
}

internal static class MovementSchema
{
    public const string Sql = """
        CREATE TABLE IF NOT EXISTS attempt_movement(
            attempt_id INTEGER PRIMARY KEY REFERENCES attempts(id) ON DELETE CASCADE,
            source TEXT NOT NULL DEFAULT 'live',
            sample_rate REAL NOT NULL DEFAULT 0,
            sample_count INTEGER NOT NULL DEFAULT 0,
            dropped_samples INTEGER NOT NULL DEFAULT 0,
            replay_status TEXT NOT NULL DEFAULT 'not_checked',
            calibration_json TEXT NOT NULL DEFAULT '{}',
            captured_at TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS attempt_movement_chunks(
            attempt_id INTEGER NOT NULL REFERENCES attempts(id) ON DELETE CASCADE,
            position INTEGER NOT NULL,
            first_map_time_ms INTEGER NOT NULL,
            last_map_time_ms INTEGER NOT NULL,
            sample_count INTEGER NOT NULL,
            payload_zlib BLOB NOT NULL,
            PRIMARY KEY(attempt_id, position)
        );
        CREATE INDEX IF NOT EXISTS idx_attempt_movement_chunks
            ON attempt_movement_chunks(attempt_id, position);
        """;
}
