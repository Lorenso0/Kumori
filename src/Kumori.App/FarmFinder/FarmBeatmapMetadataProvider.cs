using System.Globalization;
using System.IO;
using Kumori.FarmFinder;
using Kumori.Storage;

namespace Kumori.App.FarmFinder;

public interface IFarmBeatmapMetadataProvider
{
    Task<FarmBeatmap> EnrichAsync(
        FarmBeatmap beatmap,
        CancellationToken cancellationToken = default);
}

internal sealed class FarmBeatmapMetadataProvider(
    FarmBeatmapFileCache beatmapFiles,
    FarmFinderRepository repository) : IFarmBeatmapMetadataProvider
{
    public async Task<FarmBeatmap> EnrichAsync(
        FarmBeatmap beatmap,
        CancellationToken cancellationToken = default)
    {
        if (HasDifficultyStats(beatmap))
            return beatmap;

        var path = await beatmapFiles.GetAsync(beatmap, cancellationToken);
        if (string.IsNullOrWhiteSpace(path))
            return beatmap;

        var values = await ReadDifficultyAsync(path, cancellationToken);
        var enriched = beatmap with
        {
            CircleSize = values.CircleSize ?? beatmap.CircleSize,
            ApproachRate = values.ApproachRate ?? values.OverallDifficulty ?? beatmap.ApproachRate,
            OverallDifficulty = values.OverallDifficulty ?? beatmap.OverallDifficulty,
            DrainRate = values.DrainRate ?? beatmap.DrainRate,
        };
        if (!HasDifficultyStats(enriched))
            return beatmap;

        await repository.UpdateBeatmapDifficultyAsync(enriched, cancellationToken);
        return enriched;
    }

    private static bool HasDifficultyStats(FarmBeatmap beatmap) =>
        beatmap.CircleSize is not null &&
        beatmap.ApproachRate is not null &&
        beatmap.OverallDifficulty is not null &&
        beatmap.DrainRate is not null;

    private static async Task<DifficultyValues> ReadDifficultyAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream);
        var inDifficulty = false;
        double? cs = null;
        double? ar = null;
        double? od = null;
        double? hp = null;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('['))
            {
                if (inDifficulty)
                    break;
                inDifficulty = trimmed.Equals("[Difficulty]", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inDifficulty)
                continue;

            var separator = trimmed.IndexOf(':');
            if (separator <= 0 || !double.TryParse(
                    trimmed[(separator + 1)..].Trim(),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var value) ||
                !double.IsFinite(value) || value is < 0 or > 10)
                continue;

            switch (trimmed[..separator].Trim().ToUpperInvariant())
            {
                case "CIRCLESIZE": cs = value; break;
                case "APPROACHRATE": ar = value; break;
                case "OVERALLDIFFICULTY": od = value; break;
                case "HPDRAINRATE": hp = value; break;
            }
        }
        return new DifficultyValues(cs, ar, od, hp);
    }

    private sealed record DifficultyValues(
        double? CircleSize,
        double? ApproachRate,
        double? OverallDifficulty,
        double? DrainRate);
}
