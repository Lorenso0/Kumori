using System.Diagnostics;
using Xunit;

namespace Kumori.App.Tests;

public sealed class GameplayWorkCoordinatorTests
{
    [Fact]
    public async Task ColdIdleWorkStartsWithoutSettleDelay()
    {
        var settleDelay = TimeSpan.FromSeconds(2);
        using var coordinator = new GameplayWorkCoordinator(idleSettleDelay: settleDelay);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task work = coordinator.Enqueue("cold-idle", _ =>
        {
            started.TrySetResult();
            return Task.CompletedTask;
        });

        await started.Task.WaitAsync(TimeSpan.FromMilliseconds(750));
        await work.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task GameplayIdleSettleDelayIsPaidOnceBeforeQueuedWorkDrains()
    {
        var settleDelay = TimeSpan.FromMilliseconds(1_200);
        using var coordinator = new GameplayWorkCoordinator(idleSettleDelay: settleDelay);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thirdStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        coordinator.BeginGameplay();
        Task first = coordinator.Enqueue("first", async token =>
        {
            firstStarted.TrySetResult();
            await releaseFirst.Task.WaitAsync(token);
        });
        Task second = coordinator.Enqueue("second", _ =>
        {
            secondStarted.TrySetResult();
            return Task.CompletedTask;
        });
        Task third = coordinator.Enqueue("third", _ =>
        {
            thirdStarted.TrySetResult();
            return Task.CompletedTask;
        });

        var idleStartedAt = Stopwatch.GetTimestamp();
        coordinator.EndGameplay();

        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(4));
        Assert.True(
            Stopwatch.GetElapsedTime(idleStartedAt) >= TimeSpan.FromMilliseconds(900),
            "The first queued item started before the configured idle settle window elapsed.");

        releaseFirst.TrySetResult();
        await Task.WhenAll(secondStarted.Task, thirdStarted.Task)
            .WaitAsync(TimeSpan.FromMilliseconds(500));
        await Task.WhenAll(first, second, third).WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task ShutdownDrainReleasesPriorityWorkWithoutRuntimeSettleDelay()
    {
        using var coordinator = new GameplayWorkCoordinator(
            idleSettleDelay: TimeSpan.FromSeconds(30));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        coordinator.BeginGameplay();
        Task replayPersistence = coordinator.EnqueuePriority("replay-persistence", _ =>
        {
            started.TrySetResult();
            return Task.CompletedTask;
        });
        coordinator.EndGameplay();

        await Task.Delay(50);
        Assert.False(started.Task.IsCompleted);

        coordinator.BeginShutdownDrain();

        await started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await replayPersistence.WaitAsync(TimeSpan.FromSeconds(1));

        // The coordinator is sealed only for shutdown. A late source callback
        // cannot reopen gameplay and re-block the final persistence drain.
        coordinator.BeginGameplay();
        Assert.False(coordinator.IsGameplayActive);
    }

    [Fact]
    public async Task RunsDeferredWorkSerially()
    {
        using var coordinator = new GameplayWorkCoordinator(idleSettleDelay: TimeSpan.Zero);
        var concurrent = 0;
        var maximum = 0;

        Task Run(string key) => coordinator.Enqueue(key, async token =>
        {
            int current = Interlocked.Increment(ref concurrent);
            maximum = Math.Max(maximum, current);
            await Task.Delay(20, token);
            Interlocked.Decrement(ref concurrent);
        });

        await Task.WhenAll(Run("one"), Run("two"), Run("three"));

        Assert.Equal(1, maximum);
    }

    [Fact]
    public async Task CoalescesPendingWorkAndUsesLatestDelegate()
    {
        using var coordinator = new GameplayWorkCoordinator(idleSettleDelay: TimeSpan.Zero);
        var blockerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBlocker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var value = 0;

        Task blocker = coordinator.Enqueue("blocker", async token =>
        {
            blockerStarted.SetResult();
            await releaseBlocker.Task.WaitAsync(token);
        });
        await blockerStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Task first = coordinator.Enqueue("dashboard", _ =>
        {
            value = 1;
            return Task.CompletedTask;
        }, coalesce: true);
        Task latest = coordinator.Enqueue("dashboard", _ =>
        {
            value = 2;
            return Task.CompletedTask;
        }, coalesce: true);

        Assert.Same(first, latest);
        releaseBlocker.SetResult();
        await Task.WhenAll(blocker, latest).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, value);
    }

    [Fact]
    public async Task GameplayCancelsCurrentWorkAndRetriesItAfterAttempt()
    {
        using var coordinator = new GameplayWorkCoordinator(idleSettleDelay: TimeSpan.Zero);
        var firstRunStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runs = 0;

        Task work = coordinator.Enqueue("recovery", async token =>
        {
            if (Interlocked.Increment(ref runs) == 1)
            {
                firstRunStarted.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
        });

        await firstRunStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        coordinator.BeginGameplay();
        await Task.Delay(20);
        Assert.False(work.IsCompleted);

        coordinator.EndGameplay();
        await work.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, runs);
    }

    [Fact]
    public async Task InterruptedMaintenanceYieldsToCorePriorityWorkAfterGameplay()
    {
        using var coordinator = new GameplayWorkCoordinator(idleSettleDelay: TimeSpan.Zero);
        var maintenanceStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new List<string>();
        var runs = 0;

        Task maintenance = coordinator.Enqueue("maintenance", async token =>
        {
            if (Interlocked.Increment(ref runs) == 1)
            {
                maintenanceStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            order.Add("maintenance");
        });

        await maintenanceStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        coordinator.BeginGameplay();
        Task core = coordinator.EnqueuePriority("replay-persistence", _ =>
        {
            order.Add("core");
            return Task.CompletedTask;
        });
        coordinator.EndGameplay();

        await Task.WhenAll(core, maintenance).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(new[] { "core", "maintenance" }, order);
    }

    [Fact]
    public async Task CorePriorityWorkPreemptsRunningCancellationAwareMaintenance()
    {
        using var coordinator = new GameplayWorkCoordinator(idleSettleDelay: TimeSpan.Zero);
        var maintenanceStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new List<string>();
        var runs = 0;

        Task maintenance = coordinator.Enqueue("reconciliation", async token =>
        {
            if (Interlocked.Increment(ref runs) == 1)
            {
                maintenanceStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            order.Add("reconciliation");
        });

        await maintenanceStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task restart = coordinator.EnqueuePriority("mandatory-restart", _ =>
        {
            order.Add("restart");
            return Task.CompletedTask;
        });

        await Task.WhenAll(restart, maintenance).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(new[] { "restart", "reconciliation" }, order);
    }

    [Fact]
    public async Task PriorityRetrySequencePreemptsReconciliation()
    {
        using var coordinator = new GameplayWorkCoordinator(idleSettleDelay: TimeSpan.Zero);
        var reconciliationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new List<string>();
        var runs = 0;

        Task reconciliation = coordinator.Enqueue("reconciliation", async token =>
        {
            if (Interlocked.Increment(ref runs) == 1)
            {
                reconciliationStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            order.Add("reconciliation");
        });

        await reconciliationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task<bool> restart = coordinator.RunFairRetryLoopAsync(
            "mandatory-restart",
            maxAttempts: 1,
            retryDelay: TimeSpan.Zero,
            (_, _) =>
            {
                order.Add("restart");
                return Task.FromResult(true);
            },
            priority: true);

        Assert.True(await restart.WaitAsync(TimeSpan.FromSeconds(2)));
        await reconciliation.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(new[] { "restart", "reconciliation" }, order);
    }

    [Fact]
    public async Task ParentCommitQueuedLastRunsBeforeCaptureWorkQueuedDuringGameplay()
    {
        using var coordinator = new GameplayWorkCoordinator(idleSettleDelay: TimeSpan.Zero);
        coordinator.BeginGameplay();
        var order = new List<string>();

        Task stableLive = coordinator.Enqueue("stable-live", _ =>
        {
            order.Add("stable-live");
            return Task.CompletedTask;
        });
        Task stableExact = coordinator.Enqueue("stable-exact", _ =>
        {
            order.Add("stable-exact");
            return Task.CompletedTask;
        });

        await Task.Delay(30);
        Assert.Empty(order);

        Task parent = coordinator.EnqueuePriority("attempt-parent", _ =>
        {
            order.Add("attempt-parent");
            return Task.CompletedTask;
        });
        coordinator.EndGameplay();

        await Task.WhenAll(parent, stableLive, stableExact).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(new[] { "attempt-parent", "stable-live", "stable-exact" }, order);
    }

    [Fact]
    public async Task RetryBackoffDoesNotOccupyWorker()
    {
        using var coordinator = new GameplayWorkCoordinator(idleSettleDelay: TimeSpan.Zero);
        using var cancellation = new CancellationTokenSource();
        var firstPassFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<bool> retries = coordinator.RunFairRetryLoopAsync(
            "replay-recovery",
            maxAttempts: 2,
            retryDelay: TimeSpan.FromSeconds(5),
            (pass, _) =>
            {
                if (pass == 0)
                    firstPassFinished.TrySetResult();
                return Task.FromResult(false);
            },
            cancellation.Token);

        await firstPassFinished.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var unrelatedRan = false;
        await coordinator.Enqueue("capture-persistence", _ =>
        {
            unrelatedRan = true;
            return Task.CompletedTask;
        }).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(unrelatedRan);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => retries);
    }

    [Fact]
    public async Task GameplayInterruptionRetriesSameFairLoopPass()
    {
        using var coordinator = new GameplayWorkCoordinator(idleSettleDelay: TimeSpan.Zero);
        var firstRunStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var passIndexes = new List<int>();
        var runs = 0;

        Task<bool> retries = coordinator.RunFairRetryLoopAsync(
            "replay-recovery",
            maxAttempts: 2,
            retryDelay: TimeSpan.Zero,
            async (pass, token) =>
            {
                lock (passIndexes)
                    passIndexes.Add(pass);
                if (Interlocked.Increment(ref runs) == 1)
                {
                    firstRunStarted.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                return true;
            });

        await firstRunStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        coordinator.BeginGameplay();
        await Task.Delay(20);
        coordinator.EndGameplay();
        Assert.True(await retries.WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.Equal(new[] { 0, 0 }, passIndexes);
    }

    [Fact]
    public async Task ApplicationCancellationInterruptsRetryBackoff()
    {
        using var application = new CancellationTokenSource();
        using var coordinator = new GameplayWorkCoordinator(
            application.Token,
            idleSettleDelay: TimeSpan.Zero);
        var firstPassFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<bool> retries = coordinator.RunFairRetryLoopAsync(
            "replay-recovery",
            maxAttempts: 2,
            retryDelay: TimeSpan.FromSeconds(30),
            (_, _) =>
            {
                firstPassFinished.TrySetResult();
                return Task.FromResult(false);
            });

        await firstPassFinished.Task.WaitAsync(TimeSpan.FromSeconds(2));
        application.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => retries.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task AttemptStartWaitsUntilAtomicCompanionTransitionFinishes()
    {
        using var coordinator = new GameplayWorkCoordinator(idleSettleDelay: TimeSpan.Zero);
        var transitionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTransition = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var transition = Task.Run(() => coordinator.ExecuteGameplayExcludingTransition(
            CancellationToken.None,
            () =>
            {
                transitionStarted.SetResult();
                releaseTransition.Task.GetAwaiter().GetResult();
                return true;
            }));
        await transitionStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var beginGameplay = Task.Run(coordinator.BeginGameplay);
        await Task.Delay(30);
        Assert.False(beginGameplay.IsCompleted);
        Assert.False(coordinator.IsGameplayActive);

        releaseTransition.SetResult();
        Assert.True(await transition.WaitAsync(TimeSpan.FromSeconds(2)));
        await beginGameplay.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(coordinator.IsGameplayActive);
    }

    [Fact]
    public void AtomicCompanionTransitionCannotStartDuringGameplay()
    {
        using var coordinator = new GameplayWorkCoordinator(idleSettleDelay: TimeSpan.Zero);
        coordinator.BeginGameplay();
        var transitionRan = false;

        Assert.Throws<OperationCanceledException>(() =>
            coordinator.ExecuteGameplayExcludingTransition(
                CancellationToken.None,
                () => transitionRan = true));

        Assert.False(transitionRan);
    }

    [Fact]
    public void TryAtomicCompanionTransitionReportsSkipDuringGameplay()
    {
        using var coordinator = new GameplayWorkCoordinator(idleSettleDelay: TimeSpan.Zero);
        coordinator.BeginGameplay();
        var transitionRan = false;

        var admitted = coordinator.TryExecuteGameplayExcludingTransition(
            CancellationToken.None,
            () =>
            {
                transitionRan = true;
                return true;
            },
            out var result);

        Assert.False(admitted);
        Assert.False(result);
        Assert.False(transitionRan);
    }
}
