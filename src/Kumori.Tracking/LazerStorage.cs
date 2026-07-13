namespace Kumori.Tracking;

/// <summary>Access to the local osu!lazer data-store location.</summary>
public static class LazerStorage
{
    public static string? GetRoot() => LazerMediaStore.FindStorageRoot();

    public static LazerStorageDiagnostics GetDiagnostics() => LazerMediaStore.GetDiagnostics();

    public static string? ResolveReplayFile(string beatmapHash, DateTimeOffset startedAt, string? gameFolder = null, DateTimeOffset? endedAt = null)
        => LazerMediaStore.ResolveReplayFile(beatmapHash, startedAt, gameFolder, endedAt);

    public static IReadOnlyList<string> ResolveReplayFiles(string beatmapHash, DateTimeOffset startedAt, string? gameFolder = null, DateTimeOffset? endedAt = null)
        => LazerMediaStore.ResolveReplayFiles(beatmapHash, startedAt, gameFolder, endedAt);

    public static LazerBeatmapAssets? ResolveBeatmapAssets(
        long? beatmapId,
        long? beatmapSetId,
        string? difficulty = null,
        string? gameFolder = null)
    {
        if (beatmapId is not > 0 || beatmapSetId is not > 0) return null;
        var files = LazerMediaStore.ResolveFiles(new TosuMediaInfo
        {
            BeatmapId = beatmapId,
            BeatmapSetId = beatmapSetId,
            GameFolder = gameFolder,
        });
        if (files is null) return null;
        var beatmaps = files.Where(pair => pair.Key.EndsWith(".osu", StringComparison.OrdinalIgnoreCase)).ToArray();
        var beatmap = !string.IsNullOrWhiteSpace(difficulty)
            ? beatmaps.FirstOrDefault(pair => pair.Key.Contains($"[{difficulty}]", StringComparison.OrdinalIgnoreCase))
            : default;
        if (string.IsNullOrWhiteSpace(beatmap.Value))
        {
            beatmap = beatmaps.FirstOrDefault(pair => LazerMediaStore.IsBeatmapId(pair.Value, beatmapId.Value));
        }
        if (string.IsNullOrWhiteSpace(beatmap.Value)) return null;

        var parsed = TosuMediaCache.ParseBeatmapMedia(beatmap.Value, "", beatmapId.Value, beatmapSetId.Value);
        string? audioPath = ResolveNamedFile(files, parsed.AudioFile);
        if (!string.IsNullOrWhiteSpace(parsed.AudioFile) && audioPath is null)
        {
            return null;
        }
        string? backgroundPath = ResolveNamedFile(files, parsed.BackgroundFile);
        if (!string.IsNullOrWhiteSpace(parsed.BackgroundFile) && backgroundPath is null)
        {
            return null;
        }

        return new LazerBeatmapAssets(
            beatmap.Value,
            files,
            audioPath,
            backgroundPath);
    }

    private static string? ResolveNamedFile(IReadOnlyDictionary<string, string> files, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var safeName = Path.GetFileName(name.Replace('\\', '/'));
        return files.TryGetValue(safeName, out var path) && File.Exists(path) ? path : null;
    }
}

public sealed record LazerBeatmapAssets(
    string BeatmapPath,
    IReadOnlyDictionary<string, string> Files,
    string? AudioPath,
    string? BackgroundPath);

public sealed record LazerStorageDiagnostics(
    string DefaultRoot,
    string? ConfiguredRoot,
    string? ResolvedRoot,
    bool RealmExists,
    bool FilesDirectoryExists,
    bool RealmOpened,
    string? Error);
