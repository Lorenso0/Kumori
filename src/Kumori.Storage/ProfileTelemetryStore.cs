using System.Text.Json;
using Kumori.Tracking;
using Microsoft.Data.Sqlite;

namespace Kumori.Storage;

/// <summary>
/// Persists account telemetry emitted by tosu. Every record is scoped to the
/// logged-in osu! player ID, so account switches never share a baseline.
/// </summary>
public sealed class ProfileTelemetryStore : IProfileTelemetrySink
{
    private readonly SqliteConnectionFactory _factory;
    private readonly object _gate = new();
    private readonly Dictionary<long, ProfileReading> _latest = new();
    private readonly Dictionary<long, ProfileReading> _attemptBaselines = new();
    private readonly Dictionary<long, ProfileReading> _pendingResults = new();

    /// <summary>Raised after profile data or an account-change delta is persisted.</summary>
    public event Action? ProfileUpdated;

    public ProfileTelemetryStore(SqliteConnectionFactory factory)
    {
        _factory = factory;
    }

    public void Ingest(TosuSnapshot snapshot)
    {
        if (snapshot.Profile is not { } profile || profile.Id <= 0 || string.IsNullOrWhiteSpace(profile.Name)) return;

        var current = ProfileReading.From(profile, snapshot.WallTime);
        var updated = false;
        lock (_gate)
        {
            if (!_latest.TryGetValue(current.PlayerId, out var previous))
            {
                previous = ReadLatest(current.PlayerId);
            }

            if (previous is not null && previous.Fingerprint == current.Fingerprint)
            {
                _latest[current.PlayerId] = previous;
                return;
            }

            InsertSnapshot(current);
            _latest[current.PlayerId] = current;
            updated = true;

            // A profile update is attributed to the oldest completed attempt
            // awaiting an update for this same account. Never cross accounts.
            var pending = _pendingResults
                .OrderBy(pair => pair.Key)
                .FirstOrDefault(pair => pair.Value.PlayerId == current.PlayerId);
            if (pending.Value is not null)
            {
                InsertAccountChange(pending.Key, pending.Value, current);
                _pendingResults.Remove(pending.Key);
            }
        }
        if (updated) ProfileUpdated?.Invoke();
    }

    public void BeginAttempt(long attemptId)
    {
        lock (_gate)
        {
            var current = _latest.Values.OrderByDescending(value => value.CapturedAt).FirstOrDefault();
            if (current is not null) _attemptBaselines[attemptId] = current;
        }
    }

    public void CompleteAttempt(long attemptId, string outcome)
    {
        lock (_gate)
        {
            if (!_attemptBaselines.Remove(attemptId, out var baseline) ||
                !outcome.Equals("completed", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // One update cannot be reliably assigned to multiple rapid results.
            // Preserve the first pending result until its profile update arrives.
            if (!_pendingResults.Values.Any(value => value.PlayerId == baseline.PlayerId))
            {
                _pendingResults[attemptId] = baseline;
            }
        }
    }

    public void DiscardAttempt(long attemptId)
    {
        lock (_gate)
        {
            _attemptBaselines.Remove(attemptId);
            _pendingResults.Remove(attemptId);
        }
    }

    private ProfileReading? ReadLatest(long playerId)
    {
        if (!_factory.DatabaseExists) return null;
        using var con = _factory.Open();
        EnsureSchema(con);
        using var command = con.CreateCommand();
        command.CommandText = """
            SELECT captured_at, player_id, player_name, total_pp, global_rank,
                   accuracy, play_count, level, ranked_score, country_code, fingerprint
            FROM profile_snapshots
            WHERE player_id = @player_id
            ORDER BY id DESC
            LIMIT 1
            """;
        command.Parameters.AddWithValue("@player_id", playerId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ProfileReading.Read(reader) : null;
    }

    private void InsertSnapshot(ProfileReading reading)
    {
        using var con = _factory.Open();
        EnsureSchema(con);
        using var command = con.CreateCommand();
        command.CommandText = """
            INSERT INTO profile_snapshots(
                captured_at, player_id, player_name, total_pp, global_rank,
                accuracy, play_count, level, ranked_score, country_code, fingerprint)
            VALUES(@captured_at, @player_id, @player_name, @total_pp, @global_rank,
                   @accuracy, @play_count, @level, @ranked_score, @country_code, @fingerprint)
            """;
        AddSnapshotParameters(command, reading);
        command.ExecuteNonQuery();
    }

    private void InsertAccountChange(long attemptId, ProfileReading oldReading, ProfileReading newReading)
    {
        using var con = _factory.Open();
        EnsureSchema(con);
        using var command = con.CreateCommand();
        command.CommandText = """
            INSERT OR REPLACE INTO attempt_profile_changes(
                attempt_id, captured_at, old_total_pp, new_total_pp,
                old_global_rank, new_global_rank, old_accuracy, new_accuracy,
                old_play_count, new_play_count)
            VALUES(@attempt_id, @captured_at, @old_total_pp, @new_total_pp,
                   @old_global_rank, @new_global_rank, @old_accuracy, @new_accuracy,
                   @old_play_count, @new_play_count)
            """;
        command.Parameters.AddWithValue("@attempt_id", attemptId);
        command.Parameters.AddWithValue("@captured_at", newReading.CapturedAt);
        command.Parameters.AddWithValue("@old_total_pp", (object?)oldReading.TotalPp ?? DBNull.Value);
        command.Parameters.AddWithValue("@new_total_pp", (object?)newReading.TotalPp ?? DBNull.Value);
        command.Parameters.AddWithValue("@old_global_rank", (object?)oldReading.GlobalRank ?? DBNull.Value);
        command.Parameters.AddWithValue("@new_global_rank", (object?)newReading.GlobalRank ?? DBNull.Value);
        command.Parameters.AddWithValue("@old_accuracy", (object?)oldReading.Accuracy ?? DBNull.Value);
        command.Parameters.AddWithValue("@new_accuracy", (object?)newReading.Accuracy ?? DBNull.Value);
        command.Parameters.AddWithValue("@old_play_count", (object?)oldReading.PlayCount ?? DBNull.Value);
        command.Parameters.AddWithValue("@new_play_count", (object?)newReading.PlayCount ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static void EnsureSchema(SqliteConnection con)
    {
        using var command = con.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS profile_snapshots(
                id INTEGER PRIMARY KEY,
                captured_at TEXT NOT NULL,
                session_id INTEGER,
                player_id INTEGER,
                player_name TEXT,
                total_pp REAL,
                global_rank INTEGER,
                accuracy REAL,
                play_count INTEGER,
                level REAL,
                ranked_score INTEGER,
                country_code TEXT,
                fingerprint TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_profile_snapshots_player_time
                ON profile_snapshots(player_id, captured_at DESC);
            CREATE TABLE IF NOT EXISTS attempt_profile_changes(
                attempt_id INTEGER PRIMARY KEY REFERENCES attempts(id) ON DELETE CASCADE,
                captured_at TEXT NOT NULL,
                old_total_pp REAL,
                new_total_pp REAL,
                old_global_rank INTEGER,
                new_global_rank INTEGER,
                old_accuracy REAL,
                new_accuracy REAL,
                old_play_count INTEGER,
                new_play_count INTEGER
            );
            """;
        command.ExecuteNonQuery();
    }

    private static void AddSnapshotParameters(SqliteCommand command, ProfileReading reading)
    {
        command.Parameters.AddWithValue("@captured_at", reading.CapturedAt);
        command.Parameters.AddWithValue("@player_id", reading.PlayerId);
        command.Parameters.AddWithValue("@player_name", reading.PlayerName);
        command.Parameters.AddWithValue("@total_pp", (object?)reading.TotalPp ?? DBNull.Value);
        command.Parameters.AddWithValue("@global_rank", (object?)reading.GlobalRank ?? DBNull.Value);
        command.Parameters.AddWithValue("@accuracy", (object?)reading.Accuracy ?? DBNull.Value);
        command.Parameters.AddWithValue("@play_count", (object?)reading.PlayCount ?? DBNull.Value);
        command.Parameters.AddWithValue("@level", (object?)reading.Level ?? DBNull.Value);
        command.Parameters.AddWithValue("@ranked_score", (object?)reading.RankedScore ?? DBNull.Value);
        command.Parameters.AddWithValue("@country_code", (object?)reading.CountryCode ?? DBNull.Value);
        command.Parameters.AddWithValue("@fingerprint", reading.Fingerprint);
    }

    private sealed record ProfileReading(
        string CapturedAt, long PlayerId, string PlayerName, double? TotalPp,
        long? GlobalRank, double? Accuracy, long? PlayCount, double? Level,
        long? RankedScore, string? CountryCode, string Fingerprint)
    {
        public static ProfileReading From(TosuProfile profile, double unixSeconds)
        {
            var capturedAt = DateTimeOffset.FromUnixTimeMilliseconds((long)(unixSeconds * 1000)).ToString("O");
            var fingerprint = JsonSerializer.Serialize(new
            {
                profile.Id,
                profile.Name,
                profile.TotalPp,
                profile.GlobalRank,
                profile.Accuracy,
                profile.PlayCount,
                profile.Level,
                profile.RankedScore,
                profile.CountryCode,
            });
            return new ProfileReading(capturedAt, profile.Id, profile.Name, profile.TotalPp,
                profile.GlobalRank, profile.Accuracy, profile.PlayCount, profile.Level,
                profile.RankedScore, profile.CountryCode, fingerprint);
        }

        public static ProfileReading Read(SqliteDataReader reader) => new(
            reader.GetString(0),
            reader.GetInt64(1),
            reader.IsDBNull(2) ? "" : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetDouble(3),
            reader.IsDBNull(4) ? null : reader.GetInt64(4),
            reader.IsDBNull(5) ? null : reader.GetDouble(5),
            reader.IsDBNull(6) ? null : reader.GetInt64(6),
            reader.IsDBNull(7) ? null : reader.GetDouble(7),
            reader.IsDBNull(8) ? null : reader.GetInt64(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.GetString(10));
    }
}

/// <summary>Attaches a profile baseline to the exact persisted attempt ID.</summary>
public sealed class ProfileAwareAttemptSink : IAttemptSink
{
    private readonly IAttemptSink _inner;
    private readonly ProfileTelemetryStore _profiles;
    private readonly Func<long?> _currentAttemptId;

    public ProfileAwareAttemptSink(IAttemptSink inner, ProfileTelemetryStore profiles, Func<long?> currentAttemptId)
    {
        _inner = inner;
        _profiles = profiles;
        _currentAttemptId = currentAttemptId;
    }

    public void StartAttempt(AttemptStart start)
    {
        _inner.StartAttempt(start);
        if (_currentAttemptId() is { } attemptId) _profiles.BeginAttempt(attemptId);
    }

    public void Checkpoint(AttemptCheckpoint checkpoint) => _inner.Checkpoint(checkpoint);
    public void DiscardIfEmpty(AttemptDiscard discard)
    {
        var attemptId = _currentAttemptId();
        _inner.DiscardIfEmpty(discard);
        if (attemptId is { } id) _profiles.DiscardAttempt(id);
    }

    public void Finalize(AttemptFinalization finalization)
    {
        var attemptId = _currentAttemptId();
        _inner.Finalize(finalization);
        if (attemptId is { } id) _profiles.CompleteAttempt(id, finalization.Outcome);
    }
}
