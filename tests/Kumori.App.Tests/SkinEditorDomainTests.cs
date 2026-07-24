using System.Text;
using System.Windows.Media;
using Kumori.App.Skins;
using Kumori.Tracking;
using Xunit;

namespace Kumori.App.Tests;

public sealed class SkinEditorDomainTests
{
    [Fact]
    public void Categorize_orders_root_families_and_groups_subfolders()
    {
        var files = new[]
        {
            File("z/extra.png"),
            File("cursor@2x.png"),
            File("hit300-0.png"),
            File("scorebar-bg.png"),
            File("normal-hitnormal.wav"),
            File("skin.ini"),
        };

        var categories = SkinElementCategorizer.Categorize(files);

        Assert.Equal(
            ["Cursor", "Judgements", "Scorebar", "Sounds", "z"],
            categories.Select(category => category.Name));
        Assert.True(categories[^1].IsSubfolder);
        Assert.DoesNotContain(categories.SelectMany(category => category.Files),
            entry => entry.Filename == "skin.ini");
    }

    [Fact]
    public void Categorize_combines_standard_and_2x_files_and_prefers_2x()
    {
        var categories = SkinElementCategorizer.Categorize(
        [
            File("approachcircle.png"),
            File("approachcircle@2x.png"),
        ]);

        var entry = Assert.Single(Assert.Single(categories).Files);
        Assert.Equal("approachcircle@2x.png", entry.Filename);
        Assert.True(entry.HasPairedResolution);
        Assert.Equal("approachcircle.png", Assert.Single(entry.ResolutionVariants).Filename);
        Assert.Contains("1× + 2×", entry.ResolutionVariantLabel);
    }

    [Fact]
    public void Logical_element_synchronizes_recolor_edits_to_both_resolutions()
    {
        var categories = SkinElementCategorizer.Categorize(
        [
            File("cursor.png"),
            File("cursor@2x.png"),
        ]);
        var entry = Assert.Single(Assert.Single(categories).Files);
        entry.Mode = SkinRecolorMode.HueSaturation;
        entry.HueShiftDegrees = 75;
        entry.SaturationMultiplier = 1.4;
        entry.LightnessMultiplier = 0.8;

        entry.SynchronizeEditsToVariants();

        var standard = Assert.Single(entry.ResolutionVariants);
        Assert.Equal(entry.Mode, standard.Mode);
        Assert.Equal(75, standard.HueShiftDegrees);
        Assert.Equal(1.4, standard.SaturationMultiplier);
        Assert.Equal(0.8, standard.LightnessMultiplier);
        Assert.True(standard.HasEdits);
    }

    [Fact]
    public void Visible_pixel_detection_rejects_fully_transparent_placeholders()
    {
        Assert.False(SkinImageTools.HasVisiblePixels(
        [
            255, 255, 255, 0,
            0, 0, 0, 0,
        ]));
        Assert.True(SkinImageTools.HasVisiblePixels(
        [
            0, 0, 0, 0,
            255, 255, 255, 1,
        ]));
    }

    [Fact]
    public void SkinIni_round_trips_unknown_comments_and_repeated_mania_sections()
    {
        const string source =
            "// header\r\n[General]\r\nName: Example\r\nUnknownFuture: keep\r\n\r\n"
            + "[Mania]\r\nKeys: 4\r\nColumnStart: 100\r\n[Mania]\r\nKeys: 7\r\n";
        var document = SkinIniDocument.Parse(Encoding.UTF8.GetBytes(source));

        document.SetValue("General", "Name", "Edited");
        document.SetValue("Colours", "Combo1", "1,2,3");

        var result = document.ToText();
        Assert.Contains("// header\r\n", result);
        Assert.Contains("UnknownFuture: keep", result);
        Assert.Equal(2, result.Split("[Mania]").Length - 1);
        Assert.Contains("Name: Edited", result);
        Assert.Contains("[Colours]\r\nCombo1: 1,2,3", result);
    }

    [Theory]
    [InlineData("255,0,128", true)]
    [InlineData("256,0,0", false)]
    [InlineData("1,2", false)]
    public void SkinIni_validates_rgb_channels(string value, bool expected)
    {
        var definition = SkinIniSchema.Colours[0];
        Assert.Equal(expected, SkinIniDocument.TryValidate(definition, value, out _));
    }

    [Fact]
    public void Recolor_operations_preserve_alpha()
    {
        var colorized = new byte[] { 20, 40, 80, 123 };
        var tinted = (byte[])colorized.Clone();
        var shifted = (byte[])colorized.Clone();

        SkinImageTools.ApplyColorize(colorized, Color.FromRgb(255, 0, 0));
        SkinImageTools.ApplyTint(tinted, Color.FromRgb(0, 255, 0));
        SkinImageTools.ApplyHueSaturation(shifted, 90, 1.2, 0.8);

        Assert.Equal(123, colorized[3]);
        Assert.Equal(123, tinted[3]);
        Assert.Equal(123, shifted[3]);
        Assert.NotEqual(new byte[] { 20, 40, 80 }, colorized[..3]);
        Assert.NotEqual(new byte[] { 20, 40, 80 }, tinted[..3]);
        Assert.NotEqual(new byte[] { 20, 40, 80 }, shifted[..3]);
    }

    [Fact]
    public void Legacy_slider_renderer_draws_a_transparent_bgra_slider_body()
    {
        var path = LegacySliderRenderer.SampleSCurve(20, 50, 100, 50, segments: 12);

        var bitmap = LegacySliderRenderer.Render(
            120,
            100,
            path,
            12,
            Color.FromRgb(255, 192, 0),
            sliderBorder: null,
            sliderTrackOverride: null);
        var pixels = new byte[120 * 100 * 4];
        bitmap.CopyPixels(pixels, 120 * 4, 0);

        Assert.Equal(120, bitmap.PixelWidth);
        Assert.Equal(100, bitmap.PixelHeight);
        Assert.Contains(pixels.Where((_, index) => index % 4 == 3), alpha => alpha > 0);
        Assert.Equal(0, pixels[3]);
    }

    [Fact]
    public void Legacy_slider_renderer_applies_slider_track_override()
    {
        var path = new[] { new System.Windows.Point(10, 20), new System.Windows.Point(50, 20) };
        var red = LegacySliderRenderer.Render(
            60, 40, path, 10, Colors.White, null, Color.FromRgb(255, 0, 0));
        var blue = LegacySliderRenderer.Render(
            60, 40, path, 10, Colors.White, null, Color.FromRgb(0, 0, 255));
        var redPixels = new byte[60 * 40 * 4];
        var bluePixels = new byte[60 * 40 * 4];
        red.CopyPixels(redPixels, 60 * 4, 0);
        blue.CopyPixels(bluePixels, 60 * 4, 0);

        var centre = (20 * 60 + 30) * 4;
        Assert.True(redPixels[centre + 2] > redPixels[centre]);
        Assert.True(bluePixels[centre] > bluePixels[centre + 2]);
    }

    [Fact]
    public void Legacy_slider_renderer_honours_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            LegacySliderRenderer.Render(
                880,
                505,
                LegacySliderRenderer.SampleSCurve(190, 310, 670, 180),
                50,
                Colors.White,
                sliderBorder: null,
                sliderTrackOverride: null,
                cancellationToken: cancellation.Token));
    }

    private static LazerSkinFileInfo File(string filename) =>
        new(filename, new string('a', 64), 10);
}
