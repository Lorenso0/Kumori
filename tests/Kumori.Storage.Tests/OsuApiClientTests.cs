using System.Net;
using System.Text;
using Kumori.FarmFinder;
using Xunit;

namespace Kumori.Storage.Tests;

public sealed class OsuApiClientTests
{
    [Fact]
    public async Task Rankings_FollowsCursorWithoutHtmlScraping()
    {
        var rankingCalls = 0;
        using var client = CreateClient(new StubHandler(async request =>
        {
            if (request.RequestUri!.AbsolutePath == "/oauth/token")
                return Json("""{"access_token":"token","expires_in":3600}""");
            rankingCalls++;
            if (rankingCalls == 1)
                return Json("""
                    {"ranking":[{"global_rank":1,"pp":15000,"user":{"id":10,"username":"A"}}],
                     "cursor":{"page":2},"total":2}
                    """);
            Assert.Contains("cursor[page]=2", Uri.UnescapeDataString(request.RequestUri.Query));
            return Json("""
                {"ranking":[{"global_rank":2,"pp":14000,"user":{"id":11,"username":"B"}}],
                 "cursor":{},"total":2}
                """);
        }));

        var players = new List<FarmPlayer>();
        await foreach (var page in client.GetRankingPagesAsync(null))
            players.AddRange(page.Players);

        Assert.Equal([10L, 11L], players.Select(player => player.UserId));
        Assert.Equal(2, rankingCalls);
    }

    [Fact]
    public async Task Rankings_StartsAtPageContainingRequestedRank()
    {
        using var client = CreateClient(new StubHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/oauth/token")
                return Task.FromResult(Json("""{"access_token":"token","expires_in":3600}"""));
            Assert.Contains(
                "cursor[page]=160",
                Uri.UnescapeDataString(request.RequestUri.Query));
            return Task.FromResult(Json("""
                {"ranking":[{"global_rank":7951,"pp":9000,"user":{"id":10,"username":"A"}}],
                 "cursor":{},"total":10000}
                """));
        }));

        var players = new List<FarmPlayer>();
        await foreach (var page in client.GetRankingPagesAsync(null, startingRank: 8000))
            players.AddRange(page.Players);

        Assert.Equal(7951, Assert.Single(players).GlobalRank);
    }

    [Fact]
    public async Task CountryRankings_EnumerateCodesAndApplyCountryFilter()
    {
        var countryListingCalls = 0;
        using var client = CreateClient(new StubHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/oauth/token")
                return Task.FromResult(Json("""{"access_token":"token","expires_in":3600}"""));
            if (request.RequestUri.AbsolutePath == "/api/v2/rankings/osu/country")
            {
                countryListingCalls++;
                if (countryListingCalls == 1)
                    return Task.FromResult(Json("""
                        {"ranking":[{"code":"US"},{"code":"JP"}],
                         "cursor":{"page":2},"total":3}
                        """));
                Assert.Contains(
                    "cursor[page]=2",
                    Uri.UnescapeDataString(request.RequestUri.Query));
                return Task.FromResult(Json("""
                    {"ranking":[{"code":"NL"},{"code":"us"}],
                     "cursor":{},"total":3}
                    """));
            }

            Assert.Contains("country=US", request.RequestUri.Query);
            return Task.FromResult(Json("""
                {"ranking":[{"global_rank":20000,"pp":8000,
                  "user":{"id":10,"username":"A"}}],
                 "cursor":{},"total":10000}
                """));
        }));

        Assert.Equal(["JP", "NL", "US"], await client.GetCountryCodesAsync());
        var players = new List<FarmPlayer>();
        await foreach (var page in client.GetRankingPagesAsync(
                           null,
                           countryCode: "us"))
            players.AddRange(page.Players);

        Assert.Equal(20_000, Assert.Single(players).GlobalRank);
    }

    [Fact]
    public async Task UserProfile_ReturnsOfficialCountryRankAndCountryCode()
    {
        using var client = CreateClient(new StubHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/oauth/token")
                return Task.FromResult(Json("""{"access_token":"token","expires_in":3600}"""));
            Assert.Equal("/api/v2/users/4214858/osu", request.RequestUri.AbsolutePath);
            Assert.Contains("key=id", request.RequestUri.Query);
            return Task.FromResult(Json("""
                {"country_code":"nl","cover":{"url":"https://assets.ppy.sh/profile-cover.jpeg"},"statistics":{"country_rank":561}}
                """));
        }));

        var profile = await client.GetUserProfileStatsAsync(4_214_858);

        Assert.Equal(561, profile.CountryRank);
        Assert.Equal("NL", profile.CountryCode);
        Assert.Equal("https://assets.ppy.sh/profile-cover.jpeg", profile.CoverUrl);
    }

    [Fact]
    public async Task BeatmapUserScore_ReturnsOverallPositionAndLegacyScoreIdentity()
    {
        using var client = CreateClient(new StubHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/oauth/token")
                return Task.FromResult(Json("""{"access_token":"token","expires_in":3600}"""));
            Assert.Equal(
                "/api/v2/beatmaps/123/scores/users/99",
                request.RequestUri.AbsolutePath);
            Assert.Contains("mode=osu", request.RequestUri.Query);
            Assert.Contains("legacy_only=0", request.RequestUri.Query);
            return Task.FromResult(Json("""
                {
                  "position":20,
                  "score":{
                    "id":777,"legacy_score_id":555,
                    "total_score":2000000,"legacy_total_score":1234567,
                    "accuracy":0.9921,"pp":287.4,"max_combo":640,
                    "ended_at":"2026-07-25T13:57:00Z",
                    "statistics":{"great":543,"ok":5,"meh":0,"miss":1},
                    "mods":[{"acronym":"HD","settings":{}},{"acronym":"DT","settings":{}}]
                  }
                }
                """));
        }));

        OsuBeatmapUserScore score = Assert.IsType<OsuBeatmapUserScore>(
            await client.GetBeatmapUserScoreAsync(123, 99));

        Assert.Equal(20, score.Position);
        Assert.Equal(777, score.ScoreId);
        Assert.Equal(1_234_567, score.TotalScore);
        Assert.Equal(["HD", "DT"], score.Mods);
        Assert.Equal((543, 5, 0, 1), (score.N300, score.N100, score.N50, score.Misses));
    }

    [Fact]
    public async Task BestScores_PreserveOfficialPerformanceOrderAndRequestedLimit()
    {
        using var client = CreateClient(new StubHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/oauth/token")
                return Task.FromResult(Json("""{"access_token":"token","expires_in":3600}"""));
            Assert.Equal("/api/v2/users/99/scores/best", request.RequestUri.AbsolutePath);
            Assert.Contains("limit=50", request.RequestUri.Query);
            Assert.Contains("legacy_only=0", request.RequestUri.Query);
            return Task.FromResult(Json("""
                [
                  {"id":777,"total_score":1000,"accuracy":0.99,"pp":300,"max_combo":500,
                   "ended_at":"2026-07-25T13:57:00Z","statistics":{"great":400,"miss":1},
                   "mods":[],"beatmap":{"id":123}},
                  {"id":555,"total_score":900,"accuracy":0.98,"pp":290,"max_combo":450,
                   "ended_at":"2026-07-24T13:57:00Z","statistics":{"great":390,"miss":2},
                   "mods":[],"beatmap":{"id":456}}
                ]
                """));
        }));

        IReadOnlyList<OsuBeatmapUserScore> scores = await client.GetUserBestScoresAsync(99, 50);

        Assert.Equal([777L, 555L], scores.Select(score => score.ScoreId));
        Assert.Equal([1, 2], scores.Select(score => score.Position));
        Assert.Equal([123L, 456L], scores.Select(score => score.BeatmapId));
    }

    [Fact]
    public async Task TopScores_ReturnsTopHundredAndRejectsWholeMixedUnrankedCombination()
    {
        using var client = CreateClient(new StubHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/oauth/token")
                return Task.FromResult(Json("""{"access_token":"token","expires_in":3600}"""));
            Assert.Contains("limit=100", request.RequestUri.Query);
            return Task.FromResult(Json("""
                [
                  {
                    "id":1001,"pp":321.5,"accuracy":0.9876,"max_combo":900,
                    "legacy_score_id":998,"total_score":1000000,"legacy_total_score":999000,"build_id":20260730,
                    "ended_at":"2026-01-01T00:00:00Z","is_perfect_combo":true,
                    "statistics":{"miss":0},
                    "mods":[{"acronym":"HD","settings":{}},{"acronym":"DT","settings":{"adjust_pitch":true}},
                            {"acronym":"CL","settings":{"no_slider_head_accuracy":false}}],
                    "beatmap":{"id":50,"beatmapset_id":5,"version":"Insane","bpm":180,
                      "hit_length":100,"total_length":120,"difficulty_rating":6.2,
                      "cs":4,"ar":9,"accuracy":8,"drain":6,"status":"ranked"},
                    "beatmapset":{"artist":"Artist","title":"Title","creator":"Mapper","status":"ranked",
                      "ranked_date":"2025-01-01T00:00:00Z","covers":{"card":"https://example.test/card.jpg"}}
                  },
                  {
                    "id":1002,"pp":400,"accuracy":1,"max_combo":1000,
                    "ended_at":"2026-01-02T00:00:00Z","statistics":{"miss":0},
                    "mods":[{"acronym":"HD","settings":{}},{"acronym":"RX","settings":{}}],
                    "beatmap":{"id":50,"beatmapset_id":5,"version":"Insane","bpm":180,
                      "hit_length":100,"total_length":120,"difficulty_rating":6.2,"status":"ranked"}
                  }
                ]
                """));
        }), new StubRankedCatalog());
        var player = new FarmPlayer(10, "A", 1, 15000, DateTimeOffset.UtcNow);

        var payload = await client.GetTopScoresAsync(player);

        var score = Assert.Single(payload.Scores);
        Assert.Equal(1001, score.ScoreId);
        Assert.Equal(FarmScoreOrigin.Legacy, score.Origin);
        Assert.Equal(998, score.LegacyScoreId);
        Assert.Equal(1_000_000, score.TotalScore);
        Assert.Equal(999_000, score.LegacyTotalScore);
        Assert.Equal(20260730, score.BuildId);
        Assert.True(score.UsesClassicScoring);
        Assert.Equal(["HD", "DT", "CL"], score.ActualMods.Select(mod => mod.Acronym));
        Assert.Contains("\"adjust_pitch\":true", score.ActualMods[1].SettingsJson);
        Assert.Contains(
            "\"no_slider_head_accuracy\":false",
            score.ActualMods[2].SettingsJson);
        var beatmap = Assert.Single(payload.Beatmaps);
        Assert.Equal(4, beatmap.CircleSize);
        Assert.Equal(9, beatmap.ApproachRate);
        Assert.Equal(8, beatmap.OverallDifficulty);
        Assert.Equal(6, beatmap.DrainRate);
    }

    [Fact]
    public async Task DifficultyAttributes_SendCompleteModSettingsAndReturnExactStars()
    {
        using var client = CreateClient(new StubHandler(async request =>
        {
            if (request.RequestUri!.AbsolutePath == "/oauth/token")
                return Json("""{"access_token":"token","expires_in":3600}""");

            Assert.Equal(
                "/api/v2/beatmaps/50/attributes",
                request.RequestUri.AbsolutePath);
            Assert.Equal(HttpMethod.Post, request.Method);
            var body = await request.Content!.ReadAsStringAsync();
            Assert.Contains("\"acronym\":\"DT\"", body);
            Assert.Contains("\"speed_change\":1.25", body);
            Assert.Contains("\"ruleset\":\"osu\"", body);
            return Json("""{"attributes":{"star_rating":7.123}}""");
        }));

        var stars = await client.GetDifficultyStarRatingAsync(
            50,
            [new FarmMod("DT", """{"speed_change":1.25}""")]);

        Assert.Equal(7.123, stars);
    }

    [Fact]
    public async Task UnauthorizedApiRequest_RenewsTokenOnce()
    {
        var tokens = 0;
        var api = 0;
        using var client = CreateClient(new StubHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/oauth/token")
            {
                tokens++;
                return Task.FromResult(Json($$"""{"access_token":"token{{tokens}}","expires_in":3600}"""));
            }
            api++;
            return Task.FromResult(api == 1
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                : Json("""{"ranking":[],"cursor":{},"total":0}"""));
        }));

        await client.TestConnectionAsync();

        Assert.Equal(2, tokens);
        Assert.Equal(2, api);
    }

    [Fact]
    public async Task TooManyRequests_HonorsRetryAfterAndRetries()
    {
        var api = 0;
        DateTimeOffset? limitedUntil = null;
        using var client = CreateClient(new StubHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/oauth/token")
                return Task.FromResult(Json("""{"access_token":"token","expires_in":3600}"""));
            api++;
            if (api == 1)
            {
                var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
                return Task.FromResult(limited);
            }
            return Task.FromResult(Json("""{"ranking":[],"cursor":{},"total":0}"""));
        }));
        client.RateLimited += until => limitedUntil = until;

        await client.TestConnectionAsync();

        Assert.Equal(2, api);
        Assert.NotNull(limitedUntil);
    }

    [Fact]
    public async Task Cancellation_AbortsInflightApiRequest()
    {
        using var client = CreateClient(new StubHandler(async request =>
        {
            if (request.RequestUri!.AbsolutePath == "/oauth/token")
                return Json("""{"access_token":"token","expires_in":3600}""");
            await Task.Delay(Timeout.InfiniteTimeSpan, request.GetCancellationToken());
            return Json("{}");
        }));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.TestConnectionAsync(cancellation.Token));
    }

    [Fact]
    public async Task AuthenticationFailure_DoesNotExposeSecret()
    {
        const string secret = "never-print-me";
        using var client = CreateClient(
            new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized))),
            credentials: new MemoryCredentials(new OsuApiCredentials(123, secret)));

        var exception = await Assert.ThrowsAsync<OsuApiAuthenticationException>(() =>
            client.TestConnectionAsync());

        Assert.DoesNotContain(secret, exception.ToString());
    }

    [Fact]
    public async Task TokenEndpoint_RetriesTransientFailureAndMalformedPayloadFailsClearly()
    {
        var tokenCalls = 0;
        using var retrying = CreateClient(new StubHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/oauth/token")
            {
                tokenCalls++;
                if (tokenCalls == 1)
                {
                    var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                    limited.Headers.RetryAfter =
                        new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
                    return Task.FromResult(limited);
                }
                return Task.FromResult(Json("""{"access_token":"token","expires_in":3600}"""));
            }
            return Task.FromResult(Json("""{"ranking":[],"cursor":{},"total":0}"""));
        }));
        await retrying.TestConnectionAsync();
        Assert.Equal(2, tokenCalls);

        using var malformed = CreateClient(new StubHandler(request =>
            Task.FromResult(request.RequestUri!.AbsolutePath == "/oauth/token"
                ? Json("""{"access_token":""")
                : Json("{}"))));
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            malformed.TestConnectionAsync());
        Assert.Contains("malformed OAuth", exception.Message);
    }

    private static OsuApiClient CreateClient(
        HttpMessageHandler handler,
        IRankedModCatalog? catalog = null,
        IOsuCredentialsStore? credentials = null) =>
        new(
            credentials ?? new MemoryCredentials(new OsuApiCredentials(123, "secret")),
            catalog ?? new StubRankedCatalog(),
            new ClockRateCalculator(),
            new HttpClient(handler),
            TimeSpan.Zero);

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private sealed class StubHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            request.SetCancellationToken(cancellationToken);
            return response(request);
        }
    }

    private sealed class StubRankedCatalog : IRankedModCatalog
    {
        public IReadOnlyList<RankedModDescriptor> GetRankedMods() =>
            [new("NM", "No Mod"), new("HD", "Hidden"), new("DT", "Double Time")];

        public RankedModEvaluation Evaluate(FarmMod mod) =>
            mod.NormalizedAcronym is "RX" or "AP" or ""
                ? new(false, mod.NormalizedAcronym, "{}")
                : new(true, mod.NormalizedAcronym,
                    FarmFinderValidation.CanonicalJson(mod.SettingsJson));
    }

    private sealed class MemoryCredentials(OsuApiCredentials? value) : IOsuCredentialsStore
    {
        public Task<OsuApiCredentials?> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(value);

        public Task SaveAsync(OsuApiCredentials credentials, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task DeleteAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}

internal static class HttpRequestCancellationExtensions
{
    private static readonly HttpRequestOptionsKey<CancellationToken> key = new("test-cancellation");

    public static void SetCancellationToken(this HttpRequestMessage request, CancellationToken cancellationToken) =>
        request.Options.Set(key, cancellationToken);

    public static CancellationToken GetCancellationToken(this HttpRequestMessage request) =>
        request.Options.TryGetValue(key, out CancellationToken cancellationToken)
            ? cancellationToken
            : CancellationToken.None;
}
