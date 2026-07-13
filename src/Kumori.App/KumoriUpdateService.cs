using System.IO;
using System.Net.Http;
using System.Reflection;

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
    public const string LatestReleaseUrl = "https://github.com/Lorenso0/Kumori/releases/latest";

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
        using var response = await http.GetAsync(
            LatestReleaseUrl,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var tag = ParseReleaseTag(response.RequestMessage?.RequestUri)
            ?? throw new InvalidDataException("GitHub did not redirect the latest release page to a versioned release tag.");
        var latestVersion = ParseTagVersion(tag);
        if (latestVersion is null)
            throw new InvalidDataException($"The latest Kumori release tag '{tag}' is not a supported version.");

        var escapedTag = Uri.EscapeDataString(tag);
        var releaseUrl = $"{ReleasesUrl}/tag/{escapedTag}";
        var downloadBase = $"{ReleasesUrl}/download/{escapedTag}";
        return new KumoriUpdateResult(
            currentVersion,
            tag,
            $"Kumori {latestVersion.ToString(3)}",
            latestVersion,
            releaseUrl,
            null,
            new KumoriReleaseAsset("Kumori.exe", $"{downloadBase}/Kumori.exe", null, null),
            new KumoriReleaseAsset("Kumori.exe.sha256", $"{downloadBase}/Kumori.exe.sha256", null, null));
    }

    internal static string? ParseReleaseTag(Uri? finalUri)
    {
        if (finalUri is null || !string.Equals(finalUri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
            return null;

        const string marker = "/Lorenso0/Kumori/releases/tag/";
        string path = finalUri.AbsolutePath;
        int markerIndex = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
            return null;

        string encodedTag = path[(markerIndex + marker.Length)..].Trim('/');
        return string.IsNullOrWhiteSpace(encodedTag) || encodedTag.Contains('/')
            ? null
            : Uri.UnescapeDataString(encodedTag);
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
