using System.Text.Json;
using Kumori.ReplayViewer;
using osu.Framework.Extensions.Color4Extensions;
using Xunit;

namespace Kumori.Core.Tests;

public sealed class ReplayViewerThemeTests
{
    [Theory]
    [InlineData("pulse", "pulse")]
    [InlineData("windows-fluent", "windows-fluent")]
    [InlineData("custom", "custom")]
    [InlineData("unknown", "refined-kumori")]
    public void ContractNormalisesTheme(string input, string expected)
    {
        var contract = Contract(input);
        Assert.Equal(expected, contract.ThemeId);
    }

    [Fact]
    public void MissingThemeFallsBackToRefinedKumori()
    {
        Assert.Equal("refined-kumori", Contract(null).ThemeId);
    }

    [Fact]
    public void PaletteConfigurationChangesAnalyzerAccent()
    {
        AdvancedAnalyzerColours.Configure("pulse");
        Assert.Equal(Color4Extensions.FromHex("#ff4eb8"), AdvancedAnalyzerColours.Accent);
        AdvancedAnalyzerColours.Configure("unknown");
        Assert.Equal(Color4Extensions.FromHex("#ff2da8"), AdvancedAnalyzerColours.Accent);
    }

    [Fact]
    public void CustomPaletteConfiguresAnalyzerSemanticColours()
    {
        var colors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["PanelBackground"] = "#101112",
            ["AccentPink"] = "#131415",
            ["Danger"] = "#161718",
            ["Warning"] = "#191A1B",
            ["Success"] = "#1C1D1E",
        };

        AdvancedAnalyzerColours.Configure("custom", colors);

        Assert.Equal(Color4Extensions.FromHex("#131415"), AdvancedAnalyzerColours.Accent);
        Assert.Equal(Color4Extensions.FromHex("#161718"), AdvancedAnalyzerColours.Miss);
        Assert.Equal(Color4Extensions.FromHex("#191A1B"), AdvancedAnalyzerColours.Meh);
        Assert.Equal(Color4Extensions.FromHex("#1C1D1E"), AdvancedAnalyzerColours.Ok);
    }

    private static ViewerContract Contract(string? theme)
    {
        Dictionary<string, JsonElement> settings = [];
        if (theme is not null)
        {
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(theme));
            settings["kumori_theme"] = document.RootElement.Clone();
        }
        return new ViewerContract
        {
            ContractVersion = ViewerContract.CurrentVersion,
            Attempt = new AttemptContract(),
            BeatmapPath = "unused.osu",
            Settings = settings,
        };
    }
}
