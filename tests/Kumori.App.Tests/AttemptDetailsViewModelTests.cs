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
}
