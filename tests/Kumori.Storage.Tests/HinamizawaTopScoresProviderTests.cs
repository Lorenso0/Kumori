using System.Net;
using System.Text;
using Kumori.FarmFinder;
using Xunit;

namespace Kumori.Storage.Tests;

public sealed class HinamizawaTopScoresProviderTests
{
    [Fact]
    public async Task LegacyMods_ParseRankedScoresAndRejectMixedUnrankedScores()
    {
        using var provider = CreateProvider(new StubHandler(request =>
        {
            Assert.Equal(
                "/api/v1/hinai/player/10/scores",
                request.RequestUri?.AbsolutePath);
            Assert.Contains("type=best", request.RequestUri?.Query);
            Assert.Contains("limit=100", request.RequestUri?.Query);
            Assert.Contains(
                "Kumori-FarmFinder",
                request.Headers.UserAgent.ToString(),
                StringComparison.Ordinal);
            return Task.FromResult(Json("""
                {
                  "count":3,
                  "scores":[
                    {
                      "id":1001,"pp":321.5,"accuracy":0.9876,"max_combo":900,
                      "created_at":"2026-01-01T00:00:00Z","perfect":true,
                      "statistics":{"count_miss":0},"mods":["HD","DT"],
                      "beatmap":{"id":50,"beatmapset_id":5,"version":"Insane",
                        "bpm":180,"hit_length":100,"total_length":120,
                        "difficulty_rating":6.2,"status":"ranked"},
                      "beatmapset":{"artist":"Artist","title":"Title","creator":"Mapper",
                        "status":"ranked","ranked_date":"2025-01-01T00:00:00Z",
                        "covers":{"card":"https://example.test/card.jpg"}}
                    },
                    {
                      "id":1002,"pp":400,"accuracy":1,"max_combo":1000,
                      "created_at":"2026-01-02T00:00:00Z",
                      "statistics":{"count_miss":0},"mods":["HD","RX"],
                      "beatmap":{"id":51,"beatmapset_id":5,"version":"Relax",
                        "bpm":180,"hit_length":100,"total_length":120,
                        "difficulty_rating":6.2,"status":"ranked"}
                    },
                    {
                      "id":1003,"pp":null,"accuracy":1,"max_combo":1000,
                      "created_at":"2026-01-02T00:00:00Z",
                      "statistics":{"count_miss":0},"mods":["DT"],
                      "beatmap":{"id":52,"beatmapset_id":5,"version":"Custom speed",
                        "bpm":180,"hit_length":100,"total_length":120,
                        "difficulty_rating":6.2,"status":"ranked"}
                    }
                  ]
                }
                """));
        }));

        var payload = await provider.GetTopScoresAsync(Player());

        var score = Assert.Single(payload.Scores);
        Assert.Equal(1001, score.ScoreId);
        Assert.Equal(["HD", "DT"], score.ActualMods.Select(mod => mod.Acronym));
        Assert.All(score.ActualMods, mod => Assert.Equal("{}", mod.SettingsJson));
        Assert.Equal(1.5, score.ClockRate);
        Assert.True(score.IsFullCombo);
        var beatmap = Assert.Single(payload.Beatmaps);
        Assert.Equal("Artist", beatmap.Artist);
        Assert.Equal("https://example.test/card.jpg", beatmap.CoverUrl);
    }

    [Fact]
    public async Task ModernModObjects_PreserveAllowedSettings()
    {
        using var provider = CreateProvider(new StubHandler(_ =>
            Task.FromResult(Json("""
                {
                  "scores":[
                    {
                      "id":1001,"pp":321.5,"accuracy":0.9876,"max_combo":900,
                      "ended_at":"2026-01-01T00:00:00Z",
                      "is_perfect_combo":false,
                      "statistics":{"miss":1},
                      "mods":[
                        {"acronym":"HD","settings":{}},
                        {"acronym":"DT","settings":{"adjust_pitch":true}}
                      ],
                      "beatmap":{"id":50,"beatmapset_id":5,"version":"Insane",
                        "bpm":180,"hit_length":100,"total_length":120,
                        "difficulty_rating":6.2,"status":"ranked"}
                    }
                  ]
                }
                """))));

        var payload = await provider.GetTopScoresAsync(Player());

        var score = Assert.Single(payload.Scores);
        Assert.Equal(1, score.MissCount);
        Assert.Contains("\"adjust_pitch\":true", score.ActualMods[1].SettingsJson);
    }

    [Fact]
    public async Task TooManyRequests_HonorsRetryAfterAndPublishesWait()
    {
        var calls = 0;
        DateTimeOffset? limitedUntil = null;
        using var provider = CreateProvider(new StubHandler(_ =>
        {
            calls++;
            if (calls == 1)
            {
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter =
                    new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
                return Task.FromResult(response);
            }
            return Task.FromResult(Json("""{"scores":[]}"""));
        }));
        provider.RateLimited += until => limitedUntil = until;

        var payload = await provider.GetTopScoresAsync(Player());

        Assert.Empty(payload.Scores);
        Assert.Equal(2, calls);
        Assert.NotNull(limitedUntil);
    }

    [Fact]
    public async Task MalformedResponse_DoesNotReturnReplacementPayload()
    {
        using var provider = CreateProvider(new StubHandler(_ =>
            Task.FromResult(Json("""{"scores":"""))));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => provider.GetTopScoresAsync(Player()));

        Assert.Contains("malformed", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static HinamizawaTopScoresProvider CreateProvider(
        HttpMessageHandler handler) =>
        new(
            new StubCatalog(),
            new ClockRateCalculator(),
            new HttpClient(handler),
            TimeSpan.Zero);

    private static FarmPlayer Player() =>
        new(10, "Player", 20_000, 9_000, DateTimeOffset.UtcNow);

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private sealed class StubHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> response)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            response(request);
    }

    private sealed class StubCatalog : IRankedModCatalog
    {
        private static readonly HashSet<string> allowed =
            new(["HD", "DT", "HR", "FL", "CL"], StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<RankedModDescriptor> GetRankedMods() => [];

        public RankedModEvaluation Evaluate(FarmMod mod)
        {
            var acronym = mod.NormalizedAcronym;
            if (!allowed.Contains(acronym) ||
                mod.SettingsJson.Contains("speed_change", StringComparison.OrdinalIgnoreCase))
                return new(false, acronym, "{}");
            return new(
                true,
                acronym,
                FarmFinderValidation.CanonicalJson(mod.SettingsJson));
        }
    }
}
