using System.Text.Json;
using osu.Game.Beatmaps;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets.Osu.Mods;

namespace Kumori.Gameplay;

/// <summary>
/// Calculates osu!standard difficulty with the same configurable mods captured
/// by Kumori. This includes the local BPM Adjust mod, which upstream telemetry
/// cannot reconstruct on its own.
/// </summary>
public static class BeatmapDifficultyCalculator
{
    public static BeatmapDifficultyResult Calculate(
        string beatmapPath,
        IReadOnlyList<CapturedMod> capturedMods)
    {
        Beatmap beatmap = BpmAdjustBeatmap.Decode(beatmapPath);
        var ruleset = new OsuRuleset();
        Mod[] mods = CreateMods(ruleset, beatmap, capturedMods);
        var calculator = ruleset.CreateDifficultyCalculator(new FlatWorkingBeatmap(beatmap));

        return new BeatmapDifficultyResult(
            calculator.Calculate().StarRating,
            calculator.Calculate(mods).StarRating);
    }

    internal static Mod[] CreateMods(
        OsuRuleset ruleset,
        IBeatmap beatmap,
        IReadOnlyList<CapturedMod> capturedMods)
    {
        var mods = new List<Mod>();
        var settings = new List<KeyValuePair<string, JsonElement>>();

        foreach (CapturedMod captured in capturedMods)
        {
            string acronym = captured.Acronym.Trim();
            if (acronym.Equals("BPM", StringComparison.OrdinalIgnoreCase))
            {
                mods.Add(new OsuModBpmAdjust(beatmap, BpmAdjustSettings.Parse(captured.SettingsJson)));
            }
            else if (acronym.Length > 0 && ruleset.CreateModFromAcronym(acronym) is { } mod)
            {
                mods.Add(mod);
            }

            settings.AddRange(ParseSettings(captured.SettingsJson));
        }

        if (FindSetting(settings, "speed_change") is { } rate)
        {
            var rateMod = mods.OfType<ModRateAdjust>().FirstOrDefault();
            if (rateMod is null)
            {
                rateMod = rate >= 1 ? new OsuModDoubleTime() : new OsuModHalfTime();
                mods.Add(rateMod);
            }
            rateMod.SpeedChange.Value = rate;
        }

        ApplyDifficultyAdjustSettings(mods, settings);
        return mods.ToArray();
    }

    private static void ApplyDifficultyAdjustSettings(
        List<Mod> mods,
        IReadOnlyList<KeyValuePair<string, JsonElement>> settings)
    {
        double? ar = FindSetting(settings, "approach_rate", "ar");
        double? cs = FindSetting(settings, "circle_size", "cs");
        double? od = FindSetting(settings, "overall_difficulty", "od", "accuracy");
        double? hp = FindSetting(settings, "drain_rate", "hp", "hp_drain");
        if (ar is null && cs is null && od is null && hp is null)
            return;

        var difficultyAdjust = mods.OfType<OsuModDifficultyAdjust>().FirstOrDefault();
        if (difficultyAdjust is null)
        {
            difficultyAdjust = new OsuModDifficultyAdjust();
            mods.Add(difficultyAdjust);
        }

        if (ar is { } approachRate) difficultyAdjust.ApproachRate.Value = (float)approachRate;
        if (cs is { } circleSize) difficultyAdjust.CircleSize.Value = (float)circleSize;
        if (od is { } overallDifficulty) difficultyAdjust.OverallDifficulty.Value = (float)overallDifficulty;
        if (hp is { } drainRate) difficultyAdjust.DrainRate.Value = (float)drainRate;
    }

    private static IReadOnlyList<KeyValuePair<string, JsonElement>> ParseSettings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? document.RootElement.EnumerateObject()
                    .Select(property => new KeyValuePair<string, JsonElement>(property.Name, property.Value.Clone()))
                    .ToArray()
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static double? FindSetting(
        IReadOnlyList<KeyValuePair<string, JsonElement>> settings,
        params string[] keys)
    {
        foreach (KeyValuePair<string, JsonElement> setting in settings)
        {
            if (!keys.Contains(setting.Key, StringComparer.OrdinalIgnoreCase))
                continue;

            if (setting.Value.ValueKind == JsonValueKind.Number && setting.Value.TryGetDouble(out double number))
                return number;
            if (setting.Value.ValueKind == JsonValueKind.String
                && double.TryParse(
                    setting.Value.GetString(),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out number))
            {
                return number;
            }
        }

        return null;
    }
}

public readonly record struct CapturedMod(string Acronym, string SettingsJson = "{}");

public readonly record struct BeatmapDifficultyResult(double BaseStars, double AdjustedStars);
