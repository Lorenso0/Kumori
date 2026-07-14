using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Kumori.Core;
using Kumori.Core.Models;

namespace Kumori.App;

public sealed record StableReplayComparisonResult(string DirectoryPath, string ReportPath, string Summary);

/// <summary>
/// Preserves the native stable-memory stream and matching .osr stream before
/// the active movement row is replaced. The archive is assembled in a pending
/// directory and published atomically so cancellation never leaves a partial
/// diagnostic that looks complete.
/// </summary>
public static class StableReplayComparisonArchive
{
    private const int SampleSize = 26;
    private const int SamplesPerWrite = 4096;

    public static StableReplayComparisonResult Save(
        long attemptId,
        IReadOnlyList<MovementSample> memoryFrames,
        IReadOnlyList<MovementSample> replayFrames,
        string replayPath,
        string? checksum,
        string? rootDirectory = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (memoryFrames.Count == 0) throw new InvalidOperationException("Stable memory comparison has no memory frames.");
        if (replayFrames.Count == 0) throw new InvalidOperationException("Stable memory comparison has no replay frames.");
        if (!File.Exists(replayPath)) throw new FileNotFoundException("The matching stable replay disappeared before comparison.", replayPath);

        rootDirectory ??= AppPaths.StableReplayComparisonsDir;
        Directory.CreateDirectory(rootDirectory);
        string directory = Path.Combine(rootDirectory, $"attempt-{attemptId}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}");
        if (Directory.Exists(directory))
            directory += $"-{Guid.NewGuid():N}";
        string pendingDirectory = directory + $".pending-{Guid.NewGuid():N}";
        Directory.CreateDirectory(pendingDirectory);

        try
        {
            WriteSamples(
                Path.Combine(pendingDirectory, "stable-memory.samples.zlib"),
                memoryFrames,
                cancellationToken);
            WriteSamples(
                Path.Combine(pendingDirectory, "stable-replay.samples.zlib"),
                replayFrames,
                cancellationToken);
            CopyFile(
                replayPath,
                Path.Combine(pendingDirectory, "source.osr"),
                cancellationToken);

            Comparison report = Compare(
                attemptId,
                memoryFrames,
                replayFrames,
                replayPath,
                checksum,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            cancellationToken.ThrowIfCancellationRequested();
            File.WriteAllText(Path.Combine(pendingDirectory, "report.json"), json, Encoding.UTF8);
            cancellationToken.ThrowIfCancellationRequested();

            // This rename is the commit point. Never observe cancellation after
            // it or a caller could retry an archive that is already complete.
            Directory.Move(pendingDirectory, directory);
            try { PruneOldArchives(rootDirectory, keep: 20); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

            string reportFile = Path.Combine(directory, "report.json");
            string summary = $"memory={memoryFrames.Count}, osr={replayFrames.Count}, "
                             + $"transitions matched={report.InputTransitions.Matched}/{report.InputTransitions.MemoryCount}, "
                             + $"missing={report.InputTransitions.MissingFromReplay}, extra={report.InputTransitions.ExtraInReplay}, "
                             + $"max Δt={report.InputTransitions.MaxAbsoluteTimeDeltaMs:0.###}ms";
            return new StableReplayComparisonResult(directory, reportFile, summary);
        }
        catch
        {
            try { Directory.Delete(pendingDirectory, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    private static void WriteSamples(
        string path,
        IReadOnlyList<MovementSample> frames,
        CancellationToken cancellationToken)
    {
        using var output = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
        using var zlib = new ZLibStream(output, CompressionLevel.Fastest, leaveOpen: false);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(SamplesPerWrite * SampleSize);
        try
        {
            for (var offset = 0; offset < frames.Count; offset += SamplesPerWrite)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int count = Math.Min(SamplesPerWrite, frames.Count - offset);
                Span<byte> raw = buffer.AsSpan(0, count * SampleSize);
                for (var index = 0; index < count; index++)
                    WriteSample(raw.Slice(index * SampleSize, SampleSize), frames[offset + index]);
                zlib.Write(raw);
            }
            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void WriteSample(Span<byte> target, MovementSample sample)
    {
        BinaryPrimitives.WriteInt32LittleEndian(target[0..4], checked((int)Math.Round(sample.MapTimeMs)));
        BinaryPrimitives.WriteSingleLittleEndian(target[4..8], (float)sample.MonotonicMs);
        BinaryPrimitives.WriteSingleLittleEndian(target[8..12], (float)sample.X);
        BinaryPrimitives.WriteSingleLittleEndian(target[12..16], (float)sample.Y);
        BinaryPrimitives.WriteInt16LittleEndian(target[16..18], sample.RawX);
        BinaryPrimitives.WriteInt16LittleEndian(target[18..20], sample.RawY);
        target[20] = (byte)sample.Buttons;
        target[21] = (byte)sample.Flags;
        BinaryPrimitives.WriteUInt32LittleEndian(target[22..26], sample.Pressure);
    }

    private static void CopyFile(string source, string destination, CancellationToken cancellationToken)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int read = input.Read(buffer, 0, buffer.Length);
                if (read == 0)
                    break;
                output.Write(buffer, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void PruneOldArchives(string rootDirectory, int keep)
    {
        foreach (DirectoryInfo old in new DirectoryInfo(rootDirectory).EnumerateDirectories("attempt-*")
                     .Where(directory => !directory.Name.Contains(".pending-", StringComparison.Ordinal))
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
        string? checksum,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Transition[] memoryTransitions = Transitions(memoryFrames, cancellationToken);
        Transition[] replayTransitions = Transitions(replayFrames, cancellationToken);
        var matches = new List<TransitionMatch>();
        var missing = new List<Transition>();
        int replayCursor = 0;
        foreach (Transition memory in memoryTransitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int best = -1;
            double bestDelta = double.PositiveInfinity;
            for (int i = replayCursor; i < replayTransitions.Length; i++)
            {
                if ((i & 1023) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
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

        cancellationToken.ThrowIfCancellationRequested();
        HashSet<int> matchedReplay = matches.Select(match => match.ReplayTransitionIndex).ToHashSet();
        var extra = new List<Transition>();
        for (var index = 0; index < replayTransitions.Length; index++)
        {
            if ((index & 4095) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            if (!matchedReplay.Contains(index))
                extra.Add(replayTransitions[index]);
        }
        double[] absoluteTimeDeltas = matches.Select(match => Math.Abs(match.TimeDeltaMs)).ToArray();
        double[] positionDeltas = matches.Select(match => match.PositionDelta).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        Array.Sort(absoluteTimeDeltas);
        Array.Sort(positionDeltas);
        cancellationToken.ThrowIfCancellationRequested();
        var transitionSummary = new TransitionComparison(
            memoryTransitions.Length,
            replayTransitions.Length,
            matches.Count,
            missing.Count,
            extra.Count,
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
            FrameSummary(memoryFrames, cancellationToken),
            FrameSummary(replayFrames, cancellationToken),
            CompareFrameIndexes(memoryFrames, replayFrames, cancellationToken),
            transitionSummary);
    }

    private static FrameIndexComparison CompareFrameIndexes(
        IReadOnlyList<MovementSample> memoryFrames,
        IReadOnlyList<MovementSample> replayFrames,
        CancellationToken cancellationToken)
    {
        int common = Math.Min(memoryFrames.Count, replayFrames.Count);
        for (int i = 0; i < common; i++)
        {
            if ((i & 4095) == 0)
                cancellationToken.ThrowIfCancellationRequested();
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

    private static Transition[] Transitions(
        IReadOnlyList<MovementSample> frames,
        CancellationToken cancellationToken)
    {
        var result = new List<Transition>();
        int previous = 0;
        for (int i = 0; i < frames.Count; i++)
        {
            if ((i & 4095) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            int buttons = frames[i].Buttons & 0x30;
            if (buttons == previous) continue;
            result.Add(new Transition(i, frames[i].MapTimeMs, frames[i].X, frames[i].Y, previous, buttons));
            previous = buttons;
        }
        return result.ToArray();
    }

    private static StreamSummary FrameSummary(
        IReadOnlyList<MovementSample> frames,
        CancellationToken cancellationToken)
    {
        double minimum = double.PositiveInfinity;
        double maximum = double.NegativeInfinity;
        for (var index = 0; index < frames.Count; index++)
        {
            if ((index & 4095) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            minimum = Math.Min(minimum, frames[index].MapTimeMs);
            maximum = Math.Max(maximum, frames[index].MapTimeMs);
        }
        return new StreamSummary(
            frames.Count,
            frames[0].MapTimeMs,
            frames[^1].MapTimeMs,
            minimum,
            maximum);
    }

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
