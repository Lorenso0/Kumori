namespace Kumori.FarmFinder;

public sealed class FarmFinderService : IFarmFinderService, IDisposable
{
    private static readonly TimeSpan scoreStaleAge = TimeSpan.FromDays(7);
    private readonly IFarmFinderRepository repository;
    private readonly IPlayerCohortProvider cohortProvider;
    private readonly IPlayerTopScoresProvider scoresProvider;
    private readonly IPlayerTopScoresProviderMetadata? scoresProviderMetadata;
    private readonly IFarmMapAggregator aggregator;
    private DateTimeOffset? rateLimitedUntil;

    public FarmFinderService(
        IFarmFinderRepository repository,
        IPlayerCohortProvider cohortProvider,
        IPlayerTopScoresProvider scoresProvider,
        IFarmMapAggregator aggregator)
    {
        this.repository = repository;
        this.cohortProvider = cohortProvider;
        this.scoresProvider = scoresProvider;
        scoresProviderMetadata = scoresProvider as IPlayerTopScoresProviderMetadata;
        this.aggregator = aggregator;
        if (cohortProvider is IOsuRateLimitSource rateLimits)
            rateLimits.RateLimited += until => rateLimitedUntil = until;
        if (scoresProvider is IOsuRateLimitSource scoreRateLimits &&
            !ReferenceEquals(cohortProvider, scoresProvider))
            scoreRateLimits.RateLimited += until => rateLimitedUntil = until;
    }

    public async Task<FarmFinderSearchResult> SearchCachedAsync(
        FarmFinderQuery query,
        IProgress<FarmFinderProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var errors = FarmFinderValidation.Validate(query);
        if (errors.Count != 0)
            throw new ArgumentException(string.Join(Environment.NewLine, errors), nameof(query));
        await repository.InitializeAsync(cancellationToken);
        progress?.Report(new FarmFinderProgress(
            0,
            1,
            "Reading cached qualifying scores…",
            Phase: FarmFinderProgressPhase.SearchingCache));
        var coverage = await repository.GetCoverageAsync(query, cancellationToken);
        // Do not scan the score table when the selected cohort has no examined
        // players yet. During a first index build this database can already be
        // very large, while the correct result is known immediately: there
        // cannot be any cached matches. This keeps Search responsive while an
        // index job is still discovering or fetching players.
        if (coverage.AvailablePlayers == 0 || coverage.ScannedPlayers == 0)
        {
            progress?.Report(new FarmFinderProgress(
                1,
                1,
                "No cached scores are available for this range yet.",
                coverage.CachedPlayers,
                coverage.FetchedPlayers,
                Phase: FarmFinderProgressPhase.Completed));
            return new FarmFinderSearchResult([], coverage);
        }

        var candidates = await repository.QueryCandidatesAsync(query, cancellationToken);
        progress?.Report(new FarmFinderProgress(
            0,
            candidates.Count,
            "Grouping scores by beatmap and distinct player…",
            coverage.CachedPlayers,
            coverage.FetchedPlayers,
            candidates.Count,
            Phase: FarmFinderProgressPhase.AggregatingResults));
        var starRatingProgress = progress is null
            ? null
            : new CallbackProgress<FarmStarRatingProgress>(ratingProgress =>
                progress.Report(new FarmFinderProgress(
                    ratingProgress.Completed,
                    ratingProgress.Total,
                    "Calculating exact mod-adjusted star ratings…",
                    coverage.CachedPlayers,
                    coverage.FetchedPlayers,
                    candidates.Count,
                    Phase: FarmFinderProgressPhase.CalculatingStars)));
        var results = await Task.Run(
            () => aggregator.AggregateAsync(
                candidates,
                query,
                coverage.ScannedPlayers,
                cancellationToken,
                starRatingProgress),
            cancellationToken);
        coverage = coverage with
        {
            ScoresExamined = candidates.Count,
            MatchingScores = results.Sum(result => result.UniquePlayers),
            ResultingMaps = results.Count,
        };
        progress?.Report(new FarmFinderProgress(
            candidates.Count,
            candidates.Count,
            results.Count == 0 ? "No qualifying maps found." : $"Found {results.Count:N0} map groups.",
            coverage.CachedPlayers,
            coverage.FetchedPlayers,
            candidates.Count,
            coverage.MatchingScores,
            results.Count,
            Phase: FarmFinderProgressPhase.Completed));
        return new FarmFinderSearchResult(results, coverage);
    }

    private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }

    public async Task<CoverageSummary> UpdateIndexAsync(
        FarmFinderQuery query,
        bool forceRefresh,
        IProgress<FarmFinderProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var errors = FarmFinderValidation.ValidateIndexUpdate(query);
        if (errors.Count != 0)
            throw new ArgumentException(string.Join(Environment.NewLine, errors), nameof(query));
        var minimumRank = query.MinimumGlobalRank.GetValueOrDefault();
        var maximumRank = query.MaximumGlobalRank.GetValueOrDefault();

        await repository.InitializeAsync(cancellationToken);
        var job = await repository.BeginOrResumeJobAsync(
            minimumRank,
            maximumRank,
            cancellationToken);
        try
        {
            var rankingCount = job.PlayersTotal;
            if (job.PlayersTotal == 0 || !string.IsNullOrWhiteSpace(job.CursorJson))
            {
                rankingCount = job.MaximumRank >
                               OsuApiLimits.MaximumPerformanceRankingEntries
                    ? await ScanCountryUnionAsync(
                        job,
                        rankingCount,
                        progress,
                        cancellationToken)
                    : await ScanGlobalRankingAsync(
                        job,
                        rankingCount,
                        progress,
                        cancellationToken);
            }

            var players = await repository.GetPlayersInRangeAsync(job.MinimumRank, job.MaximumRank, cancellationToken);
            if (players.Count == 0)
                throw new InvalidOperationException("No players are available in the selected rank range.");
            var staleBefore = DateTimeOffset.UtcNow - scoreStaleAge;
            var pending = players.Where(player =>
                    forceRefresh || player.ScoresUpdatedAt is null || player.ScoresUpdatedAt < staleBefore)
                .ToArray();
            var cached = players.Count - pending.Length;
            var sourceName = scoresProviderMetadata?.SourceName ?? "score provider";
            progress?.Report(new FarmFinderProgress(
                cached,
                players.Count,
                pending.Length == 0
                    ? "Every player in this range is already current."
                    : $"Building the local index from {sourceName}…",
                cached,
                SourceName: sourceName,
                Phase: FarmFinderProgressPhase.FetchingScores));
            if (pending.Length == 0)
            {
                await repository.CompleteJobAsync(
                    job.Id,
                    cancelled: false,
                    cancellationToken);
                return (await repository.GetCoverageAsync(query, cancellationToken)) with
                {
                    CachedPlayers = cached,
                };
            }

            var processed = 0;
            var completed = 0;
            var failed = 0;
            var scoresExamined = 0;
            using var fatal = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            await Parallel.ForEachAsync(
                pending,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism =
                        scoresProviderMetadata?.RecommendedConcurrency ?? 2,
                    CancellationToken = fatal.Token,
                },
                async (player, token) =>
                {
                    try
                    {
                        var payload = await scoresProvider.GetTopScoresAsync(player, token);
                        await repository.ReplacePlayerScoresAsync(payload, token);
                        await repository.MarkPlayerCompletedAsync(job.Id, player.UserId, token);
                        Interlocked.Add(ref scoresExamined, payload.Scores.Count);
                        Interlocked.Increment(ref completed);
                        var current = cached + Interlocked.Increment(ref processed);
                        progress?.Report(new FarmFinderProgress(
                            current,
                            players.Count,
                            $"Indexed {current:N0} of {players.Count:N0} players from {sourceName}…",
                            cached,
                            Volatile.Read(ref completed),
                            Volatile.Read(ref scoresExamined),
                            RateLimitedUntil: rateLimitedUntil,
                            PlayersFailed: Volatile.Read(ref failed),
                            Phase: FarmFinderProgressPhase.FetchingScores,
                            SourceName: sourceName));
                    }
                    catch (OsuApiAuthenticationException)
                    {
                        fatal.Cancel();
                        throw;
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref failed);
                        await repository.RecordPlayerFailureAsync(job.Id, player.UserId, ex.Message, token);
                        var current = cached + Interlocked.Increment(ref processed);
                        progress?.Report(new FarmFinderProgress(
                            current,
                            players.Count,
                            $"Indexed {current:N0} of {players.Count:N0} players from {sourceName}…",
                            cached,
                            Volatile.Read(ref completed),
                            Volatile.Read(ref scoresExamined),
                            RateLimitedUntil: rateLimitedUntil,
                            PlayersFailed: Volatile.Read(ref failed),
                            Phase: FarmFinderProgressPhase.FetchingScores,
                            SourceName: sourceName));
                    }
                });
            await repository.CompleteJobAsync(job.Id, cancelled: false, cancellationToken);
            return (await repository.GetCoverageAsync(query, cancellationToken)) with
            {
                CachedPlayers = cached,
                FetchedPlayers = completed,
                FailedPlayers = failed,
                ScoresExamined = scoresExamined,
            };
        }
        catch (OperationCanceledException)
        {
            await repository.CompleteJobAsync(job.Id, cancelled: true, CancellationToken.None);
            throw;
        }
    }

    private async Task<int> ScanGlobalRankingAsync(
        IndexJob job,
        int rankingCount,
        IProgress<FarmFinderProgress>? progress,
        CancellationToken cancellationToken)
    {
        var cursor = job.CursorJson;
        await foreach (var page in cohortProvider.GetRankingPagesAsync(
                           cursor,
                           job.MinimumRank,
                           cancellationToken: cancellationToken))
        {
            var selected = SelectPlayers(page, job);
            if (selected.Length != 0)
                await repository.UpsertRankingPlayersAsync(job.Id, selected, cancellationToken);
            rankingCount += selected.Length;
            cursor = page.NextCursorJson;
            await repository.UpdateJobCursorAsync(
                job.Id,
                cursor,
                rankingCount,
                cancellationToken);
            var highest = page.Players.Count == 0
                ? 0
                : page.Players.Max(player => player.GlobalRank);
            var reached = page.Players.Count == 0
                ? job.MinimumRank - 1
                : Math.Clamp(highest, job.MinimumRank, job.MaximumRank);
            progress?.Report(new FarmFinderProgress(
                Math.Max(0, reached - job.MinimumRank + 1),
                job.MaximumRank - job.MinimumRank + 1,
                $"Reading the selected rank range at #{reached:N0}…",
                RateLimitedUntil: rateLimitedUntil,
                Phase: FarmFinderProgressPhase.DiscoveringPlayers));
            if (highest >= job.MaximumRank || string.IsNullOrWhiteSpace(cursor))
                break;
        }
        return rankingCount;
    }

    private async Task<int> ScanCountryUnionAsync(
        IndexJob job,
        int rankingCount,
        IProgress<FarmFinderProgress>? progress,
        CancellationToken cancellationToken)
    {
        var countryCodes = await cohortProvider.GetCountryCodesAsync(cancellationToken);
        if (countryCodes.Count == 0)
            throw new InvalidDataException("osu! returned no country leaderboards.");
        var state = ParseCountryScanState(job.CursorJson);
        var startIndex = Math.Clamp(state?.CountryIndex ?? 0, 0, countryCodes.Count);

        for (var index = startIndex; index < countryCodes.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var countryCode = countryCodes[index];
            var cursor = index == startIndex ? state?.PageCursorJson : null;
            var countryFinished = false;
            await foreach (var page in cohortProvider.GetRankingPagesAsync(
                               cursor,
                               countryCode: countryCode,
                               cancellationToken: cancellationToken))
            {
                var selected = SelectPlayers(page, job);
                if (selected.Length != 0)
                    await repository.UpsertRankingPlayersAsync(
                        job.Id,
                        selected,
                        cancellationToken);
                rankingCount += selected.Length;
                var highest = page.Players.Count == 0
                    ? 0
                    : page.Players.Max(player => player.GlobalRank);
                var reachedMaximum = highest >= job.MaximumRank;
                var exhausted = string.IsNullOrWhiteSpace(page.NextCursorJson);
                var hitApiLimit = exhausted &&
                                  !reachedMaximum &&
                                  page.Total >=
                                  OsuApiLimits.MaximumPerformanceRankingEntries;

                if (reachedMaximum || exhausted)
                {
                    var coverage = new CountryCoverage(
                        countryCode,
                        highest,
                        job.MaximumRank,
                        reachedMaximum || !hitApiLimit,
                        hitApiLimit);
                    await repository.UpsertCountryCoverageAsync(
                        job.Id,
                        coverage,
                        cancellationToken);
                    await repository.UpdateJobCursorAsync(
                        job.Id,
                        SerializeCountryScanState(new CountryScanState(index + 1, null)),
                        rankingCount,
                        cancellationToken);
                    countryFinished = true;
                }
                else
                {
                    cursor = page.NextCursorJson;
                    await repository.UpdateJobCursorAsync(
                        job.Id,
                        SerializeCountryScanState(new CountryScanState(index, cursor)),
                        rankingCount,
                        cancellationToken);
                }

                progress?.Report(new FarmFinderProgress(
                    index + 1,
                    countryCodes.Count,
                    highest > 0
                        ? $"Scanning {countryCode} · {index + 1:N0}/{countryCodes.Count:N0} countries · through global #{highest:N0}…"
                        : $"Scanning {countryCode} · {index + 1:N0}/{countryCodes.Count:N0} countries…",
                    RateLimitedUntil: rateLimitedUntil,
                    Phase: FarmFinderProgressPhase.DiscoveringPlayers));
                if (countryFinished)
                    break;
            }

            if (!countryFinished)
                throw new InvalidDataException(
                    $"osu! ended the {countryCode} ranking feed unexpectedly.");
        }

        await repository.UpdateJobCursorAsync(
            job.Id,
            null,
            rankingCount,
            cancellationToken);
        return rankingCount;
    }

    private static FarmPlayer[] SelectPlayers(RankingPage page, IndexJob job) =>
        page.Players
            .Where(player =>
                player.GlobalRank >= job.MinimumRank &&
                player.GlobalRank <= job.MaximumRank)
            .ToArray();

    private static CountryScanState? ParseCountryScanState(string? cursorJson)
    {
        if (string.IsNullOrWhiteSpace(cursorJson))
            return null;
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<CountryScanState>(
                cursorJson);
        }
        catch (System.Text.Json.JsonException)
        {
            // Older builds stored a global-ranking cursor for every range.
            return null;
        }
    }

    private static string SerializeCountryScanState(CountryScanState state) =>
        System.Text.Json.JsonSerializer.Serialize(state);

    private sealed record CountryScanState(
        int CountryIndex,
        string? PageCursorJson);

    public void Dispose()
    {
        if (scoresProvider is IDisposable disposableScores)
            disposableScores.Dispose();
        if (!ReferenceEquals(cohortProvider, scoresProvider) &&
            cohortProvider is IDisposable disposableCohort)
            disposableCohort.Dispose();
    }
}
