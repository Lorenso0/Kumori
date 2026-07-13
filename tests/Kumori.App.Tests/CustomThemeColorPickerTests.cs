using System.Windows.Media;
using Xunit;

namespace Kumori.App.Tests;

public sealed class CustomThemeColorPickerTests
{
    [Theory]
    [InlineData(0, 255, 0, 0)]
    [InlineData(120, 0, 255, 0)]
    [InlineData(240, 0, 0, 255)]
    public void FromHsv_maps_primary_hues(double hue, byte red, byte green, byte blue)
    {
        Color actual = CustomThemeColorPicker.FromHsv(hue, 1, 1, 255);

        Assert.Equal(Color.FromArgb(255, red, green, blue), actual);
    }

    [Fact]
    public void Hsv_round_trip_preserves_custom_colour_and_opacity()
    {
        Color expected = Color.FromArgb(143, 232, 85, 141);

        CustomThemeColorPicker.ToHsv(expected, out double hue, out double saturation, out double brightness);
        Color actual = CustomThemeColorPicker.FromHsv(hue, saturation, brightness, expected.A);

        Assert.Equal(expected, actual);
    }
}
