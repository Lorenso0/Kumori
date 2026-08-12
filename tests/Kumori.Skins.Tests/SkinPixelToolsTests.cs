using Kumori.Skins;
using Xunit;

namespace Kumori.Skins.Tests;

public sealed class SkinPixelToolsTests
{
    [Fact]
    public void Colour_operations_preserve_alpha_and_skip_transparent_colorize_pixels()
    {
        byte[] pixels =
        [
            10, 20, 30, 0,
            50, 100, 200, 127,
        ];

        SkinPixelTools.ApplyColorize(pixels, new SkinRgb(255, 0, 128));

        Assert.Equal([10, 20, 30, 0], pixels[..4]);
        Assert.Equal([128, 0, 255, 127], pixels[4..]);
        Assert.True(SkinPixelTools.HasVisiblePixels(pixels));
    }

    [Fact]
    public void Hue_saturation_transform_matches_primary_colour_rotation()
    {
        byte[] red = [0, 0, 255, 255];

        SkinPixelTools.ApplyHueSaturation(red, 120, 1, 1);

        Assert.InRange(red[0], 0, 1);
        Assert.InRange(red[1], 254, 255);
        Assert.InRange(red[2], 0, 1);
        Assert.Equal(255, red[3]);
    }

    [Fact]
    public void Multiplicative_tint_preserves_transparency_and_scales_channels()
    {
        byte[] pixels = [100, 120, 200, 200];

        SkinPixelTools.ApplyMultiplicativeTint(
            pixels,
            new SkinRgb(128, 255, 64));

        Assert.Equal(25, pixels[0]);
        Assert.Equal(120, pixels[1]);
        Assert.Equal(100, pixels[2]);
        Assert.Equal(200, pixels[3]);
    }
}
