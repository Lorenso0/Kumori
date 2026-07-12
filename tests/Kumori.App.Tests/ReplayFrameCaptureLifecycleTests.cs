using Kumori.Core.State;
using Kumori.Native;
using Kumori.Storage;
using Kumori.Tracking;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kumori.App.Tests;

public sealed class ReplayFrameCaptureLifecycleTests : IDisposable
{
    private readonly string databasePath = Path.Combine(Path.GetTempPath(), $"kumori-capture-{Guid.NewGuid():N}.sqlite3");

    [Fact]
    public void FinalizationUsesDrainedSourceSnapshotBeforeEndingAttempt()
    {
        var factory = new SqliteConnectionFactory(databasePath, readOnly: false);
        var attemptSink = new AttemptSqliteSink(factory);
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
        capture.Finalize(new AttemptFinalization("completed", "results", new AttemptSnapshot
        {
            Identity = start.Identity,
            LiveTimeMs = 9_900,
            DurationSeconds = 9.9,
            Progress = 1,
        }, 1));

        Assert.True(source.Finalized);
        Assert.False(source.EndedWithoutDrain);
        var metadata = new MovementRepository(factory).GetMetadata(attemptId);
        Assert.NotNull(metadata);
        Assert.Equal("stable_memory", metadata.Source);
        Assert.Equal(100, metadata.SampleCount);
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
}
