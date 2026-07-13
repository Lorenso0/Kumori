using System.Net;
using Xunit;

namespace Kumori.App.Tests;

public sealed class KumoriUpdateServiceTests
{
    [Fact]
    public async Task CheckAsync_DerivesReleaseAndAssetsFromLatestPageRedirect()
    {
        using var http = new HttpClient(new RedirectResultHandler(
            "https://github.com/Lorenso0/Kumori/releases/tag/v1.3.0"));

        var result = await new KumoriUpdateService(http).CheckAsync(new Version(1, 2, 9));

        Assert.True(result.IsUpdateAvailable);
        Assert.Equal(new Version(1, 3, 0, 0), result.LatestVersion);
        Assert.Equal("v1.3.0", result.LatestTag);
        Assert.Equal("Kumori 1.3.0", result.LatestName);
        Assert.Equal("https://github.com/Lorenso0/Kumori/releases/tag/v1.3.0", result.ReleaseUrl);
        Assert.True(result.CanAutoInstall);
        Assert.Equal(
            "https://github.com/Lorenso0/Kumori/releases/download/v1.3.0/Kumori.exe",
            result.ExecutableAsset?.DownloadUrl);
        Assert.Equal(
            "https://github.com/Lorenso0/Kumori/releases/download/v1.3.0/Kumori.exe.sha256",
            result.ChecksumAsset?.DownloadUrl);
    }

    [Fact]
    public async Task CheckAsync_RejectsAResponseThatIsNotAVersionedReleaseRedirect()
    {
        using var http = new HttpClient(new RedirectResultHandler(KumoriUpdateService.ReleasesUrl));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => new KumoriUpdateService(http).CheckAsync(new Version(1, 2, 9)));

        Assert.Contains("versioned release tag", exception.Message);
    }

    [Theory]
    [InlineData("https://github.com/Lorenso0/Kumori/releases/tag/0.3.1", "0.3.1")]
    [InlineData("https://github.com/lorenso0/kumori/releases/tag/v1.0.0-beta.1", "v1.0.0-beta.1")]
    [InlineData("https://github.com/Lorenso0/Kumori/releases/latest", null)]
    [InlineData("https://example.com/Lorenso0/Kumori/releases/tag/1.0.0", null)]
    public void ParseReleaseTag_OnlyAcceptsKumoriVersionedReleasePages(string url, string? expected)
    {
        Assert.Equal(expected, KumoriUpdateService.ParseReleaseTag(new Uri(url)));
    }

    [Theory]
    [InlineData("v1.2.3", 1, 2, 3)]
    [InlineData("2.0.0-beta.1", 2, 0, 0)]
    [InlineData("release", -1, -1, -1)]
    public void ParseTagVersion_HandlesReleaseTagFormats(string tag, int major, int minor, int build)
    {
        var parsed = KumoriUpdateService.ParseTagVersion(tag);
        if (major < 0)
        {
            Assert.Null(parsed);
            return;
        }
        Assert.Equal(new Version(major, minor, build, 0), parsed);
    }

    private sealed class RedirectResultHandler(string finalUrl) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(KumoriUpdateService.LatestReleaseUrl, request.RequestUri?.AbsoluteUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, finalUrl),
                Content = new StringContent(string.Empty),
            });
        }
    }
}
