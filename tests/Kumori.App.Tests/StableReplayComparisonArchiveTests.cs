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
}
