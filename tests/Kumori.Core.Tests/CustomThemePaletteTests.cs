using Kumori.Core.Settings;
using Xunit;

namespace Kumori.Core.Tests;

public sealed class CustomThemePaletteTests
{
    [Fact]
    public void ExportAndImportPreserveValidatedPalette()
    {
        var source = new CustomThemeSettings { Name = "Midnight Citrus" };
        source.Colors["AccentPink"] = "#12ab34";
        source.Colors["OverlayBackground"] = "#CC010203";

        var restored = CustomThemePalette.Import(CustomThemePalette.Export(source));

        Assert.Equal("Midnight Citrus", restored.Name);
        Assert.Equal("#12AB34", restored.Colors["AccentPink"]);
        Assert.Equal("#CC010203", restored.Colors["OverlayBackground"]);
        Assert.Equal(CustomThemePalette.ColorKeys.Count, restored.Colors.Count);
    }

    [Fact]
    public void ImportRejectsWrongFormatAndIncompletePalette()
    {
        Assert.Throws<InvalidDataException>(() => CustomThemePalette.Import(
            """{"format":"another-app","version":1,"name":"Nope","colors":{}}"""));
        Assert.Throws<InvalidDataException>(() => CustomThemePalette.Import(
            """{"format":"kumori-theme","version":1,"name":"Nope","colors":{"AccentPink":"#FFFFFF"}}"""));
    }

    [Theory]
    [InlineData("#123456", "#123456")]
    [InlineData("#aa123456", "#AA123456")]
    [InlineData("123456", null)]
    [InlineData("#XYZXYZ", null)]
    [InlineData("#1234", null)]
    public void HexColoursAreNormalisedStrictly(string input, string? expected)
    {
        var valid = CustomThemePalette.TryNormalizeHex(input, out var normalized);
        Assert.Equal(expected is not null, valid);
        Assert.Equal(expected ?? string.Empty, normalized);
    }
}
