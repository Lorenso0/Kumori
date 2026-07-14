using Kumori.Native;
using Xunit;

namespace Kumori.App.Tests;

public sealed class DualModeServiceTests
{
    [Fact]
    public void SlowDetectionSendsToggleExactlyOnce()
    {
        var sends = 0;
        var polls = 0;

        var reached = DualModeService.SendOnceAndPoll(
            () =>
            {
                sends++;
                return true;
            },
            () =>
            {
                polls++;
                return false;
            },
            pollCount: 8,
            waitForNextPoll: static () => { });

        Assert.False(reached);
        Assert.Equal(1, sends);
        Assert.Equal(8, polls);
    }

    [Fact]
    public void SuccessfulDetectionStillSendsToggleExactlyOnce()
    {
        var sends = 0;
        var polls = 0;

        var reached = DualModeService.SendOnceAndPoll(
            () =>
            {
                sends++;
                return true;
            },
            () => ++polls == 3,
            pollCount: 8,
            waitForNextPoll: static () => { });

        Assert.True(reached);
        Assert.Equal(1, sends);
        Assert.Equal(3, polls);
    }

    [Fact]
    public void CancellationBeforeTransitionDoesNotSendToggle()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var sends = 0;

        Assert.Throws<OperationCanceledException>(() =>
            DualModeService.SendOnceAndPoll(
                () =>
                {
                    sends++;
                    return true;
                },
                static () => false,
                pollCount: 8,
                waitForNextPoll: static () => { },
                cancellation.Token));

        Assert.Equal(0, sends);
    }

    [Fact]
    public void SlowHardwareRetriesAndStopsAsSoonAsTargetIsObserved()
    {
        var sends = 0;
        var probes = 0;

        var reached = DualModeService.SendWithRetriesAndPoll(
            () =>
            {
                sends++;
                return true;
            },
            () => ++probes >= 5,
            attemptCount: 4,
            pollsPerAttempt: 2,
            finalPollCount: 2,
            waitForNextPoll: static () => { });

        Assert.True(reached);
        Assert.Equal(2, sends);
        Assert.Equal(5, probes);
    }

    [Fact]
    public void UnresponsiveHardwareUsesOnlyBoundedRetryCount()
    {
        var sends = 0;

        var reached = DualModeService.SendWithRetriesAndPoll(
            () =>
            {
                sends++;
                return true;
            },
            static () => false,
            attemptCount: 3,
            pollsPerAttempt: 2,
            finalPollCount: 2,
            waitForNextPoll: static () => { });

        Assert.False(reached);
        Assert.Equal(3, sends);
    }
}
