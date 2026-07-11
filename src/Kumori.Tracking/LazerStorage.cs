namespace Kumori.Tracking;

/// <summary>Access to the local osu!lazer data-store location.</summary>
public static class LazerStorage
{
    public static string? GetRoot() => LazerMediaStore.FindStorageRoot();

    public static LazerStorageDiagnostics GetDiagnostics() => LazerMediaStore.GetDiagnostics();

    public static LazerBeatmapAssets? ResolveBeatmapAssets(long? beatmapId, long? beatmapSetId, string? difficulty = null)
    {
        if (beatmapId is not > 0 || beatmapSetId is not > 0) return null;
        var files = LazerMediaStore.ResolveFiles(new TosuMediaInfo { BeatmapId = beatmapId, BeatmapSetId = beatmapSetId });
        if (files is null) return null;
        var beatmaps = files.Where(pair => pair.Key.EndsWith(".osu", StringComparison.OrdinalIgnoreCase)).ToArray();
        var beatmap = !string.IsNullOrWhiteSpace(difficulty)
            ? beatmaps.FirstOrDefault(pair => pair.Key.Contains($"[{difficulty}]", StringComparison.OrdinalIgnoreCase))
            : default;
        if (string.IsNullOrWhiteSpace(beatmap.Value))
        {
            beatmap = beatmaps.FirstOrDefault(pair => LazerMediaStore.IsBeatmapId(pair.Value, beatmapId.Value));
        }
        return string.IsNullOrWhiteSpace(beatmap.Value) ? null : new LazerBeatmapAssets(beatmap.Value, files);
    }
}

public sealed record LazerBeatmapAssets(string BeatmapPath, IReadOnlyDictionary<string, string> Files);

public sealed record LazerStorageDiagnostics(
    string DefaultRoot,
    string? ConfiguredRoot,
    string? ResolvedRoot,
    bool RealmExists,
    bool FilesDirectoryExists,
    bool RealmOpened,
    string? Error);
