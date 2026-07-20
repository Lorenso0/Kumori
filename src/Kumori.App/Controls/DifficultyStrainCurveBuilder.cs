using System.Globalization;
using System.IO;
using System.Text.Json;
using Kumori.Core.Models;
using Kumori.Gameplay;
using osu.Game.Beatmaps;
using osu.Game.Beatmaps.Formats;
using osu.Game.IO;
using osu.Game.Rulesets.Difficulty.Preprocessing;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Objects;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets.Osu.Difficulty.Preprocessing;
using osu.Game.Rulesets.Osu.Difficulty.Skills;
using osu.Game.Rulesets.Osu.Mods;
using osu.Game.Utils;
using Serilog;

namespace Kumori.App.Controls;

internal static class DifficultyStrainCurveBuilder
{
    private const int section_length = 400;

    public static IReadOnlyList<PressurePoint> Build(string osuFilePath, IReadOnlyList<ModEntry> modEntries)
    {
        Beatmap decoded;
        using (var stream = File.OpenRead(osuFilePath))
        using (var reader = new LineBufferedReader(stream))
        {
            decoded = Decoder.GetDecoder<Beatmap>(reader).Decode(reader);
        }

        var ruleset = new OsuRuleset();
        var mods = BuildMods(ruleset, decoded, modEntries);
        var playable = new FlatWorkingBeatmap(decoded).GetPlayableBeatmap(ruleset.RulesetInfo, mods);
        if (playable.HitObjects.Count < 2)
        {
            return Array.Empty<PressurePoint>();
        }

        var clockRate = ModUtils.CalculateRateWithMods(mods);
        var firstObjectTime = playable.HitObjects[0].StartTime;
        var firstAdjustedTime = firstObjectTime / clockRate;
        var difficultyObjects = CreateDifficultyHitObjects(playable, clockRate);

        var aim = new KumoriAim(mods, includeSliders: true);
        var speed = new KumoriSpeed(mods);
        var peaks = new List<double>();

        foreach (var difficultyObject in difficultyObjects)
        {
            aim.Process(difficultyObject);
            speed.Process(difficultyObject);

            var section = Math.Max(0, (int)Math.Floor((difficultyObject.StartTime - firstAdjustedTime) / section_length));
            while (peaks.Count <= section)
            {
                peaks.Add(0);
            }

            var aimValue = aim.LastObjectDifficulty;
            var speedValue = speed.LastObjectDifficulty;
            peaks[section] = Math.Max(peaks[section], aimValue + speedValue);
        }

        return NormalisePeaks(peaks, (int)Math.Round(firstObjectTime), clockRate);
    }

    internal static IReadOnlyList<PressurePoint> NormalisePeaks(
        IReadOnlyList<double> peaks,
        int firstObjectTime,
        double clockRate)
    {
        if (peaks.Count == 0)
        {
            return Array.Empty<PressurePoint>();
        }

        var nonzero = peaks.Where(v => v > 0).OrderBy(v => v).ToArray();
        var scale = nonzero.Length > 0
            ? nonzero[Math.Min(nonzero.Length - 1, (int)(nonzero.Length * 0.95))]
            : 1.0;
        scale = Math.Max(scale, 0.001);

        var result = new PressurePoint[peaks.Count];
        for (var i = 0; i < peaks.Count; i++)
        {
            // Difficulty sections are built on osu!'s rate-adjusted timeline.
            // Convert them back to raw beatmap time so misses, breaks, and UR
            // samples captured from tosu line up with the pressure curve.
            var time = firstObjectTime + (int)Math.Round(i * section_length * clockRate);
            result[i] = new PressurePoint(time, Math.Min(1.0, peaks[i] / scale));
        }

        return result;
    }

    private static Mod[] BuildMods(
        OsuRuleset ruleset,
        IBeatmap decoded,
        IReadOnlyList<ModEntry> modEntries)
    {
        var mods = new List<Mod>();
        var settings = new List<KeyValuePair<string, JsonElement>>();

        foreach (var entry in modEntries)
        {
            var acronym = (entry.Acronym ?? string.Empty).Trim();
            if (acronym.Equals("BPM", StringComparison.OrdinalIgnoreCase))
            {
                mods.Add(new OsuModBpmAdjust(decoded, BpmAdjustSettings.Parse(entry.SettingsJson)));
            }
            else if (acronym.Length > 0 && ruleset.CreateModFromAcronym(acronym) is { } mod)
            {
                mods.Add(mod);
            }

            settings.AddRange(ParseSettings(entry.SettingsJson));
        }

        var speedChange = FindSetting(settings, "speed_change");
        if (speedChange is { } rate)
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
        var ar = FindSetting(settings, "approach_rate", "ar");
        var cs = FindSetting(settings, "circle_size", "cs");
        var od = FindSetting(settings, "overall_difficulty", "od");
        var hp = FindSetting(settings, "drain_rate", "hp");

        if (ar is null && cs is null && od is null && hp is null)
        {
            return;
        }

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

    private static List<DifficultyHitObject> CreateDifficultyHitObjects(IBeatmap beatmap, double clockRate)
    {
        var result = new List<DifficultyHitObject>(beatmap.HitObjects.Count);
        for (var i = 1; i < beatmap.HitObjects.Count; i++)
        {
            result.Add(new OsuDifficultyHitObject(
                beatmap.HitObjects[i],
                beatmap.HitObjects[i - 1],
                clockRate,
                result,
                result.Count));
        }

        return result;
    }

    // The NuGet packages do not expose lazer's source-only object-difficulty
    // helper. These tiny derived types capture the values while the public
    // skill processor runs, without modifying upstream code.
    private sealed class KumoriAim(Mod[] mods, bool includeSliders) : Aim(mods, includeSliders)
    {
        public double LastObjectDifficulty { get; private set; }

        protected override double StrainValueAt(DifficultyHitObject current)
            => LastObjectDifficulty = base.StrainValueAt(current);
    }

    private sealed class KumoriSpeed(Mod[] mods) : Speed(mods)
    {
        public double LastObjectDifficulty { get; private set; }

        protected override double StrainValueAt(DifficultyHitObject current)
            => LastObjectDifficulty = base.StrainValueAt(current);
    }

    private static double? FindSetting(
        IReadOnlyList<KeyValuePair<string, JsonElement>> settings,
        params string[] keys)
    {
        foreach (var setting in settings)
        {
            if (keys.Any(k => string.Equals(k, setting.Key, StringComparison.OrdinalIgnoreCase))
                && TryNumber(setting.Value) is { } value)
            {
                return value;
            }
        }

        return null;
    }

    private static IReadOnlyList<KeyValuePair<string, JsonElement>> ParseSettings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<KeyValuePair<string, JsonElement>>();
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Array.Empty<KeyValuePair<string, JsonElement>>();
            }

            return doc.RootElement.EnumerateObject()
                .Select(p => new KeyValuePair<string, JsonElement>(p.Name, p.Value.Clone()))
                .ToArray();
        }
        catch (JsonException ex)
        {
            Log.Debug(ex, "Skipping malformed mod settings JSON while building difficulty curve");
            return Array.Empty<KeyValuePair<string, JsonElement>>();
        }
    }

    private static double? TryNumber(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Number => element.TryGetDouble(out var d) ? d : null,
        JsonValueKind.String => double.TryParse(
            element.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var s) ? s : null,
        _ => null,
    };
}
