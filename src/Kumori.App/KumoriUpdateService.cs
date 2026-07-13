using System.IO;
using System.Reflection;
using System.Net.Http;
using System.Text.Json;

namespace Kumori.App;

internal sealed record KumoriUpdateResult(
    Version CurrentVersion,
    string LatestTag,
    string LatestName,
    Version? LatestVersion,
    string ReleaseUrl,
    DateTimeOffset? PublishedAt,
    KumoriReleaseAsset? ExecutableAsset = null,
    KumoriReleaseAsset? ChecksumAsset = null)
{
    public bool IsUpdateAvailable => LatestVersion is not null && LatestVersion > CurrentVersion;

    public bool CanAutoInstall => ExecutableAsset is not null &&
                                  (ChecksumAsset is not null || !string.IsNullOrWhiteSpace(ExecutableAsset.Digest));
}

internal sealed record KumoriReleaseAsset(string Name, string DownloadUrl, long? Size, string? Digest);

internal sealed class KumoriUpdateService
{
    public const string ReleasesUrl = "https://github.com/Lorenso0/Kumori/releases";
    public const string LatestApiUrl = "https://api.github.com/repos/Lorenso0/Kumori/releases/latest";

    private static readonly HttpClient SharedHttp = CreateHttpClient();
    private readonly HttpClient http;

    public KumoriUpdateService(HttpClient? http = null)
    {
        this.http = http ?? SharedHttp;
    }

    public async Task<KumoriUpdateResult> CheckAsync(
        Version? currentVersion = null,
        CancellationToken cancellationToken = default)
    {
        currentVersion = Normalize(currentVersion ?? Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0, 0));
        using var response = await http.GetAsync(LatestApiUrl, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = doc.RootElement;
        var tag = root.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() ?? "unknown" : "unknown";
        var name = root.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? tag : tag;
        var url = root.TryGetProperty("html_url", out var urlElement) ? urlElement.GetString() ?? ReleasesUrl : ReleasesUrl;
        DateTimeOffset? published = root.TryGetProperty("published_at", out var publishedElement) &&
                                   publishedElement.TryGetDateTimeOffset(out var parsedPublished)
            ? parsedPublished
            : null;
        var assets = ParseAssets(root);
        return new KumoriUpdateResult(
            currentVersion,
            tag,
            name,
            ParseTagVersion(tag),
            url,
            published,
            assets.FirstOrDefault(asset => string.Equals(asset.Name, "Kumori.exe", StringComparison.OrdinalIgnoreCase)),
            assets.FirstOrDefault(asset => string.Equals(asset.Name, "Kumori.exe.sha256", StringComparison.OrdinalIgnoreCase)));
    }

    internal static Version? ParseTagVersion(string? tag)
    {
        var value = (tag ?? string.Empty).Trim();
        if (value.StartsWith('v') || value.StartsWith('V')) value = value[1..];
        var suffix = value.IndexOfAny(['-', '+']);
        if (suffix >= 0) value = value[..suffix];
        return Version.TryParse(value, out var parsed) ? Normalize(parsed) : null;
    }

    private static Version Normalize(Version version) => new(
        Math.Max(0, version.Major),
        Math.Max(0, version.Minor),
        Math.Max(0, version.Build),
        Math.Max(0, version.Revision));

    private static IReadOnlyList<KumoriReleaseAsset> ParseAssets(JsonElement release)
    {
        if (!release.TryGetProperty("assets", out var assetsElement) || assetsElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<KumoriReleaseAsset>();
        }

        var assets = new List<KumoriReleaseAsset>();
        foreach (var asset in assetsElement.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
            var downloadUrl = asset.TryGetProperty("browser_download_url", out var urlElement) ? urlElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(downloadUrl))
            {
                continue;
            }

            long? size = asset.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var parsedSize)
                ? parsedSize
                : null;
            var digest = asset.TryGetProperty("digest", out var digestElement) ? digestElement.GetString() : null;
            assets.Add(new KumoriReleaseAsset(name, downloadUrl, size, digest));
        }

        return assets;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Kumori");
        return client;
    }
}
