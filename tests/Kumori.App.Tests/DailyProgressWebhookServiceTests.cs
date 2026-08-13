using System.Buffers.Binary;
using System.Net;
using System.Text.Json;
using Kumori.App;
using Kumori.Core.Models;
using Kumori.Core.Settings;
using Xunit;

namespace Kumori.App.Tests;

public sealed class DailyProgressWebhookServiceTests : IDisposable
{
    private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory();

    [Fact]
    public async Task PreviousDay_IsPostedOnce_WithCompactProgressFields()
    {
        var settings = CreateSettings();
        settings.Update(value =>
        {
            value.DailyWebhook.Enabled = true;
            value.DailyWebhook.WebhookUrl = "https://discord.com/api/webhooks/123/token";
        });
        var report = Report("2026-07-07");
        var handler = new CaptureHandler();
        long? requestedPlayerId = null;
        long? requestedBannerPlayerId = null;
        string? requestedCountryCode = null;
        var service = new DailyProgressWebhookService(
            settings,
            day => day == "2026-07-07" ? report : null,
            new HttpClient(handler),
            Path.Combine(directory.FullName, "daily.state"),
            (playerId, _) =>
            {
                requestedPlayerId = playerId;
                return Task.FromResult<byte[]?>(null);
            },
            (countryCode, _) =>
            {
                requestedCountryCode = countryCode;
                return Task.FromResult<byte[]?>(null);
            },
            (playerId, _) =>
            {
                requestedBannerPlayerId = playerId;
                return Task.FromResult<byte[]?>(null);
            });

        var first = await service.TrySendPreviousDayAsync(new DateTime(2026, 7, 8), CancellationToken.None);
        var second = await service.TrySendPreviousDayAsync(new DateTime(2026, 7, 8), CancellationToken.None);

        Assert.Equal(DailyWebhookOutcome.Sent, first);
        Assert.Equal(DailyWebhookOutcome.AlreadyProcessed, second);
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(99, requestedPlayerId);
        Assert.Equal(99, requestedBannerPlayerId);
        Assert.Equal("nl", requestedCountryCode);
        Assert.Equal("multipart/form-data", handler.ContentType);
        Assert.Contains("files[0]", handler.Body);
        Assert.Contains(DailyProgressWebhookService.DailyCardAttachmentName, handler.Body);
        Assert.Contains("\"attachments\"", handler.Body);
        Assert.DoesNotContain("\"embeds\"", handler.Body);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(
            DailyProgressWebhookService.BuildPayload(report, isTest: false)));
        var embed = json.RootElement.GetProperty("embeds")[0];
        Assert.Equal("Lorenzo's daily osu! recap", embed.GetProperty("title").GetString());
        Assert.Equal("https://a.ppy.sh/99", embed.GetProperty("thumbnail").GetProperty("url").GetString());
        var fields = embed.GetProperty("fields").EnumerateArray().ToArray();
        Assert.Contains(fields, field => field.GetProperty("name").GetString() == "Highest achieved PP play"
                                         && field.GetProperty("value").GetString()!.Contains("Song B"));
        Assert.Contains(fields, field => field.GetProperty("name").GetString() == "Playcount"
                                         && field.GetProperty("value").GetString() == "+40 official · 67 local · 23 distinct maps");
        Assert.Contains(fields, field => field.GetProperty("name").GetString() == "Rank"
                                         && field.GetProperty("value").GetString() == "#53,254 → #53,509 (-255)");
        Assert.Contains(fields, field => field.GetProperty("name").GetString() == "🇳🇱 Country rank"
                                         && field.GetProperty("value").GetString() == "#581 → #561 (+20)");
        Assert.Contains(fields, field => field.GetProperty("name").GetString() == "Most played map");
        Assert.Contains(fields, field => field.GetProperty("name").GetString() == "Playtime"
                                         && field.GetProperty("value").GetString() == "3h 22m playtime · K1 15,000 · K2 15,105 · 30,105 total");
        Assert.Contains(fields, field => field.GetProperty("name").GetString() == "Daily results"
                                         && field.GetProperty("value").GetString() == "67 plays · 53 completed (79%)\n98.42% average accuracy");
        Assert.DoesNotContain(fields, field => field.GetProperty("value").GetString()!.Contains("pp best"));
        Assert.DoesNotContain(fields, field => field.GetProperty("value").GetString()!.Contains("misses"));
    }

    [Fact]
    public void ImagePayload_ContainsOnlyTheDailyCardAttachment()
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(
            DailyProgressWebhookService.BuildImagePayload(Report("2026-07-07"))));
        JsonElement root = json.RootElement;

        Assert.False(root.TryGetProperty("content", out _));
        Assert.False(root.TryGetProperty("embeds", out _));
        Assert.Empty(root.GetProperty("allowed_mentions").GetProperty("parse").EnumerateArray());
        Assert.Equal(
            DailyProgressWebhookService.DailyCardAttachmentName,
            root.GetProperty("attachments")[0].GetProperty("filename").GetString());
    }

    [Fact]
    public async Task DailyCardRenderer_CreatesExpectedPngCanvas()
    {
        string output = Environment.GetEnvironmentVariable("KUMORI_DAILY_CARD_TEST_OUTPUT")
            ?? Path.Combine(directory.FullName, "daily-card.png");
        string? avatarPath = null;
        string? flagPath = null;
        string? bannerPath = null;
        string? bestArtworkPath = null;
        string? mostPlayedArtworkPath = null;
        if (long.TryParse(
                Environment.GetEnvironmentVariable("KUMORI_DAILY_CARD_NETWORK_PLAYER_ID"),
                out long playerId)
            && playerId > 0)
        {
            avatarPath = await WritePreviewAssetAsync(
                "avatar.img",
                await DailyProgressWebhookService.DownloadAvatarAsync(playerId, CancellationToken.None));
            flagPath = await WritePreviewAssetAsync(
                "flag.img",
                await DailyProgressWebhookService.DownloadCountryFlagAsync("nl", CancellationToken.None));
            bannerPath = await WritePreviewAssetAsync(
                "banner.img",
                await DailyProgressWebhookService.DownloadBannerAsync(playerId, CancellationToken.None));
            bestArtworkPath = await WritePreviewAssetAsync(
                "best-art.img",
                await DailyProgressWebhookService.LoadBeatmapArtworkAsync(
                    0,
                    2_022_711,
                    "Longing",
                    CancellationToken.None));
            mostPlayedArtworkPath = await WritePreviewAssetAsync(
                "most-played-art.img",
                await DailyProgressWebhookService.LoadBeatmapArtworkAsync(
                    0,
                    144_158,
                    "Gurvy's EXHAUST",
                    CancellationToken.None));
        }

        await DailyStatsCardRenderer.RenderAsync(
            Report("2026-07-07", longMapNames: true),
            false,
            avatarPath,
            flagPath,
            bannerPath,
            bestArtworkPath,
            mostPlayedArtworkPath,
            output);

        byte[] png = await File.ReadAllBytesAsync(output);
        Assert.True(png.Length > 10_000);
        Assert.Equal([137, 80, 78, 71, 13, 10, 26, 10], png[..8]);
        Assert.Equal(DailyStatsCardRenderer.Width, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4)));
        Assert.Equal(DailyStatsCardRenderer.Height, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4)));
    }

    private async Task<string?> WritePreviewAssetAsync(string filename, byte[]? content)
    {
        if (content is not { Length: > 0 })
            return null;
        string path = Path.Combine(directory.FullName, filename);
        await File.WriteAllBytesAsync(path, content);
        return path;
    }

    [Theory]
    [InlineData("http://discord.com/api/webhooks/1/token")]
    [InlineData("https://example.com/api/webhooks/1/token")]
    [InlineData("https://discord.com/channels/1/2")]
    public void Validation_RejectsNonDiscordWebhookUrls(string value)
    {
        Assert.False(DailyProgressWebhookService.TryValidateWebhookUrl(value, out _));
    }

    private SettingsService CreateSettings()
    {
        var settings = new SettingsService(
            Path.Combine(directory.FullName, "settings.v2.json"),
            Path.Combine(directory.FullName, "missing.json"));
        settings.Load();
        return settings;
    }

    private static DailyProgressReport Report(string day, bool longMapNames = false) => new()
    {
        PlayerName = "Lorenzo",
        Account = new DailyAccountProgress
        {
            PlayerId = 99,
            PlayerName = "Lorenzo",
            OldPlayCount = 100,
            NewPlayCount = 140,
            OldGlobalRank = 53_254,
            NewGlobalRank = 53_509,
            OldCountryRank = 581,
            NewCountryRank = 561,
            CountryCode = "NL",
            OldTotalPp = 6_250.8,
            NewTotalPp = 6_275.4,
        },
        Summary = new DailyAttemptTrend
        {
            Day = day,
            Attempts = 67,
            Completed = 53,
            AverageAccuracy = 98.42,
            BestPp = 198.2,
            TotalDurationSeconds = 12_120,
            ZTotal = 15_000,
            XTotal = 15_105,
            TotalMisses = 184,
            DistinctMaps = 23,
            TotalScore = 48_750_300,
        },
        BestPlay = new DailyPlayHighlight
        {
            BeatmapSetId = longMapNames ? 2_022_711 : 456,
            Artist = longMapNames ? "MIMI" : "Artist B",
            Title = longMapNames ? "What Call This Day ? (feat. ninzin from Rokudenashi)" : "Song B",
            Difficulty = longMapNames ? "Longing" : "Insane",
            Pp = 198.2,
            Accuracy = 99.25,
            Combo = 842,
            MaxCombo = 1_027,
            N100 = 27,
            N50 = 3,
            Misses = 1,
            SliderBreaks = 2,
            ModsKey = longMapNames ? "HD,DA,BPM" : "HD,DT",
            BaseStars = 4.29,
            AdjustedStars = 6.18,
            BaseAr = 8.7,
            AdjustedAr = 10.13,
            BaseOd = 8,
            AdjustedOd = 9.78,
            BaseCs = 4,
            AdjustedCs = 4,
            BaseBpm = 158,
            Bpm = longMapNames ? 237 : 270,
            UsedBpmAdjust = false,
        },
        MostPlayedMap = new DailyMapHighlight
        {
            BeatmapSetId = longMapNames ? 144_158 : 123,
            Artist = longMapNames ? "BlackYooh vs. siromaru" : "Artist A",
            Title = longMapNames ? "BLACK or WHITE?" : "Song A",
            Difficulty = longMapNames ? "Gurvy's EXHAUST" : "Hard",
            Plays = 12,
            Stars = 4.81,
            Ar = 9.2,
            Od = 8.5,
            Cs = 4,
            Bpm = 185,
        },
        MostUsedModCombinations =
        [
            new DailyModCombinationUsage { ModsKey = "HD,DA,BPM", Bpm = 230, Plays = 28 },
            new DailyModCombinationUsage { ModsKey = "NM", Plays = 21 },
            new DailyModCombinationUsage { ModsKey = "HR", Plays = 11 },
        ],
    };

    public void Dispose() => directory.Delete(recursive: true);

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public string? Body { get; private set; }
        public string? ContentType { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            ContentType = request.Content?.Headers.ContentType?.MediaType;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
    }
}
