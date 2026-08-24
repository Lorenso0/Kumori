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
    private readonly object _schemaGate = new();
    private int _schemaEnsured;
    private readonly Func<string, Func<CancellationToken, Task>, Task>? _deferPersistence;
    private readonly Dictionary<long, ProfileReading> _latest = new();
    private readonly Dictionary<long, ProfileReading> _attemptBaselines = new();
    private readonly Dictionary<long, ProfileReading> _pendingResults = new();
    private readonly Dictionary<long, PendingProfileWrite> _pendingProfileWrites = new();
    private readonly HashSet<long> _scheduledProfileWrites = [];

    /// <summary>Raised after profile data or an account-change delta is persisted.</summary>
    public event Action? ProfileUpdated;

    public OsuProfileIdentity? GetCurrentIdentity()
    {
        lock (_gate)
        {
            ProfileReading? current = null;
            foreach (var candidate in _latest.Values)
            {
                if (current is null || string.CompareOrdinal(candidate.CapturedAt, current.CapturedAt) > 0)
                    current = candidate;
            }
            if (current is not null)
                return new OsuProfileIdentity(current.PlayerId, current.PlayerName);
        }

        if (!_factory.DatabaseExists) return null;
        using var con = _factory.Open();
        EnsureSchema(con, CancellationToken.None);
        using var command = con.CreateCommand();
        command.CommandText = """
            SELECT player_id, COALESCE(player_name, '')
            FROM profile_snapshots
            WHERE player_id IS NOT NULL
            ORDER BY id DESC LIMIT 1
            """;
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new OsuProfileIdentity(reader.GetInt64(0), reader.GetString(1))
            : null;
    }

    public ProfileTelemetryStore(
        SqliteConnectionFactory factory,
        Func<string, Func<CancellationToken, Task>, Task>? deferPersistence = null)
    {
        _factory = factory;
        _deferPersistence = deferPersistence;
    }

    public void Ingest(TosuSnapshot snapshot)
    {
        if (snapshot.Profile is not { } profile || profile.Id <= 0 || string.IsNullOrWhiteSpace(profile.Name)) return;

        long? schedulePlayerId = null;
        var updated = false;
        lock (_gate)
        {
            if (!_latest.TryGetValue(profile.Id, out var previous))
            {
                // Production persistence is gameplay-idle. Do not turn a first
                // profile packet (including one after a mandatory tosu restart)
                // into a synchronous SQLite read on the websocket thread.
                previous = _deferPersistence is null ? ReadLatest(profile.Id, CancellationToken.None) : null;
            }

            // tosu repeats the complete profile in every packet. Compare its
            // typed fields before allocating a timestamp, reading JSON, or
            // constructing the legacy fingerprint stored in SQLite.
            if (previous is not null && previous.Matches(profile))
            {
                _latest[profile.Id] = previous;
                if (_deferPersistence is not null
                    && _pendingProfileWrites.ContainsKey(profile.Id)
                    && _scheduledProfileWrites.Add(profile.Id))
                {
                    schedulePlayerId = profile.Id;
                }
                else
                {
                    return;
                }
            }
            var current = previous is not null && previous.Matches(profile)
                ? previous
                : ProfileReading.From(profile, snapshot.WallTime, previous?.CountryRank);

            // A profile update is attributed to the oldest completed attempt
            // awaiting the matching play-count increment for this same account.
            // This distinguishes rapid consecutive results while PP/rank settle.
            var pending = _pendingResults
                .Where(pair => pair.Value.PlayerId == current.PlayerId)
                .OrderBy(pair => ProfileReading.MatchesNextPlayCount(pair.Value, current) ? 0 : 1)
                .ThenBy(pair => pair.Key)
                .FirstOrDefault();

            (long AttemptId, ProfileReading Baseline)? pendingCandidate =
                _pendingProfileWrites.TryGetValue(current.PlayerId, out var existingWrite)
                    ? existingWrite.Pending ?? (pending.Value is null ? null : (pending.Key, pending.Value))
                    : pending.Value is null ? null : (pending.Key, pending.Value);
            // Once a newer play-count has an exact pending attempt, older no-gain
            // results can no longer receive a distinct profile update. Retire them
            // so long sessions do not retain one entry for every no-gain play.
            foreach (long obsoleteAttemptId in _pendingResults
                         .Where(pair => pair.Value.PlayerId == current.PlayerId
                                        && pair.Key != pendingCandidate?.AttemptId
                                        && ProfileReading.IsPastExpectedPlayCount(pair.Value, current))
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                _pendingResults.Remove(obsoleteAttemptId);
            }
            // osu! commonly publishes the play-count increment before weighted
            // PP and rank settle. Do not consume the attempt on that intermediate
            // packet or the score card will permanently record "No change".
            (long AttemptId, ProfileReading Baseline)? pendingResult =
                pendingCandidate is { } candidate
                && ProfileReading.HasProgressionChange(candidate.Baseline, current)
                    ? candidate
                    : null;
            if (_deferPersistence is null)
            {
                PersistUpdate(current, pendingResult, CancellationToken.None);
                updated = true;
            }
            else
            {
                _pendingProfileWrites[current.PlayerId] = new PendingProfileWrite(current, pendingResult);
                if (_scheduledProfileWrites.Add(current.PlayerId))
                    schedulePlayerId = current.PlayerId;
            }
            _latest[current.PlayerId] = current;
            if (_deferPersistence is null && pendingResult is { } completed)
            {
                _pendingResults.Remove(completed.AttemptId);
            }
        }
        if (schedulePlayerId is { } playerId)
        {
            try
            {
                _ = _deferPersistence!(
                    $"profile-telemetry-{playerId}",
                    token => PersistPendingProfileUpdates(playerId, token));
            }
            catch
            {
                lock (_gate)
                    _scheduledProfileWrites.Remove(playerId);
                throw;
            }
        }
        if (updated) ProfileUpdated?.Invoke();
    }

    /// <summary>
    /// Adds an official osu! API country-rank observation to the latest local
    /// profile reading without discarding account fields supplied by tosu.
    /// </summary>
    public bool RecordCountryRank(
        long playerId,
        long countryRank,
        string? countryCode,
        DateTimeOffset capturedAt,
        CancellationToken cancellationToken = default)
    {
        if (playerId <= 0) throw new ArgumentOutOfRangeException(nameof(playerId));
        if (countryRank <= 0) throw new ArgumentOutOfRangeException(nameof(countryRank));
        cancellationToken.ThrowIfCancellationRequested();

        ProfileReading? previous;
        lock (_gate)
            _latest.TryGetValue(playerId, out previous);
        previous ??= ReadLatest(playerId, cancellationToken);
        if (previous is null) return false;

        var normalizedCountry = countryCode?.Trim().ToUpperInvariant();
        if (normalizedCountry is not { Length: 2 })
            normalizedCountry = previous.CountryCode;
        var alreadyObservedToday = DateTimeOffset.TryParse(previous.CapturedAt, out var previousCapturedAt)
                                   && previousCapturedAt.ToLocalTime().Date
                                   == capturedAt.ToLocalTime().Date;
        if (alreadyObservedToday
            && previous.CountryRank == countryRank
            && string.Equals(previous.CountryCode, normalizedCountry, StringComparison.Ordinal))
            return false;

        var reading = previous with
        {
            CapturedAt = capturedAt.ToString("O"),
            CountryRank = countryRank,
            CountryCode = normalizedCountry,
        };
        PersistUpdate(reading, pending: null, cancellationToken);
        lock (_gate)
        {
            _latest[playerId] = reading;
            if (_pendingProfileWrites.TryGetValue(playerId, out var pending))
            {
                _pendingProfileWrites[playerId] = pending with
                {
                    Reading = pending.Reading with
                    {
                        CountryRank = countryRank,
                        CountryCode = normalizedCountry,
                    },
                };
            }
        }
        ProfileUpdated?.Invoke();
        return true;
    }

    private Task PersistPendingProfileUpdates(long playerId, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            PendingProfileWrite write;
            lock (_gate)
            {
                if (!_pendingProfileWrites.TryGetValue(playerId, out write!))
                {
                    _scheduledProfileWrites.Remove(playerId);
                    return Task.CompletedTask;
                }
            }

            try
            {
                var persisted = ReadLatest(playerId, token);
                token.ThrowIfCancellationRequested();
                if (write.Pending is not null || persisted is null || !persisted.Matches(write.Reading))
                    PersistUpdate(write.Reading, write.Pending, token);
            }
            catch
            {
                lock (_gate)
                    _scheduledProfileWrites.Remove(playerId);
                throw;
            }

            // PersistUpdate is transactional. Once it returns, acknowledge the
            // committed write even if gameplay cancellation raced the commit;
            // retrying here would duplicate the snapshot row.
            var hasNewerWrite = false;
            lock (_gate)
            {
                if (_pendingProfileWrites.TryGetValue(playerId, out var current)
                    && ReferenceEquals(current, write))
                {
                    _pendingProfileWrites.Remove(playerId);
                }
                if (write.Pending is { } completed)
                {
                    _pendingResults.Remove(completed.AttemptId);
                    if (_pendingProfileWrites.TryGetValue(playerId, out var newer)
                        && newer.Pending?.AttemptId == completed.AttemptId)
                    {
                        _pendingProfileWrites[playerId] = newer with { Pending = null };
                    }
                }
                hasNewerWrite = _pendingProfileWrites.ContainsKey(playerId);
                if (!hasNewerWrite)
                    _scheduledProfileWrites.Remove(playerId);
            }
            ProfileUpdated?.Invoke();
            if (!hasNewerWrite)
                return Task.CompletedTask;
        }
    }

    public void BeginAttempt(long attemptId)
    {
        lock (_gate)
        {
            ProfileReading? current = null;
            foreach (var candidate in _latest.Values)
            {
                if (current is null || string.CompareOrdinal(candidate.CapturedAt, current.CapturedAt) > 0)
                    current = candidate;
            }
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

            _pendingResults[attemptId] = baseline;
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

    private ProfileReading? ReadLatest(long playerId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_factory.DatabaseExists) return null;
        using var con = _factory.Open();
        if (cancellationToken.CanBeCanceled)
            con.DefaultTimeout = 1;
        using var interruptRegistration = cancellationToken.Register(
            static state => SQLitePCL.raw.sqlite3_interrupt(((SqliteConnection)state!).Handle),
            con);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureSchema(con, cancellationToken);
            using var command = con.CreateCommand();
            command.CommandText = """
            SELECT captured_at, player_id, player_name, total_pp, global_rank,
                   accuracy, play_count, level, ranked_score, country_code, country_rank
            FROM profile_snapshots
            WHERE player_id = @player_id
            ORDER BY id DESC
            LIMIT 1
            """;
            command.Parameters.AddWithValue("@player_id", playerId);
            cancellationToken.ThrowIfCancellationRequested();
            using var reader = command.ExecuteReader();
            return reader.Read() ? ProfileReading.Read(reader) : null;
        }
        catch (SqliteException exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(
                "Profile telemetry read was interrupted by gameplay.",
                exception,
                cancellationToken);
        }
    }

    private void PersistUpdate(
        ProfileReading reading,
        (long AttemptId, ProfileReading Baseline)? pending,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var con = _factory.Open();
        if (cancellationToken.CanBeCanceled)
            con.DefaultTimeout = 1;
        using var interruptRegistration = cancellationToken.Register(
            static state => SQLitePCL.raw.sqlite3_interrupt(((SqliteConnection)state!).Handle),
            con);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureSchema(con, cancellationToken);
            using var tx = con.BeginTransaction();
            cancellationToken.ThrowIfCancellationRequested();
            InsertSnapshot(con, tx, reading);
            if (pending is { } result)
            {
                cancellationToken.ThrowIfCancellationRequested();
                InsertAccountChange(con, tx, result.AttemptId, result.Baseline, reading);
            }
            cancellationToken.ThrowIfCancellationRequested();
            tx.Commit();
        }
        catch (SqliteException exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(
                "Profile telemetry persistence was interrupted by gameplay.",
                exception,
                cancellationToken);
        }
    }

    private static void InsertSnapshot(
        SqliteConnection con,
        SqliteTransaction tx,
        ProfileReading reading)
    {
        using var command = con.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            INSERT INTO profile_snapshots(
                captured_at, player_id, player_name, total_pp, global_rank,
                accuracy, play_count, level, ranked_score, country_code, country_rank, fingerprint)
            VALUES(@captured_at, @player_id, @player_name, @total_pp, @global_rank,
                   @accuracy, @play_count, @level, @ranked_score, @country_code, @country_rank, @fingerprint)
            """;
        AddSnapshotParameters(command, reading);
        command.ExecuteNonQuery();
    }

    private static void InsertAccountChange(
        SqliteConnection con,
        SqliteTransaction tx,
        long attemptId,
        ProfileReading oldReading,
        ProfileReading newReading)
    {
        using var command = con.CreateCommand();
        command.Transaction = tx;
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

    private void EnsureSchema(SqliteConnection con, CancellationToken cancellationToken)
    {
        lock (_schemaGate)
        {
            if (Volatile.Read(ref _schemaEnsured) != 0)
                return;
            cancellationToken.ThrowIfCancellationRequested();
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
                country_rank INTEGER,
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
            EnsureColumn(con, "profile_snapshots", "country_rank", "INTEGER");
            Volatile.Write(ref _schemaEnsured, 1);
        }
    }

    private static void EnsureColumn(
        SqliteConnection con,
        string table,
        string column,
        string declaration)
    {
        using var info = con.CreateCommand();
        info.CommandText = $"PRAGMA table_info({table})";
        using var reader = info.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return;
        }
        reader.Close();
        using var alter = con.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {declaration}";
        alter.ExecuteNonQuery();
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
        command.Parameters.AddWithValue("@country_rank", (object?)reading.CountryRank ?? DBNull.Value);
        command.Parameters.AddWithValue("@fingerprint", reading.CreateFingerprint());
    }

    private sealed record ProfileReading(
        string CapturedAt, long PlayerId, string PlayerName, double? TotalPp,
        long? GlobalRank, double? Accuracy, long? PlayCount, double? Level,
        long? RankedScore, string? CountryCode, long? CountryRank)
    {
        public bool Matches(TosuProfile profile) =>
            PlayerId == profile.Id
            && string.Equals(PlayerName, profile.Name, StringComparison.Ordinal)
            && TotalPp == profile.TotalPp
            && GlobalRank == profile.GlobalRank
            && Accuracy == profile.Accuracy
            && PlayCount == profile.PlayCount
            && Level == profile.Level
            && RankedScore == profile.RankedScore
            && (profile.CountryRank is null || CountryRank == profile.CountryRank)
            && string.Equals(CountryCode, profile.CountryCode, StringComparison.Ordinal);

        public bool Matches(ProfileReading other) =>
            PlayerId == other.PlayerId
            && string.Equals(PlayerName, other.PlayerName, StringComparison.Ordinal)
            && TotalPp == other.TotalPp
            && GlobalRank == other.GlobalRank
            && Accuracy == other.Accuracy
            && PlayCount == other.PlayCount
            && Level == other.Level
            && RankedScore == other.RankedScore
            && CountryRank == other.CountryRank
            && string.Equals(CountryCode, other.CountryCode, StringComparison.Ordinal);

        public static bool HasProgressionChange(ProfileReading baseline, ProfileReading current) =>
            baseline.TotalPp != current.TotalPp
            || baseline.GlobalRank != current.GlobalRank;

        public static bool MatchesNextPlayCount(ProfileReading baseline, ProfileReading current) =>
            baseline.PlayCount is { } oldCount
            && current.PlayCount == oldCount + 1;

        public static bool IsPastExpectedPlayCount(ProfileReading baseline, ProfileReading current) =>
            baseline.PlayCount is { } oldCount
            && current.PlayCount is { } currentCount
            && currentCount > oldCount + 1;

        public string CreateFingerprint() => JsonSerializer.Serialize(new
        {
            Id = PlayerId,
            Name = PlayerName,
            TotalPp,
            GlobalRank,
            Accuracy,
            PlayCount,
            Level,
            RankedScore,
            CountryCode,
            CountryRank,
        });

        public static ProfileReading From(
            TosuProfile profile,
            double unixSeconds,
            long? previousCountryRank = null)
        {
            var capturedAt = DateTimeOffset.FromUnixTimeMilliseconds((long)(unixSeconds * 1000)).ToString("O");
            return new ProfileReading(capturedAt, profile.Id, profile.Name, profile.TotalPp,
                profile.GlobalRank, profile.Accuracy, profile.PlayCount, profile.Level,
                profile.RankedScore, profile.CountryCode, profile.CountryRank ?? previousCountryRank);
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
            reader.IsDBNull(10) ? null : reader.GetInt64(10));
    }

    private sealed record PendingProfileWrite(
        ProfileReading Reading,
        (long AttemptId, ProfileReading Baseline)? Pending);
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
