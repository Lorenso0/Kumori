using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;

namespace Kumori.App;

internal sealed record WallpaperSearchResult(
    string Provider,
    string Title,
    Uri ImageUri,
    Uri? SourceUri,
    int Width,
    int Height,
    double Similarity,
    string Domain,
    Uri? ThumbnailUri = null)
{
    public long PixelCount => (long)Width * Height;
    public string ResolutionText => Width > 0 && Height > 0
        ? $"{Width:N0} × {Height:N0}"
        : "Resolution unavailable";
    public string MatchText => Similarity > 0
        ? $"{Similarity:0.##}% match"
        : Provider;
}

internal sealed record WallpaperSearchResponse(
    IReadOnlyList<WallpaperSearchResult> Results,
    IReadOnlyList<string> Warnings);

internal sealed record WallpaperImageData(
    byte[] Bytes,
    string? ContentType,
    int Width,
    int Height);

internal sealed class WallpaperSourceSearchService
{
    private static readonly Uri SauceNaoSearchUri = new("https://saucenao.com/search.php");
    private static readonly Uri YandexImageSearchUri = new("https://yandex.com/images/search");
    private static readonly HttpClient SharedHttpClient = CreateHttpClient();

    private readonly HttpClient httpClient;
    private readonly ConcurrentDictionary<string, Task<WallpaperImageData>> imageCache = new();

    public WallpaperSourceSearchService(HttpClient? httpClient = null)
    {
        this.httpClient = httpClient ?? SharedHttpClient;
    }

    public async Task<WallpaperSearchResponse> SearchAsync(
        string? artworkSource,
        CancellationToken cancellationToken = default)
    {
        string sauceNaoHtml;
        Uri? remoteArtwork = null;
        if (TryHttpUri(artworkSource, out remoteArtwork))
        {
            using var response = await httpClient.GetAsync(
                SauceNaoUrl(remoteArtwork!),
                cancellationToken);
            response.EnsureSuccessStatusCode();
            sauceNaoHtml = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(artworkSource) || !File.Exists(artworkSource))
            {
                throw new InvalidOperationException("No wallpaper is available for this map yet.");
            }

            sauceNaoHtml = await UploadLocalWallpaperAsync(artworkSource, cancellationToken);
        }

        var warnings = new List<string>();
        var sauceNaoResults = ParseSauceNaoResults(sauceNaoHtml).ToList();
        var queryImage = ExtractUploadedArtworkUri(sauceNaoHtml) ?? remoteArtwork;

        IReadOnlyList<WallpaperSearchResult> yandexResults = [];
        if (queryImage is not null)
        {
            try
            {
                yandexResults = await SearchYandexAsync(queryImage, cancellationToken);
            }
            catch (Exception exception) when (exception is HttpRequestException
                                              or JsonException
                                              or InvalidOperationException
                                              || exception is TaskCanceledException
                                                 && !cancellationToken.IsCancellationRequested)
            {
                warnings.Add("Yandex visual matches were unavailable for this search.");
            }
        }

        sauceNaoResults = (await Task.WhenAll(sauceNaoResults.Select(result =>
                AddMeasuredResolutionAsync(result, cancellationToken))))
            .ToList();

        var merged = sauceNaoResults
            .Concat(yandexResults)
            .Where(result => TryHttpUri(result.ImageUri.AbsoluteUri, out _))
            .GroupBy(ResultIdentity, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(result => result.PixelCount)
                .ThenByDescending(result => result.Similarity)
                .First())
            .OrderByDescending(result => result.PixelCount)
            .ThenByDescending(result => result.Similarity)
            .ThenBy(result => result.Provider, StringComparer.OrdinalIgnoreCase)
            .Take(40)
            .ToArray();

        return new WallpaperSearchResponse(merged, warnings);
    }

    public async Task<WallpaperImageData> DownloadImageAsync(
        Uri imageUri,
        CancellationToken cancellationToken = default)
    {
        if (!TryHttpUri(imageUri.AbsoluteUri, out _))
        {
            throw new InvalidOperationException("The selected result does not have a downloadable web image.");
        }

        var download = imageCache.GetOrAdd(
            imageUri.AbsoluteUri,
            _ => DownloadImageCoreAsync(imageUri, CancellationToken.None));
        try
        {
            return await download.WaitAsync(cancellationToken);
        }
        catch when (download.IsFaulted || download.IsCanceled)
        {
            imageCache.TryRemove(imageUri.AbsoluteUri, out _);
            throw;
        }
    }

    private async Task<IReadOnlyList<WallpaperSearchResult>> SearchYandexAsync(
        Uri queryImage,
        CancellationToken cancellationToken)
    {
        string initialUrl = $"{YandexImageSearchUri}?rpt=imageview&url={Uri.EscapeDataString(queryImage.AbsoluteUri)}";
        string initialHtml = await httpClient.GetStringAsync(initialUrl, cancellationToken);
        string decodedInitial = WebUtility.HtmlDecode(initialHtml);
        string? cbirId = JsonStringProperty(decodedInitial, "cbirId");
        string? originalImageUrl = JsonStringProperty(decodedInitial, "originalImageUrl");
        if (string.IsNullOrWhiteSpace(cbirId))
        {
            throw new InvalidOperationException("Yandex did not return a visual-search identifier.");
        }

        originalImageUrl ??= $"https://avatars.mds.yandex.net/get-images-cbir/{cbirId}/orig";
        string sitesUrl = $"{YandexImageSearchUri}?rpt=imageview"
                          + $"&url={Uri.EscapeDataString(originalImageUrl)}"
                          + $"&cbir_id={Uri.EscapeDataString(cbirId)}"
                          + "&cbir_page=sites";
        string sitesHtml = await httpClient.GetStringAsync(sitesUrl, cancellationToken);
        return ParseYandexSiteResults(WebUtility.HtmlDecode(sitesHtml));
    }

    internal static IReadOnlyList<WallpaperSearchResult> ParseYandexSiteResults(string html)
    {
        string? sitesJson = ExtractBalancedJsonAfter(html, "\"cbirSites\":{\"sites\":", '[', ']');
        if (sitesJson is null)
        {
            return [];
        }

        using var document = JsonDocument.Parse(sitesJson);
        var results = new List<WallpaperSearchResult>();
        foreach (var site in document.RootElement.EnumerateArray())
        {
            string? imageUrl = NestedString(site, "originalImage", "url");
            string? sourceUrl = StringProperty(site, "url");
            if (!TryHttpUri(WebUtility.HtmlDecode(imageUrl), out var imageUri))
            {
                continue;
            }

            TryHttpUri(WebUtility.HtmlDecode(sourceUrl), out var sourceUri);
            int width = NestedInt32(site, "originalImage", "width");
            int height = NestedInt32(site, "originalImage", "height");
            string? thumbnailUrl = NestedString(site, "thumb", "url");
            if (thumbnailUrl?.StartsWith("//", StringComparison.Ordinal) == true)
            {
                thumbnailUrl = $"https:{thumbnailUrl}";
            }
            TryHttpUri(WebUtility.HtmlDecode(thumbnailUrl), out var thumbnailUri);
            string domain = StringProperty(site, "domain")
                            ?? sourceUri?.Host
                            ?? imageUri!.Host;
            results.Add(new WallpaperSearchResult(
                "Yandex",
                StringProperty(site, "title") ?? domain,
                imageUri!,
                sourceUri,
                width,
                height,
                0,
                domain,
                thumbnailUri));
        }

        return results;
    }

    internal static IReadOnlyList<WallpaperSearchResult> ParseSauceNaoResults(string html)
    {
        var results = new List<WallpaperSearchResult>();
        foreach (Match resultMatch in Regex.Matches(
                     html,
                     "<div\\s+class=\\\"result(?:\\s+hidden)?\\\">(?<body>.*?)</table>\\s*</div>",
                     RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant))
        {
            string body = resultMatch.Groups["body"].Value;
            string? imageUrl = AttributeValue(body, "data-src")
                               ?? ResultImageSource(body);
            if (!TryHttpUri(WebUtility.HtmlDecode(imageUrl), out var imageUri))
            {
                continue;
            }

            string title = CleanHtml(CaptureGroup(
                body,
                "<div\\s+class=\\\"resulttitle\\\">(?<value>.*?)</div>"));
            double similarity = double.TryParse(
                CaptureGroup(body, "class=\\\"resultsimilarityinfo\\\">(?<value>[0-9.]+)%"),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double parsedSimilarity)
                ? parsedSimilarity
                : 0;
            Uri? sourceUri = ExternalSourceUri(body);
            string domain = sourceUri?.Host ?? imageUri!.Host;
            results.Add(new WallpaperSearchResult(
                "SauceNAO",
                string.IsNullOrWhiteSpace(title) ? domain : title,
                imageUri!,
                sourceUri,
                0,
                0,
                similarity,
                domain));
        }

        return results;
    }

    internal static Uri? ExtractUploadedArtworkUri(string html)
    {
        var match = Regex.Match(
            html,
            "(?:https?://saucenao\\.com)?(?<path>/userdata/[^\\\"'<>?\\s]+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success
            ? new Uri(SauceNaoSearchUri, WebUtility.HtmlDecode(match.Groups["path"].Value))
            : null;
    }

    private async Task<WallpaperSearchResult> AddMeasuredResolutionAsync(
        WallpaperSearchResult result,
        CancellationToken cancellationToken)
    {
        if (result.Width > 0 && result.Height > 0)
        {
            return result;
        }

        try
        {
            var image = await DownloadImageAsync(result.ImageUri, cancellationToken);
            return result with { Width = image.Width, Height = image.Height };
        }
        catch (Exception exception) when (exception is HttpRequestException
                                          or InvalidOperationException
                                          or NotSupportedException
                                          or IOException)
        {
            return result;
        }
    }

    private async Task<WallpaperImageData> DownloadImageCoreAsync(
        Uri imageUri,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(
            imageUri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        const int maximumImageBytes = 32 * 1024 * 1024;
        if (response.Content.Headers.ContentLength is > maximumImageBytes)
        {
            throw new InvalidOperationException("The selected image is larger than Kumori's 32 MB preview limit.");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var destination = new MemoryStream();
        await source.CopyToAsync(destination, cancellationToken);
        if (destination.Length > maximumImageBytes)
        {
            throw new InvalidOperationException("The selected image is larger than Kumori's 32 MB preview limit.");
        }

        byte[] bytes = destination.ToArray();
        var (width, height) = ImageDimensions(bytes);
        return new WallpaperImageData(
            bytes,
            response.Content.Headers.ContentType?.MediaType,
            width,
            height);
    }

    private async Task<string> UploadLocalWallpaperAsync(
        string artworkPath,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            artworkPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            useAsync: true);
        using var file = new StreamContent(stream);
        file.Headers.ContentType = new MediaTypeHeaderValue(ContentTypeFor(artworkPath));
        using var form = new MultipartFormDataContent();
        form.Add(file, "file", Path.GetFileName(artworkPath));
        using var response = await httpClient.PostAsync(SauceNaoSearchUri, form, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private static (int Width, int Height) ImageDimensions(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var decoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        var frame = decoder.Frames.FirstOrDefault();
        return frame is null ? (0, 0) : (frame.PixelWidth, frame.PixelHeight);
    }

    private static string ResultIdentity(WallpaperSearchResult result)
    {
        var builder = new UriBuilder(result.ImageUri) { Query = "", Fragment = "" };
        return builder.Uri.AbsoluteUri;
    }

    private static string SauceNaoUrl(Uri artworkUri) =>
        $"{SauceNaoSearchUri}?db=999&url={Uri.EscapeDataString(artworkUri.AbsoluteUri)}";

    private static Uri? ExternalSourceUri(string body)
    {
        foreach (Match link in Regex.Matches(
                     body,
                     "href=\\\"(?<url>https?://[^\\\"]+)\\\"",
                     RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            if (!TryHttpUri(WebUtility.HtmlDecode(link.Groups["url"].Value), out var uri)
                || uri!.Host.EndsWith("saucenao.com", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return uri;
        }

        return null;
    }

    private static string? ResultImageSource(string body)
    {
        string resultImage = CaptureGroup(
            body,
            "<div\\s+class=\\\"resultimage\\\"[^>]*>(?<value>.*?)</div>");
        return AttributeValue(resultImage, "src");
    }

    private static string? AttributeValue(string html, string attribute)
    {
        var match = Regex.Match(
            html,
            $"\\b{Regex.Escape(attribute)}=\\\"(?<value>[^\\\"]+)\\\"",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? match.Groups["value"].Value : null;
    }

    private static string CaptureGroup(string input, string pattern)
    {
        var match = Regex.Match(
            input,
            pattern,
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
        return match.Success ? match.Groups["value"].Value : "";
    }

    private static string CleanHtml(string value)
    {
        string withoutTags = Regex.Replace(value, "<[^>]+>", " ");
        return Regex.Replace(WebUtility.HtmlDecode(withoutTags), "\\s+", " ").Trim();
    }

    private static string? JsonStringProperty(string jsonLikeHtml, string property)
    {
        var match = Regex.Match(
            jsonLikeHtml,
            $"\\\"{Regex.Escape(property)}\\\":\\\"(?<value>(?:\\\\.|[^\\\"])*)\\\"",
            RegexOptions.CultureInvariant);
        return match.Success
            ? JsonSerializer.Deserialize<string>($"\"{match.Groups["value"].Value}\"")
            : null;
    }

    private static string? ExtractBalancedJsonAfter(
        string input,
        string marker,
        char opening,
        char closing)
    {
        int markerIndex = input.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            return null;
        }

        int start = input.IndexOf(opening, markerIndex + marker.Length);
        if (start < 0)
        {
            return null;
        }

        int depth = 0;
        bool inString = false;
        bool escaped = false;
        for (int index = start; index < input.Length; index++)
        {
            char current = input[index];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (current == '\\')
                {
                    escaped = true;
                }
                else if (current == '"')
                {
                    inString = false;
                }
                continue;
            }

            if (current == '"')
            {
                inString = true;
            }
            else if (current == opening)
            {
                depth++;
            }
            else if (current == closing && --depth == 0)
            {
                return input[start..(index + 1)];
            }
        }

        return null;
    }

    private static string? StringProperty(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? NestedString(JsonElement element, string parent, string name) =>
        element.TryGetProperty(parent, out var nested)
            ? StringProperty(nested, name)
            : null;

    private static int NestedInt32(JsonElement element, string parent, string name) =>
        element.TryGetProperty(parent, out var nested)
        && nested.TryGetProperty(name, out var value)
        && value.TryGetInt32(out int parsed)
            ? parsed
            : 0;

    private static bool TryHttpUri(string? value, out Uri? uri)
    {
        bool valid = Uri.TryCreate(value, UriKind.Absolute, out uri)
                     && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
        if (!valid)
        {
            uri = null;
        }
        return valid;
    }

    private static string ContentTypeFor(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            _ => "image/jpeg",
        };

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Kumori/WallpaperSourceSearch");
        return client;
    }
}
