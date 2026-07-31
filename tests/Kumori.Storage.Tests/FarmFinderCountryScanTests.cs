using System.Runtime.CompilerServices;
using Kumori.FarmFinder;
using Kumori.Storage;
using Xunit;

namespace Kumori.Storage.Tests;

public sealed class FarmFinderCountryScanTests : IDisposable
{
    private readonly string databasePath =
        Path.Combine(
            Path.GetTempPath(),
            $"kumori-farm-country-{Guid.NewGuid():N}.sqlite3");

    [Fact]
    public async Task CountryUnion_DeduplicatesPlayersAndReportsApiLimitedGap()
    {
        var repository = new FarmFinderRepository(databasePath);
        var cohort = new StubCountryCohort();
        var progressMessages = new List<string>();
        var service = CreateService(
            repository,
            cohort);

        var coverage = await service.UpdateIndexAsync(
            new FarmFinderQuery
            {
                MinimumGlobalRank = 20_000,
                MaximumGlobalRank = 60_000,
            },
            forceRefresh: false,
            new InlineProgress<FarmFinderProgress>(
                value => progressMessages.Add(value.Text)));

        Assert.Equal(3, coverage.AvailablePlayers);
        Assert.Equal(3, coverage.ScannedPlayers);
        var gap = Assert.Single(coverage.CountryGaps!);
        Assert.Equal("US", gap.CountryCode);
        Assert.Equal(51_207, gap.CoveredThroughGlobalRank);
        Assert.Contains(
            progressMessages,
            message => message.Contains("Scanning US", StringComparison.Ordinal));
        Assert.Contains(
            progressMessages,
            message => message.Contains("Scanning NL", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CountryUnion_ResumesInsideCountryAfterCancellation()
    {
        var repository = new FarmFinderRepository(databasePath);
        var cohort = new ResumableCountryCohort();
        var service = CreateService(repository, cohort);
        using var cancellation = new CancellationTokenSource();
        var cancelled = false;
        var progress = new InlineProgress<FarmFinderProgress>(value =>
        {
            if (!cancelled &&
                value.Text.Contains("Scanning US", StringComparison.Ordinal))
            {
                cancelled = true;
                cancellation.Cancel();
            }
        });
        var query = new FarmFinderQuery
        {
            MinimumGlobalRank = 20_000,
            MaximumGlobalRank = 20_002,
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.UpdateIndexAsync(
                query,
                forceRefresh: false,
                progress,
                cancellation.Token));

        var paused = Assert.IsType<IndexJob>(
            await repository.GetResumableJobAsync());
        Assert.Contains("page", paused.CursorJson, StringComparison.Ordinal);

        var resumed = await service.UpdateIndexAsync(
            query,
            forceRefresh: false);

        Assert.Equal(3, resumed.AvailablePlayers);
        Assert.Contains(
            cohort.ReceivedCursors,
            cursor => cursor?.Contains("\"page\":2", StringComparison.Ordinal) == true);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var path = databasePath + suffix;
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private static FarmFinderService CreateService(
        IFarmFinderRepository repository,
        IPlayerCohortProvider cohort) =>
        new(
            repository,
            cohort,
            new EmptyScoresProvider(),
            new FarmMapAggregator(
                new ModNormalizer(new ClockRateCalculator()),
                new ModMatcher()));

    private sealed class StubCountryCohort : IPlayerCohortProvider
    {
        public Task<IReadOnlyList<string>> GetCountryCodesAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(["US", "NL"]);

        public async IAsyncEnumerable<RankingPage> GetRankingPagesAsync(
            string? cursorJson,
            int startingRank = 1,
            string? countryCode = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            if (countryCode == "US")
            {
                yield return new RankingPage(
                    [
                        Player(1, 20_000),
                        Player(2, 51_207),
                    ],
                    null,
                    OsuApiLimits.MaximumPerformanceRankingEntries);
                yield break;
            }
            yield return new RankingPage(
                [
                    Player(2, 51_207),
                    Player(3, 60_000),
                ],
                null,
                500);
        }
    }

    private sealed class ResumableCountryCohort : IPlayerCohortProvider
    {
        public List<string?> ReceivedCursors { get; } = [];

        public Task<IReadOnlyList<string>> GetCountryCodesAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(["US"]);

        public async IAsyncEnumerable<RankingPage> GetRankingPagesAsync(
            string? cursorJson,
            int startingRank = 1,
            string? countryCode = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ReceivedCursors.Add(cursorJson);
            if (string.IsNullOrWhiteSpace(cursorJson))
            {
                yield return new RankingPage(
                    [Player(1, 20_000)],
                    """{"page":2}""",
                    OsuApiLimits.MaximumPerformanceRankingEntries);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                yield break;
            }

            cancellationToken.ThrowIfCancellationRequested();
            yield return new RankingPage(
                [
                    Player(2, 20_001),
                    Player(3, 20_002),
                ],
                null,
                OsuApiLimits.MaximumPerformanceRankingEntries);
        }
    }

    private sealed class EmptyScoresProvider : IPlayerTopScoresProvider
    {
        public Task<PlayerScoresPayload> GetTopScoresAsync(
            FarmPlayer player,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new PlayerScoresPayload(
                player with { ScoresUpdatedAt = DateTimeOffset.UtcNow },
                [],
                []));
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private static FarmPlayer Player(long id, int rank) =>
        new(
            id,
            $"Player {id}",
            rank,
            10_000 - rank / 100d,
            DateTimeOffset.UtcNow);
}
