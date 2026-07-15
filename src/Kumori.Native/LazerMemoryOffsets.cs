using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Kumori.Core;
using Kumori.Tracking;

namespace Kumori.Native;

internal sealed record LazerMemoryOffsets(
    string OsuVersion,
    long GameBaseVtable,
    int OsuGameScreenStack,
    int ScreenStackStack,
    int PlayerScore,
    int ExternalLinkOpenerApi,
    int ApiAccessGame,
    int PlayerDrawableRuleset,
    int DrawableRulesetReplayScore)
{
    private const string OfficialOffsetsUrl =
        "https://raw.githubusercontent.com/tosuapp/tosu/master/packages/tosu/src/assets/offsets.json";
    private static readonly HttpClient OfficialOffsetsClient = CreateOfficialOffsetsClient();

    public static LazerMemoryOffsets Load(string? path, bool refreshOfficialCache = false)
    {
        path ??= EnsureDefaultOffsetsPath(refreshOfficialCache);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("osu!lazer offsets.json was not found.", path);
        }

        return Parse(File.ReadAllText(path));
    }

    public static LazerMemoryOffsets? LoadCached(string? path)
    {
        path ??= Path.Combine(AppPaths.CacheDir, "tosu", "offsets.json");
        if (!File.Exists(path))
            return null;
        try { return Parse(File.ReadAllText(path)); }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException)
        {
            return null;
        }
    }

    public static async Task<LazerMemoryOffsets> LoadAsync(
        string? path,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            if (!File.Exists(path))
                throw new FileNotFoundException("osu!lazer offsets.json was not found.", path);
            var explicitJson = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return Parse(explicitJson);
        }

        path = Path.Combine(AppPaths.CacheDir, "tosu", "offsets.json");
        if (File.Exists(path))
        {
            try
            {
                var cachedJson = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return Parse(cachedJson);
            }
            catch (Exception ex) when (
                ex is IOException or JsonException or InvalidDataException)
            {
                // Replace an unreadable cache only after a fresh response has
                // been downloaded and validated below.
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = await DownloadOfficialJsonAsync(cancellationToken).ConfigureAwait(false);
        var offsets = Parse(json);
        cancellationToken.ThrowIfCancellationRequested();

        var isNew = !File.Exists(path);
        await ReplaceCacheAsync(path, json, cancellationToken).ConfigureAwait(false);
        if (isNew)
            CacheActivityLog.RecordAddition(path, "tosu-memory-offsets");
        return offsets;
    }

    internal static async Task<LazerMemoryOffsetRefreshResult> RefreshCachedAsync(
        LazerMemoryOffsets current,
        string? path = null,
        Func<CancellationToken, Task<string>>? downloadJson = null,
        CancellationToken cancellationToken = default)
    {
        path ??= Path.Combine(AppPaths.CacheDir, "tosu", "offsets.json");
        var json = await (downloadJson ?? DownloadOfficialJsonAsync)(cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        // Parse and validate the complete document before touching the existing
        // cache. This guarantees a truncated or incompatible upstream response
        // cannot replace the last-known-good offsets.
        var candidate = Parse(json);
        if (!LazerMemoryOffsetRefreshPolicy.ShouldReplace(current, candidate))
            return new LazerMemoryOffsetRefreshResult(current, Updated: false);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await ReplaceCacheAsync(path, json, cancellationToken).ConfigureAwait(false);
        return new LazerMemoryOffsetRefreshResult(candidate, Updated: true);
    }

    private static LazerMemoryOffsets Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return new LazerMemoryOffsets(
            root.TryGetProperty("OsuVersion", out var version) ? version.GetString() ?? "unknown" : "unknown",
            GetInt64(root, "GameBaseVtable"),
            GetOffset(root, "osu.Game.OsuGame", "<ScreenStack>k__BackingField"),
            GetOffset(root, "osu.Framework.Screens.ScreenStack", "stack"),
            GetOffset(root, "osu.Game.Screens.Play.Player", "<Score>k__BackingField"),
            GetOffset(root, "osu.Game.Online.Chat.ExternalLinkOpener", "<api>k__BackingField"),
            GetOffset(root, "osu.Game.Online.API.APIAccess", "game"),
            GetOptionalOffset(root, "osu.Game.Screens.Play.Player", "<DrawableRuleset>k__BackingField"),
            GetOptionalOffset(root, "osu.Game.Rulesets.UI.DrawableRuleset", "<ReplayScore>k__BackingField"));
    }

    private static string EnsureDefaultOffsetsPath(bool refreshOfficialCache)
    {
        var path = Path.Combine(AppPaths.CacheDir, "tosu", "offsets.json");
        if (File.Exists(path) && !refreshOfficialCache)
            return path;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            var isNew = !File.Exists(path);
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Kumori");
            var json = http.GetStringAsync(OfficialOffsetsUrl).GetAwaiter().GetResult();
            _ = Parse(json); // validate before replacing the last known-good cache.

            var temp = path + ".new";
            File.WriteAllText(temp, json);
            File.Move(temp, path, overwrite: true);
            if (isNew) CacheActivityLog.RecordAddition(path, "tosu-memory-offsets");
        }
        catch when (File.Exists(path))
        {
            // A previous valid cache is safer than disabling replay capture
            // when the upstream response is unavailable or malformed.
        }

        return path;
    }

    private static int GetOffset(JsonElement root, string type, string field)
    {
        if (!root.TryGetProperty(type, out var typeElement) ||
            !typeElement.TryGetProperty(field, out var fieldElement) ||
            !fieldElement.TryGetInt32(out var offset))
        {
            throw new InvalidDataException($"Missing offset {type}.{field}.");
        }

        return offset;
    }

    private static int GetOptionalOffset(JsonElement root, string type, string field)
        => root.TryGetProperty(type, out var typeElement)
           && typeElement.TryGetProperty(field, out var fieldElement)
           && fieldElement.TryGetInt32(out var offset)
            ? offset
            : -1;

    private static long GetInt64(JsonElement root, string field)
    {
        if (!root.TryGetProperty(field, out var element) || !element.TryGetInt64(out var value))
        {
            throw new InvalidDataException($"Missing offset {field}.");
        }

        return value;
    }

    private static HttpClient CreateOfficialOffsetsClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Kumori");
        return client;
    }

    private static Task<string> DownloadOfficialJsonAsync(CancellationToken cancellationToken) =>
        OfficialOffsetsClient.GetStringAsync(OfficialOffsetsUrl, cancellationToken);

    private static async Task ReplaceCacheAsync(
        string path,
        string json,
        CancellationToken cancellationToken)
    {
        var temp = path + $".new-{Guid.NewGuid():N}";
        try
        {
            await File.WriteAllTextAsync(temp, json, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temp); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}

internal readonly record struct LazerMemoryOffsetRefreshResult(
    LazerMemoryOffsets Offsets,
    bool Updated);

internal static class LazerMemoryOffsetRefreshPolicy
{
    internal static bool ShouldReplace(LazerMemoryOffsets current, LazerMemoryOffsets candidate)
    {
        if (candidate == current)
            return false;
        if (string.Equals(candidate.OsuVersion, current.OsuVersion, StringComparison.OrdinalIgnoreCase))
        {
            // Allow tosu to correct offsets for a release without changing its
            // version label.
            return true;
        }
        if (string.Equals(candidate.OsuVersion, "unknown", StringComparison.OrdinalIgnoreCase))
            return false;
        if (string.Equals(current.OsuVersion, "unknown", StringComparison.OrdinalIgnoreCase))
            return true;

        return Version.TryParse(candidate.OsuVersion, out var candidateVersion)
               && Version.TryParse(current.OsuVersion, out var currentVersion)
               && candidateVersion > currentVersion;
    }
}
