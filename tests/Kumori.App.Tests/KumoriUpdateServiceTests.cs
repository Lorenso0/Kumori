using System.Net;
using System.Text;
using Xunit;

namespace Kumori.App.Tests;

public sealed class KumoriUpdateServiceTests
{
    [Fact]
    public async Task CheckAsync_DetectsNewerGithubReleaseAndUsesItsPage()
    {
        const string json =
            """
            {
              "tag_name": "v1.3.0",
              "name": "Kumori 1.3.0",
              "html_url": "https://github.com/Lorenso0/Kumori/releases/tag/v1.3.0",
              "published_at": "2026-07-12T18:00:00Z"
            }
            """;
        using var http = new HttpClient(new StubHandler(json));

        var result = await new KumoriUpdateService(http).CheckAsync(new Version(1, 2, 9));

        Assert.True(result.IsUpdateAvailable);
        Assert.Equal(new Version(1, 3, 0, 0), result.LatestVersion);
        Assert.Equal("https://github.com/Lorenso0/Kumori/releases/tag/v1.3.0", result.ReleaseUrl);
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

    private sealed class StubHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(KumoriUpdateService.LatestApiUrl, request.RequestUri?.AbsoluteUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }
}
