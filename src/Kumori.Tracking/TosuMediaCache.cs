using System.Globalization;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using Kumori.Core;
using Kumori.Core.Models;
using Serilog;

namespace Kumori.Tracking;

internal static class TosuMediaCache
{
    private const long max_file_bytes = 80L * 1024 * 1024;
    private const long max_cache_bytes = 300L * 1024 * 1024;
    private const long max_archive_bytes = 250L * 1024 * 1024;
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(90),
    };

    static TosuMediaCache()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("Kumori");
    }

    public static CachedMedia? Cache(
        TosuMediaInfo media,
        string primaryMirror = "https://api.rai.moe",
        IReadOnlyList<string>? fallbackMirrors = null)
    {
        try
        {
            var beatmapId = media.BeatmapId ?? 0;
            var beatmapSource = ResolvePath(media.BeatmapFile, media.SongsFolder, media.GameFolder);

            var key = MediaCacheKey(media.Checksum, beatmapId);
            if (string.IsNullOrWhiteSpace(key))
            {
                Log.Debug("tosu media cache skipped: no checksum or beatmap id");
                return null;
            }

            var lazer = LazerStorage.ResolveBeatmapAssets(media.BeatmapId, media.BeatmapSetId);
            if (lazer is not null)
            {
                return ParseBeatmapMedia(lazer.BeatmapPath, key, beatmapId, media.BeatmapSetId ?? 0);
            }

            var existing = ReadCachedMedia(key);
            if (existing is not null)
            {
                return existing;
            }

            if (beatmapSource is null || !File.Exists(beatmapSource))
            {
                Log.Debug("tosu media local beatmap missing; trying mirrors for beatmap {BeatmapId}", beatmapId);
                return CacheFromLazer(media, key)
                    ?? DownloadMedia(media, key, primaryMirror, fallbackMirrors);
            }

            var target = Path.Combine(AppPaths.BeatmapMediaDir, key);
            Directory.CreateDirectory(target);

            var parsed = ParseBeatmapMedia(beatmapSource, key, beatmapId, media.BeatmapSetId ?? 0)
                with { BeatmapFile = $"{(beatmapId > 0 ? beatmapId : "map")}.osu" };
            File.Copy(beatmapSource, Path.Combine(target, parsed.BeatmapFile), overwrite: true);

            var copied = new FileInfo(Path.Combine(target, parsed.BeatmapFile)).Length;
            var names = parsed.SampleEvents.Select(e => e.Filename)
                .Append(parsed.AudioFile)
                .Append(parsed.BackgroundFile)
                .Append("combobreak.wav")
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var directSources = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [parsed.AudioFile] = ResolvePath(media.AudioFile, media.SongsFolder, media.GameFolder),
                [parsed.BackgroundFile] = ResolvePath(media.BackgroundFile, media.SongsFolder, media.GameFolder),
            };
            var beatmapFolder = ResolvePath(media.BeatmapFolder, media.SongsFolder, media.GameFolder);
            var skinFolder = ResolvePath(media.SkinFolder, media.SongsFolder, media.GameFolder);

            foreach (var name in names)
            {
                var safe = SafeName(name);
                var source = CandidateSources(directSources.GetValueOrDefault(name), beatmapFolder, skinFolder, safe)
                    .FirstOrDefault(File.Exists);
                if (source is null)
                {
                    continue;
                }

                var size = new FileInfo(source).Length;
                if (size > max_file_bytes || copied + size > max_cache_bytes)
                {
                    Log.Debug("tosu media cache skipped large file {File} ({Bytes} bytes)", source, size);
                    continue;
                }

                File.Copy(source, Path.Combine(target, safe), overwrite: true);
                copied += size;
            }

            WriteManifest(target, parsed);
            if (!string.IsNullOrWhiteSpace(parsed.AudioFile) &&
                !File.Exists(Path.Combine(target, parsed.AudioFile)))
            {
                Log.Debug("tosu media audio missing after local cache; trying osu!lazer store for beatmap {BeatmapId}", beatmapId);
                return CacheFromLazer(media, key) ?? DownloadMedia(media, key, primaryMirror, fallbackMirrors) ?? parsed;
            }
            Log.Debug("tosu media cached for beatmap {BeatmapId} at {Path}", beatmapId, target);
            return parsed;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "tosu media cache failed");
            return null;
        }
    }

    /// <summary>
    /// Rebuilds cache entries from the pre-link cache when the same beatmaps exist in osu!lazer.
    /// Old entries remain untouched as a fallback for maps no longer installed in lazer.
    /// </summary>
    public static int MigrateOldLazerCache()
    {
        if (!Directory.Exists(AppPaths.OldBeatmapMediaDir))
        {
            return 0;
        }

        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        var migrated = 0;
        foreach (var manifest in Directory.EnumerateFiles(AppPaths.OldBeatmapMediaDir, "manifest.json", SearchOption.AllDirectories).ToArray())
        {
            try
            {
                var key = Path.GetFileName(Path.GetDirectoryName(manifest)!);
                if (File.Exists(Path.Combine(AppPaths.BeatmapMediaDir, key, "manifest.json")))
                {
                    continue;
                }

                var old = JsonSerializer.Deserialize<CachedMedia>(File.ReadAllText(manifest), options);
                if (old is null || old.BeatmapId <= 0 || old.SetId <= 0)
                {
                    continue;
                }

                var linked = CacheFromLazer(new TosuMediaInfo
                {
                    BeatmapId = old.BeatmapId,
                    BeatmapSetId = old.SetId,
                }, key);
                if (linked is not null)
                {
                    migrated++;
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                Log.Debug(ex, "Could not migrate old beatmap cache manifest {Manifest}", manifest);
            }
        }

        Log.Information("Migrated {Count} cached beatmaps to osu!lazer links", migrated);
        return migrated;
    }

    internal static bool NeedsRecovery(AttemptSummary attempt)
    {
        if (LazerStorage.ResolveBeatmapAssets(attempt.OsuBeatmapId, attempt.BeatmapSetId, attempt.Difficulty) is not null)
        {
            return false;
        }
        var key = MediaCacheKey(attempt.Checksum, attempt.OsuBeatmapId);
        return string.IsNullOrWhiteSpace(key) || ReadCachedMedia(key) is null;
    }

    private static IEnumerable<string> CandidateSources(
        string? direct,
        string? beatmapFolder,
        string? skinFolder,
        string safeName)
    {
        if (!string.IsNullOrWhiteSpace(direct))
        {
            yield return direct;
        }
        if (!string.IsNullOrWhiteSpace(beatmapFolder))
        {
            yield return Path.Combine(beatmapFolder, safeName);
        }
        if (!string.IsNullOrWhiteSpace(skinFolder))
        {
            yield return Path.Combine(skinFolder, safeName);
        }
    }

    private static CachedMedia? CacheFromLazer(TosuMediaInfo media, string key)
    {
        var beatmapId = media.BeatmapId ?? 0;
        var setId = media.BeatmapSetId ?? 0;
        if (beatmapId <= 0 || setId <= 0)
        {
            return null;
        }

        var files = LazerMediaStore.ResolveFiles(media);
        if (files is null)
        {
            return null;
        }

        var osuSource = files
            .Where(pair => pair.Key.EndsWith(".osu", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault(pair => LazerMediaStore.IsBeatmapId(pair.Value, beatmapId));
        if (string.IsNullOrWhiteSpace(osuSource.Value))
        {
            return null;
        }

        var target = Path.Combine(AppPaths.BeatmapMediaDir, key);
        var osuName = $"{beatmapId}.osu";
        var osuTarget = Path.Combine(target, osuName);
        if (!LazerMediaStore.TryLink(osuSource.Value, osuTarget))
        {
            return null;
        }

        var parsed = ParseBeatmapMedia(osuTarget, key, beatmapId, setId) with { BeatmapFile = osuName };
        var wanted = parsed.SampleEvents.Select(e => e.Filename)
            .Append(parsed.AudioFile)
            .Append(parsed.BackgroundFile)
            .Append("combobreak.wav")
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(SafeName)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var name in wanted)
        {
            if (files.TryGetValue(name, out var source))
            {
                LazerMediaStore.TryLink(source, Path.Combine(target, name));
            }
        }

        if (!string.IsNullOrWhiteSpace(parsed.AudioFile) && !File.Exists(Path.Combine(target, parsed.AudioFile)))
        {
            return null;
        }

        WriteManifest(target, parsed);
        Log.Debug("tosu media hard-linked from osu!lazer for beatmap {BeatmapId} at {Path}", beatmapId, target);
        return parsed;
    }

    private static string? ResolvePath(string? path, string? songsFolder, string? gameFolder)
    {
        if (string.IsNullOrWhiteSpace(path) || path == ".")
        {
            return null;
        }

        var normalized = path.Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalized))
        {
            return normalized;
        }

        foreach (var root in new[] { songsFolder, gameFolder })
        {
            if (!string.IsNullOrWhiteSpace(root))
            {
                var candidate = Path.Combine(root, normalized);
                if (File.Exists(candidate) || Directory.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return normalized;
    }

    private static CachedMedia ParseBeatmapMedia(string path, string cacheKey, long beatmapId, long setId)
    {
        var section = "";
        var audio = "";
        var background = "";
        var leadIn = 0;
        var defaultSample = "normal";
        var timing = new List<(int Time, string SampleSet, double Volume, int SampleIndex)>();
        var events = new List<SampleEvent>();

        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                section = line;
                continue;
            }
            if (line.Length == 0 || line.StartsWith("//"))
            {
                continue;
            }

            if (section == "[General]")
            {
                if (line.StartsWith("AudioFilename:", StringComparison.OrdinalIgnoreCase))
                {
                    audio = SafeName(line.Split(':', 2)[1].Trim());
                }
                else if (line.StartsWith("AudioLeadIn:", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(line.Split(':', 2)[1].Trim(), out var parsedLeadIn))
                {
                    leadIn = parsedLeadIn;
                }
                else if (line.StartsWith("SampleSet:", StringComparison.OrdinalIgnoreCase))
                {
                    defaultSample = line.Split(':', 2)[1].Trim().ToLowerInvariant();
                }
            }
            else if (section == "[Events]" && (line.StartsWith("0,") || line.StartsWith("Background,", StringComparison.OrdinalIgnoreCase)))
            {
                var parts = line.Split(',');
                if (parts.Length >= 3)
                {
                    background = SafeName(parts[2].Trim().Trim('"'));
                }
            }
            else if (section == "[TimingPoints]")
            {
                var parts = line.Split(',');
                if (parts.Length >= 6
                    && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var time)
                    && int.TryParse(parts[4], out var sampleIndex)
                    && int.TryParse(parts[5], out var volume))
                {
                    timing.Add(((int)time, parts[3] switch
                    {
                        "2" => "soft",
                        "3" => "drum",
                        _ => defaultSample,
                    }, Math.Clamp(volume / 100.0, 0, 1), sampleIndex));
                }
            }
            else if (section == "[HitObjects]")
            {
                var parts = line.Split(',');
                if (parts.Length < 5
                    || !int.TryParse(parts[2], out var time)
                    || !int.TryParse(parts[4], out var soundBits))
                {
                    continue;
                }

                var sampleSet = defaultSample;
                var volume = 1.0;
                var sampleIndex = 0;
                foreach (var point in timing)
                {
                    if (point.Time > time) break;
                    sampleSet = point.SampleSet;
                    volume = point.Volume;
                    sampleIndex = point.SampleIndex;
                }

                var custom = "";
                var tail = parts[^1].Split(':');
                if (tail.Length >= 5)
                {
                    custom = SafeName(tail[4]);
                    if (int.TryParse(tail[3], out var customVolume) && customVolume > 0)
                    {
                        volume = customVolume / 100.0;
                    }
                }

                foreach (var sound in HitSounds(soundBits))
                {
                    var suffix = sampleIndex > 1 ? sampleIndex.ToString() : "";
                    events.Add(new SampleEvent(time, sound, volume, custom.Length > 0 ? custom : $"{sampleSet}-hit{sound}{suffix}.wav"));
                }
            }
        }

        return new CachedMedia(cacheKey, beatmapId, setId, leadIn, audio, background, $"{beatmapId}.osu", events);
    }

    private static IEnumerable<string> HitSounds(int bits)
    {
        yield return "normal";
        if ((bits & 2) != 0) yield return "whistle";
        if ((bits & 4) != 0) yield return "finish";
        if ((bits & 8) != 0) yield return "clap";
    }

    private static void WriteManifest(string target, CachedMedia media)
    {
        var json = JsonSerializer.Serialize(media, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        });
        File.WriteAllText(Path.Combine(target, "manifest.json"), json);
    }

    private static CachedMedia? ReadCachedMedia(string key)
    {
        var target = Path.Combine(AppPaths.BeatmapMediaDir, key);
        var manifest = Path.Combine(target, "manifest.json");
        if (!File.Exists(manifest))
        {
            return null;
        }

        try
        {
            var media = JsonSerializer.Deserialize<CachedMedia>(File.ReadAllText(manifest), new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            });
            if (media is null || !File.Exists(Path.Combine(target, media.BeatmapFile)))
            {
                return null;
            }
            return string.IsNullOrWhiteSpace(media.AudioFile) || File.Exists(Path.Combine(target, media.AudioFile))
                ? media
                : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string SafeName(string value)
        => Path.GetFileName(value.Replace('\\', '/'));

    private static string MediaCacheKey(string? checksum, long? beatmapId)
    {
        var cleaned = new string((checksum ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).Take(64).ToArray());
        return cleaned.Length > 0 ? cleaned : beatmapId is > 0 ? $"id-{beatmapId}" : "";
    }

    private static CachedMedia? DownloadMedia(
        TosuMediaInfo media,
        string key,
        string primaryMirror,
        IReadOnlyList<string>? fallbackMirrors)
    {
        var beatmapId = media.BeatmapId ?? 0;
        var setId = media.BeatmapSetId ?? 0;
        if (beatmapId <= 0 || setId <= 0)
        {
            return null;
        }

        var errors = new List<string>();
        foreach (var mirror in MirrorSequence(primaryMirror, fallbackMirrors))
        {
            try
            {
                return DownloadFromMirror(beatmapId, setId, media.Checksum ?? "", key, mirror);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or HttpRequestException or JsonException or InvalidOperationException)
            {
                errors.Add($"{mirror}: {ex.Message}");
            }
        }
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(string.Join("; ", errors));
        }
        return null;
    }

    private static CachedMedia? DownloadFromMirror(
        long beatmapId,
        long setId,
        string checksum,
        string key,
        string mirror)
    {
        var temp = Path.Combine(Path.GetTempPath(), $"kumori-osz-{Guid.NewGuid():N}.osz");
        try
        {
            DownloadArchive(MirrorDownloadUrl(mirror, setId), temp);
            using var archive = ZipFile.OpenRead(temp);
            var members = archive.Entries
                .Where(IsSafeMember)
                .ToArray();
            var osuEntry = members.FirstOrDefault(entry =>
            {
                if (!entry.FullName.EndsWith(".osu", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
                using var stream = entry.Open();
                using var reader = new StreamReader(stream);
                var text = reader.ReadToEnd().Replace(" ", "", StringComparison.Ordinal);
                return text.Contains($"BeatmapID:{beatmapId}", StringComparison.OrdinalIgnoreCase);
            });
            if (osuEntry is null)
            {
                return null;
            }

            var target = Path.Combine(AppPaths.BeatmapMediaDir, key);
            Directory.CreateDirectory(target);
            var osuPath = Path.Combine(target, $"{beatmapId}.osu");
            osuEntry.ExtractToFile(osuPath, overwrite: true);
            var parsed = ParseBeatmapMedia(osuPath, key, beatmapId, setId)
                with { BeatmapFile = $"{beatmapId}.osu" };
            var wanted = parsed.SampleEvents.Select(e => e.Filename)
                .Append(parsed.AudioFile)
                .Append(parsed.BackgroundFile)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(SafeName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var byName = members.ToDictionary(
                entry => SafeName(entry.FullName),
                entry => entry,
                StringComparer.OrdinalIgnoreCase);
            var copied = new FileInfo(osuPath).Length;
            foreach (var name in wanted)
            {
                if (!byName.TryGetValue(name, out var entry))
                {
                    continue;
                }
                if (entry.Length > max_file_bytes || copied + entry.Length > max_cache_bytes)
                {
                    continue;
                }
                entry.ExtractToFile(Path.Combine(target, name), overwrite: true);
                copied += entry.Length;
            }
            WriteManifest(target, parsed);
            Log.Debug("tosu media downloaded for beatmap {BeatmapId} from {Mirror}", beatmapId, mirror);
            return parsed;
        }
        finally
        {
            try { File.Delete(temp); } catch { }
        }
    }

    private static void DownloadArchive(string url, string destination)
    {
        using var response = Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
        if (contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            using var doc = JsonDocument.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            if (!doc.RootElement.TryGetProperty("url", out var signedUrlElement))
            {
                throw new InvalidOperationException("mirror JSON did not include a download URL");
            }
            var signedUrl = signedUrlElement.GetString();
            if (string.IsNullOrWhiteSpace(signedUrl))
            {
                throw new InvalidOperationException("mirror JSON included an empty download URL");
            }
            DownloadArchive(signedUrl, destination);
            return;
        }

        using var input = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
        using var output = File.Create(destination);
        var buffer = new byte[1024 * 1024];
        long total = 0;
        while (true)
        {
            var read = input.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }
            total += read;
            if (total > max_archive_bytes)
            {
                throw new InvalidOperationException("beatmap archive exceeds size limit");
            }
            output.Write(buffer, 0, read);
        }
    }

    private static string MirrorDownloadUrl(string mirrorBase, long setId)
    {
        var baseUrl = mirrorBase.Trim().TrimEnd('/');
        if (baseUrl.Contains("catboy.best", StringComparison.OrdinalIgnoreCase))
        {
            return $"{baseUrl}/d/{setId}";
        }
        return $"{baseUrl}/beatmaps/{setId}/download";
    }

    private static IEnumerable<string> MirrorSequence(string primary, IReadOnlyList<string>? fallbacks)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in new[] { primary }
                     .Concat(fallbacks ?? Array.Empty<string>())
                     .Concat(new[] { "https://catboy.best" }))
        {
            foreach (var item in (value ?? "").Replace('\n', ',').Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var mirror = item.TrimEnd('/');
                if (seen.Add(mirror))
                {
                    yield return mirror;
                }
            }
        }
    }

    private static bool IsSafeMember(ZipArchiveEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Name) || entry.Length > max_file_bytes)
        {
            return false;
        }
        var normalized = entry.FullName.Replace('\\', '/');
        return !normalized.StartsWith("/", StringComparison.Ordinal) &&
               !normalized.Split('/').Contains("..");
    }

    internal sealed record CachedMedia(
        string CacheKey,
        long BeatmapId,
        long SetId,
        int AudioLeadIn,
        string AudioFile,
        string BackgroundFile,
        string BeatmapFile,
        IReadOnlyList<SampleEvent> SampleEvents);

    internal sealed record SampleEvent(int TimeMs, string Kind, double Volume, string Filename);
}
