using System.IO;
using System.Text.Json;
using Kumori.Core;
using Kumori.Core.Models;
using Kumori.Storage;

namespace Kumori.App;

public sealed record StableReplayComparisonResult(string DirectoryPath, string ReportPath, string Summary);

/// <summary>
/// Preserves the native stable-memory stream and matching .osr stream before
/// the active movement row is replaced. Files use MovementRepository's compact
/// zlib format so even long-map diagnostics remain reasonably small.
/// </summary>
public static class StableReplayComparisonArchive
{
    public static StableReplayComparisonResult Save(
        long attemptId,
        IReadOnlyList<MovementSample> memoryFrames,
        IReadOnlyList<MovementSample> replayFrames,
        string replayPath,
        string? checksum,
        string? rootDirectory = null)
    {
        if (memoryFrames.Count == 0) throw new InvalidOperationException("Stable memory comparison has no memory frames.");
        if (replayFrames.Count == 0) throw new InvalidOperationException("Stable memory comparison has no replay frames.");
        if (!File.Exists(replayPath)) throw new FileNotFoundException("The matching stable replay disappeared before comparison.", replayPath);

        rootDirectory ??= AppPaths.StableReplayComparisonsDir;
        Directory.CreateDirectory(rootDirectory);
        string directory = Path.Combine(rootDirectory, $"attempt-{attemptId}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}");
        Directory.CreateDirectory(directory);

        string memoryFile = Path.Combine(directory, "stable-memory.samples.zlib");
        string replayFile = Path.Combine(directory, "stable-replay.samples.zlib");
        string osrFile = Path.Combine(directory, "source.osr");
        string reportFile = Path.Combine(directory, "report.json");
        File.WriteAllBytes(memoryFile, MovementRepository.EncodeSamples(memoryFrames));
        File.WriteAllBytes(replayFile, MovementRepository.EncodeSamples(replayFrames));
        File.Copy(replayPath, osrFile, overwrite: false);

        Comparison report = Compare(attemptId, memoryFrames, replayFrames, replayPath, checksum);
        File.WriteAllText(reportFile, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        PruneOldArchives(rootDirectory, keep: 20);
        string summary = $"memory={memoryFrames.Count}, osr={replayFrames.Count}, "
                         + $"transitions matched={report.InputTransitions.Matched}/{report.InputTransitions.MemoryCount}, "
                         + $"missing={report.InputTransitions.MissingFromReplay}, extra={report.InputTransitions.ExtraInReplay}, "
                         + $"max Δt={report.InputTransitions.MaxAbsoluteTimeDeltaMs:0.###}ms";
        return new StableReplayComparisonResult(directory, reportFile, summary);
    }

    private static void PruneOldArchives(string rootDirectory, int keep)
    {
        foreach (DirectoryInfo old in new DirectoryInfo(rootDirectory).EnumerateDirectories("attempt-*")
                     .OrderByDescending(directory => directory.CreationTimeUtc)
                     .Skip(keep))
        {
            try { old.Delete(recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    internal static Comparison Compare(
        long attemptId,
        IReadOnlyList<MovementSample> memoryFrames,
        IReadOnlyList<MovementSample> replayFrames,
        string replayPath,
        string? checksum)
    {
        Transition[] memoryTransitions = Transitions(memoryFrames);
        Transition[] replayTransitions = Transitions(replayFrames);
        var matches = new List<TransitionMatch>();
        var missing = new List<Transition>();
        int replayCursor = 0;
        foreach (Transition memory in memoryTransitions)
        {
            int best = -1;
            double bestDelta = double.PositiveInfinity;
            for (int i = replayCursor; i < replayTransitions.Length; i++)
            {
                double delta = Math.Abs(replayTransitions[i].TimeMs - memory.TimeMs);
                if (delta > 250 && replayTransitions[i].TimeMs > memory.TimeMs) break;
                if (replayTransitions[i].Buttons != memory.Buttons || delta >= bestDelta) continue;
                best = i;
                bestDelta = delta;
            }
            if (best < 0)
            {
                missing.Add(memory);
                continue;
            }
            Transition replay = replayTransitions[best];
            matches.Add(new TransitionMatch(
                memory.Index, best, memory.TimeMs, replay.TimeMs,
                replay.TimeMs - memory.TimeMs,
                Distance(memory.X, memory.Y, replay.X, replay.Y),
                memory.Buttons));
            replayCursor = best + 1;
        }

        HashSet<int> matchedReplay = matches.Select(match => match.ReplayTransitionIndex).ToHashSet();
        Transition[] extra = replayTransitions.Where((_, index) => !matchedReplay.Contains(index)).ToArray();
        double[] absoluteTimeDeltas = matches.Select(match => Math.Abs(match.TimeDeltaMs)).Order().ToArray();
        double[] positionDeltas = matches.Select(match => match.PositionDelta).Order().ToArray();
        var transitionSummary = new TransitionComparison(
            memoryTransitions.Length,
            replayTransitions.Length,
            matches.Count,
            missing.Count,
            extra.Length,
            Median(absoluteTimeDeltas),
            absoluteTimeDeltas.LastOrDefault(),
            Median(positionDeltas),
            positionDeltas.LastOrDefault(),
            missing.Take(20).ToArray(),
            extra.Take(20).ToArray(),
            matches.Take(20).ToArray());

        return new Comparison(
            1,
            attemptId,
            DateTimeOffset.UtcNow,
            checksum,
            replayPath,
            FrameSummary(memoryFrames),
            FrameSummary(replayFrames),
            CompareFrameIndexes(memoryFrames, replayFrames),
            transitionSummary);
    }

    private static FrameIndexComparison CompareFrameIndexes(
        IReadOnlyList<MovementSample> memoryFrames,
        IReadOnlyList<MovementSample> replayFrames)
    {
        int common = Math.Min(memoryFrames.Count, replayFrames.Count);
        for (int i = 0; i < common; i++)
        {
            MovementSample memory = memoryFrames[i];
            MovementSample replay = replayFrames[i];
            double timeDelta = replay.MapTimeMs - memory.MapTimeMs;
            double positionDelta = Distance(memory.X, memory.Y, replay.X, replay.Y);
            int memoryButtons = memory.Buttons & 0x30;
            int replayButtons = replay.Buttons & 0x30;
            if (Math.Abs(timeDelta) <= 0.001 && positionDelta <= 0.001 && memoryButtons == replayButtons)
                continue;
            return new FrameIndexComparison(i, new FrameDifference(i, memory.MapTimeMs, replay.MapTimeMs,
                timeDelta, positionDelta, memoryButtons, replayButtons));
        }
        return new FrameIndexComparison(common, null);
    }

    private static Transition[] Transitions(IReadOnlyList<MovementSample> frames)
    {
        var result = new List<Transition>();
        int previous = 0;
        for (int i = 0; i < frames.Count; i++)
        {
            int buttons = frames[i].Buttons & 0x30;
            if (buttons == previous) continue;
            result.Add(new Transition(i, frames[i].MapTimeMs, frames[i].X, frames[i].Y, previous, buttons));
            previous = buttons;
        }
        return result.ToArray();
    }

    private static StreamSummary FrameSummary(IReadOnlyList<MovementSample> frames)
        => new(frames.Count, frames[0].MapTimeMs, frames[^1].MapTimeMs,
            frames.Min(frame => frame.MapTimeMs), frames.Max(frame => frame.MapTimeMs));

    private static double Distance(double x1, double y1, double x2, double y2)
        => Math.Sqrt(Math.Pow(x1 - x2, 2) + Math.Pow(y1 - y2, 2));

    private static double Median(double[] sorted)
        => sorted.Length == 0 ? 0 : sorted.Length % 2 == 1
            ? sorted[sorted.Length / 2]
            : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;

    internal sealed record Comparison(int Version, long AttemptId, DateTimeOffset CreatedAt, string? Checksum,
        string SourceReplayPath, StreamSummary Memory, StreamSummary Replay,
        FrameIndexComparison FrameIndexes, TransitionComparison InputTransitions);
    internal sealed record StreamSummary(int FrameCount, double FirstSequenceTimeMs, double LastSequenceTimeMs,
        double MinimumTimeMs, double MaximumTimeMs);
    internal sealed record TransitionComparison(int MemoryCount, int ReplayCount, int Matched,
        int MissingFromReplay, int ExtraInReplay, double MedianAbsoluteTimeDeltaMs, double MaxAbsoluteTimeDeltaMs,
        double MedianPositionDelta, double MaxPositionDelta, IReadOnlyList<Transition> FirstMissingFromReplay,
        IReadOnlyList<Transition> FirstExtraInReplay, IReadOnlyList<TransitionMatch> FirstMatches);
    internal sealed record Transition(int Index, double TimeMs, double X, double Y, int PreviousButtons, int Buttons);
    internal sealed record TransitionMatch(int MemoryTransitionIndex, int ReplayTransitionIndex,
        double MemoryTimeMs, double ReplayTimeMs, double TimeDeltaMs, double PositionDelta, int Buttons);
    internal sealed record FrameIndexComparison(int IdenticalPrefixFrames, FrameDifference? FirstDifference);
    internal sealed record FrameDifference(int Index, double MemoryTimeMs, double ReplayTimeMs,
        double TimeDeltaMs, double PositionDelta, int MemoryButtons, int ReplayButtons);
}
