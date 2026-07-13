using Kumori.App.ViewModels;
using Kumori.Core.Models;
using Xunit;

namespace Kumori.App.Tests;

public sealed class AttemptDetailsViewModelTests
{
    [Fact]
    public void AccuracyValue_truncates_to_match_the_in_game_display()
    {
        var viewModel = new AttemptDetailsViewModel(null!)
        {
            Details = new AttemptDetails
            {
                Summary = new AttemptSummary { Accuracy = 90.08754793430288 },
            },
        };

        Assert.Equal("90.08%", viewModel.AccuracyValue);
    }

    [Fact]
    public void Stable_slider_overview_remains_visible_but_is_marked_unsupported()
    {
        var viewModel = new AttemptDetailsViewModel(null!)
        {
            Details = new AttemptDetails
            {
                ClientKind = "stable",
                Mods = [new ModEntry("HD", "{}"), new ModEntry("CL", "{}")],
            },
        };

        Assert.True(viewModel.HasRichSliderData);
        Assert.True(viewModel.IsStablePlay);
        Assert.Equal("—", viewModel.LargeTickText);
        Assert.Equal("—", viewModel.SliderTailText);
        Assert.Equal("—", viewModel.SliderBreakText);
        Assert.Equal(0.38, viewModel.SliderStatsOpacity);
        Assert.Contains("not available", viewModel.SliderStatsToolTip);

        viewModel.Details = new AttemptDetails { ClientKind = "lazer", Mods = [new ModEntry("HD", "{}")] };
        Assert.True(viewModel.HasRichSliderData);
        Assert.False(viewModel.IsStablePlay);
        Assert.Equal(1, viewModel.SliderStatsOpacity);
    }

    [Fact]
    public void Replay_recovery_notice_explains_missing_tosu_data_and_simulation()
    {
        var viewModel = new AttemptDetailsViewModel(null!)
        {
            Details = new AttemptDetails
            {
                ResultRecoveredFromReplay = true,
                ResultRecoverySimulationCompleted = true,
            },
        };

        Assert.True(viewModel.HasReplayRecoveredResult);
        Assert.Contains("tosu gameplay data was unavailable", viewModel.ReplayRecoveryNotice);
        Assert.Contains("re-simulated", viewModel.ReplayRecoveryNotice);
    }
}
