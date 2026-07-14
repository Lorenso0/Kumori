using System.Text.Json;
using Kumori.App;
using Kumori.Core.Models;
using Xunit;

namespace Kumori.App.Tests;

public sealed class StableReplayComparisonArchiveTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"kumori-stable-compare-{Guid.NewGuid():N}");

    [Fact]
    public void SavePreservesBothStreamsAndReportsFirstDifference()
    {
        Directory.CreateDirectory(root);
        string osr = Path.Combine(root, "matching.osr");
        File.WriteAllBytes(osr, [1, 2, 3, 4]);
        MovementSample[] memory = [Frame(10, 0), Frame(20, 0x10), Frame(30, 0)];
        MovementSample[] replay = [Frame(10, 0), Frame(22, 0x10), Frame(30, 0)];

        StableReplayComparisonResult result = StableReplayComparisonArchive.Save(
            42, memory, replay, osr, "checksum", Path.Combine(root, "comparisons"));

        Assert.True(File.Exists(Path.Combine(result.DirectoryPath, "stable-memory.samples.zlib")));
        Assert.True(File.Exists(Path.Combine(result.DirectoryPath, "stable-replay.samples.zlib")));
        Assert.True(File.Exists(Path.Combine(result.DirectoryPath, "source.osr")));
        Assert.True(File.Exists(result.ReportPath));
        using var report = JsonDocument.Parse(File.ReadAllText(result.ReportPath));
        Assert.Equal(42, report.RootElement.GetProperty("AttemptId").GetInt64());
        Assert.Equal(1, report.RootElement.GetProperty("FrameIndexes").GetProperty("IdenticalPrefixFrames").GetInt32());
        Assert.Equal(2, report.RootElement.GetProperty("FrameIndexes").GetProperty("FirstDifference").GetProperty("TimeDeltaMs").GetDouble());
        Assert.Equal(2, report.RootElement.GetProperty("InputTransitions").GetProperty("Matched").GetInt32());
    }

    [Fact]
    public void CancellationRemovesPendingArchiveWithoutPublishingIt()
    {
        Directory.CreateDirectory(root);
        string osr = Path.Combine(root, "matching.osr");
        File.WriteAllBytes(osr, [1, 2, 3, 4]);
        using var cancellation = new CancellationTokenSource();
        var frames = new CancellingFrames(5_000, cancellation, cancelAt: 100);
        string comparisons = Path.Combine(root, "comparisons");

        Assert.Throws<OperationCanceledException>(() => StableReplayComparisonArchive.Save(
            42,
            frames,
            Enumerable.Range(0, 5_000).Select(index => Frame(index, 0)).ToArray(),
            osr,
            "checksum",
            comparisons,
            cancellation.Token));

        Assert.Empty(Directory.EnumerateDirectories(comparisons));
    }

    [Fact]
    public void ReplayDecoderRejectsOversizedCandidateBeforeParsing()
    {
        Directory.CreateDirectory(root);
        string replay = Path.Combine(root, "oversized.osr");
        using (var stream = File.Create(replay))
            stream.SetLength(StableReplayFrameRecoverySink.MaximumReplayFileBytes + 1);

        bool decoded = StableReplayFrameRecoverySink.TryRead(
            replay,
            Path.Combine(root, "missing.osu"),
            checksum: null,
            out _);

        Assert.False(decoded);
    }

    [Fact]
    public void ReplayDecoderPropagatesCancellationBeforeOpeningCandidate()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => StableReplayFrameRecoverySink.TryRead(
            Path.Combine(root, "missing.osr"),
            Path.Combine(root, "missing.osu"),
            checksum: null,
            out _,
            out _,
            cancellation.Token));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private static MovementSample Frame(double time, int buttons) => new()
    {
        MapTimeMs = time,
        MonotonicMs = time,
        X = time,
        Y = time,
        Buttons = buttons,
        Flags = 1,
    };

    private sealed class CancellingFrames(
        int count,
        CancellationTokenSource cancellation,
        int cancelAt) : IReadOnlyList<MovementSample>
    {
        public int Count => count;

        public MovementSample this[int index]
        {
            get
            {
                if (index == cancelAt)
                    cancellation.Cancel();
                return Frame(index, 0);
            }
        }

        public IEnumerator<MovementSample> GetEnumerator()
        {
            for (var index = 0; index < count; index++)
                yield return this[index];
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
