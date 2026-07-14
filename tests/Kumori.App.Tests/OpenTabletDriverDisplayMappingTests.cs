using Kumori.Native;
using Xunit;

namespace Kumori.App.Tests;

public sealed class OpenTabletDriverDisplayMappingTests
{
    [Fact]
    public void ResolutionChangeRequiresCachedDisplayRefresh()
    {
        OpenTabletDriverService.OtdMonitor[] previous =
        [
            new("DISPLAY1", -1440, -774, 1440, 2560),
            new("DISPLAY2", 0, 0, 4096, 1728),
        ];
        OpenTabletDriverService.OtdMonitor[] current =
        [
            new("DISPLAY1", -1440, -774, 1440, 2560),
            new("DISPLAY2", 0, 0, 1920, 1080),
        ];

        Assert.False(OpenTabletDriverService.TopologyEquals(previous, current));
    }

    [Fact]
    public void IdenticalTopologyDoesNotRequireCachedDisplayRefresh()
    {
        OpenTabletDriverService.OtdMonitor[] topology =
        [
            new("DISPLAY1", -1440, -774, 1440, 2560),
            new("DISPLAY2", 0, 0, 1920, 1080),
        ];

        Assert.True(OpenTabletDriverService.TopologyEquals(topology, topology.ToArray()));
    }
}
