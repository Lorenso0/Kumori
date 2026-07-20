using osu.Game.Beatmaps;
using osu.Game.Beatmaps.Formats;
using osu.Game.IO;

namespace Kumori.Gameplay;

public static class BpmAdjustBeatmap
{
    public static Beatmap Decode(string beatmapPath)
    {
        using var stream = File.OpenRead(beatmapPath);
        using var reader = new LineBufferedReader(stream);
        return Decoder.GetDecoder<Beatmap>(reader).Decode(reader);
    }

    public static double SourceBpm(IBeatmap beatmap)
    {
        double beatLength = beatmap.GetMostCommonBeatLength();
        double bpm = beatLength > 0 ? 60000 / beatLength : 0;
        return bpm > 0 && double.IsFinite(bpm) ? bpm : 0;
    }

    public static double SourceBpm(string beatmapPath) => SourceBpm(Decode(beatmapPath));
}
