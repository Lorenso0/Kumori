using Kumori.Tracking;
using Serilog;
using System.Runtime.ExceptionServices;

namespace Kumori.App;

/// <summary>
/// Serializes non-essential disk/analysis work and keeps it off the gameplay
/// critical path. Work interrupted by a new attempt is retained and retried
/// once gameplay has been idle for a short settling period.
/// </summary>
internal sealed class GameplayWorkCoordinator : IDisposable
{
    private static readonly TimeSpan DefaultIdleSettleDelay = TimeSpan.FromSeconds(2);
    private readonly object gate = new();
    private readonly object gameplayTransitionGate = new();
    private readonly CancellationToken applicationToken;
    private readonly TimeSpan idleSettleDelay;
    private readonly Queue<WorkItem> priorityPending = new();
    private readonly Queue<WorkItem> pending = new();
    private readonly Dictionary<string, WorkItem> pendingByKey = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource shutdownSettleInterrupt = new();
    private CancellationTokenSource gameplayInterrupt = new();
    private CancellationTokenSource priorityInterrupt = new();
    private TaskCompletionSource idleSignal = CompletedSignal();
    private Task? worker;
    private WorkItem? currentItem;
    private bool gameplayActive;
    private bool idleSettled = true;
    private bool shutdownDraining;
    private bool disposed;

    public GameplayWorkCoordinator(
        CancellationToken applicationToken = default,
        TimeSpan? idleSettleDelay = null)
    {
        this.applicationToken = applicationToken;
        this.idleSettleDelay = idleSettleDelay ?? DefaultIdleSettleDelay;
    }

    public bool IsGameplayActive
    {
        get
        {
            lock (gate)
                return gameplayActive;
        }
    }

    public void BeginGameplay()
    {
        lock (gameplayTransitionGate)
        {
            CancellationTokenSource interrupted;
            TaskCompletionSource previousIdleSignal;
            lock (gate)
            {
                if (disposed || shutdownDraining)
                    return;

                previousIdleSignal = idleSignal;
                gameplayActive = true;
                idleSettled = false;
                idleSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                interrupted = gameplayInterrupt;
                gameplayInterrupt = new CancellationTokenSource();
            }

            previousIdleSignal.TrySetResult();
            // CancelAsync avoids running arbitrary cancellation callbacks inline on
            // the tosu packet thread that announced the new attempt.
            _ = interrupted.CancelAsync().ContinueWith(
                _ => interrupted.Dispose(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    /// <summary>
    /// Executes the short close/relaunch portion of a companion transition
    /// atomically with respect to attempt start. Readiness waits happen after
    /// this returns and never hold the gameplay packet thread.
    /// </summary>
    internal T ExecuteGameplayExcludingTransition<T>(
        CancellationToken cancellationToken,
        Func<T> transition)
    {
        if (!TryExecuteGameplayExcludingTransition(cancellationToken, transition, out var result))
            throw new OperationCanceledException(cancellationToken);
        return result;
    }

    /// <summary>
    /// Attempts a short disruptive transition only if gameplay has not begun.
    /// Once admitted, attempt start waits for the transition to finish.
    /// </summary>
    internal bool TryExecuteGameplayExcludingTransition<T>(
        CancellationToken cancellationToken,
        Func<T> transition,
        out T result)
    {
        ArgumentNullException.ThrowIfNull(transition);
        lock (gameplayTransitionGate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate)
            {
                if (gameplayActive)
                {
                    result = default!;
                    return false;
                }
            }
            result = transition();
            return true;
        }
    }

    public void EndGameplay()
    {
        TaskCompletionSource signal;
        lock (gate)
        {
            if (disposed)
                return;

            gameplayActive = false;
            signal = idleSignal;
        }
        signal.TrySetResult();
    }

    /// <summary>
    /// Seals gameplay transitions and releases queued core work immediately
    /// after live tracking has stopped. Normal runtime callers still pay the
    /// post-game settle delay; this is only for the final bounded shutdown
    /// drain, where no new attempt can begin.
    /// </summary>
    internal void BeginShutdownDrain()
    {
        TaskCompletionSource signal;
        lock (gameplayTransitionGate)
        {
            lock (gate)
            {
                if (disposed)
                    return;

                shutdownDraining = true;
                gameplayActive = false;
                idleSettled = true;
                signal = idleSignal;
            }
            signal.TrySetResult();
            shutdownSettleInterrupt.Cancel();
        }
    }

    public Task Enqueue(string key, Func<CancellationToken, Task> work, bool coalesce = false)
        => EnqueueCore(key, work, coalesce, priority: false);

    /// <summary>
    /// Queues a prerequisite that must run before ordinary idle work queued
    /// during the same gameplay window. The worker selects work only after the
    /// idle window opens, so an earlier capture cannot get stuck ahead of a
    /// parent-row commit while gameplay is still active.
    /// </summary>
    internal Task EnqueuePriority(string key, Func<CancellationToken, Task> work, bool coalesce = false)
        => EnqueueCore(key, work, coalesce, priority: true);

    private Task EnqueueCore(
        string key,
        Func<CancellationToken, Task> work,
        bool coalesce,
        bool priority)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(work);

        CancellationTokenSource? interruptedOrdinaryWork = null;
        Task completion;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (coalesce
                && pendingByKey.TryGetValue(key, out var existing)
                && !existing.Executing)
            {
                existing.Work = work;
                completion = existing.Completion.Task;
            }
            else
            {
                var item = new WorkItem(key, work, priority);
                if (priority)
                    priorityPending.Enqueue(item);
                else
                    pending.Enqueue(item);
                if (coalesce)
                    pendingByKey[key] = item;
                worker ??= Task.Run(RunAsync, CancellationToken.None);
                completion = item.Completion.Task;
            }

            // Priority work represents a core correctness prerequisite. If an
            // ordinary cancellation-aware maintenance item is already running,
            // move it back to the queue so the priority item can run next.
            if (priority && currentItem is { Priority: false })
            {
                interruptedOrdinaryWork = priorityInterrupt;
                priorityInterrupt = new CancellationTokenSource();
            }
        }

        if (interruptedOrdinaryWork is not null)
        {
            _ = interruptedOrdinaryWork.CancelAsync().ContinueWith(
                _ => interruptedOrdinaryWork.Dispose(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        return completion;
    }

    /// <summary>
    /// Runs a bounded retry sequence without retaining the single maintenance
    /// worker during the delay between attempts. Each attempt is appended to
    /// the queue independently, so unrelated work gets a fair turn. Gameplay
    /// interruption is still handled by <see cref="RunAsync"/> and retries the
    /// interrupted attempt without consuming one of <paramref name="maxAttempts"/>.
    /// Priority sequences also preempt cancellation-aware ordinary maintenance
    /// before each attempt, while retry delays remain outside the worker.
    /// </summary>
    internal async Task<bool> RunFairRetryLoopAsync(
        string key,
        int maxAttempts,
        TimeSpan retryDelay,
        Func<int, CancellationToken, Task<bool>> attempt,
        CancellationToken cancellationToken = default,
        bool priority = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(retryDelay, TimeSpan.Zero);
        ArgumentNullException.ThrowIfNull(attempt);

        using var sequenceCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            applicationToken,
            cancellationToken);
        var sequenceToken = sequenceCancellation.Token;
        for (var attemptIndex = 0; attemptIndex < maxAttempts; attemptIndex++)
        {
            sequenceToken.ThrowIfCancellationRequested();

            var completed = false;
            ExceptionDispatchInfo? failure = null;
            async Task RunQueuedAttempt(CancellationToken gameplayToken)
            {
                // The coordinator owns gameplay/application cancellation. Link
                // the caller token only for the duration of this one pass.
                using var operation = CancellationTokenSource.CreateLinkedTokenSource(
                    gameplayToken,
                    sequenceToken);
                try
                {
                    completed = await attempt(attemptIndex, operation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (
                    !gameplayToken.IsCancellationRequested &&
                    sequenceToken.IsCancellationRequested)
                {
                    // Complete this queued item normally. The outer loop throws
                    // below, avoiding a canceled orphan item in the worker.
                }
                catch (Exception ex) when (
                    ex is not OperationCanceledException ||
                    !gameplayToken.IsCancellationRequested)
                {
                    // Enqueue intentionally isolates maintenance failures. Keep
                    // that behavior for the worker, then surface the failure to
                    // the owner of this retry sequence.
                    failure = ExceptionDispatchInfo.Capture(ex);
                }
            }
            Task queuedAttempt = priority
                ? EnqueuePriority(key, RunQueuedAttempt)
                : Enqueue(key, RunQueuedAttempt);
            await queuedAttempt.ConfigureAwait(false);

            sequenceToken.ThrowIfCancellationRequested();
            failure?.Throw();
            if (completed)
                return true;

            if (attemptIndex + 1 < maxAttempts)
                await Task.Delay(retryDelay, sequenceToken).ConfigureAwait(false);
        }

        return false;
    }

    private async Task RunAsync()
    {
        while (!applicationToken.IsCancellationRequested)
        {
            lock (gate)
            {
                if (priorityPending.Count == 0 && pending.Count == 0)
                {
                    worker = null;
                    return;
                }
            }

            // Do not reserve an ordinary item while gameplay is active. More
            // work can be queued by the remaining Finalize sinks, including a
            // priority attempt-parent commit required by capture persistence.
            var initialInterrupt = await WaitForIdleWindowAsync(applicationToken).ConfigureAwait(false);

            WorkItem? item;
            CancellationToken priorityCancellation;
            lock (gate)
            {
                if (priorityPending.Count > 0)
                {
                    item = priorityPending.Dequeue();
                }
                else if (pending.Count > 0)
                {
                    item = pending.Dequeue();
                }
                else
                {
                    continue;
                }
                item.Executing = true;
                currentItem = item;
                priorityCancellation = item.Priority
                    ? CancellationToken.None
                    : priorityInterrupt.Token;
            }

            try
            {
                var firstPass = true;
                var interruptedByGameplay = false;
                while (!applicationToken.IsCancellationRequested)
                {
                    var interrupt = firstPass
                        ? initialInterrupt
                        : await WaitForIdleWindowAsync(applicationToken).ConfigureAwait(false);
                    firstPass = false;
                    using var operation = priorityCancellation.CanBeCanceled
                        ? CancellationTokenSource.CreateLinkedTokenSource(
                            applicationToken,
                            interrupt,
                            priorityCancellation)
                        : CancellationTokenSource.CreateLinkedTokenSource(applicationToken, interrupt);
                    try
                    {
                        await item.Work(operation.Token).ConfigureAwait(false);
                        item.Completion.TrySetResult();
                        break;
                    }
                    catch (OperationCanceledException) when (!applicationToken.IsCancellationRequested && interrupt.IsCancellationRequested)
                    {
                        Log.Debug("Gameplay interrupted deferred operation {Operation}; it was requeued behind core idle work", item.Key);
                        lock (gate)
                        {
                            item.Executing = false;
                            if (item.Priority)
                                priorityPending.Enqueue(item);
                            else
                                pending.Enqueue(item);
                        }
                        interruptedByGameplay = true;
                        break;
                    }
                    catch (OperationCanceledException) when (
                        !applicationToken.IsCancellationRequested
                        && priorityCancellation.IsCancellationRequested)
                    {
                        Log.Debug("Core priority work preempted deferred operation {Operation}; it was requeued", item.Key);
                        lock (gate)
                        {
                            item.Executing = false;
                            pending.Enqueue(item);
                        }
                        interruptedByGameplay = true;
                        break;
                    }
                }
                if (interruptedByGameplay)
                    continue;
                if (applicationToken.IsCancellationRequested)
                    item.Completion.TrySetCanceled(applicationToken);
            }
            catch (OperationCanceledException) when (applicationToken.IsCancellationRequested)
            {
                item.Completion.TrySetCanceled(applicationToken);
                return;
            }
            catch (Exception ex)
            {
                // A failed maintenance item must not stop later recovery work.
                Log.Warning(ex, "Deferred operation {Operation} failed", item.Key);
                item.Completion.TrySetResult();
            }
            finally
            {
                lock (gate)
                {
                    if (ReferenceEquals(currentItem, item))
                        currentItem = null;
                    if (item.Executing &&
                        pendingByKey.TryGetValue(item.Key, out var registered) &&
                        ReferenceEquals(registered, item))
                        pendingByKey.Remove(item.Key);
                }
            }
        }
    }

    private async Task<CancellationToken> WaitForIdleWindowAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task wait;
            CancellationToken interrupt;
            lock (gate)
            {
                wait = gameplayActive ? idleSignal.Task : Task.CompletedTask;
                interrupt = gameplayInterrupt.Token;
                if (!gameplayActive && idleSettled)
                    return interrupt;
            }

            await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
            using var settle = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                interrupt,
                shutdownSettleInterrupt.Token);
            try
            {
                await Task.Delay(idleSettleDelay, settle.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                !cancellationToken.IsCancellationRequested &&
                (interrupt.IsCancellationRequested || shutdownSettleInterrupt.IsCancellationRequested))
            {
                continue;
            }

            lock (gate)
            {
                if (!gameplayActive && interrupt == gameplayInterrupt.Token)
                {
                    idleSettled = true;
                    return interrupt;
                }
            }
        }
    }

    public void Dispose()
    {
        CancellationTokenSource interrupt;
        CancellationTokenSource priority;
        WorkItem[] abandoned;
        lock (gate)
        {
            if (disposed)
                return;
            disposed = true;
            gameplayActive = false;
            idleSignal.TrySetResult();
            interrupt = gameplayInterrupt;
            priority = priorityInterrupt;
            abandoned = priorityPending.Concat(pending).ToArray();
            priorityPending.Clear();
            pending.Clear();
            pendingByKey.Clear();
        }
        interrupt.Cancel();
        interrupt.Dispose();
        priority.Cancel();
        priority.Dispose();
        shutdownSettleInterrupt.Cancel();
        shutdownSettleInterrupt.Dispose();
        foreach (var item in abandoned)
            item.Completion.TrySetCanceled();
    }

    private static TaskCompletionSource CompletedSignal()
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        signal.SetResult();
        return signal;
    }

    private sealed class WorkItem(string key, Func<CancellationToken, Task> work, bool priority)
    {
        public string Key { get; } = key;
        public Func<CancellationToken, Task> Work { get; set; } = work;
        public bool Priority { get; } = priority;
        public bool Executing { get; set; }
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

/// <summary>Publishes authoritative attempt activity around the real sink.</summary>
internal sealed class GameplayActivityAttemptSink(IAttemptSink inner, GameplayWorkCoordinator coordinator) : IAttemptSink
{
    public void StartAttempt(AttemptStart start)
    {
        coordinator.BeginGameplay();
        try { inner.StartAttempt(start); }
        catch
        {
            coordinator.EndGameplay();
            throw;
        }
    }

    public void Checkpoint(AttemptCheckpoint checkpoint) => inner.Checkpoint(checkpoint);

    public void DiscardIfEmpty(AttemptDiscard discard)
    {
        try { inner.DiscardIfEmpty(discard); }
        finally { coordinator.EndGameplay(); }
    }

    public void Finalize(AttemptFinalization finalization)
    {
        try { inner.Finalize(finalization); }
        finally { coordinator.EndGameplay(); }
    }
}
