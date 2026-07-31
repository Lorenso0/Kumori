using Kumori.FarmFinder;
using Xunit;

namespace Kumori.Storage.Tests;

public sealed class FarmFinderDomainTests
{
    private readonly IClockRateCalculator clockRates = new ClockRateCalculator();

    [Fact]
    public void Validation_UsesInclusiveOrderedRanges()
    {
        var valid = new FarmFinderQuery
        {
            MinimumGlobalRank = 500,
            MaximumGlobalRank = 500,
            MinimumPp = 123.45,
            MaximumPp = 123.45,
        };
        Assert.Empty(FarmFinderValidation.Validate(valid));

        var invalid = valid with { MinimumGlobalRank = 501, MaximumGlobalRank = 500 };
        Assert.Contains(FarmFinderValidation.Validate(invalid), error => error.Contains("Global rank"));
    }

    [Fact]
    public void IndexUpdateValidation_AllowsCountryUnionRanges()
    {
        var errors = FarmFinderValidation.ValidateIndexUpdate(new FarmFinderQuery
        {
            MinimumGlobalRank = 20_000,
            MaximumGlobalRank = 60_000,
        });

        Assert.Empty(errors);
    }

    [Theory]
    [InlineData("DT", "{}", 1.5)]
    [InlineData("NC", "{}", 1.5)]
    [InlineData("HT", "{}", 0.75)]
    [InlineData("DT", "{\"speed_change\":1.25}", 1.25)]
    public void ClockRateCalculator_SupportsDefaultsAndCustomParsing(
        string acronym,
        string settings,
        double expected)
    {
        Assert.Equal(expected, clockRates.Calculate([new FarmMod(acronym, settings)]), 6);
    }

    [Fact]
    public void HiddenWildcardAndNightcore_CreateOneDtFamilyGroup()
    {
        var aggregator = new FarmMapAggregator(new ModNormalizer(clockRates), new ModMatcher());
        var mods = new[]
        {
            new[] { new FarmMod("DT") },
            new[] { new FarmMod("HD"), new FarmMod("DT") },
            new[] { new FarmMod("NC", "{\"adjust_pitch\":true}") },
            new[] { new FarmMod("HD"), new FarmMod("NC", "{\"adjust_pitch\":false}") },
        };
        var candidates = Enumerable.Range(1, 48)
            .Select(index => Candidate(
                userId: index,
                pp: 300 + index,
                accuracy: 0.98,
                date: new DateTimeOffset(2026, 1, 1, 0, index, 0, TimeSpan.Zero),
                mods[(index - 1) % mods.Length]))
            .ToArray();
        var query = new FarmFinderQuery
        {
            MinimumGlobalRank = 1,
            MaximumGlobalRank = 48,
            MinimumUniquePlayers = 48,
            HiddenWildcard = true,
            TreatNightcoreAsDoubleTime = true,
            Mods = [new FarmModFilter("DT", ModRequirement.Required)],
        };

        var result = Assert.Single(aggregator.Aggregate(candidates, query, 48));

        Assert.Equal("DT", result.NormalizedMods);
        Assert.Equal(48, result.UniquePlayers);
        Assert.Equal(270, result.EffectiveBpm, 6);
        Assert.Equal(100d / 1.5d, result.EffectiveLengthSeconds, 6);
        Assert.Equal("1:07", result.EffectiveLengthText);
    }

    [Fact]
    public void Aggregator_DeduplicatesPlayersWithPpAccuracyDateTieBreak()
    {
        var aggregator = new FarmMapAggregator(new ModNormalizer(clockRates), new ModMatcher());
        var older = Candidate(1, 400, 0.99, DateTimeOffset.Parse("2026-01-01"), [new FarmMod("DT")], 11);
        var higherPp = Candidate(1, 401, 0.95, DateTimeOffset.Parse("2025-01-01"), [new FarmMod("DT")], 12);
        var secondPlayer = Candidate(2, 350, 0.98, DateTimeOffset.Parse("2026-01-02"), [new FarmMod("DT")], 13);

        var result = Assert.Single(aggregator.Aggregate(
            [older, higherPp, secondPlayer],
            new FarmFinderQuery(),
            2));

        Assert.Equal(2, result.UniquePlayers);
        Assert.Contains(result.Players, player => player.UserId == 1 && player.ScoreId == 12);
        Assert.Equal("https://osu.ppy.sh/beatmaps/100", result.Players[0].BeatmapUrl);
        Assert.Equal(375.5, result.AveragePp, 6);
    }

    [Fact]
    public void Aggregator_ReportsTheArithmeticAverageOfQualifyingPlays()
    {
        var aggregator = new FarmMapAggregator(
            new ModNormalizer(clockRates),
            new ModMatcher());
        var date = DateTimeOffset.Parse("2026-01-01");

        var result = Assert.Single(aggregator.Aggregate(
            [
                Candidate(1, 100, .98, date, [new FarmMod("DT")]),
                Candidate(2, 100, .98, date, [new FarmMod("DT")]),
                Candidate(3, 400, .98, date, [new FarmMod("DT")]),
            ],
            new FarmFinderQuery(),
            3));

        Assert.Equal(200, result.AveragePp, 6);
        Assert.Equal(["DT"], result.ModAcronyms);
    }

    [Fact]
    public void AggregatorNormalizesEachCanonicalModSetOnlyOnce()
    {
        var normalizer = new CountingModNormalizer(new ModNormalizer(clockRates));
        var aggregator = new FarmMapAggregator(normalizer, new ModMatcher());
        var date = DateTimeOffset.Parse("2026-01-01");

        var result = Assert.Single(aggregator.Aggregate(
            [
                Candidate(1, 300, .98, date, [new FarmMod("HD"), new FarmMod("DT")]),
                Candidate(2, 310, .99, date, [new FarmMod("HD"), new FarmMod("DT")]),
            ],
            new FarmFinderQuery(),
            2));

        Assert.Equal(2, result.UniquePlayers);
        Assert.Equal(1, normalizer.CallCount);
    }

    [Fact]
    public void AggregatorUsesCalculatedStarsWithoutApplyingHeuristics()
    {
        const double calculatedStars = 7.123456;
        var aggregator = new FarmMapAggregator(
            new ModNormalizer(clockRates),
            new ModMatcher(),
            new FixedStarRatingCalculator(calculatedStars));
        var date = DateTimeOffset.Parse("2026-01-01");

        var result = Assert.Single(aggregator.Aggregate(
            [Candidate(1, 300, .98, date, [new FarmMod("DT")])],
            new FarmFinderQuery(),
            1));

        Assert.Equal(calculatedStars, result.EffectiveStarRating, 6);
        Assert.True(result.HasCalculatedStarRating);
        Assert.DoesNotContain("base", result.EffectiveStarRatingText);
    }

    [Fact]
    public void AggregatorLabelsTheOfficialBaseRatingWhenCalculationIsUnavailable()
    {
        var aggregator = new FarmMapAggregator(
            new ModNormalizer(clockRates),
            new ModMatcher());
        var date = DateTimeOffset.Parse("2026-01-01");

        var result = Assert.Single(aggregator.Aggregate(
            [Candidate(1, 300, .98, date, [new FarmMod("DT")])],
            new FarmFinderQuery(),
            1));

        Assert.Equal(result.Beatmap.StarRating, result.EffectiveStarRating);
        Assert.False(result.HasCalculatedStarRating);
        Assert.EndsWith("base", result.EffectiveStarRatingText);
    }

    [Fact]
    public void ModMatcher_ImplementsNmRequiredAndExcluded()
    {
        var matcher = new ModMatcher();
        var empty = new NormalizedMods([], "NM", 1);
        var dt = new NormalizedMods(["DT"], "DT", 1.5);

        Assert.True(matcher.Matches(empty, new FarmFinderQuery
        {
            Mods = [new FarmModFilter("NM", ModRequirement.Required)],
        }));
        Assert.False(matcher.Matches(dt, new FarmFinderQuery
        {
            Mods = [new FarmModFilter("NM", ModRequirement.Required)],
        }));
        Assert.False(matcher.Matches(empty, new FarmFinderQuery
        {
            Mods = [new FarmModFilter("NM", ModRequirement.Excluded)],
        }));
    }

    [Fact]
    public void WildcardModIsIgnoredForNormalizationAndMatching()
    {
        var normalizer = new ModNormalizer(clockRates);
        var normalized = normalizer.Normalize(
            [new FarmMod("DT"), new FarmMod("HD")],
            new ModNormalizationOptions(
                TreatNightcoreAsDoubleTime: true,
                HiddenWildcard: false,
                WildcardMods: new HashSet<string>(["HD"], StringComparer.OrdinalIgnoreCase)));

        Assert.Equal(["DT"], normalized.Acronyms);

        var matcher = new ModMatcher();
        var query = new FarmFinderQuery
        {
            Mods =
            [
                new FarmModFilter("DT", ModRequirement.Required),
                new FarmModFilter("HD", ModRequirement.Wildcard),
            ],
            ExactModScope = ["DT", "HD"],
            ModMatchMode = ModMatchMode.Exact,
        };

        Assert.True(matcher.Matches(new NormalizedMods(["DT", "HD"], "DT+HD", 1.5), query));
        Assert.True(matcher.Matches(new NormalizedMods(["DT"], "DT", 1.5), query));
    }

    [Fact]
    public void WildcardModsAreNotSilentlyIncludedInTheDisplayedGroupsStarRating()
    {
        var calculator = new RecordingStarRatingCalculator(7.1);
        var aggregator = new FarmMapAggregator(
            new ModNormalizer(clockRates),
            new ModMatcher(),
            calculator);
        var query = new FarmFinderQuery
        {
            Mods =
            [
                new FarmModFilter("DT", ModRequirement.Required),
                new FarmModFilter("HD", ModRequirement.Wildcard),
            ],
        };

        var result = Assert.Single(aggregator.Aggregate(
            [Candidate(
                1,
                300,
                .98,
                DateTimeOffset.Parse("2026-01-01"),
                [new FarmMod("HD"), new FarmMod("NC", """{"adjust_pitch":true}""")])],
            query,
            1));

        Assert.Equal(["DT"], result.ModAcronyms);
        var requested = Assert.Single(calculator.Requests);
        var mod = Assert.Single(requested);
        Assert.Equal("DT", mod.Acronym);
        Assert.Equal("{}", mod.SettingsJson);
    }

    [Fact]
    public async Task AggregatorReportsExactStarCalculationProgress()
    {
        var aggregator = new FarmMapAggregator(
            new ModNormalizer(clockRates),
            new ModMatcher(),
            new FixedStarRatingCalculator(7.1));
        var first = Candidate(
            1,
            300,
            .98,
            DateTimeOffset.Parse("2026-01-01"),
            [new FarmMod("DT")]);
        var second = Candidate(
            2,
            310,
            .99,
            DateTimeOffset.Parse("2026-01-02"),
            [new FarmMod("DT")]);
        second = second with
        {
            Beatmap = second.Beatmap with { BeatmapId = 101 },
            Score = second.Score with { BeatmapId = 101 },
        };
        var progress = new CapturingProgress<FarmStarRatingProgress>();

        var results = await aggregator.AggregateAsync(
            [first, second],
            new FarmFinderQuery(),
            2,
            starRatingProgress: progress);

        Assert.Equal(2, results.Count);
        Assert.Equal(
            [
                new FarmStarRatingProgress(0, 2),
                new FarmStarRatingProgress(1, 2),
                new FarmStarRatingProgress(2, 2),
            ],
            progress.Values);
    }

    [Fact]
    public void ExactMatchingTreatsModsOutsideConfiguredScopeAsWildcards()
    {
        var matcher = new ModMatcher();
        var query = new FarmFinderQuery
        {
            Mods = [new FarmModFilter("DT", ModRequirement.Required)],
            ExactModScope = ["DT", "HD", "HR", "FL"],
            ModMatchMode = ModMatchMode.Exact,
        };

        Assert.True(matcher.Matches(
            new NormalizedMods(["DT", "CL", "SD"], "CL+DT+SD", 1.5),
            query));
        Assert.False(matcher.Matches(
            new NormalizedMods(["DT", "HR", "SD"], "DT+HR+SD", 1.5),
            query));

        var aggregator = new FarmMapAggregator(
            new ModNormalizer(clockRates),
            matcher);
        var result = Assert.Single(aggregator.Aggregate(
            [Candidate(
                1,
                300,
                .98,
                DateTimeOffset.Parse("2026-01-01"),
                [new FarmMod("DT"), new FarmMod("CL"), new FarmMod("SD")])],
            query,
            1));

        Assert.Equal("CL+DT+SD", result.NormalizedMods);
    }

    [Fact]
    public void Aggregation_HonorsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var aggregator = new FarmMapAggregator(new ModNormalizer(clockRates), new ModMatcher());

        Assert.Throws<OperationCanceledException>(() =>
            aggregator.Aggregate([Candidate(1, 1, 1, DateTimeOffset.UtcNow, [])],
                new FarmFinderQuery(), 1, cancellation.Token));
    }

    [Fact]
    public void AggregationCapsMaterializedResultsBeforeBuildingPlayerDetails()
    {
        var aggregator = new FarmMapAggregator(
            new ModNormalizer(clockRates),
            new ModMatcher());
        var candidates = Enumerable.Range(1, 600)
            .Select(index =>
            {
                var candidate = Candidate(
                    index,
                    300 + index,
                    .98,
                    DateTimeOffset.Parse("2026-01-01"),
                    [new FarmMod("DT")]);
                var beatmap = candidate.Beatmap with { BeatmapId = index };
                return candidate with
                {
                    Beatmap = beatmap,
                    Score = candidate.Score with { BeatmapId = index },
                };
            })
            .ToArray();

        var results = aggregator.Aggregate(
            candidates,
            new FarmFinderQuery { MaximumResults = 100 },
            scannedCohortSize: 600);

        Assert.Equal(100, results.Count);
        Assert.Equal(
            Enumerable.Range(1, 100).Select(index => (long)index),
            results.Select(result => result.Beatmap.BeatmapId));
    }

    private static FarmScoreCandidate Candidate(
        long userId,
        double pp,
        double accuracy,
        DateTimeOffset date,
        IReadOnlyList<FarmMod> mods,
        long? scoreId = null)
    {
        var player = new FarmPlayer(
            userId, $"Player {userId}", (int)userId, 10_000, date, date);
        var map = new FarmBeatmap(
            100, 10, "Artist", "Title", "Insane", "Mapper",
            180, 100, 120, 6.2, "ranked", date, "");
        var clockRate = new ClockRateCalculator().Calculate(mods);
        var normalized = new ModNormalizer(new ClockRateCalculator()).Normalize(
            mods, new ModNormalizationOptions(true, false));
        var score = new FarmScore(
            scoreId ?? userId,
            userId,
            map.BeatmapId,
            pp,
            accuracy,
            0,
            1000,
            true,
            date,
            mods,
            normalized.Signature,
            clockRate);
        return new FarmScoreCandidate(player, score, map);
    }

    private sealed class FixedStarRatingCalculator(double value) : IFarmStarRatingCalculator
    {
        public ValueTask<double?> CalculateAsync(
            FarmBeatmap beatmap,
            IReadOnlyList<FarmMod> mods,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<double?>(value);
    }

    private sealed class RecordingStarRatingCalculator(double value)
        : IFarmStarRatingCalculator
    {
        public List<IReadOnlyList<FarmMod>> Requests { get; } = [];

        public ValueTask<double?> CalculateAsync(
            FarmBeatmap beatmap,
            IReadOnlyList<FarmMod> mods,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(mods);
            return ValueTask.FromResult<double?>(value);
        }
    }

    private sealed class CountingModNormalizer(IModNormalizer inner) : IModNormalizer
    {
        public int CallCount { get; private set; }

        public NormalizedMods Normalize(
            IReadOnlyList<FarmMod> mods,
            ModNormalizationOptions options)
        {
            CallCount++;
            return inner.Normalize(mods, options);
        }
    }

    private sealed class CapturingProgress<T> : IProgress<T>
    {
        public List<T> Values { get; } = [];

        public void Report(T value) => Values.Add(value);
    }
}
