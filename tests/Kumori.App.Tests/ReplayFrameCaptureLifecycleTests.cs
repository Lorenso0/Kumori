using Kumori.Core.State;
using Kumori.Core.Models;
using Kumori.Native;
using Kumori.Storage;
using Kumori.Tracking;
using Microsoft.Data.Sqlite;
using System.Threading.Channels;
using Xunit;

namespace Kumori.App.Tests;

public sealed class ReplayFrameCaptureLifecycleTests : IDisposable
{
    private readonly string databasePath = Path.Combine(Path.GetTempPath(), $"kumori-capture-{Guid.NewGuid():N}.sqlite3");

    [Fact]
    public async Task LazerCaptureArmsForTachyonAttempts()
    {
        var status = new MemoryStatusSink();
        var source = new PushAttemptAwareSource();
        var capture = new LazerReplayFrameCaptureService(
            new AppStateStore(),
            new SqliteConnectionFactory(databasePath, readOnly: false),
            () => 812,
            source,
            status,
            sourceName: "lazer_memory");

        capture.StartAttempt(new AttemptStart
        {
            Identity = "tachyon-attempt",
            ClientKind = OsuClientKind.Tachyon,
        });

        Assert.Equal(812, status.Load().ActiveAttemptId);
        Assert.Equal("attempt_armed", status.Load().State);
        await capture.DisposeAsync();
    }

    [Fact]
    public async Task StartedServicePreReadsBeforeAttemptAndArmsConsumerBeforeSourceWakeup()
    {
        var status = new MemoryStatusSink();
        status.Update(s =>
        {
            s.State = "stale_previous_run";
            s.FramesEmitted = 88_606;
            s.FramesBufferedForAttempt = 42;
            s.FramesStored = 15_047;
            s.ActiveAttemptId = 373;
            s.LastError = "stale";
            s.LocalReplayState = "waiting";
            s.LocalReplayPath = "replay-from-attempt-371";
            s.LocalReplayFrames = 5_388;
            s.LocalReplayError = "stale replay search";
        });
        var source = new PushAttemptAwareSource();
        var capture = new LazerReplayFrameCaptureService(
            new AppStateStore(),
            new SqliteConnectionFactory(databasePath, readOnly: false),
            () => 374,
            source,
            status,
            sourceName: "lazer_memory");

        capture.Start();
        var starting = status.Load();
        Assert.Equal("starting", starting.State);
        Assert.Equal(0, starting.FramesEmitted);
        Assert.Equal(0, starting.FramesBufferedForAttempt);
        Assert.Equal(0, starting.FramesStored);
        Assert.Null(starting.ActiveAttemptId);
        Assert.Null(starting.LastError);
        Assert.Equal("idle", starting.LocalReplayState);
        Assert.Null(starting.LocalReplayPath);
        Assert.Equal(0, starting.LocalReplayFrames);
        Assert.Null(starting.LocalReplayError);

        source.Publish(new LazerReplayFrame { MapTimeMs = -100, Sequence = 1 });
        await WaitUntilAsync(() => status.Load().FramesEmitted == 1);
        Assert.Equal(0, status.Load().FramesBufferedForAttempt);
        await Task.Delay(300);

        capture.StartAttempt(new AttemptStart
        {
            Identity = "always-on-lazer",
            ClientKind = OsuClientKind.Lazer,
        });

        // StartAttempt synchronously wakes this source. The consumer must be
        // armed first or this first batch can be discarded as pre-attempt data.
        await WaitUntilAsync(() => status.Load().FramesBufferedForAttempt == 1);
        Assert.Equal(374, status.Load().ActiveAttemptId);

        capture.DiscardIfEmpty(new AttemptDiscard(
            "test complete",
            new AttemptSnapshot { Identity = "always-on-lazer" },
            1));
        await capture.DisposeAsync();
    }

    [Fact]
    public void FinalizationUsesDrainedSourceSnapshotBeforeEndingAttempt()
    {
        var factory = new SqliteConnectionFactory(databasePath, readOnly: false);
        var attemptSink = new AttemptSqliteSink(factory, (_, work) => work(CancellationToken.None));
        var start = new AttemptStart
        {
            Identity = "capture-lifecycle",
            WallTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d,
            ClientKind = OsuClientKind.Stable,
        };
        attemptSink.StartAttempt(start);
        long attemptId = Assert.IsType<long>(attemptSink.CurrentAttemptId);

        var source = new FinalizableSource(Enumerable.Range(0, 100)
            .Select(index => new LazerReplayFrame
            {
                MapTimeMs = index * 100,
                MonotonicMs = index * 100,
                X = index,
                Y = index,
                Sequence = index + 1,
            }).ToArray());
        var capture = new LazerReplayFrameCaptureService(
            new AppStateStore(), factory, () => attemptId, source,
            sourceName: "stable_memory", clientKind: OsuClientKind.Stable);

        capture.StartAttempt(start);
        var finalization = new AttemptFinalization("completed", "results", new AttemptSnapshot
        {
            Identity = start.Identity,
            LiveTimeMs = 9_900,
            DurationSeconds = 9.9,
            Progress = 1,
        }, 1);
        attemptSink.Finalize(finalization);
        capture.Finalize(finalization);

        Assert.True(source.Finalized);
        Assert.False(source.EndedWithoutDrain);
        var metadata = new MovementRepository(factory).GetMetadata(attemptId);
        Assert.NotNull(metadata);
        Assert.Equal("stable_memory", metadata.Source);
        Assert.Equal(100, metadata.SampleCount);
    }

    [Fact]
    public async Task DeferredFinalizationLeavesMovementUntouchedUntilQueuedWorkRuns()
    {
        var factory = new SqliteConnectionFactory(databasePath, readOnly: false);
        var attemptSink = new AttemptSqliteSink(factory, (_, work) => work(CancellationToken.None));
        var start = new AttemptStart
        {
            Identity = "capture-deferred",
            WallTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d,
            ClientKind = OsuClientKind.Stable,
        };
        attemptSink.StartAttempt(start);
        long attemptId = Assert.IsType<long>(attemptSink.CurrentAttemptId);
        var source = new FinalizableSource(CreateFrames(100));
        var status = new MemoryStatusSink();
        Func<CancellationToken, Task>? queuedWork = null;
        var committedCount = 0;
        long? committedAttemptId = null;
        MovementMetadata? metadataAtNotification = null;
        using var cancelAfterCommit = new CancellationTokenSource();
        var capture = new LazerReplayFrameCaptureService(
            new AppStateStore(),
            factory,
            () => attemptId,
            source,
            status,
            sourceName: "stable_memory",
            clientKind: OsuClientKind.Stable,
            deferPersistence: (_, work) =>
            {
                queuedWork = work;
                return Task.CompletedTask;
            },
            captureCommitted: id =>
            {
                committedCount++;
                committedAttemptId = id;
                metadataAtNotification = new MovementRepository(factory).GetMetadata(id);
                cancelAfterCommit.Cancel();
            });

        capture.StartAttempt(start);
        capture.Finalize(Finalization(start));

        Assert.NotNull(queuedWork);
        Assert.Equal("persistence_queued", status.Load().State);
        Assert.Null(new MovementRepository(factory).GetMetadata(attemptId));

        // Production CompositeAttemptSink queues capture persistence first,
        // then commits the parent attempt row before the idle worker runs.
        attemptSink.Finalize(Finalization(start));
        await queuedWork(cancelAfterCommit.Token);

        var metadata = new MovementRepository(factory).GetMetadata(attemptId);
        Assert.NotNull(metadata);
        Assert.Equal("stable_memory", metadata.Source);
        Assert.Equal(100, metadata.SampleCount);
        Assert.Equal(1, committedCount);
        Assert.Equal(attemptId, committedAttemptId);
        Assert.NotNull(metadataAtNotification);
        Assert.Equal("stable_memory", metadataAtNotification.Source);
        Assert.True(cancelAfterCommit.IsCancellationRequested);
        await capture.DisposeAsync();
    }

    [Fact]
    public async Task DeferredPreparationHonorsCancellationBeforeCompressionOrSqliteWrites()
    {
        var factory = new SqliteConnectionFactory(databasePath, readOnly: false);
        var attemptSink = new AttemptSqliteSink(factory, (_, work) => work(CancellationToken.None));
        var start = new AttemptStart
        {
            Identity = "capture-cancelled",
            WallTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d,
            ClientKind = OsuClientKind.Stable,
        };
        attemptSink.StartAttempt(start);
        long attemptId = Assert.IsType<long>(attemptSink.CurrentAttemptId);
        Func<CancellationToken, Task>? queuedWork = null;
        var committedCount = 0;
        var capture = new LazerReplayFrameCaptureService(
            new AppStateStore(),
            factory,
            () => attemptId,
            new FinalizableSource(CreateFrames(10_000)),
            new MemoryStatusSink(),
            sourceName: "stable_memory",
            clientKind: OsuClientKind.Stable,
            deferPersistence: (_, work) =>
            {
                queuedWork = work;
                return Task.CompletedTask;
            },
            captureCommitted: _ => committedCount++);

        capture.StartAttempt(start);
        capture.Finalize(Finalization(start));
        Assert.NotNull(queuedWork);
        attemptSink.Finalize(Finalization(start));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queuedWork(cancelled.Token));
        Assert.Null(new MovementRepository(factory).GetMetadata(attemptId));
        Assert.Equal(0, committedCount);
        await capture.DisposeAsync();
    }

    [Fact]
    public async Task ShutdownDrainPersistsQueuedReplayBeforeCaptureServiceDisposes()
    {
        var factory = new SqliteConnectionFactory(databasePath, readOnly: false);
        var attemptSink = new AttemptSqliteSink(factory, (_, work) => work(CancellationToken.None));
        var start = new AttemptStart
        {
            Identity = "capture-shutdown-drain",
            WallTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d,
            ClientKind = OsuClientKind.Stable,
        };
        attemptSink.StartAttempt(start);
        long attemptId = Assert.IsType<long>(attemptSink.CurrentAttemptId);
        using var coordinator = new GameplayWorkCoordinator(
            idleSettleDelay: TimeSpan.FromSeconds(30));
        var capture = new LazerReplayFrameCaptureService(
            new AppStateStore(),
            factory,
            () => attemptId,
            new FinalizableSource(CreateFrames(100)),
            sourceName: "stable_memory",
            clientKind: OsuClientKind.Stable,
            deferPersistence: (key, work) => coordinator.EnqueuePriority(key, async token =>
            {
                await attemptSink.FlushPendingPersistenceAsync(token);
                await work(token);
            }));

        coordinator.BeginGameplay();
        capture.StartAttempt(start);
        capture.Finalize(Finalization(start));
        attemptSink.Finalize(Finalization(start));
        coordinator.EndGameplay();

        Task disposal = capture.DisposeAsync().AsTask();
        await Task.Delay(50);
        Assert.False(disposal.IsCompleted);
        Assert.Null(new MovementRepository(factory).GetMetadata(attemptId));

        coordinator.BeginShutdownDrain();

        await disposal.WaitAsync(TimeSpan.FromSeconds(2));
        var metadata = new MovementRepository(factory).GetMetadata(attemptId);
        Assert.NotNull(metadata);
        Assert.Equal(100, metadata.SampleCount);
    }

    [Fact]
    public async Task Dispose_is_idempotent_and_never_runs_blocking_source_teardown_on_caller()
    {
        var source = new BlockingDisposeSource();
        var capture = new LazerReplayFrameCaptureService(
            new AppStateStore(),
            new SqliteConnectionFactory(databasePath, readOnly: false),
            () => null,
            source);

        var started = DateTime.UtcNow;
        Task first = capture.DisposeAsync().AsTask();
        Task second = capture.DisposeAsync().AsTask();

        Assert.True(
            DateTime.UtcNow - started < TimeSpan.FromSeconds(1),
            "DisposeAsync synchronously blocked its caller.");
        Assert.Same(first, second);
        Assert.True(source.Entered.Wait(TimeSpan.FromSeconds(2)));
        Assert.False(first.IsCompleted);

        source.Release.Set();
        await first.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, source.DisposeCalls);
    }

    private static AttemptFinalization Finalization(AttemptStart start) => new(
        "completed",
        "results",
        new AttemptSnapshot
        {
            Identity = start.Identity,
            LiveTimeMs = 9_900,
            DurationSeconds = 9.9,
            Progress = 1,
        },
        1);

    private static IReadOnlyList<LazerReplayFrame> CreateFrames(int count) => Enumerable.Range(0, count)
        .Select(index => new LazerReplayFrame
        {
            MapTimeMs = index * 100,
            MonotonicMs = index * 100,
            X = index,
            Y = index,
            Sequence = index + 1,
        })
        .ToArray();

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for replay capture state.");
            await Task.Delay(10);
        }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(databasePath)) File.Delete(databasePath);
    }

    private sealed class FinalizableSource(IReadOnlyList<LazerReplayFrame> frames)
        : ILazerReplayFrameSource, IAttemptAwareReplayFrameSource, IFinalizableReplayFrameSource
    {
        public bool Finalized { get; private set; }
        public bool EndedWithoutDrain { get; private set; }
        public async IAsyncEnumerable<LazerReplayFrame> ReadFramesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }
        public void StartAttempt(AttemptStart start) { }
        public void UpdateAttempt(AttemptSnapshot snapshot) { }
        public void EndAttempt() => EndedWithoutDrain = true;
        public IReadOnlyList<LazerReplayFrame> FinalizeAttemptSnapshot()
        {
            Finalized = true;
            return frames;
        }
    }

    private sealed class BlockingDisposeSource
        : ILazerReplayFrameSource, IAsyncDisposable
    {
        public ManualResetEventSlim Entered { get; } = new();
        public ManualResetEventSlim Release { get; } = new();
        public int DisposeCalls { get; private set; }

        public async IAsyncEnumerable<LazerReplayFrame> ReadFramesAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            Entered.Set();
            Release.Wait();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class MemoryStatusSink : IReplayFrameStatusSink
    {
        private readonly object gate = new();
        private readonly LazerReplayFrameStatus status = new();

        public void Update(Action<LazerReplayFrameStatus> mutate)
        {
            lock (gate)
                mutate(status);
        }

        public LazerReplayFrameStatus Load()
        {
            lock (gate)
                return status;
        }
    }

    private sealed class PushAttemptAwareSource
        : ILazerReplayFrameSource, IAttemptAwareReplayFrameSource
    {
        private readonly Channel<LazerReplayFrame> frames = Channel.CreateUnbounded<LazerReplayFrame>();

        public void Publish(LazerReplayFrame frame) => frames.Writer.TryWrite(frame);

        public async IAsyncEnumerable<LazerReplayFrame> ReadFramesAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var frame in frames.Reader.ReadAllAsync(cancellationToken))
                yield return frame;
        }

        public void StartAttempt(AttemptStart start) =>
            Publish(new LazerReplayFrame { MapTimeMs = 0, Sequence = 1 });

        public void UpdateAttempt(AttemptSnapshot snapshot) { }

        public void EndAttempt() { }
    }
}
