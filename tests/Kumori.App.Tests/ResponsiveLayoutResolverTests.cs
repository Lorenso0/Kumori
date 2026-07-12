using Xunit;

namespace Kumori.App.Tests;

public sealed class ResponsiveLayoutResolverTests
{
    [Theory]
    [InlineData(720, ResponsiveLayoutMode.Compact)]
    [InlineData(899, ResponsiveLayoutMode.Compact)]
    [InlineData(900, ResponsiveLayoutMode.Standard)]
    [InlineData(1279, ResponsiveLayoutMode.Standard)]
    [InlineData(1280, ResponsiveLayoutMode.Wide)]
    [InlineData(3840, ResponsiveLayoutMode.Wide)]
    public void ResolveUsesStableWidthBreakpoints(double width, ResponsiveLayoutMode expected)
    {
        Assert.Equal(expected, ResponsiveLayoutResolver.Resolve(width, 800).Mode);
    }

    [Theory]
    [InlineData(599, true)]
    [InlineData(600, false)]
    public void ResolveTracksShortHeightIndependently(double height, bool expected)
    {
        Assert.Equal(expected, ResponsiveLayoutResolver.Resolve(1280, height).IsShort);
    }
}
