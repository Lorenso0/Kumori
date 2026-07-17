using Kumori.Native;
using System.Text;
using Xunit;

namespace Kumori.App.Tests;

public sealed class DualModeServiceTests
{
    [Theory]
    [InlineData("LG ULTRAGEAR+", true)]
    [InlineData("LG Electronics 5K2K", true)]
    [InlineData("GSM", false)]
    [InlineData("Generic PnP Monitor", false)]
    [InlineData("", false)]
    public void CompatibilityDetectionOnlyAcceptsLgDualModeDescriptions(
        string description,
        bool expected)
    {
        Assert.Equal(expected, DualModeService.IsCompatibleMonitorDescription(description));
    }

    [Theory]
    [InlineData(@"\\?\DISPLAY#GSM7862#5&14613921&1&UID4357", "GSM7862")]
    [InlineData(@"MONITOR\AUS27FD\5&14613921&1&UID4355", "AUS27FD")]
    public void MonitorHardwareIdHandlesDeviceInterfaceAndMonitorIds(
        string deviceId,
        string expected)
    {
        Assert.Equal(expected, DualModeService.MonitorHardwareId(deviceId));
    }

    [Fact]
    public void EdidMonitorNameReadsWindowsFriendlyNameDescriptor()
    {
        var edid = new byte[128];
        edid[57] = 0xFC;
        Encoding.ASCII.GetBytes("LG ULTRAGEAR+").CopyTo(edid, 59);

        Assert.Equal("LG ULTRAGEAR+", DualModeService.EdidMonitorName(edid));
    }

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
