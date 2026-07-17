using Kumori.Core.State;
using Xunit;

namespace Kumori.App.Tests;

public sealed class TrayTrackingStatusTests
{
    [Fact]
    public void ClosedOsuOverridesTransientTosuConnectionFailure()
    {
        var state = new AppState
        {
            Companions = new CompanionStatus { OsuRunning = false },
            Tracking = new TrackingStatus
            {
                TosuConnected = false,
                Health = HealthLevel.Error,
                Detail = "tosu: A task was canceled.",
            },
        };

        Assert.Equal("tosu: Waiting for osu!", App.FormatTrayTrackingStatus(state));
    }

    [Fact]
    public void RunningOsuKeepsUsefulTosuFailure()
    {
        var state = new AppState
        {
            Companions = new CompanionStatus { OsuRunning = true },
            Tracking = new TrackingStatus
            {
                TosuConnected = false,
                Health = HealthLevel.Error,
                Detail = "tosu: not reachable",
            },
        };

        Assert.Equal("tosu: not reachable", App.FormatTrayTrackingStatus(state));
    }
}
