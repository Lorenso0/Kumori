using System.Text.Json;
using Kumori.ReplayViewer;
using osu.Game.Rulesets.Osu.Mods;
using Xunit;

namespace Kumori.Core.Tests;

public class LazerReplayAdapterTests
{
    [Fact]
    public void CreateCapturedMods_AppliesDifficultyAdjustSettingsFromStructuredMods()
    {
        var attempt = new AttemptContract
        {
            Mods =
            [
                new ModContract
                {
                    Acronym = "DA",
                    Settings = Settings("""
                    {
                        "circle_size": 4.2,
                        "approach_rate": 9.8,
                        "overall_difficulty": 8.7,
                        "drain_rate": 6.5
                    }
                    """),
                },
            ],
        };

        var da = Assert.IsType<OsuModDifficultyAdjust>(Assert.Single(LazerReplayAdapter.CreateCapturedMods(attempt)));
        Assert.Equal(4.2f, da.CircleSize.Value);
        Assert.Equal(9.8f, da.ApproachRate.Value);
        Assert.Equal(8.7f, da.OverallDifficulty.Value);
        Assert.Equal(6.5f, da.DrainRate.Value);
    }

    [Fact]
    public void CreateCapturedMods_AppliesDifficultyAdjustShortSettingNamesFromJsonKey()
    {
        var mods = LazerReplayAdapter.CreateCapturedMods("""
        [
            {
                "acronym": "DA",
                "settings": {
                    "cs": 3.8,
                    "ar": 10.2,
                    "od": 9.1,
                    "hp": 5.4
                }
            }
        ]
        """);

        var da = Assert.IsType<OsuModDifficultyAdjust>(Assert.Single(mods));
        Assert.Equal(3.8f, da.CircleSize.Value);
        Assert.Equal(10.2f, da.ApproachRate.Value);
        Assert.Equal(9.1f, da.OverallDifficulty.Value);
        Assert.Equal(5.4f, da.DrainRate.Value);
    }

    private static Dictionary<string, JsonElement> Settings(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateObject()
                       .ToDictionary(property => property.Name, property => property.Value.Clone());
    }
}
