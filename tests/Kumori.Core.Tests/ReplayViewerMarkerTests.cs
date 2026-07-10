using Kumori.ReplayViewer;
using osu.Game.Rulesets.Scoring;
using Xunit;

namespace Kumori.Core.Tests;

public class ReplayViewerMarkerTests
{
    [Theory]
    [InlineData("miss", "Miss")]
    [InlineData("slider_break", "SliderBreak")]
    [InlineData("100", "Ok")]
    [InlineData("hit_100", "Ok")]
    [InlineData("50", "Meh")]
    [InlineData("hit_50", "Meh")]
    public void ContractKindsAreNormalised(string value, string expected)
        => Assert.Equal(expected, KumoriTimelineMarkers.KindFromContract(value)?.ToString());

    [Theory]
    [InlineData(HitResult.Miss, "Miss")]
    [InlineData(HitResult.Ok, "Ok")]
    [InlineData(HitResult.Meh, "Meh")]
    [InlineData(HitResult.LargeTickMiss, "SliderBreak")]
    [InlineData(HitResult.SmallTickMiss, "SliderBreak")]
    [InlineData(HitResult.ComboBreak, "SliderBreak")]
    public void HitResultsMapToTimelineKinds(HitResult value, string expected)
        => Assert.Equal(expected, KumoriTimelineMarkers.KindFromHitResult(value)?.ToString());

    [Theory]
    [InlineData("300")]
    [InlineData("great")]
    [InlineData("")]
    public void UnsupportedContractKindsAreIgnored(string value)
        => Assert.Null(KumoriTimelineMarkers.KindFromContract(value));

    [Theory]
    [InlineData(HitResult.Great)]
    [InlineData(HitResult.Perfect)]
    [InlineData(HitResult.SmallTickHit)]
    public void UnsupportedHitResultsAreIgnored(HitResult value)
        => Assert.Null(KumoriTimelineMarkers.KindFromHitResult(value));
}
