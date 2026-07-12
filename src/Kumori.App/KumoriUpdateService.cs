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
    DateTimeOffset? PublishedAt)
{
    public bool IsUpdateAvailable => LatestVersion is not null && LatestVersion > CurrentVersion;
}

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
        return new KumoriUpdateResult(currentVersion, tag, name, ParseTagVersion(tag), url, published);
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

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Kumori");
        return client;
    }
}
