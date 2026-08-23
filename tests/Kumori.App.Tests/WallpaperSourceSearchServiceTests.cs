using System.Net;
using System.Text;
using Xunit;

namespace Kumori.App.Tests;

public sealed class WallpaperSourceSearchServiceTests
{
    [Fact]
    public async Task LocalRealmArtworkIsSearchedInAppAndSortedByResolution()
    {
        string directory = Directory.CreateTempSubdirectory("kumori-wallpaper-search-").FullName;
        string artwork = Path.Combine(directory, "wallpaper.png");
        await File.WriteAllBytesAsync(artwork, [1, 2, 3, 4]);
        var handler = new SearchHandler();

        try
        {
            using var http = new HttpClient(handler);
            var service = new WallpaperSourceSearchService(http);

            var response = await service.SearchAsync(artwork);

            Assert.Equal(3, handler.CallCount);
            Assert.Contains("name=file", handler.UploadBody, StringComparison.Ordinal);
            Assert.Contains("filename=wallpaper.png", handler.UploadBody, StringComparison.Ordinal);
            Assert.Equal(2, response.Results.Count);
            Assert.Equal("Four K", response.Results[0].Title);
            Assert.Equal(3840, response.Results[0].Width);
            Assert.Equal(2160, response.Results[0].Height);
            Assert.Equal("Full HD", response.Results[1].Title);
            Assert.Empty(response.Warnings);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SauceNaoResultsIncludeAttributionAndSimilarity()
    {
        const string html = """
            <div class="result"><table><tr>
              <td><div class="resultimage"><img data-src="https://img.saucenao.com/result.jpg"></div></td>
              <td><div class="resultsimilarityinfo">92.45%</div>
                  <div class="resulttitle"><strong>Original Artwork</strong></div>
                  <a href="https://www.pixiv.net/artworks/123">Pixiv</a></td>
            </tr></table></div>
            """;

        var result = Assert.Single(WallpaperSourceSearchService.ParseSauceNaoResults(html));

        Assert.Equal("Original Artwork", result.Title);
        Assert.Equal(92.45, result.Similarity, precision: 2);
        Assert.Equal("https://www.pixiv.net/artworks/123", result.SourceUri?.AbsoluteUri);
        Assert.Equal("https://img.saucenao.com/result.jpg", result.ImageUri.AbsoluteUri);
    }

    [Fact]
    public void YandexSitesExposeOriginalResolutionAndThumbnail()
    {
        const string html = """
            before "cbirSites":{"sites":[
              {"title":"Large","url":"https://source.example/post","domain":"source.example",
               "thumb":{"url":"//thumb.example/image.jpg","height":90,"width":160},
               "originalImage":{"url":"https://source.example/image.jpg","height":1440,"width":2560}}
            ]} after
            """;

        var result = Assert.Single(WallpaperSourceSearchService.ParseYandexSiteResults(html));

        Assert.Equal(2560, result.Width);
        Assert.Equal(1440, result.Height);
        Assert.Equal("https://thumb.example/image.jpg", result.ThumbnailUri?.AbsoluteUri);
        Assert.Equal("https://source.example/post", result.SourceUri?.AbsoluteUri);
    }

    [Fact]
    public void UploadedArtworkUriIsRestrictedToSauceNaoUserdata()
    {
        Assert.Equal(
            "https://saucenao.com/userdata/example.png.jpg",
            WallpaperSourceSearchService.ExtractUploadedArtworkUri(
                "<img src=\"/userdata/example.png.jpg\">")?.AbsoluteUri);
        Assert.Null(WallpaperSourceSearchService.ExtractUploadedArtworkUri(
            "<img src=\"https://example.com/not-the-query.jpg\">"));
    }

    [Fact]
    public async Task MissingArtworkReportsThatNoWallpaperIsAvailable()
    {
        var service = new WallpaperSourceSearchService();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SearchAsync("missing-wallpaper.jpg"));

        Assert.Equal("No wallpaper is available for this map yet.", exception.Message);
    }

    [Fact]
    public void ResultsWindowReadOnlyBindingsAreExplicitlyOneWay()
    {
        string xaml = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "Kumori.App",
            "WallpaperSourceResultsWindow.xaml"));

        Assert.DoesNotContain("<Run Text=\"{Binding", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding SummaryText, Mode=OneWay}\"", xaml, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "Kumori.sln")))
            {
                return current;
            }
            current = Directory.GetParent(current)?.FullName ?? string.Empty;
        }

        throw new DirectoryNotFoundException("Could not locate Kumori.sln.");
    }

    private sealed class SearchHandler : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        public string UploadBody { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            if (request.Method == HttpMethod.Post)
            {
                using var buffer = new MemoryStream();
                await request.Content!.CopyToAsync(buffer, cancellationToken);
                UploadBody = Encoding.Latin1.GetString(buffer.ToArray());
                return Html("<html><img src=\"/userdata/query.png\"></html>");
            }

            if (request.RequestUri!.Query.Contains("cbir_page=sites", StringComparison.Ordinal))
            {
                return Html("""
                    <script>state={"cbirSites":{"sites":[
                      {"title":"Full HD","url":"https://one.example/post","domain":"one.example",
                       "thumb":{"url":"//thumb.example/one.jpg","height":90,"width":160},
                       "originalImage":{"url":"https://one.example/wallpaper.jpg","height":1080,"width":1920}},
                      {"title":"Four K","url":"https://two.example/post","domain":"two.example",
                       "thumb":{"url":"//thumb.example/two.jpg","height":90,"width":160},
                       "originalImage":{"url":"https://two.example/wallpaper.png","height":2160,"width":3840}}
                    ]}};</script>
                    """);
            }

            return Html("""
                <script>state={"cbirId":"123/query-id","originalImageUrl":"https://avatars.example/query/orig"};</script>
                """);
        }

        private static HttpResponseMessage Html(string value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(value, Encoding.UTF8, "text/html"),
        };
    }
}
