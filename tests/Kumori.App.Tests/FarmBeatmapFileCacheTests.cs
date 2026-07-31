using System.Net;
using Kumori.App.FarmFinder;
using Kumori.FarmFinder;
using Kumori.Gameplay;
using Xunit;

namespace Kumori.App.Tests;

public sealed class FarmBeatmapFileCacheTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(),
        $"kumori-farm-beatmaps-{Guid.NewGuid():N}");

    [Fact]
    public async Task DownloadsAndReusesAValidatedBeatmapFile()
    {
        var handler = new BeatmapHandler();
        using var http = new HttpClient(handler);
        var cache = new FarmBeatmapFileCache(
            directory,
            http,
            _ => null);
        var beatmap = new FarmBeatmap(
            987_654_321,
            123,
            "Artist",
            "Title",
            "Insane",
            "Mapper",
            180,
            90,
            100,
            6,
            "ranked",
            DateTimeOffset.UtcNow,
            "");

        var first = await cache.GetAsync(beatmap);
        var second = await cache.GetAsync(beatmap);

        Assert.Equal(first, second);
        Assert.NotNull(first);
        Assert.True(File.Exists(first));
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task ExactRatingIsCalculatedLocallyWithoutCallingTheApi()
    {
        var beatmapPath = Path.Combine(
            FindRepositoryRoot(),
            "tests",
            "Kumori.App.Tests",
            "Fixtures",
            "difficulty-curve.osu");
        var capturedMods = new[]
        {
            new CapturedMod("DT", """{"speed_change":1.5}"""),
        };
        var expected = BeatmapDifficultyCalculator.Calculate(beatmapPath, capturedMods);
        var cache = new RecordingStarCache();
        var handler = new ThrowingHandler();
        using var http = new HttpClient(handler);
        using var api = new OsuApiClient(
            new EmptyCredentialsStore(),
            new OsuRankedModCatalog(),
            new ClockRateCalculator(),
            http,
            TimeSpan.Zero);
        var files = new FarmBeatmapFileCache(
            directory,
            http,
            _ => beatmapPath);
        var calculator = new FarmStarRatingCalculator(
            cache,
            api,
            new HinamizawaStarRatingClient(http),
            files);
        var beatmap = new FarmBeatmap(
            123,
            456,
            "Artist",
            "Title",
            "Insane",
            "Mapper",
            180,
            90,
            100,
            expected.BaseStars,
            "ranked",
            DateTimeOffset.UtcNow,
            "");

        var actual = await calculator.CalculateAsync(
            beatmap,
            [new FarmMod("DT", """{"speed_change":1.5}""")]);

        Assert.Equal(expected.AdjustedStars, actual!.Value, 8);
        Assert.Equal(0, handler.RequestCount);
        Assert.Equal(expected.AdjustedStars, Assert.Single(cache.Saved).StarRating, 8);
    }

    [Fact]
    public async Task HinamizawaReturnsMultipleExactModRatingsWithOneRequest()
    {
        var handler = new StarTableHandler();
        using var http = new HttpClient(handler);
        var client = new HinamizawaStarRatingClient(http);
        var beatmap = new FarmBeatmap(
            252_238, 39_804, "xi", "FREEDOM DiVE", "FOUR DIMENSIONS", "Nakagawa-Kanon",
            174, 220, 230, 5.96, "ranked", DateTimeOffset.UtcNow, "");

        var hdDt = await client.GetStarRatingAsync(
            beatmap,
            [new FarmMod("HD"), new FarmMod("DT")]);
        var dt = await client.GetStarRatingAsync(
            beatmap,
            [new FarmMod("DT")]);

        Assert.Equal(8.18, hdDt);
        Assert.Equal(7.91, dt);
        Assert.Equal(1, handler.RequestCount);

        var ht = await client.GetStarRatingAsync(
            beatmap,
            [new FarmMod("HT")]);

        Assert.Equal(4.82, ht);
        Assert.Equal(2, handler.RequestCount);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, true);
    }

    private sealed class BeatmapHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "osu file format v14\n\n[General]\nMode: 0\n"),
            });
        }
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Kumori.sln")))
                return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Could not find the repository root.");
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            throw new InvalidOperationException("The exact rating should be calculated locally.");
        }
    }

    private sealed class StarTableHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            if (request.RequestUri?.AbsolutePath.Contains(
                    "/pp-calc/",
                    StringComparison.Ordinal) == true)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"success":true,"difficulty":{"stars":4.82}}"""),
                });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"success":true,"mods":{"NM":{"stars":5.96},"DT":{"stars":7.91},"HDDT":{"stars":8.18}}}"""),
            });
        }
    }

    private sealed class EmptyCredentialsStore : IOsuCredentialsStore
    {
        public Task<OsuApiCredentials?> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<OsuApiCredentials?>(null);

        public Task SaveAsync(
            OsuApiCredentials credentials,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task DeleteAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class RecordingStarCache : IFarmStarRatingCache
    {
        public List<SavedRating> Saved { get; } = [];

        public Task<IReadOnlyList<FarmCachedStarRating>> LoadAsync(
            string calculatorVersion,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<FarmCachedStarRating>>([]);

        public Task<double?> GetAsync(
            long beatmapId,
            string modsKey,
            string calculatorVersion,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<double?>(null);

        public Task SaveAsync(
            long beatmapId,
            string modsKey,
            string calculatorVersion,
            double starRating,
            CancellationToken cancellationToken = default)
        {
            Saved.Add(new SavedRating(beatmapId, modsKey, calculatorVersion, starRating));
            return Task.CompletedTask;
        }
    }

    private sealed record SavedRating(
        long BeatmapId,
        string ModsKey,
        string CalculatorVersion,
        double StarRating);
}
