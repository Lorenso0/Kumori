using Kumori.Native;
using Xunit;

namespace Kumori.App.Tests;

public sealed class StableGraphDiscoveryPolicyTests
{
    [Fact]
    public void PolicyKeepsDiscoverySlicesSmallAndTotalTraversalBounded()
    {
        Assert.InRange(StableGraphDiscoveryPolicy.MaximumPollDuration.TotalMilliseconds, 2, 3);
        Assert.InRange(StableGraphDiscoveryPolicy.MaximumObjectsPerPoll, 1, 256);
        Assert.Equal(25_000, StableGraphDiscoveryPolicy.MaximumObjects);
        Assert.Equal(5_000_000, StableGraphDiscoveryPolicy.MaximumHeapObjects);
        Assert.Equal(3, StableGraphDiscoveryPolicy.MaximumAttempts);
        Assert.Equal(64, StableGraphDiscoveryPolicy.MaximumTailFramesPerPoll);
    }

    [Fact]
    public void PollBudgetStopsAtEveryIndependentLimit()
    {
        Assert.True(StableGraphDiscoveryPolicy.HasBudget(TimeSpan.Zero, 0, 0, 0));
        Assert.False(StableGraphDiscoveryPolicy.HasBudget(
            StableGraphDiscoveryPolicy.MaximumPollDuration,
            0,
            0,
            0));
        Assert.False(StableGraphDiscoveryPolicy.HasBudget(
            TimeSpan.Zero,
            StableGraphDiscoveryPolicy.MaximumObjectsPerPoll,
            0,
            0));
        Assert.False(StableGraphDiscoveryPolicy.HasBudget(
            TimeSpan.Zero,
            0,
            StableGraphDiscoveryPolicy.MaximumReferencesPerPoll,
            0));
        Assert.False(StableGraphDiscoveryPolicy.HasBudget(
            TimeSpan.Zero,
            0,
            0,
            StableGraphDiscoveryPolicy.MaximumCandidateOperationsPerPoll));
    }

    [Fact]
    public void DiscoveryUsesFastPollOnlyWhileWorkOrTailIsActive()
    {
        Assert.Equal(
            StableGraphDiscoveryPolicy.ActivePollInterval,
            StableGraphDiscoveryPolicy.PollInterval(discoveryInProgress: true, hasFrames: false));
        Assert.Equal(
            StableGraphDiscoveryPolicy.ActivePollInterval,
            StableGraphDiscoveryPolicy.PollInterval(discoveryInProgress: false, hasFrames: true));
        Assert.Equal(
            StableGraphDiscoveryPolicy.IdlePollInterval,
            StableGraphDiscoveryPolicy.PollInterval(discoveryInProgress: false, hasFrames: false));
    }

    [Theory]
    [InlineData(1, 32)]
    [InlineData(16, 32)]
    [InlineData(25, 50)]
    public void FinalizationAllowsTwoBridgePollsToDrain(double pollMilliseconds, double expectedMilliseconds)
    {
        var delay = StableGraphDiscoveryPolicy.FinalizationDrainDelay(
            TimeSpan.FromMilliseconds(pollMilliseconds));

        Assert.Equal(expectedMilliseconds, delay.TotalMilliseconds);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    [InlineData(4, true)]
    public void DiscoveryNegativeCachesAtAttemptCap(int attempts, bool exhausted)
        => Assert.Equal(exhausted, StableGraphDiscoveryPolicy.AttemptsExhausted(attempts));

    [Fact]
    public void NegativeCacheExpiresAfterBoundedCooldown()
    {
        var now = DateTimeOffset.UtcNow;
        var retryAt = now + StableGraphDiscoveryPolicy.RediscoveryCooldown;

        Assert.False(StableGraphDiscoveryPolicy.CanRetryNegativeCache(now, retryAt));
        Assert.True(StableGraphDiscoveryPolicy.CanRetryNegativeCache(retryAt, retryAt));
    }

    [Theory]
    [InlineData(10, 2, 2, 6, true)]
    [InlineData(12, 2, 3, 4, true)]
    [InlineData(13, 2, 2, 6, false)]
    [InlineData(10, 3, 2, 6, false)]
    [InlineData(10, 2, 1, 6, false)]
    [InlineData(10, 2, 2, 3, false)]
    [InlineData(10, 2, 2, 9, false)]
    public void ReplayFrameMetadataShapeRejectsIrrelevantListTypes(
        int totalFields,
        int floatFields,
        int integerFields,
        int booleanFields,
        bool expected)
        => Assert.Equal(
            expected,
            StableGraphDiscoveryPolicy.IsReplayFrameFieldShape(
                totalFields,
                floatFields,
                integerFields,
                booleanFields));

    [Fact]
    public void DiscoveryAlwaysIncludesAuthoritativeRulesetRoot()
    {
        Assert.Equal(
            [0x1234_0000u, 0x2345_0000u],
            StableGraphDiscoveryPolicy.DiscoveryRoots(0x1234_0000, 0x2345_0000));
        Assert.Equal(
            [0x1234_0000u],
            StableGraphDiscoveryPolicy.DiscoveryRoots(0x1234_0000, 0));
        Assert.Equal(
            [0x1234_0000u],
            StableGraphDiscoveryPolicy.DiscoveryRoots(0x1234_0000, 0x1234_0000));
    }

    [Theory]
    [InlineData("stable replay graph discovery in progress", false)]
    [InlineData("waiting for stable gameplay ruleset", false)]
    [InlineData("stable typed heap fallback in background (scan attempt 1/3)", false)]
    [InlineData("replay list not matched in bounded discovery attempt 1/3", true)]
    [InlineData("stable typed heap fallback did not find a populated replay list (scan attempt 1/3)", true)]
    [InlineData("stable typed heap fallback reached object cap 5000000 (scan attempt 1/3)", true)]
    [InlineData("stable typed heap fallback failed: InvalidOperationException: test", true)]
    public void DiagnosticSnapshotWaitsForAnActualDiscoveryFailure(string diagnostic, bool expected)
        => Assert.Equal(expected, StableGraphDiscoveryPolicy.ShouldCaptureDiagnosticSnapshot(diagnostic));
}
