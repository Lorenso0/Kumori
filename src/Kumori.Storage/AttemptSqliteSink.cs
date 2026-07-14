using Kumori.Tracking;
using Microsoft.Data.Sqlite;
using Serilog;
using System.Text.Json;

namespace Kumori.Storage;

public sealed class AttemptSqliteSink : IAttemptSink, ISessionSink
{
    private readonly SqliteConnectionFactory _factory;
    private readonly Func<string, Func<CancellationToken, Task>, Task>? _deferPersistence;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _persistenceDrainGate = new(1, 1);
    private readonly Queue<PersistenceOperation> _persistenceQueue = [];
    private readonly string _persistenceInstanceId = Guid.NewGuid().ToString("N");
    private long? _sessionId;
    private long? _attemptId;
    private long _nextSessionId;
    private long _nextAttemptId;
    private long _nextPersistenceSequence;
    private SessionStart? _pendingSessionStart;
    private AttemptStart? _pendingAttemptStart;
    private AttemptSnapshot? _latestAttemptSnapshot;
    private double _pendingActiveSeconds;
    private bool _persistenceScheduled;
    private TaskCompletionSource _persistenceDrained = CompletedSignal();

    /// <summary>
    /// Raised after a finalized attempt transaction has committed and the final
    /// row is visible to independent readers. Start/checkpoint durability is not
    /// published as a completed attempt. Subscribers are isolated from persistence.
    /// </summary>
    public event Action<long>? AttemptPersisted;

    public AttemptSqliteSink(
        SqliteConnectionFactory factory,
        Func<string, Func<CancellationToken, Task>, Task>? deferPersistence = null)
    {
        _factory = factory;
        _deferPersistence = deferPersistence;
        EnsureSchema();
        InitializeIdAllocators();
    }

    public long? CurrentSessionId
    {
        get { lock (_gate) return _sessionId; }
    }

    public long? CurrentAttemptId
    {
        get { lock (_gate) return _attemptId; }
    }

    public void StartSession(SessionStart start)
    {
        PersistenceOperation? operation = null;
        lock (_gate)
        {
            if (_sessionId is null)
            {
                _sessionId = ReserveSessionId();
                _pendingSessionStart = start;
                operation = CreatePersistenceOperationLocked(
                    "session-start",
                    _sessionId.Value,
                    _sessionId.Value,
                    start,
                    activeSeconds: 0,
                    attempt: null,
                    closure: null);
                EnqueuePersistenceLocked(operation);
            }
        }

        SchedulePersistenceIfNeeded(operation is not null);
    }

    public void AddActiveSeconds(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds <= 0)
        {
            return;
        }

        lock (_gate)
        {
            if (_sessionId is null)
            {
                return;
            }

            _pendingActiveSeconds += seconds;
        }
    }

    public void PromptOsuClosed(SessionClosePrompt prompt)
    {
        // The WPF/tray notification layer observes this through app state in production.
        // The sink intentionally has no UI side effect.
    }

    public void EndSession(SessionEnd end) => EndSession(end.Interrupted, end.WallTime);

    public void StartAttempt(AttemptStart start)
    {
        PersistenceOperation? operation = null;
        lock (_gate)
        {
            if (_sessionId is null)
            {
                _sessionId = ReserveSessionId();
                _pendingSessionStart = new SessionStart(start.WallTime, start.StartedMonoTime);
            }

            _attemptId = ReserveAttemptId();
            _pendingAttemptStart = start;
            _latestAttemptSnapshot = null;
            operation = CreatePersistenceOperationLocked(
                "attempt-start",
                _attemptId.Value,
                _sessionId.Value,
                _pendingSessionStart!,
                _pendingActiveSeconds,
                new DetachedAttempt(
                    _attemptId.Value,
                    Detach(start),
                    Snapshot: null,
                    Events: [],
                    Finalization: null,
                    PersistRichSnapshot: false,
                    Delete: false),
                closure: null);
            _pendingActiveSeconds = 0;
            EnqueuePersistenceLocked(operation);
        }

        SchedulePersistenceIfNeeded(operation is not null);
    }

    public void Checkpoint(AttemptCheckpoint checkpoint)
    {
        PersistenceOperation? operation = null;
        lock (_gate)
        {
            if (_attemptId is not { } attemptId ||
                _sessionId is not { } sessionId ||
                _pendingSessionStart is not { } sessionStart)
            {
                return;
            }

            var snapshot = Detach(checkpoint.Snapshot);
            var events = checkpoint.Events
                .Select(evt => new PendingAttemptEvent(
                    checkpoint.Snapshot.WallTime,
                    checkpoint.Snapshot.LiveTimeMs,
                    evt))
                .ToArray();
            _latestAttemptSnapshot = snapshot;
            operation = CreatePersistenceOperationLocked(
                "attempt-checkpoint",
                attemptId,
                sessionId,
                sessionStart,
                _pendingActiveSeconds,
                new DetachedAttempt(
                    attemptId,
                    Start: null,
                    Snapshot: snapshot,
                    Events: events,
                    Finalization: null,
                    PersistRichSnapshot: checkpoint.Forced,
                    Delete: false),
                closure: null);
            _pendingActiveSeconds = 0;
            EnqueuePersistenceLocked(operation);
        }

        SchedulePersistenceIfNeeded(operation is not null);
    }

    public void DiscardIfEmpty(AttemptDiscard discard)
    {
        PersistenceOperation? operation = null;
        lock (_gate)
        {
            if (_attemptId is not { } attemptId ||
                _sessionId is not { } sessionId ||
                _pendingSessionStart is not { } sessionStart)
            {
                return;
            }

            operation = CreatePersistenceOperationLocked(
                "attempt-discard",
                attemptId,
                sessionId,
                sessionStart,
                _pendingActiveSeconds,
                new DetachedAttempt(
                    attemptId,
                    Start: null,
                    Snapshot: null,
                    Events: [],
                    Finalization: null,
                    PersistRichSnapshot: false,
                    Delete: true),
                closure: null);
            _pendingActiveSeconds = 0;
            _pendingAttemptStart = null;
            _latestAttemptSnapshot = null;
            if (_nextAttemptId == checked(attemptId + 1))
            {
                // The ordered delete is committed before a replacement start,
                // so an empty pulse can still reuse its public attempt ID.
                _nextAttemptId = attemptId;
            }
            _attemptId = null;
            EnqueuePersistenceLocked(operation);
        }

        SchedulePersistenceIfNeeded(operation is not null);
    }

    public void Finalize(AttemptFinalization finalization)
    {
        PersistenceOperation? operation = null;
        lock (_gate)
        {
            if (_attemptId is not { } attemptId ||
                _sessionId is not { } sessionId ||
                _pendingSessionStart is not { } sessionStart ||
                _pendingAttemptStart is null)
            {
                return;
            }

            var snapshot = Detach(finalization.Snapshot);
            operation = CreatePersistenceOperationLocked(
                "attempt-final",
                attemptId,
                sessionId,
                sessionStart,
                _pendingActiveSeconds,
                new DetachedAttempt(
                    attemptId,
                    Start: null,
                    Snapshot: snapshot,
                    Events: [],
                    Finalization: new DetachedFinalization(
                        finalization.Outcome,
                        finalization.Evidence,
                        snapshot),
                    PersistRichSnapshot: true,
                    Delete: false),
                closure: null);

            _pendingActiveSeconds = 0;
            _pendingAttemptStart = null;
            _latestAttemptSnapshot = null;
            _attemptId = null;
            EnqueuePersistenceLocked(operation);
        }

        SchedulePersistenceIfNeeded(operation is not null);
    }

    public void EndSession(bool interrupted = false, double? wallTime = null)
    {
        PersistenceOperation? operation = null;
        lock (_gate)
        {
            if (_sessionId is not { } sessionId ||
                _pendingSessionStart is not { } sessionStart)
            {
                return;
            }

            DetachedAttempt? activeAttempt = null;
            if (_attemptId is { } attemptId && _pendingAttemptStart is not null)
            {
                activeAttempt = new DetachedAttempt(
                    attemptId,
                    Start: null,
                    Snapshot: _latestAttemptSnapshot is null ? null : Detach(_latestAttemptSnapshot),
                    Events: [],
                    Finalization: null,
                    PersistRichSnapshot: true,
                    Delete: false);
            }

            var endedAt = wallTime is { } value
                ? DateTimeOffset.FromUnixTimeMilliseconds(UnixMilliseconds(value))
                : DateTimeOffset.UtcNow;
            operation = CreatePersistenceOperationLocked(
                "session-end",
                sessionId,
                sessionId,
                sessionStart,
                _pendingActiveSeconds,
                activeAttempt,
                new SessionClosure(endedAt, interrupted));

            _pendingActiveSeconds = 0;
            _pendingAttemptStart = null;
            _latestAttemptSnapshot = null;
            _attemptId = null;
            _pendingSessionStart = null;
            _sessionId = null;
            EnqueuePersistenceLocked(operation);
        }

        SchedulePersistenceIfNeeded(operation is not null);
    }

    /// <summary>
    /// Waits until every detached operation queued before the call is durable.
    /// Normal packet processing never calls this; it is intended for bounded
    /// shutdown and deterministic tools/tests.
    /// </summary>
    public async Task FlushPendingPersistenceAsync(CancellationToken cancellationToken = default)
    {
        // A flush is an explicit shutdown/tool boundary. Run the same ordered
        // drain directly so it does not spend most of a bounded shutdown wait
        // in the coordinator's normal gameplay-idle settling delay.
        await Task.Run(
                () => DrainPersistenceAsync(cancellationToken, schedulerOwned: false),
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal int PendingPersistenceCount
    {
        get { lock (_gate) return _persistenceQueue.Count; }
    }

    private long ReserveSessionId()
    {
        var id = _nextSessionId;
        _nextSessionId = checked(id + 1);
        return id;
    }

    private long ReserveAttemptId()
    {
        var id = _nextAttemptId;
        _nextAttemptId = checked(id + 1);
        return id;
    }

    private PersistenceOperation CreatePersistenceOperationLocked(
        string kind,
        long subjectId,
        long sessionId,
        SessionStart sessionStart,
        double activeSeconds,
        DetachedAttempt? attempt,
        SessionClosure? closure)
    {
        var sequence = ++_nextPersistenceSequence;
        return new PersistenceOperation(
            sequence,
            $"{_persistenceInstanceId}:{sequence}:{kind}:{subjectId}",
            sessionId,
            sessionStart,
            activeSeconds,
            attempt,
            closure);
    }

    private void EnqueuePersistenceLocked(PersistenceOperation operation)
    {
        if (_persistenceQueue.Count == 0)
            _persistenceDrained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _persistenceQueue.Enqueue(operation);
    }

    private void SchedulePersistenceIfNeeded(bool operationQueued)
    {
        if (!operationQueued)
            return;

        lock (_gate)
        {
            if (_persistenceScheduled)
                return;
            _persistenceScheduled = true;
        }

        Task scheduled;
        try
        {
            scheduled = _deferPersistence is null
                ? Task.Run(() => DrainPersistenceAsync(CancellationToken.None, schedulerOwned: true))
                : _deferPersistence(
                    "attempt-sqlite-persistence",
                    token => DrainPersistenceAsync(token, schedulerOwned: true));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not schedule ordered attempt persistence; using the dedicated fallback worker");
            scheduled = Task.Run(() => DrainPersistenceAsync(CancellationToken.None, schedulerOwned: true));
        }

        _ = scheduled.ContinueWith(
            static (completed, state) => ((AttemptSqliteSink)state!).OnPersistenceSchedulerStopped(completed),
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void OnPersistenceSchedulerStopped(Task completed)
    {
        var reschedule = false;
        lock (_gate)
        {
            if (_persistenceQueue.Count == 0)
            {
                _persistenceScheduled = false;
                _persistenceDrained.TrySetResult();
                return;
            }

            // A coordinator cancellation during application shutdown must not
            // strand already-detached attempts. The fallback worker is not tied
            // to gameplay and is only reached after the scheduler has stopped.
            if (completed.IsCanceled || completed.IsFaulted)
            {
                _persistenceScheduled = false;
                reschedule = true;
            }
        }

        if (reschedule)
        {
            if (completed.Exception is { } failure)
                Log.Warning(failure, "Ordered attempt persistence scheduler stopped before the queue drained");
            SchedulePersistenceFallback();
        }
    }

    private void SchedulePersistenceFallback()
    {
        lock (_gate)
        {
            if (_persistenceScheduled || _persistenceQueue.Count == 0)
                return;
            _persistenceScheduled = true;
        }

        var fallback = Task.Run(() => DrainPersistenceAsync(CancellationToken.None, schedulerOwned: true));
        _ = fallback.ContinueWith(
            static (completed, state) => ((AttemptSqliteSink)state!).OnPersistenceSchedulerStopped(completed),
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task DrainPersistenceAsync(
        CancellationToken cancellationToken,
        bool schedulerOwned)
    {
        await _persistenceDrainGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var failures = 0;
            while (true)
            {
                PersistenceOperation operation;
                lock (_gate)
                {
                    if (_persistenceQueue.Count == 0)
                    {
                        if (schedulerOwned)
                            _persistenceScheduled = false;
                        _persistenceDrained.TrySetResult();
                        return;
                    }
                    operation = _persistenceQueue.Peek();
                }

                try
                {
                    var applied = Persist(operation, cancellationToken);
                    if (applied && operation.Attempt is { Finalization: not null } finalizedAttempt)
                        NotifyAttemptPersisted(finalizedAttempt.Id);
                    failures = 0;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(
                        "Attempt persistence was interrupted by active gameplay.",
                        ex,
                        cancellationToken);
                }
                catch (Exception ex)
                {
                    failures++;
                    if (failures == 1 || (failures & (failures - 1)) == 0)
                    {
                        Log.Warning(
                            ex,
                            "Attempt persistence operation {Sequence} failed; the immutable operation remains queued for retry",
                            operation.Sequence);
                    }

                    var delayMs = Math.Min(2_000, 100 * (1 << Math.Min(failures - 1, 4)));
                    await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                lock (_gate)
                {
                    if (_persistenceQueue.Count == 0 || !ReferenceEquals(_persistenceQueue.Peek(), operation))
                        throw new InvalidOperationException("The ordered attempt persistence queue was modified out of sequence.");
                    _persistenceQueue.Dequeue();
                    if (_persistenceQueue.Count == 0)
                    {
                        if (schedulerOwned)
                            _persistenceScheduled = false;
                        _persistenceDrained.TrySetResult();
                        return;
                    }
                }
            }
        }
        finally
        {
            _persistenceDrainGate.Release();
        }
    }

    private bool Persist(PersistenceOperation operation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var con = _factory.Open();
        if (cancellationToken.CanBeCanceled)
            con.DefaultTimeout = 1;
        using var interruptRegistration = cancellationToken.Register(
            static state => SQLitePCL.raw.sqlite3_interrupt(((SqliteConnection)state!).Handle),
            con);
        cancellationToken.ThrowIfCancellationRequested();
        using var tx = con.BeginTransaction();

        var claimed = ClaimPersistenceOperation(con, tx, operation.OperationKey);
        PruneCompletedPersistenceOperations(con, tx, operation.OperationKey);
        if (!claimed)
        {
            tx.Commit();
            return false;
        }
        PersistSession(con, tx, operation.SessionId, operation.SessionStart);
        cancellationToken.ThrowIfCancellationRequested();
        if (operation.Attempt is { } attempt)
        {
            PersistAttempt(con, tx, operation.SessionId, attempt, cancellationToken);
        }
        WriteActiveSeconds(con, tx, operation.SessionId, operation.ActiveSeconds);
        if (operation.Closure is { } closure)
        {
            CloseSession(con, tx, operation.SessionId, closure);
        }

        cancellationToken.ThrowIfCancellationRequested();
        tx.Commit();
        // Do not observe cancellation after Commit. Once the transaction is
        // durable the queue item must be acknowledged exactly once.
        return true;
    }

    private void NotifyAttemptPersisted(long attemptId)
    {
        var handlers = AttemptPersisted;
        if (handlers is null)
            return;

        foreach (Action<long> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(attemptId);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Attempt-persisted subscriber failed for attempt {AttemptId}", attemptId);
            }
        }
    }

    private static bool ClaimPersistenceOperation(
        SqliteConnection con,
        SqliteTransaction tx,
        string operationKey)
    {
        using var cmd = con.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR IGNORE INTO attempt_persistence_commits(operation_key, committed_at)
            VALUES(@operation_key, @committed_at)
            """;
        cmd.Parameters.AddWithValue("@operation_key", operationKey);
        cmd.Parameters.AddWithValue("@committed_at", DateTimeOffset.UtcNow.ToString("O"));
        return cmd.ExecuteNonQuery() == 1;
    }

    private void PruneCompletedPersistenceOperations(
        SqliteConnection con,
        SqliteTransaction tx,
        string currentOperationKey)
    {
        // The queue is strictly serial: once the next item is executing, every
        // earlier item from this sink has already been acknowledged and can no
        // longer be retried. Keep only the current marker for crash-safe retry.
        using var cmd = con.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            DELETE FROM attempt_persistence_commits
            WHERE substr(operation_key, 1, length(@instance_prefix)) = @instance_prefix
              AND operation_key <> @current_operation_key
            """;
        cmd.Parameters.AddWithValue("@instance_prefix", $"{_persistenceInstanceId}:");
        cmd.Parameters.AddWithValue("@current_operation_key", currentOperationKey);
        cmd.ExecuteNonQuery();
    }

    private static void PersistAttempt(
        SqliteConnection con,
        SqliteTransaction tx,
        long sessionId,
        DetachedAttempt attempt,
        CancellationToken cancellationToken)
    {
        if (attempt.Delete)
        {
            DeleteAttempt(con, tx, attempt.Id);
            return;
        }

        if (attempt.Start is { } start)
            InsertAttempt(con, tx, attempt.Id, sessionId, start);
        cancellationToken.ThrowIfCancellationRequested();
        if (attempt.Snapshot is { } snapshot)
        {
            UpdateAttempt(con, tx, attempt.Id, snapshot);
            if (snapshot.Mods.Count > 0 || !snapshot.ModsKey.Equals("NM", StringComparison.OrdinalIgnoreCase))
                ReplaceMods(con, tx, attempt.Id, snapshot.ModsKey, snapshot.Mods);
            if (attempt.PersistRichSnapshot)
            {
                UpsertTiming(con, tx, attempt.Id, snapshot);
                UpsertContext(con, tx, attempt.Id, snapshot);
            }
        }
        foreach (var pending in attempt.Events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InsertEvent(con, tx, attempt.Id, pending.WallTime, pending.LiveTimeMs, pending.Event);
        }

        if (attempt.Finalization is { } finalization)
        {
            using var cmd = con.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE attempts
                SET outcome = @outcome,
                    termination_evidence = @evidence,
                    ended_at = @ended_at,
                    ended_at_utc_ms = @ended_at_utc_ms,
                    progress = CASE WHEN @outcome = 'completed' THEN 1 ELSE progress END
                WHERE id = @id
                """;
            cmd.Parameters.AddWithValue("@outcome", finalization.Outcome);
            cmd.Parameters.AddWithValue("@evidence", finalization.Evidence);
            cmd.Parameters.AddWithValue("@ended_at", IsoFromUnixSeconds(finalization.Snapshot.WallTime));
            cmd.Parameters.AddWithValue("@ended_at_utc_ms", UnixMilliseconds(finalization.Snapshot.WallTime));
            cmd.Parameters.AddWithValue("@id", attempt.Id);
            cmd.ExecuteNonQuery();
        }

        if (attempt.Start is not null || attempt.Finalization is not null)
            UpsertInputSummary(con, tx, attempt.Id);
        if (attempt.Finalization?.Outcome is "completed" or "failed")
            UpdatePersonalBests(con, tx, attempt.Id);
    }

    private static void DeleteAttempt(
        SqliteConnection con,
        SqliteTransaction tx,
        long attemptId)
    {
        using var cmd = con.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM attempts WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", attemptId);
        cmd.ExecuteNonQuery();
    }

    private static void PersistSession(
        SqliteConnection con,
        SqliteTransaction tx,
        long sessionId,
        SessionStart start)
    {
        using var cmd = con.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR IGNORE INTO sessions(id, started_at, started_at_utc_ms, key1_binding, key2_binding)
            VALUES(@id, @started_at, @started_at_utc_ms, 'Z', 'X')
            """;
        cmd.Parameters.AddWithValue("@id", sessionId);
        cmd.Parameters.AddWithValue("@started_at", IsoFromUnixSeconds(start.WallTime));
        cmd.Parameters.AddWithValue("@started_at_utc_ms", UnixMilliseconds(start.WallTime));
        cmd.ExecuteNonQuery();
    }

    private static void WriteActiveSeconds(
        SqliteConnection con,
        SqliteTransaction tx,
        long sessionId,
        double seconds)
    {
        if (seconds <= 0)
            return;
        using var cmd = con.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE sessions SET active_seconds = active_seconds + @seconds WHERE id = @id";
        cmd.Parameters.AddWithValue("@seconds", seconds);
        cmd.Parameters.AddWithValue("@id", sessionId);
        cmd.ExecuteNonQuery();
    }

    private static void CloseSession(
        SqliteConnection con,
        SqliteTransaction tx,
        long sessionId,
        SessionClosure closure)
    {
        using var cmd = con.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE sessions
            SET ended_at = @ended_at, ended_at_utc_ms = @ended_at_utc_ms, interrupted = @interrupted
            WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@ended_at", closure.EndedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@ended_at_utc_ms", closure.EndedAt.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("@interrupted", closure.Interrupted ? 1 : 0);
        cmd.Parameters.AddWithValue("@id", sessionId);
        cmd.ExecuteNonQuery();
    }

    private static AttemptStart Detach(AttemptStart start) => start with
    {
        Mods = start.Mods.ToArray(),
    };

    private static AttemptSnapshot Detach(AttemptSnapshot snapshot) => snapshot with
    {
        TimingOffsets = snapshot.TimingOffsets.ToArray(),
        Mods = snapshot.Mods.ToArray(),
    };

    private static TaskCompletionSource CompletedSignal()
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        signal.SetResult();
        return signal;
    }

    private sealed record PendingAttemptEvent(
        double WallTime,
        long LiveTimeMs,
        JudgementCapture.CapturedEvent Event);

    private sealed record DetachedFinalization(
        string Outcome,
        string Evidence,
        AttemptSnapshot Snapshot);

    private sealed record DetachedAttempt(
        long Id,
        AttemptStart? Start,
        AttemptSnapshot? Snapshot,
        PendingAttemptEvent[] Events,
        DetachedFinalization? Finalization,
        bool PersistRichSnapshot,
        bool Delete);

    private sealed record SessionClosure(DateTimeOffset EndedAt, bool Interrupted);

    private sealed record PersistenceOperation(
        long Sequence,
        string OperationKey,
        long SessionId,
        SessionStart SessionStart,
        double ActiveSeconds,
        DetachedAttempt? Attempt,
        SessionClosure? Closure);

    private void EnsureSchema()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_factory.DatabasePath)!);
        using var con = _factory.Open();
        using (var journal = con.CreateCommand())
        {
            journal.CommandText = "PRAGMA journal_mode=WAL;";
            journal.ExecuteNonQuery();
        }
        using var tx = con.BeginTransaction();
        using var cmd = con.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS metadata(
                key TEXT PRIMARY KEY, value TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS attempt_persistence_commits(
                operation_key TEXT PRIMARY KEY,
                committed_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS sessions(
                id INTEGER PRIMARY KEY,
                started_at TEXT NOT NULL,
                ended_at TEXT,
                active_seconds REAL NOT NULL DEFAULT 0,
                z_count INTEGER NOT NULL DEFAULT 0,
                x_count INTEGER NOT NULL DEFAULT 0,
                key1_binding TEXT NOT NULL DEFAULT 'Z',
                key2_binding TEXT NOT NULL DEFAULT 'X',
                player_name TEXT,
                interrupted INTEGER NOT NULL DEFAULT 0,
                legacy INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS beatmaps(
                id INTEGER PRIMARY KEY,
                identity TEXT NOT NULL UNIQUE,
                beatmap_id INTEGER,
                set_id INTEGER,
                checksum TEXT,
                artist TEXT,
                title TEXT,
                mapper TEXT,
                difficulty TEXT,
                stars REAL,
                ar REAL,
                cs REAL,
                od REAL,
                hp REAL,
                bpm REAL,
                max_combo INTEGER NOT NULL DEFAULT 0,
                raw_json TEXT
            );
            CREATE TABLE IF NOT EXISTS attempts(
                id INTEGER PRIMARY KEY,
                session_id INTEGER NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
                beatmap_id INTEGER NOT NULL REFERENCES beatmaps(id),
                started_at TEXT NOT NULL,
                ended_at TEXT,
                outcome TEXT NOT NULL DEFAULT 'active',
                termination_evidence TEXT,
                progress REAL NOT NULL DEFAULT 0,
                duration_seconds REAL NOT NULL DEFAULT 0,
                score INTEGER NOT NULL DEFAULT 0,
                accuracy REAL NOT NULL DEFAULT 0,
                grade TEXT,
                pp REAL NOT NULL DEFAULT 0,
                fc_pp REAL NOT NULL DEFAULT 0,
                max_pp REAL NOT NULL DEFAULT 0,
                combo INTEGER NOT NULL DEFAULT 0,
                n300 INTEGER NOT NULL DEFAULT 0,
                n100 INTEGER NOT NULL DEFAULT 0,
                n50 INTEGER NOT NULL DEFAULT 0,
                misses INTEGER NOT NULL DEFAULT 0,
                geki INTEGER NOT NULL DEFAULT 0,
                katu INTEGER NOT NULL DEFAULT 0,
                slider_breaks INTEGER NOT NULL DEFAULT 0,
                large_tick_hits INTEGER NOT NULL DEFAULT 0,
                large_tick_misses INTEGER NOT NULL DEFAULT 0,
                small_tick_hits INTEGER NOT NULL DEFAULT 0,
                small_tick_misses INTEGER NOT NULL DEFAULT 0,
                slider_tail_hits INTEGER NOT NULL DEFAULT 0,
                slider_tail_misses INTEGER NOT NULL DEFAULT 0,
                unstable_rate REAL NOT NULL DEFAULT 0,
                z_count INTEGER NOT NULL DEFAULT 0,
                x_count INTEGER NOT NULL DEFAULT 0,
                key1_binding TEXT NOT NULL DEFAULT 'Z',
                key2_binding TEXT NOT NULL DEFAULT 'X',
                mods_key TEXT NOT NULL DEFAULT 'NM',
                raw_json TEXT
            );
            CREATE TABLE IF NOT EXISTS attempt_mods(
                attempt_id INTEGER NOT NULL REFERENCES attempts(id) ON DELETE CASCADE,
                position INTEGER NOT NULL,
                acronym TEXT NOT NULL,
                settings_json TEXT NOT NULL DEFAULT '{}',
                PRIMARY KEY(attempt_id, position)
            );
            CREATE TABLE IF NOT EXISTS attempt_timing(
                attempt_id INTEGER PRIMARY KEY REFERENCES attempts(id) ON DELETE CASCADE,
                offsets_zlib BLOB NOT NULL,
                hit_count INTEGER NOT NULL,
                early_count INTEGER NOT NULL,
                late_count INTEGER NOT NULL,
                mean REAL NOT NULL,
                median REAL NOT NULL,
                deviation REAL NOT NULL
            );
            CREATE TABLE IF NOT EXISTS attempt_events(
                id INTEGER PRIMARY KEY,
                attempt_id INTEGER NOT NULL REFERENCES attempts(id) ON DELETE CASCADE,
                captured_at TEXT NOT NULL,
                map_time_ms INTEGER,
                event_type TEXT NOT NULL,
                value REAL,
                data_json TEXT NOT NULL DEFAULT '{}'
            );
            CREATE TABLE IF NOT EXISTS attempt_context(
                attempt_id INTEGER PRIMARY KEY REFERENCES attempts(id) ON DELETE CASCADE,
                source_json TEXT NOT NULL DEFAULT '{}',
                pp_json TEXT NOT NULL DEFAULT '{}',
                beatmap_json TEXT NOT NULL DEFAULT '{}',
                score_json TEXT NOT NULL DEFAULT '{}',
                session_json TEXT NOT NULL DEFAULT '{}',
                multiplayer_json TEXT NOT NULL DEFAULT '{}'
            );
            CREATE TABLE IF NOT EXISTS attempt_input_summary(
                attempt_id INTEGER PRIMARY KEY REFERENCES attempts(id) ON DELETE CASCADE,
                key1_presses INTEGER NOT NULL DEFAULT 0,
                key2_presses INTEGER NOT NULL DEFAULT 0,
                alternations INTEGER NOT NULL DEFAULT 0,
                same_key_repeats INTEGER NOT NULL DEFAULT 0,
                simultaneous_presses INTEGER NOT NULL DEFAULT 0,
                key1_hold_ms REAL NOT NULL DEFAULT 0,
                key2_hold_ms REAL NOT NULL DEFAULT 0,
                peak_kps INTEGER NOT NULL DEFAULT 0,
                average_kps REAL NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS personal_bests(
                beatmap_id INTEGER NOT NULL REFERENCES beatmaps(id) ON DELETE CASCADE,
                mods_key TEXT NOT NULL,
                metric TEXT NOT NULL,
                attempt_id INTEGER NOT NULL REFERENCES attempts(id) ON DELETE CASCADE,
                value REAL NOT NULL,
                PRIMARY KEY(beatmap_id, mods_key, metric)
            );
            CREATE TABLE IF NOT EXISTS attempt_improvements(
                attempt_id INTEGER NOT NULL REFERENCES attempts(id) ON DELETE CASCADE,
                metric TEXT NOT NULL,
                previous_value REAL,
                new_value REAL NOT NULL,
                delta REAL,
                PRIMARY KEY(attempt_id, metric)
            );
            """ + MovementSchema.Sql + """
            CREATE INDEX IF NOT EXISTS idx_attempt_session ON attempts(session_id, started_at);
            CREATE INDEX IF NOT EXISTS idx_attempt_map_mods ON attempts(beatmap_id, mods_key);
            CREATE INDEX IF NOT EXISTS idx_attempt_events ON attempt_events(attempt_id, map_time_ms);
            """;
        cmd.ExecuteNonQuery();

        EnsureColumn(con, tx, "beatmaps", "beatmap_id", "INTEGER");
        EnsureColumn(con, tx, "beatmaps", "set_id", "INTEGER");
        EnsureColumn(con, tx, "beatmaps", "checksum", "TEXT");
        EnsureColumn(con, tx, "beatmaps", "ar", "REAL");
        EnsureColumn(con, tx, "beatmaps", "cs", "REAL");
        EnsureColumn(con, tx, "beatmaps", "od", "REAL");
        EnsureColumn(con, tx, "beatmaps", "hp", "REAL");
        EnsureColumn(con, tx, "beatmaps", "bpm", "REAL");
        EnsureColumn(con, tx, "beatmaps", "max_combo", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(con, tx, "sessions", "player_name", "TEXT");
        EnsureColumn(con, tx, "attempts", "geki", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(con, tx, "attempts", "katu", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(con, tx, "attempts", "large_tick_hits", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(con, tx, "attempts", "large_tick_misses", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(con, tx, "attempts", "small_tick_hits", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(con, tx, "attempts", "small_tick_misses", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(con, tx, "attempts", "slider_tail_hits", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(con, tx, "attempts", "slider_tail_misses", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(con, tx, "attempts", "base_stars", "REAL");
        EnsureColumn(con, tx, "attempts", "adjusted_stars", "REAL");
        BackfillAttemptStars(con, tx);
        using (var version = con.CreateCommand())
        {
            version.Transaction = tx;
            version.CommandText = "INSERT OR IGNORE INTO metadata(key, value) VALUES('schema_version', '2')";
            version.ExecuteNonQuery();
        }
        tx.Commit();
        DatabaseMigrator.Apply(con);
    }

    /// <summary>
    /// Migrates historical attempts from their immutable captured beatmap
    /// context. The beatmaps table cannot be used here because one row is
    /// shared between every mod combination of a map.
    /// </summary>
    private static void BackfillAttemptStars(SqliteConnection con, SqliteTransaction tx)
    {
        if (!TableExists(con, tx, "attempt_context"))
        {
            return;
        }

        var values = new List<(long AttemptId, double BaseStars, double AdjustedStars)>();
        using (var select = con.CreateCommand())
        {
            select.Transaction = tx;
            select.CommandText = """
                SELECT a.id, c.beatmap_json
                FROM attempts a
                JOIN attempt_context c ON c.attempt_id = a.id
                WHERE a.base_stars IS NULL OR a.adjusted_stars IS NULL
                """;
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                try
                {
                    using var document = JsonDocument.Parse(reader.GetString(1));
                    if (!document.RootElement.TryGetProperty("stats", out var stats)
                        || !stats.TryGetProperty("stars", out var stars)
                        || stars.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    double? original = TryGetDouble(stars, "original");
                    double? adjusted = TryGetDouble(stars, "total") ?? TryGetDouble(stars, "converted") ?? original;
                    if (adjusted is { } adjustedValue)
                    {
                        values.Add((reader.GetInt64(0), original ?? adjustedValue, adjustedValue));
                    }
                }
                catch (JsonException ex)
                {
                    Log.Warning(ex, "Invalid captured beatmap context while backfilling stars for attempt {AttemptId}", reader.GetInt64(0));
                }
            }
        }

        foreach (var value in values)
        {
            using var update = con.CreateCommand();
            update.Transaction = tx;
            update.CommandText = """
                UPDATE attempts
                SET base_stars = COALESCE(base_stars, @base_stars),
                    adjusted_stars = COALESCE(adjusted_stars, @adjusted_stars)
                WHERE id = @id
                """;
            update.Parameters.AddWithValue("@base_stars", value.BaseStars);
            update.Parameters.AddWithValue("@adjusted_stars", value.AdjustedStars);
            update.Parameters.AddWithValue("@id", value.AttemptId);
            update.ExecuteNonQuery();
        }
    }

    private static double? TryGetDouble(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetDouble(out var number)
            ? number
            : null;

    private static bool TableExists(SqliteConnection con, SqliteTransaction tx, string table)
    {
        using var cmd = con.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @name";
        cmd.Parameters.AddWithValue("@name", table);
        return cmd.ExecuteScalar() is not null;
    }

    private void InitializeIdAllocators()
    {
        using var con = _factory.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            SELECT COALESCE((SELECT MAX(id) FROM sessions), 0),
                   COALESCE((SELECT MAX(id) FROM attempts), 0)
            """;
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            throw new InvalidOperationException("Could not initialize tracking ID allocators.");
        }
        _nextSessionId = checked(reader.GetInt64(0) + 1);
        _nextAttemptId = checked(reader.GetInt64(1) + 1);
    }

    private static void InsertAttempt(
        SqliteConnection con,
        SqliteTransaction tx,
        long attemptId,
        long sessionId,
        AttemptStart start)
    {
        var beatmapId = EnsureBeatmap(con, tx, start);
        using (var insert = con.CreateCommand())
        {
            insert.Transaction = tx;
            insert.CommandText = """
            INSERT OR IGNORE INTO attempts(id, session_id, beatmap_id, started_at, started_at_utc_ms, outcome,
                                           progress, duration_seconds, mods_key,
                                           key1_binding, key2_binding, base_stars, adjusted_stars)
            VALUES(@id, @session_id, @beatmap_id, @started_at, @started_at_utc_ms, 'active',
                   0, 0, @mods_key, 'Z', 'X', @base_stars, @adjusted_stars)
            """;
            insert.Parameters.AddWithValue("@id", attemptId);
            insert.Parameters.AddWithValue("@session_id", sessionId);
            insert.Parameters.AddWithValue("@beatmap_id", beatmapId);
            insert.Parameters.AddWithValue("@started_at", IsoFromUnixSeconds(start.WallTime));
            insert.Parameters.AddWithValue("@started_at_utc_ms", UnixMilliseconds(start.WallTime));
            insert.Parameters.AddWithValue("@mods_key", start.ModsKey);
            insert.Parameters.AddWithValue("@base_stars", (object?)start.BeatmapStats.BaseStars ?? DBNull.Value);
            insert.Parameters.AddWithValue("@adjusted_stars", (object?)start.BeatmapStats.Stars ?? DBNull.Value);
            insert.ExecuteNonQuery();
        }

        UpsertSourceContext(con, tx, attemptId, start);
        ReplaceMods(con, tx, attemptId, start.ModsKey, start.Mods);
    }

    private static long EnsureBeatmap(
        SqliteConnection con,
        SqliteTransaction tx,
        AttemptStart start)
    {
        using var cmd = con.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO beatmaps(identity, beatmap_id, set_id, checksum, artist, title, mapper,
                                 difficulty, stars, ar, cs, od, hp, bpm, max_combo, raw_json)
            VALUES(@identity, @beatmap_id, @set_id, @checksum, @artist, @title, @mapper,
                   @difficulty, @stars, @ar, @cs, @od, @hp, @bpm, @max_combo, @raw_json)
            ON CONFLICT(identity) DO UPDATE SET
                beatmap_id = COALESCE(excluded.beatmap_id, beatmaps.beatmap_id),
                set_id = COALESCE(excluded.set_id, beatmaps.set_id),
                checksum = COALESCE(excluded.checksum, beatmaps.checksum),
                artist = COALESCE(excluded.artist, beatmaps.artist),
                title = COALESCE(excluded.title, beatmaps.title),
                mapper = COALESCE(excluded.mapper, beatmaps.mapper),
                difficulty = COALESCE(excluded.difficulty, beatmaps.difficulty),
                stars = COALESCE(excluded.stars, beatmaps.stars),
                ar = COALESCE(excluded.ar, beatmaps.ar),
                cs = COALESCE(excluded.cs, beatmaps.cs),
                od = COALESCE(excluded.od, beatmaps.od),
                hp = COALESCE(excluded.hp, beatmaps.hp),
                bpm = COALESCE(excluded.bpm, beatmaps.bpm),
                max_combo = CASE WHEN excluded.max_combo > 0 THEN excluded.max_combo ELSE beatmaps.max_combo END,
                raw_json = excluded.raw_json
            RETURNING id
            """;
        cmd.Parameters.AddWithValue("@identity", start.Identity);
        cmd.Parameters.AddWithValue("@beatmap_id", (object?)start.BeatmapId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@set_id", (object?)start.BeatmapSetId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@checksum", (object?)start.Checksum ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@artist", (object?)start.Artist ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@title", (object?)start.Title ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@mapper", (object?)start.Mapper ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@difficulty", (object?)start.Difficulty ?? DBNull.Value);
        // This is shared map metadata. The mod-adjusted value belongs on the
        // attempt, otherwise a DT/HR play can change the apparent base stars
        // for every other play of the same map.
        cmd.Parameters.AddWithValue("@stars", (object?)(start.BeatmapStats.BaseStars ?? start.BeatmapStats.Stars) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ar", (object?)start.BeatmapStats.ApproachRate ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@cs", (object?)start.BeatmapStats.CircleSize ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@od", (object?)start.BeatmapStats.OverallDifficulty ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@hp", (object?)start.BeatmapStats.DrainRate ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@bpm", (object?)start.BeatmapStats.Bpm ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@max_combo", start.BeatmapStats.MaxCombo ?? 0);
        cmd.Parameters.AddWithValue("@raw_json", start.BeatmapStats.RawJson);
        return (long)cmd.ExecuteScalar()!;
    }

    private static void UpdateAttempt(
        SqliteConnection con,
        SqliteTransaction tx,
        long attemptId,
        AttemptSnapshot snapshot)
    {
        using var cmd = con.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE attempts
            SET progress = @progress,
                duration_seconds = @duration,
                score = @score,
                accuracy = @accuracy,
                grade = @grade,
                pp = @pp,
                fc_pp = @fc_pp,
                max_pp = @max_pp,
                combo = @combo,
                n300 = @n300,
                n100 = @n100,
                n50 = @n50,
                misses = @misses,
                geki = @geki,
                katu = @katu,
                slider_breaks = @slider_breaks,
                large_tick_hits = @large_tick_hits,
                large_tick_misses = @large_tick_misses,
                small_tick_hits = @small_tick_hits,
                small_tick_misses = @small_tick_misses,
                slider_tail_hits = @slider_tail_hits,
                slider_tail_misses = @slider_tail_misses,
                unstable_rate = @unstable_rate,
                base_stars = COALESCE(@base_stars, base_stars),
                adjusted_stars = COALESCE(@adjusted_stars, adjusted_stars),
                raw_json = @raw_json
            WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@progress", snapshot.Progress);
        cmd.Parameters.AddWithValue("@duration", snapshot.DurationSeconds);
        cmd.Parameters.AddWithValue("@score", snapshot.Score);
        cmd.Parameters.AddWithValue("@accuracy", snapshot.Accuracy);
        cmd.Parameters.AddWithValue("@grade", (object?)snapshot.Grade ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@pp", snapshot.Pp);
        cmd.Parameters.AddWithValue("@fc_pp", snapshot.FcPp);
        cmd.Parameters.AddWithValue("@max_pp", snapshot.MaxPp);
        cmd.Parameters.AddWithValue("@combo", snapshot.Combo);
        cmd.Parameters.AddWithValue("@n300", snapshot.N300);
        cmd.Parameters.AddWithValue("@n100", snapshot.N100);
        cmd.Parameters.AddWithValue("@n50", snapshot.N50);
        cmd.Parameters.AddWithValue("@misses", snapshot.Misses);
        cmd.Parameters.AddWithValue("@geki", snapshot.Geki);
        cmd.Parameters.AddWithValue("@katu", snapshot.Katu);
        cmd.Parameters.AddWithValue("@slider_breaks", snapshot.SliderBreaks);
        cmd.Parameters.AddWithValue("@large_tick_hits", snapshot.LargeTickHits);
        cmd.Parameters.AddWithValue("@large_tick_misses", snapshot.LargeTickMisses);
        cmd.Parameters.AddWithValue("@small_tick_hits", snapshot.SmallTickHits);
        cmd.Parameters.AddWithValue("@small_tick_misses", snapshot.SmallTickMisses);
        cmd.Parameters.AddWithValue("@slider_tail_hits", snapshot.SliderTailHits);
        cmd.Parameters.AddWithValue("@slider_tail_misses", snapshot.SliderTailMisses);
        cmd.Parameters.AddWithValue("@unstable_rate", snapshot.UnstableRate);
        cmd.Parameters.AddWithValue("@base_stars", (object?)snapshot.BeatmapStats.BaseStars ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@adjusted_stars", (object?)snapshot.BeatmapStats.Stars ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@raw_json", "{}");
        cmd.Parameters.AddWithValue("@id", attemptId);
        cmd.ExecuteNonQuery();
    }

    private static void ReplaceMods(
        SqliteConnection con,
        SqliteTransaction tx,
        long attemptId,
        string modsKey,
        IReadOnlyList<AttemptMod> mods)
    {
        using (var update = con.CreateCommand())
        {
            update.Transaction = tx;
            update.CommandText = "UPDATE attempts SET mods_key = @mods_key WHERE id = @id";
            update.Parameters.AddWithValue("@mods_key", string.IsNullOrWhiteSpace(modsKey) ? "NM" : modsKey);
            update.Parameters.AddWithValue("@id", attemptId);
            update.ExecuteNonQuery();
        }
        using (var delete = con.CreateCommand())
        {
            delete.Transaction = tx;
            delete.CommandText = "DELETE FROM attempt_mods WHERE attempt_id = @id";
            delete.Parameters.AddWithValue("@id", attemptId);
            delete.ExecuteNonQuery();
        }
        for (var position = 0; position < mods.Count; position++)
        {
            using var insert = con.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = "INSERT INTO attempt_mods(attempt_id, position, acronym, settings_json) VALUES(@id, @position, @acronym, @settings)";
            insert.Parameters.AddWithValue("@id", attemptId);
            insert.Parameters.AddWithValue("@position", position);
            insert.Parameters.AddWithValue("@acronym", mods[position].Acronym);
            insert.Parameters.AddWithValue("@settings", mods[position].SettingsJson);
            insert.ExecuteNonQuery();
        }
    }

    private static void InsertEvent(
        SqliteConnection con,
        SqliteTransaction tx,
        long attemptId,
        double wallTime,
        long liveTimeMs,
        JudgementCapture.CapturedEvent evt)
    {
        using var cmd = con.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO attempt_events(attempt_id, captured_at, map_time_ms, event_type, value, data_json)
            VALUES(@attempt_id, @captured_at, @map_time_ms, @event_type, @value, @data_json)
            """;
        cmd.Parameters.AddWithValue("@attempt_id", attemptId);
        cmd.Parameters.AddWithValue("@captured_at", IsoFromUnixSeconds(wallTime));
        cmd.Parameters.AddWithValue("@map_time_ms", liveTimeMs);
        cmd.Parameters.AddWithValue("@event_type", evt.EventType);
        cmd.Parameters.AddWithValue("@value", evt.Value);
        cmd.Parameters.AddWithValue("@data_json", evt.DataJson);
        cmd.ExecuteNonQuery();
    }

    private static void UpsertTiming(
        SqliteConnection con,
        SqliteTransaction tx,
        long attemptId,
        AttemptSnapshot snapshot)
    {
        using var cmd = con.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO attempt_timing(attempt_id, offsets_zlib, hit_count, early_count,
                                       late_count, mean, median, deviation)
            VALUES(@attempt_id, @offsets, @hit_count, @early_count, @late_count,
                   @mean, @median, @deviation)
            ON CONFLICT(attempt_id) DO UPDATE SET
                offsets_zlib = excluded.offsets_zlib,
                hit_count = excluded.hit_count,
                early_count = excluded.early_count,
                late_count = excluded.late_count,
                mean = excluded.mean,
                median = excluded.median,
                deviation = excluded.deviation
            """;
        var offsets = snapshot.TimingOffsets.ToArray();
        var sorted = offsets.OrderBy(v => v).ToArray();
        var mean = offsets.Length > 0 ? offsets.Average() : 0;
        var median = sorted.Length switch
        {
            0 => 0,
            var n when n % 2 == 1 => sorted[n / 2],
            var n => (sorted[n / 2 - 1] + sorted[n / 2]) / 2,
        };
        var deviation = offsets.Length > 0
            ? Math.Sqrt(offsets.Average(v => Math.Pow(v - mean, 2)))
            : 0;
        cmd.Parameters.AddWithValue("@attempt_id", attemptId);
        cmd.Parameters.Add("@offsets", SqliteType.Blob).Value = BlobCodec.EncodeOffsets(offsets);
        cmd.Parameters.AddWithValue("@hit_count", offsets.Length);
        cmd.Parameters.AddWithValue("@early_count", offsets.Count(v => v < 0));
        cmd.Parameters.AddWithValue("@late_count", offsets.Count(v => v > 0));
        cmd.Parameters.AddWithValue("@mean", mean);
        cmd.Parameters.AddWithValue("@median", median);
        cmd.Parameters.AddWithValue("@deviation", deviation);
        cmd.ExecuteNonQuery();
    }

    private static void UpsertContext(
        SqliteConnection con,
        SqliteTransaction tx,
        long attemptId,
        AttemptSnapshot snapshot)
    {
        using var cmd = con.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO attempt_context(attempt_id, source_json, pp_json, beatmap_json,
                                        score_json, session_json, multiplayer_json)
            VALUES(@attempt_id, '{}', @pp_json, @beatmap_json, @score_json, '{}', '{}')
            ON CONFLICT(attempt_id) DO UPDATE SET
                pp_json = excluded.pp_json,
                beatmap_json = excluded.beatmap_json,
                score_json = excluded.score_json
            """;
        cmd.Parameters.AddWithValue("@attempt_id", attemptId);
        cmd.Parameters.AddWithValue("@pp_json", JsonSerializer.Serialize(new { pp = snapshot.Pp, fc_pp = snapshot.FcPp, max_pp = snapshot.MaxPp }));
        cmd.Parameters.AddWithValue("@beatmap_json", BeatmapContextJson(snapshot.BeatmapStats));
        cmd.Parameters.AddWithValue("@score_json", JsonSerializer.Serialize(new
        {
            score = snapshot.Score,
            grade = snapshot.Grade ?? "",
            hits = new
            {
                _300 = snapshot.N300,
                _100 = snapshot.N100,
                _50 = snapshot.N50,
                _0 = snapshot.Misses,
                geki = snapshot.Geki,
                katu = snapshot.Katu,
                largeTickHits = snapshot.LargeTickHits,
                largeTickMisses = snapshot.LargeTickMisses,
                smallTickHits = snapshot.SmallTickHits,
                smallTickMisses = snapshot.SmallTickMisses,
                sliderTailHits = snapshot.SliderTailHits,
                sliderTailMisses = snapshot.SliderTailMisses,
                sliderBreaks = snapshot.SliderBreaks,
            },
        }));
        cmd.ExecuteNonQuery();
    }

    private static void UpsertSourceContext(
        SqliteConnection con,
        SqliteTransaction tx,
        long attemptId,
        AttemptStart start)
    {
        string? beatmapPath = start.ClientKind == OsuClientKind.Stable ? ResolveStableBeatmapPath(start) : null;
        string? mediaDirectory = beatmapPath is null ? null : Path.GetDirectoryName(beatmapPath);
        using var cmd = con.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO attempt_context(attempt_id, source_json, pp_json, beatmap_json,
                                        score_json, session_json, multiplayer_json)
            VALUES(@attempt_id, @source_json, '{}', '{}', '{}', '{}', '{}')
            ON CONFLICT(attempt_id) DO UPDATE SET source_json = excluded.source_json
            """;
        cmd.Parameters.AddWithValue("@attempt_id", attemptId);
        cmd.Parameters.AddWithValue("@source_json", JsonSerializer.Serialize(new
        {
            client_kind = start.ClientKind.ToString().ToLowerInvariant(),
            beatmap_path = beatmapPath,
            media_directory = mediaDirectory,
            game_folder = start.GameFolder,
            songs_folder = start.SongsFolder,
        }));
        cmd.ExecuteNonQuery();
    }

    private static string? ResolveStableBeatmapPath(AttemptStart start)
    {
        if (string.IsNullOrWhiteSpace(start.BeatmapFile))
            return null;
        string file = start.BeatmapFile;
        var candidates = new List<string>();
        if (Path.IsPathRooted(file))
            candidates.Add(file);
        string? songs = start.SongsFolder;
        if (!string.IsNullOrWhiteSpace(songs) && !Path.IsPathRooted(songs) && !string.IsNullOrWhiteSpace(start.GameFolder))
            songs = Path.Combine(start.GameFolder, songs);
        if (!string.IsNullOrWhiteSpace(songs))
            candidates.Add(Path.Combine(songs, file));
        if (!string.IsNullOrWhiteSpace(start.BeatmapFolder))
        {
            string folder = start.BeatmapFolder;
            if (!Path.IsPathRooted(folder) && !string.IsNullOrWhiteSpace(songs))
                folder = Path.Combine(songs, folder);
            candidates.Add(Path.Combine(folder, Path.GetFileName(file)));
        }
        if (!string.IsNullOrWhiteSpace(start.GameFolder))
            candidates.Add(Path.Combine(start.GameFolder, "Songs", file));
        return candidates.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).FirstOrDefault(File.Exists);
    }

    private static string BeatmapContextJson(BeatmapStats stats)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(stats.RawJson) ? "{}" : stats.RawJson);
            return JsonSerializer.Serialize(new { stats = doc.RootElement });
        }
        catch (JsonException ex)
        {
            Log.Warning(ex, "Invalid beatmap stats JSON while writing attempt context");
            return "{}";
        }
    }

    private static void UpsertInputSummary(SqliteConnection con, SqliteTransaction tx, long attemptId)
    {
        using var cmd = con.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO attempt_input_summary(attempt_id)
            VALUES(@attempt_id)
            ON CONFLICT(attempt_id) DO NOTHING
            """;
        cmd.Parameters.AddWithValue("@attempt_id", attemptId);
        cmd.ExecuteNonQuery();
    }

    private static void UpdatePersonalBests(SqliteConnection con, SqliteTransaction tx, long attemptId)
    {
        using var rowCmd = con.CreateCommand();
        rowCmd.Transaction = tx;
        rowCmd.CommandText = """
            SELECT beatmap_id, mods_key, score, accuracy, pp, combo, misses
            FROM attempts WHERE id = @id
            """;
        rowCmd.Parameters.AddWithValue("@id", attemptId);
        using var reader = rowCmd.ExecuteReader();
        if (!reader.Read())
        {
            return;
        }

        var beatmapId = reader.GetInt64(0);
        var modsKey = reader.GetString(1);
        var metrics = new Dictionary<string, double>
        {
            ["score"] = reader.GetDouble(2),
            ["accuracy"] = reader.GetDouble(3),
            ["pp"] = reader.GetDouble(4),
            ["combo"] = reader.GetDouble(5),
            ["fewest_misses"] = reader.GetDouble(6),
        };
        reader.Close();

        foreach (var (metric, value) in metrics)
        {
            var lowerIsBetter = metric == "fewest_misses";
            using var existing = con.CreateCommand();
            existing.Transaction = tx;
            existing.CommandText = """
                SELECT value FROM personal_bests
                WHERE beatmap_id = @beatmap_id AND mods_key = @mods_key AND metric = @metric
                """;
            existing.Parameters.AddWithValue("@beatmap_id", beatmapId);
            existing.Parameters.AddWithValue("@mods_key", modsKey);
            existing.Parameters.AddWithValue("@metric", metric);
            var previous = existing.ExecuteScalar();
            var improved = previous is null || previous == DBNull.Value
                || (lowerIsBetter ? value < Convert.ToDouble(previous) : value > Convert.ToDouble(previous));
            if (!improved)
            {
                continue;
            }

            using var best = con.CreateCommand();
            best.Transaction = tx;
            best.CommandText = """
                INSERT INTO personal_bests(beatmap_id, mods_key, metric, attempt_id, value)
                VALUES(@beatmap_id, @mods_key, @metric, @attempt_id, @value)
                ON CONFLICT(beatmap_id, mods_key, metric) DO UPDATE SET
                    attempt_id = excluded.attempt_id,
                    value = excluded.value
                """;
            best.Parameters.AddWithValue("@beatmap_id", beatmapId);
            best.Parameters.AddWithValue("@mods_key", modsKey);
            best.Parameters.AddWithValue("@metric", metric);
            best.Parameters.AddWithValue("@attempt_id", attemptId);
            best.Parameters.AddWithValue("@value", value);
            best.ExecuteNonQuery();
        }
    }

    private static string IsoFromUnixSeconds(double unixSeconds) =>
        DateTimeOffset.FromUnixTimeMilliseconds((long)(unixSeconds * 1000)).ToString("O");

    private static long UnixMilliseconds(double unixSeconds) => (long)(unixSeconds * 1000);

    private static void EnsureColumn(SqliteConnection con, SqliteTransaction tx, string table, string column, string definition)
    {
        using (var info = con.CreateCommand())
        {
            info.Transaction = tx;
            info.CommandText = $"PRAGMA table_info({table})";
            using var reader = info.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
        }

        using var alter = con.CreateCommand();
        alter.Transaction = tx;
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition}";
        alter.ExecuteNonQuery();
    }
}
