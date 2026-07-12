using Kumori.Tracking;

namespace Kumori.Native;

/// <summary>
/// Turns stable's replaceable replay-frame lists into one monotonic stream.
/// Stable may rotate the underlying list during a play, so its local index is
/// not a capture sequence number.
/// </summary>
public sealed class StableReplayFrameEmissionCursor
{
    private long emittedSequence;
    private LazerReplayFrame? lastEmitted;
    private readonly Dictionary<FrameFingerprint, int> emittedOccurrences = [];
    private int previousSnapshotCount;

    public IReadOnlyList<LazerReplayFrame> TakeNew(
        IReadOnlyList<LazerReplayFrame> frames,
        out bool rotated)
    {
        int previousTailIndex = lastEmitted is null
            ? -1
            : Enumerable.Range(0, frames.Count).LastOrDefault(index => SameFrame(frames[index], lastEmitted), -1);
        rotated = lastEmitted is not null && (previousTailIndex >= 0
            ? previousTailIndex != previousSnapshotCount - 1
            : frames.Any(frame => frame.MapTimeMs > lastEmitted.MapTimeMs));
        var result = new List<LazerReplayFrame>();
        var snapshotOccurrences = new Dictionary<FrameFingerprint, int>();
        foreach (LazerReplayFrame source in frames)
        {
            var fingerprint = Fingerprint(source);
            int occurrence = snapshotOccurrences.TryGetValue(fingerprint, out int seenInSnapshot)
                ? seenInSnapshot + 1
                : 1;
            snapshotOccurrences[fingerprint] = occurrence;
            int alreadyEmitted = emittedOccurrences.TryGetValue(fingerprint, out int emitted) ? emitted : 0;
            if (occurrence <= alreadyEmitted)
                continue;
            result.Add(source with { Sequence = ++emittedSequence });
            lastEmitted = source;
            emittedOccurrences[fingerprint] = occurrence;
        }
        if (result.Count > 0)
            previousSnapshotCount = frames.Count;
        return result;
    }

    private static bool SameFrame(LazerReplayFrame left, LazerReplayFrame right)
        => left.MapTimeMs.Equals(right.MapTimeMs)
           && left.X.Equals(right.X)
           && left.Y.Equals(right.Y)
           && left.LeftPressed == right.LeftPressed
           && left.RightPressed == right.RightPressed;

    private static FrameFingerprint Fingerprint(LazerReplayFrame frame)
        => new(frame.MapTimeMs, frame.X, frame.Y, frame.LeftPressed, frame.RightPressed);

    private readonly record struct FrameFingerprint(double Time, double X, double Y, bool Left, bool Right);
}
