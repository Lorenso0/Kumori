using Kumori.Tracking;
using Microsoft.Data.Sqlite;
using Serilog;
using System.Text.Json;

namespace Kumori.Storage;

public sealed partial class AttemptSqliteSink : IAttemptSink, ISessionSink
{
    internal const int IdReservationBlockSize = 64;

    private readonly SqliteConnectionFactory _factory;
    private readonly Func<string, Func<CancellationToken, Task>, Task>? _deferPersistence;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _persistenceDrainGate = new(1, 1);
    private readonly Queue<PersistenceOperation> _persistenceQueue = [];
    private readonly string _persistenceInstanceId = Guid.NewGuid().ToString("N");
    private long? _sessionId;
    private long? _attemptId;
    private long _nextReservedSessionId;
    private long _reservedSessionIdEndExclusive;
    private long _nextReservedAttemptId;
    private long _reservedAttemptIdEndExclusive;
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
            INSERT INTO sessions(id, started_at, started_at_utc_ms, key1_binding, key2_binding)
            VALUES(@id, @started_at, @started_at_utc_ms, 'Z', 'X')
            ON CONFLICT(id) DO NOTHING
            """;
        cmd.Parameters.AddWithValue("@id", sessionId);
        var startedAt = IsoFromUnixSeconds(start.WallTime);
        cmd.Parameters.AddWithValue("@started_at", startedAt);
        cmd.Parameters.AddWithValue("@started_at_utc_ms", UnixMilliseconds(start.WallTime));
        if (cmd.ExecuteNonQuery() == 1)
        {
            return;
        }

        using var verify = con.CreateCommand();
        verify.Transaction = tx;
        verify.CommandText = "SELECT started_at FROM sessions WHERE id = @id";
        verify.Parameters.AddWithValue("@id", sessionId);
        if (!string.Equals(Convert.ToString(verify.ExecuteScalar()), startedAt, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Session ID {sessionId} is already owned by a different tracking session.");
        }
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

}
