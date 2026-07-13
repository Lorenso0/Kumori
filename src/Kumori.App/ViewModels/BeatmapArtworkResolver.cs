using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using Kumori.Core;
using Kumori.Core.Models;
using Kumori.Tracking;
using Serilog;

namespace Kumori.App.ViewModels;

internal static class BeatmapArtworkResolver
{
    private static readonly ConcurrentDictionary<string, string?> Cache = new();

    public static string? Resolve(AttemptSummary summary)
    {
        var key = MediaCacheKey(summary.Checksum, summary.OsuBeatmapId);
        if (!string.IsNullOrWhiteSpace(key))
        {
            if (!Cache.TryGetValue(key, out var local))
            {
                local = LazerStorage.ResolveBeatmapAssets(
                    summary.OsuBeatmapId,
                    summary.BeatmapSetId,
                    summary.Difficulty)?.BackgroundPath
                    ?? ResolveLocal(key);
                if (!string.IsNullOrWhiteSpace(local))
                {
                    Cache[key] = local;
                }
            }
            if (!string.IsNullOrWhiteSpace(local))
            {
                return local;
            }
        }

        return summary.BeatmapSetId is > 0
            ? $"https://assets.ppy.sh/beatmaps/{summary.BeatmapSetId}/covers/cover.jpg"
            : null;
    }

    public static string? ResolveBeatmapFile(AttemptSummary summary)
    {
        var lazer = LazerStorage.ResolveBeatmapAssets(summary.OsuBeatmapId, summary.BeatmapSetId, summary.Difficulty);
        if (lazer is not null)
        {
            return lazer.BeatmapPath;
        }

        var key = MediaCacheKey(summary.Checksum, summary.OsuBeatmapId);
        var manifestFile = string.IsNullOrWhiteSpace(key)
            ? null
            : ResolveManifestFile(key, "beatmap_file");
        if (!string.IsNullOrWhiteSpace(manifestFile))
        {
            return manifestFile;
        }

        if (summary.OsuBeatmapId is > 0)
        {
            foreach (var directory in new[] { AppPaths.LegacyBeatmapFilesDir, AppPaths.OldLegacyBeatmapFilesDir })
            {
                var legacy = Path.Combine(directory, $"{summary.OsuBeatmapId}.osu");
                if (File.Exists(legacy))
                {
                    return legacy;
                }
            }
        }

        return null;
    }

    public static string? ResolveMediaDirectory(AttemptSummary summary)
    {
        var key = MediaCacheKey(summary.Checksum, summary.OsuBeatmapId);
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        foreach (var root in MediaRoots())
        {
            var directory = Path.Combine(root, key);
            if (Directory.Exists(directory))
            {
                return directory;
            }
        }
        return null;
    }

    private static string? ResolveLocal(string cacheKey)
        => ResolveManifestFile(cacheKey, "background_file");

    private static string? ResolveManifestFile(string cacheKey, string propertyName)
    {
        foreach (var root in MediaRoots())
        {
            var resolved = ResolveManifestFileFromRoot(root, cacheKey, propertyName);
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                return resolved;
            }
        }
        return null;
    }

    private static string? ResolveManifestFileFromRoot(string root, string cacheKey, string propertyName)
    {
        var manifestPath = Path.Combine(root, cacheKey, "manifest.json");
        if (!File.Exists(manifestPath)) return null;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (!doc.RootElement.TryGetProperty(propertyName, out var fileProperty))
            {
                return null;
            }

            var file = Path.GetFileName(fileProperty.GetString());
            if (string.IsNullOrWhiteSpace(file))
            {
                return null;
            }

            var path = Path.Combine(Path.GetDirectoryName(manifestPath)!, file);
            return File.Exists(path) ? path : null;
        }
        catch (IOException ex)
        {
            Log.Warning(ex, "Beatmap media manifest read failed for {ManifestPath}", manifestPath);
            return null;
        }
        catch (JsonException ex)
        {
            Log.Warning(ex, "Beatmap media manifest parse failed for {ManifestPath}", manifestPath);
            return null;
        }
    }

    private static IEnumerable<string> MediaRoots()
    {
        yield return AppPaths.BeatmapMediaDir;
        if (!Directory.Exists(AppPaths.BeatmapCacheDir)) yield break;

        foreach (var directory in Directory.EnumerateDirectories(AppPaths.BeatmapCacheDir, "media.old*"))
        {
            yield return directory;
        }
    }

    private static string MediaCacheKey(string? checksum, long? beatmapId)
    {
        var cleaned = new string((checksum ?? "")
            .ToLowerInvariant()
            .Where(char.IsLetterOrDigit)
            .Take(64)
            .ToArray());
        if (!string.IsNullOrWhiteSpace(cleaned))
        {
            return cleaned;
        }

        return beatmapId is > 0 ? $"id-{beatmapId}" : "";
    }
}
