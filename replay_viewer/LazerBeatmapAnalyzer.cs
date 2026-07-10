using osu.Game.Beatmaps;
using osu.Game.Beatmaps.Formats;
using osu.Game.IO;
using osu.Game.Rulesets.Objects;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets.Osu.Objects;
using osu.Game.Rulesets.Osu.Scoring;
using osu.Game.Rulesets.Scoring;

namespace Kumori.ReplayViewer;

/// <summary>
/// The canonical beatmap model for Kumori. No beatmap, difficulty, slider,
/// radius, or hit-window formula is reimplemented here; every value is read
/// from the playable beatmap produced by lazer.
/// </summary>
public static class LazerBeatmapAnalyzer
{
    public static BeatmapAnalysis Decode(string path, IReadOnlyList<Mod>? mods = null)
    {
        Beatmap decoded;
        using (var stream = File.OpenRead(path))
        using (var reader = new LineBufferedReader(stream))
            decoded = Decoder.GetDecoder<Beatmap>(reader).Decode(reader);

        var ruleset = new OsuRuleset();
        var playable = new FlatWorkingBeatmap(decoded).GetPlayableBeatmap(
            ruleset.RulesetInfo, mods ?? Array.Empty<Mod>());
        var objects = playable.HitObjects.Cast<OsuHitObject>().ToArray();

        var windows = new OsuHitWindows();
        windows.SetDifficulty(playable.Difficulty.OverallDifficulty);

        return new BeatmapAnalysis(
            decoded.BeatmapInfo.Metadata.Artist,
            decoded.BeatmapInfo.Metadata.Title,
            decoded.BeatmapInfo.DifficultyName,
            playable.Difficulty.ApproachRate,
            playable.Difficulty.OverallDifficulty,
            playable.Difficulty.CircleSize,
            objects.Select(createObject).ToArray(),
            new HitWindowAnalysis(
                windows.WindowFor(HitResult.Great),
                windows.WindowFor(HitResult.Ok),
                windows.WindowFor(HitResult.Meh),
                windows.WindowFor(HitResult.Miss)));
    }

    private static HitObjectAnalysis createObject(OsuHitObject hitObject)
    {
        var renderedPosition = hitObject.StackedPosition;
        var nested = hitObject.NestedHitObjects.OfType<OsuHitObject>()
                              .Select(n => new NestedObjectAnalysis(
                                  n.GetType().Name, n.StartTime, n.StackedPosition.X, n.StackedPosition.Y))
                              .ToArray();

        return hitObject switch
        {
            Slider slider => new HitObjectAnalysis(
                nameof(Slider), slider.StartTime, slider.EndTime, renderedPosition.X, renderedPosition.Y,
                slider.Radius, slider.TimePreempt, slider.RepeatCount, slider.Velocity,
                slider.TickDistance, nested),
            Spinner spinner => new HitObjectAnalysis(
                nameof(Spinner), spinner.StartTime, spinner.EndTime, renderedPosition.X, renderedPosition.Y,
                spinner.Radius, spinner.TimePreempt, 0, 0, 0, nested),
            HitCircle circle => new HitObjectAnalysis(
                nameof(HitCircle), circle.StartTime, circle.StartTime, renderedPosition.X, renderedPosition.Y,
                circle.Radius, circle.TimePreempt, 0, 0, 0, nested),
            _ => throw new InvalidDataException($"Unsupported osu! hit object {hitObject.GetType().FullName}."),
        };
    }
}

public sealed record BeatmapAnalysis(
    string Artist,
    string Title,
    string Difficulty,
    double ApproachRate,
    double OverallDifficulty,
    double CircleSize,
    IReadOnlyList<HitObjectAnalysis> Objects,
    HitWindowAnalysis HitWindows);

public sealed record HitObjectAnalysis(
    string Kind,
    double StartTime,
    double EndTime,
    float X,
    float Y,
    double Radius,
    double TimePreempt,
    int RepeatCount,
    double Velocity,
    double TickDistance,
    IReadOnlyList<NestedObjectAnalysis> NestedObjects);

public sealed record NestedObjectAnalysis(string Kind, double StartTime, float X, float Y);
public sealed record HitWindowAnalysis(double Great, double Ok, double Meh, double Miss);
