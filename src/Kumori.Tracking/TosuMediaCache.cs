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
    private const long max_mirror_metadata_bytes = 256L * 1024;
    private const int max_signed_url_hops = 3;
    private const int max_archive_entries = 10_000;
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

            // Realm is the index for lazer's authoritative content store. Read
            // those files in place; the replay viewer accepts a filename-to-path
            // map and no longer needs a linked compatibility directory.
            var directLazerMedia = ReadDirectLazerMedia(media, key);
            if (directLazerMedia is not null)
            {
                Log.Debug("Using osu!lazer media directly for beatmap {BeatmapId}", beatmapId);
                return directLazerMedia;
            }

            var existing = ReadCachedMedia(key);
            if (existing is not null)
                return existing;

            if (beatmapSource is null || !File.Exists(beatmapSource))
            {
                Log.Debug("tosu media local beatmap missing; trying mirrors for beatmap {BeatmapId}", beatmapId);
                return DownloadMedia(media, key, primaryMirror, fallbackMirrors);
            }

            var target = Path.Combine(AppPaths.BeatmapMediaDir, key);
            Directory.CreateDirectory(target);

            var parsed = ParseBeatmapMedia(beatmapSource, key, beatmapId, media.BeatmapSetId ?? 0)
                with
            { BeatmapFile = $"{(beatmapId > 0 ? beatmapId : "map")}.osu" };
            LinkOrCopyIntoCache(beatmapSource, Path.Combine(target, parsed.BeatmapFile), "local-beatmap");

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

                LinkOrCopyIntoCache(source, Path.Combine(target, safe), "local-beatmap-media");
                copied += size;
            }

            WriteManifest(
                target,
                parsed,
                "local-osu-installation",
                "This map was opened during tracked gameplay, so Kumori copied its local beatmap media for history and replay playback.");
            if (!string.IsNullOrWhiteSpace(parsed.AudioFile) &&
                !File.Exists(Path.Combine(target, parsed.AudioFile)))
            {
                Log.Debug("tosu media audio missing after local cache; trying osu!lazer store for beatmap {BeatmapId}", beatmapId);
                return DownloadMedia(media, key, primaryMirror, fallbackMirrors) ?? parsed;
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

    private static CachedMedia? ReadDirectLazerMedia(TosuMediaInfo media, string key)
    {
        var beatmapId = media.BeatmapId ?? 0;
        var setId = media.BeatmapSetId ?? 0;
        if (beatmapId <= 0 || setId <= 0)
        {
            return null;
        }

        var assets = LazerStorage.ResolveBeatmapAssets(beatmapId, setId, gameFolder: media.GameFolder);
        if (assets is null)
        {
            return null;
        }
        return ParseBeatmapMedia(assets.BeatmapPath, key, beatmapId, setId);
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

    internal static CachedMedia ParseBeatmapMedia(string path, string cacheKey, long beatmapId, long setId)
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

    private static void WriteManifest(string target, CachedMedia media, string source, string reason)
    {
        // The .osu file already contains hit-sample timing. Persist only the
        // lookup metadata needed to find linked files; serialising every event
        // made the metadata cache grow by tens of kilobytes per difficulty.
        var json = JsonSerializer.Serialize(new
        {
            cache_key = media.CacheKey,
            beatmap_id = media.BeatmapId,
            set_id = media.SetId,
            audio_lead_in = media.AudioLeadIn,
            audio_file = media.AudioFile,
            background_file = media.BackgroundFile,
            beatmap_file = media.BeatmapFile,
        });
        var path = Path.Combine(target, "manifest.json");
        var isNew = !File.Exists(path);
        File.WriteAllText(path, json);
        if (isNew)
        {
            CacheActivityLog.RecordAddition(
                path,
                source,
                reason: reason,
                beatmapId: media.BeatmapId > 0 ? media.BeatmapId : null,
                beatmapSetId: media.SetId > 0 ? media.SetId : null,
                cacheKey: media.CacheKey);
        }
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
            using var document = JsonDocument.Parse(File.ReadAllText(manifest));
            var root = document.RootElement;
            var media = new CachedMedia(
                root.TryGetProperty("cache_key", out var cacheKey) ? cacheKey.GetString() ?? key : key,
                root.TryGetProperty("beatmap_id", out var beatmapId) ? beatmapId.GetInt64() : 0,
                root.TryGetProperty("set_id", out var setId) ? setId.GetInt64() : 0,
                root.TryGetProperty("audio_lead_in", out var leadIn) ? leadIn.GetInt32() : 0,
                root.TryGetProperty("audio_file", out var audio) ? audio.GetString() ?? "" : "",
                root.TryGetProperty("background_file", out var background) ? background.GetString() ?? "" : "",
                root.TryGetProperty("beatmap_file", out var beatmap) ? beatmap.GetString() ?? "" : "",
                []);
            if (string.IsNullOrWhiteSpace(media.BeatmapFile) || !File.Exists(Path.Combine(target, media.BeatmapFile)))
            {
                return null;
            }
            return string.IsNullOrWhiteSpace(media.AudioFile) || File.Exists(Path.Combine(target, media.AudioFile))
                ? media
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or FormatException)
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
            if (archive.Entries.Count > max_archive_entries)
                throw new InvalidDataException("beatmap archive contains too many entries");
            var members = archive.Entries
                .Where(IsSafeMember)
                .ToArray();
            var osuEntry = members.FirstOrDefault(entry =>
            {
                if (!entry.FullName.EndsWith(".osu", StringComparison.OrdinalIgnoreCase)
                    || entry.Length > max_file_bytes)
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
            ExtractIntoCache(osuEntry, osuPath, $"mirror:{mirror}");
            var parsed = ParseBeatmapMedia(osuPath, key, beatmapId, setId)
                with
            { BeatmapFile = $"{beatmapId}.osu" };
            var wanted = parsed.SampleEvents.Select(e => e.Filename)
                .Append(parsed.AudioFile)
                .Append(parsed.BackgroundFile)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(SafeName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var byName = members
                .GroupBy(entry => SafeName(entry.FullName), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
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
                ExtractIntoCache(entry, Path.Combine(target, name), $"mirror:{mirror}");
                copied += entry.Length;
            }
            WriteManifest(
                target,
                parsed,
                $"mirror:{mirror}",
                "This tracked map was not available with complete local media, so Kumori downloaded the missing replay-viewer assets from the configured mirror.");
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
        var currentUrl = url;
        for (var hop = 0; hop <= max_signed_url_hops; hop++)
        {
            if (!Uri.TryCreate(currentUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            {
                throw new InvalidOperationException("beatmap mirror returned a non-HTTPS download URL");
            }

            using var response = Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
            response.EnsureSuccessStatusCode();
            var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
            if (contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
            {
                if (hop == max_signed_url_hops)
                    throw new InvalidOperationException("mirror returned too many signed URL indirections");
                if (response.Content.Headers.ContentLength is > max_mirror_metadata_bytes)
                    throw new InvalidDataException("mirror metadata exceeds size limit");

                using var metadata = response.Content.ReadAsStream();
                using var limited = new LimitedReadStream(metadata, max_mirror_metadata_bytes);
                using var doc = JsonDocument.Parse(limited);
                if (!doc.RootElement.TryGetProperty("url", out var signedUrlElement) ||
                    string.IsNullOrWhiteSpace(signedUrlElement.GetString()))
                {
                    throw new InvalidOperationException("mirror JSON did not include a download URL");
                }
                currentUrl = signedUrlElement.GetString()!;
                continue;
            }

            CopyArchiveResponse(response, destination);
            return;
        }
        throw new InvalidOperationException("mirror download could not be resolved");
    }

    private static void CopyArchiveResponse(HttpResponseMessage response, string destination)
    {
        if (response.Content.Headers.ContentLength is > max_archive_bytes)
            throw new InvalidDataException("beatmap archive exceeds size limit");
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

    private sealed class LimitedReadStream(Stream inner, long limit) : Stream
    {
        private long readTotal;
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, (int)Math.Min(count, limit - readTotal + 1));
            readTotal += read;
            if (readTotal > limit) throw new InvalidDataException("mirror metadata exceeds size limit");
            return read;
        }
        public override int Read(Span<byte> buffer)
        {
            var read = inner.Read(buffer[..(int)Math.Min(buffer.Length, limit - readTotal + 1)]);
            readTotal += read;
            if (readTotal > limit) throw new InvalidDataException("mirror metadata exceeds size limit");
            return read;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static void LinkOrCopyIntoCache(string source, string destination, string origin)
    {
        if (LazerMediaStore.TryLink(
                source,
                destination,
                $"{origin}-hardlink",
                $"{origin}-symlink",
                "Kumori referenced an existing local osu! file instead of storing a second copy."))
        {
            return;
        }

        var isNew = !File.Exists(destination);
        File.Copy(source, destination, overwrite: true);
        if (isNew)
        {
            CacheActivityLog.RecordAddition(
                destination,
                $"{origin}-copy",
                reason: "The local source could not be hard-linked or symlinked, so a fallback copy was required for replay playback.");
        }
    }

    private static void ExtractIntoCache(ZipArchiveEntry entry, string destination, string origin)
    {
        var isNew = !File.Exists(destination);
        entry.ExtractToFile(destination, overwrite: true);
        if (isNew) CacheActivityLog.RecordAddition(destination, origin);
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
