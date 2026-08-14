using Xunit;

namespace Kumori.Skins.Tests;

public sealed class SkinStudioEffectiveAssetsTests
{
    [Fact]
    public void Slider_start_falls_back_only_when_custom_base_is_absent()
    {
        var fallback = SkinStudioEffectiveAssetResolver.Resolve(
            "sliderstartcircleoverlay",
            ["hitcircle.png", "hitcircleoverlay.png"]);
        Assert.Equal(SkinStudioEffectiveAssetState.Fallback, fallback.State);
        Assert.Equal("hitcircleoverlay", fallback.ResolvedComponent);

        var blocked = SkinStudioEffectiveAssetResolver.Resolve(
            "sliderstartcircleoverlay",
            ["hitcircleoverlay.png", "sliderstartcircle.png"]);
        Assert.Equal(SkinStudioEffectiveAssetState.BlockedFallback, blocked.State);
        Assert.False(blocked.IsAvailable);
    }

    [Fact]
    public void Deleting_custom_slider_overlay_offers_safe_pair_removal()
    {
        var impact = SkinStudioEffectiveAssetResolver.DescribeDeletion(
            "sliderstartcircleoverlay.png",
            ["sliderstartcircle.png", "sliderstartcircleoverlay.png"]);
        Assert.True(impact.HasDependency);
        Assert.Equal(["sliderstartcircle"], impact.SafeFallbackComponents);
    }

    [Fact]
    public void Preflight_detects_transparent_endpoint_that_blocks_fallback()
    {
        var report = SkinStudioEffectiveAssetResolver.BuildPreflight([
            file("sliderstartcircle.png", "transparent"),
            file("hitcircle.png"),
            file("hitcircleoverlay.png"),
        ], "osu.slider");

        Assert.Contains(report.Issues, issue => issue.Code == "invisible-slider-endpoint");
        Assert.True(report.HasErrors);
    }

    [Fact]
    public void Random_hitsounds_chooses_one_fresh_pack_per_bank()
    {
        var chosen = SkinStudioRandomMix.ChooseHitsounds([
            new("normal-old", "audio.hitsounds.normal", true),
            new("normal-fresh", "audio.hitsounds.normal", false),
            new("soft", "audio.hitsounds.soft", false),
            new("drum", "audio.hitsounds.drum", false),
        ], new Random(1));

        Assert.Equal(3, chosen.Count);
        Assert.Contains("normal-fresh", chosen);
        Assert.DoesNotContain("normal-old", chosen);
    }

    private static SkinExtraManifestFile file(
        string filename,
        string? similarity = null) =>
        new(filename, filename, filename, filename, filename, similarity);
}
