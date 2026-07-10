using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using Kumori.Core;
using Kumori.Core.Models;
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
                local = ResolveLocal(key);
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
            var legacy = Path.Combine(AppPaths.LegacyBeatmapFilesDir, $"{summary.OsuBeatmapId}.osu");
            if (File.Exists(legacy))
            {
                return legacy;
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

        var directory = Path.Combine(AppPaths.BeatmapMediaDir, key);
        return Directory.Exists(directory) ? directory : null;
    }

    private static string? ResolveLocal(string cacheKey)
        => ResolveManifestFile(cacheKey, "background_file");

    private static string? ResolveManifestFile(string cacheKey, string propertyName)
    {
        var manifestPath = Path.Combine(AppPaths.BeatmapMediaDir, cacheKey, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            return null;
        }

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
