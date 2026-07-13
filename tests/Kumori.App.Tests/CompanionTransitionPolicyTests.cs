using Xunit;

namespace Kumori.App.Tests;

public sealed class CompanionTransitionPolicyTests
{
    [Theory]
    [InlineData(false, false, false, CompanionTransition.None)]
    [InlineData(false, false, true, CompanionTransition.EnsureTracking)]
    [InlineData(true, false, true, CompanionTransition.Started)]
    [InlineData(true, true, false, CompanionTransition.Stopped)]
    [InlineData(true, true, true, CompanionTransition.None)]
    [InlineData(true, false, false, CompanionTransition.None)]
    public void Evaluate_OnlyReturnsActionsForRealTransitions(
        bool hasObservation,
        bool wasRunning,
        bool isRunning,
        CompanionTransition expected)
    {
        Assert.Equal(expected, CompanionTransitionPolicy.Evaluate(hasObservation, wasRunning, isRunning));
    }
}
