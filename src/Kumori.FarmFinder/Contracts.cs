namespace Kumori.FarmFinder;

public interface IRankedModCatalog
{
    IReadOnlyList<RankedModDescriptor> GetRankedMods();
    RankedModEvaluation Evaluate(FarmMod mod);
}

public interface IModNormalizer
{
    NormalizedMods Normalize(IReadOnlyList<FarmMod> mods, ModNormalizationOptions options);
}

public interface IModMatcher
{
    bool Matches(NormalizedMods normalized, FarmFinderQuery query);
}

public interface IClockRateCalculator
{
    double Calculate(IReadOnlyList<FarmMod> mods);
}

public interface IFarmStarRatingCalculator
{
    ValueTask<double?> CalculateAsync(
        FarmBeatmap beatmap,
        IReadOnlyList<FarmMod> mods,
        CancellationToken cancellationToken = default);
}

public interface IFarmStarRatingCache
{
    Task<IReadOnlyList<FarmCachedStarRating>> LoadAsync(
        string calculatorVersion,
        CancellationToken cancellationToken = default);

    Task<double?> GetAsync(
        long beatmapId,
        string modsKey,
        string calculatorVersion,
        CancellationToken cancellationToken = default);

    Task SaveAsync(
        long beatmapId,
        string modsKey,
        string calculatorVersion,
        double starRating,
        CancellationToken cancellationToken = default);
}

public sealed record FarmCachedStarRating(
    long BeatmapId,
    string ModsKey,
    double StarRating);

public interface IFarmMapAggregator
{
    IReadOnlyList<FarmMapResult> Aggregate(
        IReadOnlyList<FarmScoreCandidate> candidates,
        FarmFinderQuery query,
        int scannedCohortSize,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FarmMapResult>> AggregateAsync(
        IReadOnlyList<FarmScoreCandidate> candidates,
        FarmFinderQuery query,
        int scannedCohortSize,
        CancellationToken cancellationToken = default,
        IProgress<FarmStarRatingProgress>? starRatingProgress = null);
}

public interface IPlayerCohortProvider
{
    Task<IReadOnlyList<string>> GetCountryCodesAsync(
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<RankingPage> GetRankingPagesAsync(
        string? cursorJson,
        int startingRank = 1,
        string? countryCode = null,
        CancellationToken cancellationToken = default);
}

public interface IPlayerTopScoresProvider
{
    Task<PlayerScoresPayload> GetTopScoresAsync(
        FarmPlayer player,
        CancellationToken cancellationToken = default);
}

public interface IPlayerTopScoresProviderMetadata
{
    string SourceName { get; }
    int RecommendedConcurrency { get; }
    int RequestsPerMinute { get; }
}

public interface IOsuRateLimitSource
{
    event Action<DateTimeOffset>? RateLimited;
}

public interface IOsuCredentialsStore
{
    Task<OsuApiCredentials?> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(OsuApiCredentials credentials, CancellationToken cancellationToken = default);
    Task DeleteAsync(CancellationToken cancellationToken = default);
}

public interface IOsuBeatmapScoreProvider
{
    Task<OsuBeatmapUserScore?> GetBeatmapUserScoreAsync(
        long beatmapId,
        long userId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OsuBeatmapUserScore>> GetUserBestScoresAsync(
        long userId,
        int limit,
        CancellationToken cancellationToken = default);
}

public interface IOsuUserProfileProvider
{
    Task<OsuUserProfileStats> GetUserProfileStatsAsync(
        long userId,
        CancellationToken cancellationToken = default);
}

public interface IOsuScoreReplayProvider
{
    Task<byte[]?> DownloadScoreReplayAsync(
        long scoreId,
        int maximumBytes,
        CancellationToken cancellationToken = default);
}

public interface IFarmFinderRepository
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task ReloadAsync(CancellationToken cancellationToken = default);
    Task<IndexJob?> GetResumableJobAsync(CancellationToken cancellationToken = default);
    Task<IndexJob> BeginOrResumeJobAsync(int minimumRank, int maximumRank, CancellationToken cancellationToken = default);
    Task UpdateJobCursorAsync(long jobId, string? cursorJson, int playersTotal, CancellationToken cancellationToken = default);
    Task UpsertRankingPlayersAsync(long jobId, IReadOnlyList<FarmPlayer> players, CancellationToken cancellationToken = default);
    Task UpsertCountryCoverageAsync(long jobId, CountryCoverage coverage, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FarmPlayer>> GetPlayersInRangeAsync(int minimumRank, int maximumRank, CancellationToken cancellationToken = default);
    Task<FarmScoreMetadataRepairStatus> GetScoreMetadataRepairStatusAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FarmPlayer>> GetPlayersNeedingScoreMetadataRepairAsync(CancellationToken cancellationToken = default);
    Task ReplacePlayerScoresAsync(PlayerScoresPayload payload, CancellationToken cancellationToken = default);
    Task RecordPlayerFailureAsync(long jobId, long userId, string error, CancellationToken cancellationToken = default);
    Task MarkPlayerCompletedAsync(long jobId, long userId, CancellationToken cancellationToken = default);
    Task CompleteJobAsync(long jobId, bool cancelled, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FarmScoreCandidate>> QueryCandidatesAsync(FarmFinderQuery query, CancellationToken cancellationToken = default);
    Task<CoverageSummary> GetCoverageAsync(FarmFinderQuery query, CancellationToken cancellationToken = default);
}

public interface IFarmFinderCacheInstaller
{
    bool IsConfigured { get; }

    Task<FarmCacheInstallResult> FetchAndInstallAsync(
        IProgress<FarmCacheDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public interface IFarmFinderService
{
    Task<FarmFinderSearchResult> SearchCachedAsync(
        FarmFinderQuery query,
        IProgress<FarmFinderProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<CoverageSummary> UpdateIndexAsync(
        FarmFinderQuery query,
        bool forceRefresh,
        IProgress<FarmFinderProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<FarmScoreMetadataRepairResult> RepairScoreMetadataAsync(
        IProgress<FarmFinderProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public interface IExternalUrlLauncher
{
    void Open(string url);
    void Copy(string text);
}
