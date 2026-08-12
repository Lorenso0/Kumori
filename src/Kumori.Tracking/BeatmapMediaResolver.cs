using Kumori.Core;

namespace Kumori.Tracking;

public sealed record BeatmapMediaResolution(
    string BeatmapPath,
    string AudioLogicalName,
    string AudioPath,
    string? BackgroundLogicalName,
    string? BackgroundPath,
    IReadOnlyDictionary<string, string> Files);

public static class BeatmapMediaResolver
{
    public static BeatmapMediaResolution? Resolve(
        long beatmapId,
        long beatmapSetId,
        string? checksum,
        string? difficulty,
        string primaryMirror,
        IReadOnlyList<string>? fallbackMirrors = null,
        CancellationToken cancellationToken = default)
    {
        if (beatmapId <= 0 || beatmapSetId <= 0)
            return null;

        LazerBeatmapAssets? lazer = LazerStorage.ResolveBeatmapAssets(
            beatmapId,
            beatmapSetId,
            difficulty);
        if (lazer?.AudioPath is { } lazerAudio)
        {
            var parsed = TosuMediaCache.ParseBeatmapMedia(
                lazer.BeatmapPath,
                "",
                beatmapId,
                beatmapSetId,
                cancellationToken);
            return new BeatmapMediaResolution(
                lazer.BeatmapPath,
                parsed.AudioFile,
                lazerAudio,
                parsed.BackgroundFile,
                lazer.BackgroundPath,
                lazer.Files);
        }

        TosuMediaCache.CachedMedia? cached = TosuMediaCache.Cache(
            new TosuMediaInfo
            {
                BeatmapId = beatmapId,
                BeatmapSetId = beatmapSetId,
                Checksum = checksum,
            },
            primaryMirror,
            fallbackMirrors,
            cancellationToken);
        if (cached is null || string.IsNullOrWhiteSpace(cached.AudioFile))
            return null;
        string root = Path.Combine(AppPaths.BeatmapMediaDir, cached.CacheKey);
        string beatmapPath = Path.Combine(root, cached.BeatmapFile);
        string audioPath = Path.Combine(root, cached.AudioFile);
        if (!File.Exists(beatmapPath) || !File.Exists(audioPath))
            return null;
        var files = Directory.EnumerateFiles(root)
            .ToDictionary(path => Path.GetFileName(path)!, path => path, StringComparer.OrdinalIgnoreCase);
        string? backgroundPath = string.IsNullOrWhiteSpace(cached.BackgroundFile)
            ? null
            : Path.Combine(root, cached.BackgroundFile);
        if (backgroundPath is not null && !File.Exists(backgroundPath))
            backgroundPath = null;
        return new BeatmapMediaResolution(
            beatmapPath,
            cached.AudioFile,
            audioPath,
            cached.BackgroundFile,
            backgroundPath,
            files);
    }
}
