using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Text.Json;
using Kumori.App;
using Kumori.Core.Models;
using Kumori.Core.Settings;
using Kumori.FarmFinder;
using Kumori.Storage;
using Kumori.Tracking;
using Xunit;

namespace Kumori.App.Tests;

public sealed class ScoreWebhookServiceTests : IDisposable
{
    private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory();


    [Fact]
    public void Matching_RequiresScoreTimeModsComboAccuracyAndHitStatistics()
    {
        AttemptDetails local = Attempt();
        var online = new OsuBeatmapUserScore(
            20, 777, 99, 123,
            DateTimeOffset.Parse(local.Summary.EndedAt!),
            local.Summary.Score,
            local.Summary.Accuracy / 100,
            local.Summary.Pp,
            local.Summary.Combo,
            local.N300,
            local.N100,
            local.N50,
            local.Summary.Misses,
            ["HD", "DT", "CL"]);

        Assert.True(ScoreWebhookService.Matches(local, online));
        Assert.False(ScoreWebhookService.Matches(local, online with { TotalScore = online.TotalScore + 1 }));
        Assert.False(ScoreWebhookService.Matches(local, online with { Mods = ["HD"] }));
        Assert.False(ScoreWebhookService.Matches(local, online with { N100 = online.N100 + 1 }));
        Assert.False(ScoreWebhookService.Matches(
            local,
            online with { EndedAt = online.EndedAt.AddMinutes(3) }));
    }

    [Fact]
    public void Payload_FormatsPbCardAndDisablesMentions()
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(
            ScoreWebhookService.BuildPayload(Attempt(), "Lorenzo", 20, 777, false, false));
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        Assert.Empty(root.GetProperty("allowed_mentions").GetProperty("parse").EnumerateArray());
        JsonElement embed = root.GetProperty("embeds")[0];
        Assert.Equal("New #20 for Lorenzo in osu!", embed.GetProperty("title").GetString());
        Assert.Equal("https://osu.ppy.sh/scores/777", embed.GetProperty("url").GetString());
        Assert.Contains("Yellow", embed.GetProperty("description").GetString());
        Assert.Contains("Replay attachment unavailable", embed.GetProperty("footer").GetProperty("text").GetString());
        Assert.Equal(
            "https://assets.ppy.sh/beatmaps/456/covers/list@2x.jpg",
            embed.GetProperty("thumbnail").GetProperty("url").GetString());
    }

    [Fact]
    public void TestPayloadWithoutOfficialMatch_DoesNotInventAScoreLink()
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(
            ScoreWebhookService.BuildPayload(Attempt(), "Lorenzo", null, 0, true, true));
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement embed = document.RootElement.GetProperty("embeds")[0];
        Assert.Equal("PB alert test for Lorenzo", embed.GetProperty("title").GetString());
        Assert.Equal(JsonValueKind.Null, embed.GetProperty("url").ValueKind);
    }

    [Fact]
    public void ImagePayload_ContainsOnlyTheUploadedScoreCard()
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(
            ScoreWebhookService.BuildImagePayload(Attempt()));
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        Assert.False(root.TryGetProperty("content", out _));
        Assert.False(root.TryGetProperty("embeds", out _));
        Assert.Empty(root.GetProperty("allowed_mentions").GetProperty("parse").EnumerateArray());
        JsonElement attachment = root.GetProperty("attachments")[0];
        Assert.Equal(0, attachment.GetProperty("id").GetInt32());
        Assert.Equal("kumori-pb-card.png", attachment.GetProperty("filename").GetString());
        Assert.Contains("Yellow", attachment.GetProperty("description").GetString());
    }

    [Fact]
    public async Task ScoreCardRenderer_CreatesExpectedPngCanvas()
    {
        string output = Environment.GetEnvironmentVariable("KUMORI_SCORE_CARD_TEST_OUTPUT")
            ?? Path.Combine(directory.FullName, "score-card.png");

        await PbScoreCardRenderer.RenderAsync(
            Attempt(),
            "Lorenzo",
            20,
            777,
            new ScoreAlertProfileChange(12_450.25, 12_458.54, 25_500, 25_420),
            replayAttached: true,
            isTest: false,
            artworkPath: null,
            avatarPath: Environment.GetEnvironmentVariable("KUMORI_SCORE_CARD_TEST_AVATAR"),
            output);

        byte[] png = await File.ReadAllBytesAsync(output);
        Assert.True(png.Length > 10_000);
        Assert.Equal([137, 80, 78, 71, 13, 10, 26, 10], png[..8]);
        Assert.Equal(PbScoreCardRenderer.Width, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4)));
        Assert.Equal(PbScoreCardRenderer.Height, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4)));
    }

    [Fact]
    public void MapSettings_ShowsAdjustedValuesWithOriginalsAndUsesBpmInsteadOfHp()
    {
        Assert.Equal(
            "AR10 (9.6) · OD10 (9.2) · CS4 · BPM270 (180)",
            PbScoreCardRenderer.MapSettings(Attempt()));
    }

    [Fact]
    public async Task Webhook_UsesMultipartPayloadJsonAndFileField()
    {
        string attachment = Path.Combine(directory.FullName, "Lorenzo - Yellow [Insane].kumori");
        await File.WriteAllBytesAsync(attachment, Encoding.UTF8.GetBytes("replay bytes"));
        var handler = new CaptureHandler();
        ScoreWebhookService service = CreateService(new HttpClient(handler));

        await service.SendWebhookAsync(
            "https://discord.com/api/webhooks/123/token",
            ScoreWebhookService.BuildPayload(Attempt(), "Lorenzo", 20, 777, true, false),
            attachment,
            Path.GetFileName(attachment),
            CancellationToken.None);

        Assert.Equal("multipart/form-data", handler.ContentType);
        Assert.Contains("payload_json", handler.Body);
        Assert.Contains("files[0]", handler.Body);
        Assert.Contains("Lorenzo - Yellow [Insane].kumori", handler.Body);
        Assert.Contains("replay bytes", handler.Body);
        Assert.Contains("wait=true", handler.RequestUri!.Query);
    }

    [Fact]
    public async Task TestAlert_ExportsExtensionlessLazerBeatmapAsValidCompactReplay()
    {
        string database = Path.Combine(directory.FullName, "lazer-test.sqlite3");
        var factory = new SqliteConnectionFactory(database, readOnly: false);
        var sink = new AttemptSqliteSink(factory, (_, work) => work(CancellationToken.None));
        string contentStoreBeatmap = Path.Combine(directory.FullName, "4f8b58fc969b765e72f52a2dd8f968c4");
        await File.WriteAllTextAsync(
            contentStoreBeatmap,
            "osu file format v14\n\n[General]\nAudioFilename: audio.mp3\n");
        await File.WriteAllBytesAsync(
            Path.Combine(directory.FullName, "audio.mp3"),
            Encoding.UTF8.GetBytes("audio payload"));
        sink.StartAttempt(new AttemptStart
        {
            Identity = "lazer-map",
            WallTime = 1_786_000_000,
            PlayerName = "Lorenzo",
            Artist = "Kano",
            Title = "Yellow",
            Mapper = "Mapper",
            Difficulty = "Insane",
            BeatmapId = 123,
            BeatmapSetId = 456,
            Checksum = "checksum",
            ClientKind = OsuClientKind.Stable,
            BeatmapFile = contentStoreBeatmap,
            BeatmapStats = new BeatmapStats { BaseStars = 6.03, Stars = 6.03, MaxCombo = 767 },
        });
        long attemptId = Assert.IsType<long>(sink.CurrentAttemptId);
        sink.Finalize(new AttemptFinalization(
            "completed",
            "results_screen",
            new AttemptSnapshot
            {
                Identity = "lazer-map",
                WallTime = 1_786_000_100,
                PlayerName = "Lorenzo",
                DurationSeconds = 100,
                Score = 1_234_567,
                Accuracy = 99.21,
                Grade = "S",
                Pp = 287,
                Combo = 640,
                N300 = 543,
                N100 = 5,
                Misses = 1,
                Progress = 1,
                BeatmapStats = new BeatmapStats { BaseStars = 6.03, Stars = 6.03, MaxCombo = 767 },
            },
            1));
        var capture = new MovementCaptureStore(factory);
        capture.Start(attemptId);
        capture.AddSamples([new MovementSample { MapTimeMs = 1, MonotonicMs = 1, X = 256, Y = 192 }]);
        capture.Complete(0, "live", "{}");

        var settings = new SettingsService(
            Path.Combine(directory.FullName, "lazer-settings.json"),
            Path.Combine(directory.FullName, "missing.json"));
        settings.Load();
        var handler = new CaptureHandler();
        var service = new ScoreWebhookService(
            settings,
            new ScoreWebhookRepository(factory),
            new AttemptDetailsRepository(factory),
            new MovementRepository(factory),
            new PlaySharePackageService(
                new AttemptDetailsRepository(factory),
                new MovementRepository(factory),
                new SessionRepository(factory),
                Path.Combine(directory.FullName, "lazer-imports.sqlite3")),
            new NullScoreProvider(),
            () => null,
            new HttpClient(handler));

        await service.SendTestAsync("https://discord.com/api/webhooks/123/token");

        Assert.Equal(2, handler.Bodies.Count);
        Assert.Contains("files[0]", handler.Bodies[0]);
        Assert.Contains("kumori-pb-card.png", handler.Bodies[0]);
        Assert.Contains("\"attachments\"", handler.Bodies[0]);
        Assert.DoesNotContain("\"embeds\"", handler.Bodies[0]);
        Assert.Contains("files[0]", handler.Bodies[1]);
        Assert.Contains("Lorenzo - Yellow [Insane].kumori", handler.Bodies[1]);
    }

    private ScoreWebhookService CreateService(HttpClient http)
    {
        string database = Path.Combine(directory.FullName, "tracking.sqlite3");
        var factory = new SqliteConnectionFactory(database, readOnly: false);
        var settings = new SettingsService(
            Path.Combine(directory.FullName, "settings.json"),
            Path.Combine(directory.FullName, "missing.json"));
        settings.Load();
        return new ScoreWebhookService(
            settings,
            new ScoreWebhookRepository(factory),
            new AttemptDetailsRepository(factory),
            new MovementRepository(factory),
            new PlaySharePackageService(
                new AttemptDetailsRepository(factory),
                new MovementRepository(factory),
                new SessionRepository(factory),
                Path.Combine(directory.FullName, "imports.sqlite3")),
            new NullScoreProvider(),
            () => null,
            http);
    }

    private static AttemptDetails Attempt() => new()
    {
        Summary = new AttemptSummary
        {
            Outcome = "completed",
            EndedAt = "2026-07-25T13:57:00Z",
            Artist = "Kano",
            Title = "Yellow",
            Difficulty = "Insane",
            Mapper = "Mapper",
            AdjustedStars = 6.03,
            Score = 1_234_567,
            Pp = 287,
            Accuracy = 99.21,
            Grade = "A",
            Combo = 640,
            BeatmapMaxCombo = 767,
            Misses = 1,
            ModsKey = "HDDT",
            Mods = [new ModEntry("HD", "{}"), new ModEntry("DT", "{}")],
            OsuBeatmapId = 123,
            BeatmapSetId = 456,
            Progress = 1,
        },
        N300 = 543,
        N100 = 5,
        N50 = 0,
        SliderBreaks = 0,
        LargeTickHits = 7,
        LargeTickMisses = 0,
        SliderTailHits = 119,
        SliderTailMisses = 0,
        FcPp = 312.45,
        BaseStars = 5.15,
        UnstableRate = 88.2,
        Bpm = 180,
        Mapper = "Mapper",
        BeatmapAr = 9.6,
        BeatmapOd = 9.2,
        BeatmapCs = 4,
        BeatmapHp = 6,
        Key1Count = 321,
        Key2Count = 306,
        Input = new InputSummary
        {
            Key1Presses = 321,
            Key2Presses = 306,
            PeakKps = 12,
        },
        Mods = [new ModEntry("HD", "{}"), new ModEntry("DT", "{}")],
        ClientKind = "lazer",
        CapturedDifficulty = new Dictionary<string, DifficultyPair>
        {
            ["ar"] = new(9.6, 10),
            ["od"] = new(9.2, 10),
            ["cs"] = new(4, 4),
            ["bpm"] = new(180, 270),
        },
    };

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        directory.Delete(recursive: true);
    }

    private sealed class NullScoreProvider : IOsuBeatmapScoreProvider
    {
        public Task<OsuBeatmapUserScore?> GetBeatmapUserScoreAsync(
            long beatmapId,
            long userId,
            CancellationToken cancellationToken = default) => Task.FromResult<OsuBeatmapUserScore?>(null);
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        public string? ContentType { get; private set; }
        public string Body { get; private set; } = "";
        public Uri? RequestUri { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            ContentType = request.Content?.Headers.ContentType?.MediaType;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Bodies.Add(Body);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
    }
}
