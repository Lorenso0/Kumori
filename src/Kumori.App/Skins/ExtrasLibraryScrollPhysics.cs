namespace Kumori.App.Skins;

internal static class ExtrasLibraryScrollPhysics
{
    internal const double SettleDistance = 0.25;
    private const double PixelsPerWheelNotch = 52;
    private const double MaximumQueuedDistance = 220;
    private const double ResponseSeconds = 0.07;

    internal static double TargetOffset(
        double currentOffset,
        double previousTarget,
        int wheelDelta,
        double maximumOffset)
    {
        var requested = previousTarget
                        - wheelDelta / 120d * PixelsPerWheelNotch;
        var queued = Math.Clamp(
            requested,
            currentOffset - MaximumQueuedDistance,
            currentOffset + MaximumQueuedDistance);
        return Math.Clamp(queued, 0, Math.Max(0, maximumOffset));
    }

    internal static double NextOffset(
        double currentOffset,
        double targetOffset,
        double elapsedSeconds)
    {
        var elapsed = Math.Clamp(elapsedSeconds, 0, 0.05);
        var blend = 1 - Math.Exp(-elapsed / ResponseSeconds);
        return currentOffset + (targetOffset - currentOffset) * blend;
    }

    internal static bool IsSettled(double currentOffset, double targetOffset) =>
        Math.Abs(targetOffset - currentOffset) <= SettleDistance;
}
