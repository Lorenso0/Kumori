namespace Kumori.App;

public enum CompanionTransition
{
    None,
    EnsureTracking,
    Started,
    Replaced,
    Stopped,
}

internal readonly record struct CompanionObservation(
    CompanionTransition Transition,
    bool IsRunning,
    IReadOnlySet<int> ProcessIds);

/// <summary>
/// Debounces transient process-enumeration misses and confirms a stable PID
/// before changing companion lifetime. A replacement PID is distinguished from
/// a real stop so long-lived input helpers are never torn down on a process flap.
/// </summary>
internal sealed class CompanionTransitionPolicy(
    int requiredPresentObservations = 2,
    int requiredMissingObservations = 4)
{
    private readonly int requiredPresent = Math.Max(1, requiredPresentObservations);
    private readonly int requiredMissing = Math.Max(1, requiredMissingObservations);
    private readonly HashSet<int> confirmed = [];
    private readonly HashSet<int> candidate = [];
    private bool hasObservation;
    private bool initialCandidate;
    private int candidateObservations;
    private int missingObservations;

    public CompanionObservation Observe(IReadOnlySet<int> observed)
    {
        ArgumentNullException.ThrowIfNull(observed);

        if (!hasObservation)
        {
            hasObservation = true;
            initialCandidate = observed.Count > 0;
            SetCandidate(observed);
            if (observed.Count == 0)
                return Current(CompanionTransition.None);
            if (requiredPresent == 1)
                return ConfirmCandidate(CompanionTransition.EnsureTracking);
            return Current(CompanionTransition.None);
        }

        if (confirmed.Count > 0)
        {
            if (observed.Overlaps(confirmed))
            {
                missingObservations = 0;
                // Preserve the confirmed process identity. A replacement can
                // overlap the old process for several polls; adopting every
                // observed PID here would make that hand-off invisible.
                var additions = observed.Where(id => !confirmed.Contains(id)).ToHashSet();
                if (additions.Count == 0)
                    SetCandidate(additions);
                else
                    ObserveCandidate(additions);
                return Current(CompanionTransition.None);
            }

            if (observed.Count == 0)
            {
                if (++missingObservations < requiredMissing)
                    return Current(CompanionTransition.None);

                confirmed.Clear();
                candidate.Clear();
                candidateObservations = 0;
                missingObservations = 0;
                initialCandidate = false;
                return Current(CompanionTransition.Stopped);
            }

            missingObservations = 0;
            if (ObserveCandidate(observed) < requiredPresent)
                return Current(CompanionTransition.None);
            return ConfirmCandidate(CompanionTransition.Replaced);
        }

        if (observed.Count == 0)
        {
            candidate.Clear();
            candidateObservations = 0;
            initialCandidate = false;
            return Current(CompanionTransition.None);
        }

        if (ObserveCandidate(observed) < requiredPresent)
            return Current(CompanionTransition.None);

        var transition = initialCandidate ? CompanionTransition.EnsureTracking : CompanionTransition.Started;
        return ConfirmCandidate(transition);
    }

    private int ObserveCandidate(IReadOnlySet<int> observed)
    {
        if (candidate.Count > 0 && observed.Overlaps(candidate))
        {
            candidate.IntersectWith(observed);
            candidateObservations++;
        }
        else
        {
            SetCandidate(observed);
        }
        return candidateObservations;
    }

    private void SetCandidate(IReadOnlySet<int> observed)
    {
        candidate.Clear();
        candidate.UnionWith(observed);
        candidateObservations = observed.Count > 0 ? 1 : 0;
    }

    private CompanionObservation ConfirmCandidate(CompanionTransition transition)
    {
        confirmed.Clear();
        confirmed.UnionWith(candidate);
        candidate.Clear();
        candidateObservations = 0;
        missingObservations = 0;
        initialCandidate = false;
        return Current(transition);
    }

    private CompanionObservation Current(CompanionTransition transition) =>
        new(transition, confirmed.Count > 0, confirmed.ToHashSet());
}
