using Kumori.Skins;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Kumori.Skins.Tests;

public sealed class SkinImageTransformServiceTests
{
    [Fact]
    public void Colorize_preserves_alpha_and_encodes_a_valid_png()
    {
        var source = png(new Rgba32(20, 30, 40, 99));
        var transformed = new SkinImageTransformService().Apply(
            source,
            "cursor.png",
            new SkinImageTransform(
                SkinImageTransformMode.Colorize,
                new SkinRgb(200, 100, 50)));

        using var image = Image.Load<Rgba32>(transformed);
        var pixel = image[0, 0];
        Assert.Equal(200, pixel.R);
        Assert.Equal(100, pixel.G);
        Assert.Equal(50, pixel.B);
        Assert.Equal(99, pixel.A);
    }

    [Fact]
    public void Invalid_transform_values_fail_closed()
    {
        var source = png(new Rgba32(20, 30, 40, 255));
        var service = new SkinImageTransformService();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            service.Apply(
                source,
                "cursor.png",
                new SkinImageTransform(
                    SkinImageTransformMode.HueSaturationLightness,
                    new SkinRgb(),
                    SaturationMultiplier: -1)));
    }

    [Fact]
    public void External_image_validation_decodes_dimensions_and_visibility()
    {
        var result = SkinMediaValidationService.ValidateImage(
            "cursor.png",
            png(new Rgba32(20, 30, 40, 255)));

        Assert.Equal(1, result.Width);
        Assert.Equal(1, result.Height);
        Assert.True(result.HasVisiblePixels);
    }

    [Theory]
    [InlineData("cursor.wav")]
    [InlineData("cursor.txt")]
    public void External_image_validation_rejects_wrong_media_type(
        string filename)
    {
        Assert.Throws<InvalidDataException>(() =>
            SkinMediaValidationService.ValidateImage(
                filename,
                png(new Rgba32(20, 30, 40, 255))));
    }

    [Fact]
    public void External_image_validation_rejects_malformed_content()
    {
        Assert.Throws<InvalidDataException>(() =>
            SkinMediaValidationService.ValidateImage(
                "cursor.png",
                "not an image"u8.ToArray()));
    }

    private static byte[] png(Rgba32 pixel)
    {
        using var image = new Image<Rgba32>(1, 1, pixel);
        using var stream = new MemoryStream();
        image.SaveAsPng(stream);
        return stream.ToArray();
    }
}
