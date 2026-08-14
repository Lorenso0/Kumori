namespace Kumori.Skins;

/// <summary>
/// A short, deterministic audition of the legacy osu! hitsound flags. Each
/// step represents one hit object; components in the same step play together.
/// </summary>
public sealed record SkinHitsoundAuditionStep(
    string Label,
    IReadOnlyList<string> Components);

public sealed record SkinHitsoundAuditionPlan(
    string Bank,
    bool LayeredHitSounds,
    IReadOnlyList<SkinHitsoundAuditionStep> Steps,
    double IntervalMilliseconds)
{
    public string MappingSummary => LayeredHitSounds
        ? "Normal = hitnormal · additions = hitnormal + whistle / finish / clap"
        : "Normal = hitnormal · additions = whistle / finish / clap (base suppressed)";
}

public static class SkinHitsoundAudition
{
    private const double default_interval = 500;

    public static bool IsHitsoundFamily(string familyId) =>
        familyId.StartsWith("audio.hitsounds.", StringComparison.OrdinalIgnoreCase);

    public static SkinHitsoundAuditionPlan? Build(
        string familyId,
        bool layeredHitSounds)
    {
        if (!IsHitsoundFamily(familyId))
            return null;

        var bank = familyId.Split('.').Last().ToLowerInvariant();
        if (bank == "taiko")
        {
            return new SkinHitsoundAuditionPlan(
                bank,
                layeredHitSounds,
                [
                    new("Normal", ["taiko-normal-hitnormal"]),
                    new("Clap", ["taiko-normal-hitclap"]),
                    new("Finish", ["taiko-normal-hitfinish"]),
                    new("Whistle", ["taiko-normal-hitwhistle"]),
                ],
                default_interval);
        }

        if (bank is not ("normal" or "soft" or "drum"))
            return null;

        var normal = $"{bank}-hitnormal";
        return new SkinHitsoundAuditionPlan(
            bank,
            layeredHitSounds,
            [
                new("Normal", [normal]),
                addition("Whistle", $"{bank}-hitwhistle"),
                addition("Finish", $"{bank}-hitfinish"),
                addition("Clap", $"{bank}-hitclap"),
            ],
            default_interval);

        SkinHitsoundAuditionStep addition(string label, string component) =>
            new(
                label,
                layeredHitSounds ? [normal, component] : [component]);
    }
}

/// <summary>Gameplay-shaped auditions for the other legacy audio families.</summary>
public static class SkinAudioScenarioAudition
{
    public static SkinHitsoundAuditionPlan? Build(string familyId, bool layeredHitSounds)
    {
        var hitsounds = SkinHitsoundAudition.Build(familyId, layeredHitSounds);
        if (hitsounds is not null)
            return hitsounds;
        return familyId.ToLowerInvariant() switch
        {
            "audio.spinner" => plan("spinner", 850,
                ("Spin", new[] { "spinnerspin" }),
                ("Bonus", new[] { "spinnerbonus" })),
            "audio.countdown" => plan("countdown", 650,
                ("Ready", new[] { "ready" }),
                ("Three", new[] { "count3" }),
                ("Two", new[] { "count2" }),
                ("One", new[] { "count1" }),
                ("Go", new[] { "go" })),
            "audio.nightcore" => plan("nightcore", 320,
                ("Kick", new[] { "nightcore-kick" }),
                ("Clap", new[] { "nightcore-clap" }),
                ("Kick", new[] { "nightcore-kick" }),
                ("Hat", new[] { "nightcore-hat" })),
            "audio.interface" => plan("interface", 450,
                ("Hover", new[] { "menuclick" }),
                ("Select", new[] { "menuhit" }),
                ("Back", new[] { "back-button-click" })),
            "audio.gameplay" => plan("gameplay", 700,
                ("Pause", new[] { "pause-loop" }),
                ("Resume", new[] { "pause-loop" })),
            "audio.combobreak" => plan("combobreak", 700,
                ("Combo break", new[] { "combobreak" })),
            "audio.sectionpass" => plan("section pass", 700,
                ("Section pass", new[] { "sectionpass" })),
            "audio.sectionfail" => plan("section fail", 700,
                ("Section fail", new[] { "sectionfail" })),
            "audio.applause" => plan("results", 700,
                ("Results applause", new[] { "applause" })),
            _ => null,
        };

        static SkinHitsoundAuditionPlan plan(
            string name,
            double interval,
            params (string Label, string[] Components)[] steps) =>
            new(
                name,
                false,
                steps.Select(step => new SkinHitsoundAuditionStep(
                    step.Label,
                    step.Components)).ToArray(),
                interval);
    }

    public static string Describe(SkinHitsoundAuditionPlan plan) => plan.Bank switch
    {
        "normal" or "soft" or "drum" or "taiko" => plan.MappingSummary,
        "spinner" => "spinner spin → bonus",
        "countdown" => "ready → 3 → 2 → 1 → go",
        "nightcore" => "kick → clap → kick → hat",
        "interface" => "hover → select → back",
        _ => $"osu! {plan.Bank} scenario",
    };
}
