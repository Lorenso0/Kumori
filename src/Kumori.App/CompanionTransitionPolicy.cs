namespace Kumori.App;

public enum CompanionTransition
{
    None,
    EnsureTracking,
    Started,
    Stopped,
}

internal static class CompanionTransitionPolicy
{
    public static CompanionTransition Evaluate(bool hasObservation, bool wasRunning, bool isRunning)
    {
        if (!hasObservation) return isRunning ? CompanionTransition.EnsureTracking : CompanionTransition.None;
        if (!wasRunning && isRunning) return CompanionTransition.Started;
        if (wasRunning && !isRunning) return CompanionTransition.Stopped;
        return CompanionTransition.None;
    }
}
