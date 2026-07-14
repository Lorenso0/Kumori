using Xunit;

namespace Kumori.App.Tests;

public sealed class CompanionTransitionPolicyTests
{
    [Fact]
    public void ExistingProcessRequiresPidConfirmationAndOnlyEnsuresTracking()
    {
        var policy = new CompanionTransitionPolicy();

        Assert.Equal(CompanionTransition.None, policy.Observe(Pids(42)).Transition);
        CompanionObservation confirmed = policy.Observe(Pids(42));

        Assert.Equal(CompanionTransition.EnsureTracking, confirmed.Transition);
        Assert.True(confirmed.IsRunning);
        Assert.Contains(42, confirmed.ProcessIds);
    }

    [Fact]
    public void ProcessLaunchedDuringAppStartupIsStartedAfterEmptyEarlyBaseline()
    {
        var policy = new CompanionTransitionPolicy();

        Assert.Equal(CompanionTransition.None, policy.Observe(Pids()).Transition);
        Assert.Equal(CompanionTransition.None, policy.Observe(Pids(7)).Transition);
        Assert.Equal(CompanionTransition.Started, policy.Observe(Pids(7)).Transition);
    }

    [Fact]
    public void TransientEnumerationMissDoesNotStopConfirmedSession()
    {
        var policy = new CompanionTransitionPolicy();
        policy.Observe(Pids());
        policy.Observe(Pids(7));
        policy.Observe(Pids(7));

        for (var miss = 0; miss < 3; miss++)
        {
            CompanionObservation observation = policy.Observe(Pids());
            Assert.Equal(CompanionTransition.None, observation.Transition);
            Assert.True(observation.IsRunning);
        }

        CompanionObservation stopped = policy.Observe(Pids());
        Assert.Equal(CompanionTransition.Stopped, stopped.Transition);
        Assert.False(stopped.IsRunning);
    }

    [Fact]
    public void StableReplacementPidIsReportedWithoutAStop()
    {
        var policy = new CompanionTransitionPolicy();
        policy.Observe(Pids());
        policy.Observe(Pids(7));
        policy.Observe(Pids(7));

        CompanionObservation first = policy.Observe(Pids(9));
        CompanionObservation replacement = policy.Observe(Pids(9));

        Assert.Equal(CompanionTransition.None, first.Transition);
        Assert.True(first.IsRunning);
        Assert.Equal(CompanionTransition.Replaced, replacement.Transition);
        Assert.True(replacement.IsRunning);
        Assert.Contains(9, replacement.ProcessIds);
    }

    [Fact]
    public void OverlappingReplacementDoesNotSilentlyBecomePartOfConfirmedIdentity()
    {
        var policy = new CompanionTransitionPolicy();
        policy.Observe(Pids());
        policy.Observe(Pids(7));
        policy.Observe(Pids(7));

        Assert.Equal(CompanionTransition.None, policy.Observe(Pids(7, 9)).Transition);
        Assert.Equal(CompanionTransition.None, policy.Observe(Pids(7, 9)).Transition);
        CompanionObservation replacement = policy.Observe(Pids(9));

        Assert.Equal(CompanionTransition.Replaced, replacement.Transition);
        Assert.DoesNotContain(7, replacement.ProcessIds);
        Assert.Contains(9, replacement.ProcessIds);
    }

    private static IReadOnlySet<int> Pids(params int[] ids) => ids.ToHashSet();
}
