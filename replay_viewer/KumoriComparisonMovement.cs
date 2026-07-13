using osuTK;

namespace Kumori.ReplayViewer;

internal static class KumoriComparisonMovement
{
    public const int PausedFlag = 0x02;
    private const double maximumInterpolationGapMs = 250;

    public static MovementSample[] Prepare(IReadOnlyList<MovementSample> samples) =>
        samples
            .Where(sample => (sample.Flags & PausedFlag) == 0)
            .OrderBy(sample => sample.MapTimeMs)
            // Captures can contain more than one observation for the same map
            // millisecond. Keeping the newest one avoids a zero-duration kink.
            .GroupBy(sample => sample.MapTimeMs)
            .Select(group => group.Last())
            .ToArray();

    public static bool TryPositionAt(IReadOnlyList<MovementSample> samples, double time, out Vector2 position)
    {
        position = default;
        if (samples.Count == 0 || time < samples[0].MapTimeMs || time > samples[^1].MapTimeMs)
            return false;

        var low = 0;
        var high = samples.Count - 1;
        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            if (samples[middle].MapTimeMs < time)
                low = middle + 1;
            else
                high = middle - 1;
        }

        var afterIndex = Math.Clamp(low, 0, samples.Count - 1);
        var beforeIndex = Math.Max(0, afterIndex - 1);
        MovementSample before = samples[beforeIndex];
        MovementSample after = samples[afterIndex];
        if (Math.Abs(after.MapTimeMs - time) < 0.0001)
        {
            position = samplePosition(after);
            return true;
        }
        double gap = after.MapTimeMs - before.MapTimeMs;
        if (gap > maximumInterpolationGapMs)
            return false;

        if (gap <= 0)
        {
            position = new Vector2((float)after.X, (float)after.Y);
            return true;
        }

        // Match OsuFramedReplayInputHandler, which uses Interpolation.ValueAt
        // between the surrounding replay frames (linear with no easing).
        // Any higher-order curve changes the recorded cursor trajectory.
        float progress = (float)Math.Clamp((time - before.MapTimeMs) / gap, 0, 1);
        position = Vector2.Lerp(samplePosition(before), samplePosition(after), progress);
        return true;
    }

    private static Vector2 samplePosition(MovementSample sample)
        => new((float)sample.X, (float)sample.Y);
}
