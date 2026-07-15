using Xunit;

namespace Kumori.App.Tests;

public sealed class WindowMaximizeWorkAreaTests
{
    [Theory]
    [InlineData(1.0, 720, 480)]
    [InlineData(1.25, 900, 600)]
    [InlineData(1.5, 1080, 720)]
    [InlineData(2.0, 1440, 960)]
    public void MinimumTrackSizeUsesPhysicalPixels(double scale, int expectedWidth, int expectedHeight)
    {
        var result = WindowMaximizeWorkArea.ComputeMinimumTrackSize(720, 480, scale, scale);

        Assert.Equal(expectedWidth, result.Width);
        Assert.Equal(expectedHeight, result.Height);
    }

    [Theory]
    [InlineData(double.NaN, 1.0)]
    [InlineData(720, double.NaN)]
    [InlineData(720, 0)]
    [InlineData(-1, 1.0)]
    public void InvalidDimensionsDoNotCreateAnInvalidConstraint(double width, double scale)
    {
        var result = WindowMaximizeWorkArea.ComputeMinimumTrackSize(width, 480, scale, 1);

        Assert.Equal(0, result.Width);
        Assert.Equal(480, result.Height);
    }
}
