using Kumori.App.Skins;
using Xunit;

namespace Kumori.App.Tests;

public sealed class SkinExtraCompletenessTests
{
    [Fact]
    public void Hit_circle_pack_reports_the_missing_logical_element()
    {
        var report = SkinExtraCompleteness.Analyze(
            "osu.hitcircles",
            ["hitcircle.png", "hitcircle@2x.png", "hitcircleoverlay.png"]);

        var missing = Assert.Single(report.MissingAssets);
        Assert.Equal("approachcircle", missing.Key);
        Assert.Equal("approach circle", missing.DisplayName);
    }

    [Fact]
    public void Resolution_variants_do_not_count_as_separate_required_elements()
    {
        var report = SkinExtraCompleteness.Analyze(
            "osu.hitcircles",
            [
                "hitcircle@2x.png",
                "hitcircleoverlay@2x.png",
                "approachcircle.png",
            ]);

        Assert.True(report.IsComplete);
    }

    [Fact]
    public void Number_font_reports_each_missing_digit_and_accepts_any_prefix()
    {
        var files = Enumerable.Range(0, 9)
            .Select(digit => $"custom-{digit}@2x.png")
            .ToArray();

        var report = SkinExtraCompleteness.Analyze("osu.number-font", files);

        var missing = Assert.Single(report.MissingAssets);
        Assert.Equal("digit:9", missing.Key);
        Assert.True(SkinExtraCompleteness.Supplies(
            "osu.number-font",
            "other-9.png",
            missing.Key));
    }

    [Fact]
    public void Hitsound_pack_reports_missing_core_suffixes()
    {
        var report = SkinExtraCompleteness.Analyze(
            "audio.hitsounds.normal",
            ["normal-hitnormal.wav", "normal-hitclap.ogg"]);

        Assert.Equal(
            ["hitsound:hitwhistle", "hitsound:hitfinish"],
            report.MissingAssets.Select(asset => asset.Key));
    }

    [Fact]
    public void Families_without_a_completeness_contract_are_left_alone()
    {
        var report = SkinExtraCompleteness.Analyze(
            "osu.cursor",
            ["cursor.png"]);

        Assert.True(report.IsComplete);
    }

    [Fact]
    public void Donor_font_digit_keeps_its_original_role_prefix()
    {
        var partialFingerprint = new string('a', 64);
        var donor = new SkinExtraPackManifest
        {
            Id = "donor",
            DisplayName = "Donor",
            FamilyId = "osu.number-font",
            Area = "osu!",
            FamilyName = "Number fonts",
            Fingerprint = partialFingerprint,
            FontRoles = ["Hitcircle"],
            IniPatch =
            [
                new SkinExtraIniPatchEntry("Fonts", "HitCirclePrefix", "other"),
            ],
        };

        var plan = SkinExtraPackPlanner.BuildFamilyPlan(
            donor,
            [],
            [new SkinExtraPackFile("other-9.png", [9])],
            SkinIniDocument.ParseText(
                "[Fonts]\nHitCirclePrefix: kumori-font-aaaaaaaaaaaa\n"));

        var change = Assert.Single(plan.Changes);
        Assert.Equal("other-9.png", change.Filename);
        Assert.Contains(plan.IniPatch, entry =>
            entry.Key == "HitCirclePrefix"
            && entry.Value == "other");
    }
}
