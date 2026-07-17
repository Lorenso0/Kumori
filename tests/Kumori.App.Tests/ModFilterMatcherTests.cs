using Kumori.App.ViewModels;
using Kumori.Core.Models;
using Xunit;

namespace Kumori.App.Tests;

public sealed class ModFilterMatcherTests
{
    private static readonly AttemptSummary HiddenHardRock = new()
    {
        ModsKey = "HDHR",
        Mods =
        [
            new ModEntry("HD", "{}"),
            new ModEntry("HR", "{}"),
        ],
    };

    [Fact]
    public void ContainsMode_AllowsAdditionalActiveMods()
    {
        Assert.True(ModFilterMatcher.Matches(HiddenHardRock, ["HD"], exact: false));
        Assert.True(ModFilterMatcher.Matches(HiddenHardRock, ["HD", "HR"], exact: false));
        Assert.False(ModFilterMatcher.Matches(HiddenHardRock, ["DT"], exact: false));
    }

    [Fact]
    public void ExactMode_RequiresTheSameCombination()
    {
        Assert.False(ModFilterMatcher.Matches(HiddenHardRock, ["HD"], exact: true));
        Assert.True(ModFilterMatcher.Matches(HiddenHardRock, ["HR", "HD"], exact: true));
    }

    [Fact]
    public void LegacyPackedModKeysUseTheSameMatchingRules()
    {
        var attempt = new AttemptSummary { ModsKey = "HD10K" };

        Assert.True(ModFilterMatcher.Matches(attempt, ["10K"], exact: false));
        Assert.True(ModFilterMatcher.Matches(attempt, ["10K", "HD"], exact: true));
    }

    [Fact]
    public void EmptySelectionDoesNotFilterNoModPlays()
    {
        var attempt = new AttemptSummary { ModsKey = "NM" };

        Assert.True(ModFilterMatcher.Matches(attempt, [], exact: true));
    }
}
