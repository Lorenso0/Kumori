using Kumori.Skins;
using Xunit;

namespace Kumori.Skins.Tests;

public sealed class SkinHitsoundAuditionTests
{
    [Fact]
    public void Layered_hitsounds_play_hitnormal_with_each_addition()
    {
        var plan = Assert.IsType<SkinHitsoundAuditionPlan>(
            SkinHitsoundAudition.Build("audio.hitsounds.soft", true));

        Assert.Equal("soft", plan.Bank);
        Assert.Equal(["soft-hitnormal"], plan.Steps[0].Components);
        Assert.Equal(
            ["soft-hitnormal", "soft-hitwhistle"],
            plan.Steps[1].Components);
        Assert.Equal(
            ["soft-hitnormal", "soft-hitfinish"],
            plan.Steps[2].Components);
        Assert.Equal(
            ["soft-hitnormal", "soft-hitclap"],
            plan.Steps[3].Components);
    }

    [Fact]
    public void Disabled_layering_suppresses_hitnormal_on_addition_hits()
    {
        var plan = Assert.IsType<SkinHitsoundAuditionPlan>(
            SkinHitsoundAudition.Build("audio.hitsounds.drum", false));

        Assert.Equal(["drum-hitnormal"], plan.Steps[0].Components);
        Assert.Equal(["drum-hitwhistle"], plan.Steps[1].Components);
        Assert.Equal(["drum-hitfinish"], plan.Steps[2].Components);
        Assert.Equal(["drum-hitclap"], plan.Steps[3].Components);
    }

    [Fact]
    public void Non_hitsound_families_have_no_hitsound_audition()
    {
        Assert.Null(SkinHitsoundAudition.Build("audio.spinner", true));
    }

    [Fact]
    public void Countdown_scenario_follows_osu_gameplay_order()
    {
        var plan = Assert.IsType<SkinHitsoundAuditionPlan>(
            SkinAudioScenarioAudition.Build("audio.countdown", true));

        Assert.Equal(["Ready", "Three", "Two", "One", "Go"],
            plan.Steps.Select(step => step.Label));
        Assert.Equal(["count3"], plan.Steps[1].Components);
    }

    [Fact]
    public void Spinner_scenario_plays_spin_before_bonus()
    {
        var plan = Assert.IsType<SkinHitsoundAuditionPlan>(
            SkinAudioScenarioAudition.Build("audio.spinner", true));

        Assert.Equal(["spinnerspin"], plan.Steps[0].Components);
        Assert.Equal(["spinnerbonus"], plan.Steps[1].Components);
    }
}
