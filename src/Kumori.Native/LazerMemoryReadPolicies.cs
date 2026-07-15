using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Kumori.Core;
using Kumori.Tracking;

namespace Kumori.Native;

internal readonly record struct LazerProcessCandidate(int ProcessId, DateTime StartTime);

internal static class LazerProcessSelectionPolicy
{
    internal static int? Select(
        IReadOnlyList<LazerProcessCandidate> candidates,
        int? preferredProcessId)
    {
        if (preferredProcessId is { } preferred &&
            candidates.Any(candidate => candidate.ProcessId == preferred))
            return preferred;

        return candidates
            .OrderByDescending(candidate => candidate.StartTime)
            .Select(candidate => (int?)candidate.ProcessId)
            .FirstOrDefault();
    }
}

internal static class LazerAttemptFrameBufferPolicy
{
    internal static bool BeginsNewGeneration(
        bool framesListChanged,
        long previousSequence,
        IReadOnlyList<LazerReplayFrame> frames) =>
        framesListChanged ||
        (previousSequence > 0 &&
         frames.Count > 0 &&
         frames[0].Sequence is { } firstSequence &&
         firstSequence <= previousSequence);

    internal static void Append(
        List<LazerReplayFrame> attemptFrames,
        IReadOnlyList<LazerReplayFrame> frames,
        bool attemptActive,
        bool beginsNewGeneration)
    {
        if (!attemptActive)
            return;
        if (beginsNewGeneration)
            attemptFrames.Clear();
        if (frames.Count > 0)
            attemptFrames.AddRange(frames);
    }
}

internal static class TosuGameBaseAdoptionPolicy
{
    internal static bool ShouldAdopt(
        nint candidate,
        bool vtableMatches,
        bool screenStackUsable) =>
        candidate.ToInt64() > 0x10000 && vtableMatches && screenStackUsable;
}

internal static class LazerMemoryReadPolicy
{
    internal static readonly TimeSpan CachedReadBudget = TimeSpan.FromMilliseconds(2);
    internal static readonly TimeSpan DiscoveryReadBudget = TimeSpan.FromMilliseconds(4);
    // 1 MiB every 16 ms is about 62.5 MiB/s at most. That is fast enough to
    // finish discovery during lazer startup/menu time while remaining a small,
    // time-bounded, below-normal-priority memory-read workload. The previous
    // 250 ms cadence could require several minutes for a multi-gigabyte lazer
    // process, longer than a complete beatmap.
    internal static readonly TimeSpan DiscoveryStepInterval = TimeSpan.FromMilliseconds(16);
    internal const int DiscoveryBytesPerStep = 1024 * 1024;

    internal static bool ShouldDiscover(nint gameBase) => gameBase == 0;

    internal static bool ShouldRearmDiscovery(nint gameBase, bool discoveryExhausted) =>
        gameBase == 0 && discoveryExhausted;

    internal static bool MayAttemptUnit(bool isFirst, bool budgetExpired) =>
        isFirst || !budgetExpired;

    internal static int FindAlignedPointerOffset(
        ReadOnlySpan<byte> buffer,
        long expected,
        int searchOffset)
    {
        var alignedSearchOffset = (Math.Max(0, searchOffset) + sizeof(long) - 1) & ~(sizeof(long) - 1);
        for (var offset = alignedSearchOffset; offset <= buffer.Length - sizeof(long); offset += sizeof(long))
        {
            if (BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset, sizeof(long))) == expected)
                return offset;
        }
        return -1;
    }
}
