using System.IO;
using osu.Game.Beatmaps;
using osu.Game.Beatmaps.Formats;
using osu.Game.IO;
using osu.Game.Rulesets.Osu;

namespace Kumori.App.Controls;

/// <summary>
/// Calculates the unmodded star rating directly from a cached .osu file.
/// tosu's live payload exposes only the active (modded) star rating.
/// </summary>
internal static class BeatmapStarRatingCalculator
{
    public static double CalculateOriginal(string osuFilePath)
    {
        Beatmap decoded;
        using (var stream = File.OpenRead(osuFilePath))
        using (var reader = new LineBufferedReader(stream))
        {
            decoded = Decoder.GetDecoder<Beatmap>(reader).Decode(reader);
        }

        var ruleset = new OsuRuleset();
        var calculator = ruleset.CreateDifficultyCalculator(new FlatWorkingBeatmap(decoded));
        return calculator.Calculate().StarRating;
    }
}
