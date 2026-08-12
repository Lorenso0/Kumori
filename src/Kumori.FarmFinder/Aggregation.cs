namespace Kumori.FarmFinder;

public sealed class FarmMapAggregator(
    IModNormalizer normalizer,
    IModMatcher matcher,
    IFarmStarRatingCalculator? starRatings = null) : IFarmMapAggregator
{
    public IReadOnlyList<FarmMapResult> Aggregate(
        IReadOnlyList<FarmScoreCandidate> candidates,
        FarmFinderQuery query,
        int scannedCohortSize,
        CancellationToken cancellationToken = default) =>
        AggregateAsync(candidates, query, scannedCohortSize, cancellationToken)
            .GetAwaiter()
            .GetResult();

    public async Task<IReadOnlyList<FarmMapResult>> AggregateAsync(
        IReadOnlyList<FarmScoreCandidate> candidates,
        FarmFinderQuery query,
        int scannedCohortSize,
        CancellationToken cancellationToken = default,
        IProgress<FarmStarRatingProgress>? starRatingProgress = null)
    {
        var wildcardMods = query.Mods
            .Where(filter => filter.Requirement == ModRequirement.Wildcard)
            .Select(filter => NormalizeAcronym(filter.Acronym, query))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var effectiveHiddenWildcard = query.HiddenWildcard
                                      && !query.Mods.Any(filter =>
                                          filter.Acronym.Equals("HD", StringComparison.OrdinalIgnoreCase)
                                          && filter.Requirement != ModRequirement.Ignore);
        if (effectiveHiddenWildcard)
            wildcardMods.Add("HD");
        var options = new ModNormalizationOptions(
            query.TreatNightcoreAsDoubleTime,
            false,
            wildcardMods);
        var groups = new Dictionary<GroupKey, Dictionary<long, Qualified>>();
        var normalizedModSets = new Dictionary<string, NormalizedMods>(
            StringComparer.Ordinal);

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!MatchesScalarFilters(candidate, query))
                continue;

            if (!normalizedModSets.TryGetValue(
                    candidate.Score.CanonicalModSignature,
                    out var normalized))
            {
                normalized = normalizer.Normalize(candidate.Score.ActualMods, options);
                normalizedModSets.Add(
                    candidate.Score.CanonicalModSignature,
                    normalized);
            }
            if (!matcher.Matches(normalized, query))
                continue;

            var effectiveBpm = candidate.Beatmap.BaseBpm * normalized.ClockRate;
            var effectiveLength = candidate.Beatmap.HitLengthSeconds / normalized.ClockRate;
            if (!InRange(effectiveBpm, query.MinimumEffectiveBpm, query.MaximumEffectiveBpm)
                || !InRange(effectiveLength, query.MinimumEffectiveLengthSeconds, query.MaximumEffectiveLengthSeconds))
                continue;

            var key = new GroupKey(candidate.Beatmap.BeatmapId, normalized.Signature, normalized.ClockRate);
            if (!groups.TryGetValue(key, out var players))
                groups[key] = players = [];

            var qualified = new Qualified(
                candidate,
                normalized,
                effectiveBpm,
                effectiveLength);
            if (!players.TryGetValue(candidate.Player.UserId, out var existing)
                || IsBetter(qualified.Candidate.Score, existing.Candidate.Score))
                players[candidate.Player.UserId] = qualified;
        }

        IEnumerable<KeyValuePair<GroupKey, Dictionary<long, Qualified>>> eligibleGroups =
            groups.Where(group => group.Value.Count >= query.MinimumUniquePlayers);
        var starsAffectSelection = query.SortField == FarmSortField.StarRating
                                   || query.MinimumStarRating is not null
                                   || query.MaximumStarRating is not null;
        if (query.SortField == FarmSortField.UniquePlayers && !starsAffectSelection)
        {
            eligibleGroups = query.SortDirection == FarmSortDirection.Ascending
                ? eligibleGroups.OrderBy(group => group.Value.Count)
                    .ThenBy(group => group.Key.BeatmapId)
                    .ThenBy(group => group.Key.Signature, StringComparer.Ordinal)
                    .ThenBy(group => group.Key.ClockRate)
                : eligibleGroups.OrderByDescending(group => group.Value.Count)
                    .ThenBy(group => group.Key.BeatmapId)
                    .ThenBy(group => group.Key.Signature, StringComparer.Ordinal)
                    .ThenBy(group => group.Key.ClockRate);
        }

        var groupsToMaterialize = (query.SortField == FarmSortField.UniquePlayers
                                   && !starsAffectSelection
                ? eligibleGroups.Take(query.MaximumResults)
                : eligibleGroups)
            .ToArray();
        var completedRatings = 0;
        var progressLock = new object();
        starRatingProgress?.Report(new FarmStarRatingProgress(
            completedRatings,
            groupsToMaterialize.Length));
        var ratedGroups = await Task.WhenAll(groupsToMaterialize.Select(async entry =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var first = entry.Value.Values.First();
                double? calculated = null;
                if (starRatings is not null)
                {
                    var difficultyMods = DifficultyModsForGroup(
                        first.Candidate.Score.ActualMods,
                        wildcardMods,
                        query);
                    calculated = await starRatings.CalculateAsync(
                        first.Candidate.Beatmap,
                        difficultyMods,
                        cancellationToken);
                    if (calculated is not > 0 || !double.IsFinite(calculated.Value))
                        calculated = null;
                }
                var effective = calculated ?? first.Candidate.Beatmap.StarRating;
                return InRange(effective, query.MinimumStarRating, query.MaximumStarRating)
                    ? new RatedGroup(entry, calculated)
                    : null;
            }
            finally
            {
                lock (progressLock)
                {
                    completedRatings++;
                    starRatingProgress?.Report(new FarmStarRatingProgress(
                        completedRatings,
                        groupsToMaterialize.Length));
                }
            }
        }));
        var results = new List<FarmMapResult>(
            query.SortField == FarmSortField.UniquePlayers
                ? Math.Min(groups.Count, query.MaximumResults)
                : groups.Count);
        foreach (var rated in ratedGroups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (rated is null)
                continue;
            var entry = rated.Entry;
            var group = entry.Value;
            var values = group.Values.ToArray();
            var first = values[0];
            var scores = values.Select(value => value.Candidate.Score).ToArray();
            var details = values.Select(value => new FarmScoreDetail(
                    value.Candidate.Player.UserId,
                    value.Candidate.Player.Username,
                    value.Candidate.Player.GlobalRank,
                    value.Candidate.Score.ScoreId,
                    value.Candidate.Score.Pp,
                    value.Candidate.Score.Accuracy,
                    value.Candidate.Score.MissCount,
                    value.Candidate.Score.MaxCombo,
                    value.Candidate.Score.IsFullCombo,
                    value.Candidate.Score.EndedAt,
                    value.Candidate.Score.ActualMods)
            {
                BeatmapId = value.Candidate.Score.BeatmapId,
                Origin = value.Candidate.Score.Origin,
                LegacyScoreId = value.Candidate.Score.LegacyScoreId,
                TotalScore = value.Candidate.Score.TotalScore,
                LegacyTotalScore = value.Candidate.Score.LegacyTotalScore,
                BuildId = value.Candidate.Score.BuildId,
                SourceType = value.Candidate.Score.SourceType,
            })
                .OrderByDescending(detail => detail.Pp)
                .ThenByDescending(detail => detail.Accuracy)
                .ThenByDescending(detail => detail.ScoreDate)
                .ThenBy(detail => detail.UserId)
                .Select((detail, index) => detail with
                {
                    LeaderboardRank = index + 1,
                })
                .ToArray();

            var fullCombos = scores.Count(score => score.IsFullCombo);
            results.Add(new FarmMapResult
            {
                Beatmap = first.Candidate.Beatmap,
                NormalizedMods = first.Normalized.Signature,
                ModAcronyms = first.Normalized.Acronyms.Count == 0
                    ? ["NM"]
                    : first.Normalized.Acronyms,
                ClockRate = first.Normalized.ClockRate,
                UniquePlayers = values.Length,
                CohortPercentage = scannedCohortSize == 0 ? 0 : 100d * values.Length / scannedCohortSize,
                AveragePp = scores.Average(score => score.Pp),
                MinimumPp = scores.Min(score => score.Pp),
                MaximumPp = scores.Max(score => score.Pp),
                EffectiveBpm = first.EffectiveBpm,
                EffectiveLengthSeconds = first.EffectiveLength,
                AdjustedStarRating = rated.CalculatedStars,
                MedianAccuracy = Median(scores.Select(score => score.Accuracy)),
                AverageMissCount = scores.Average(score => score.MissCount),
                FullComboCount = fullCombos,
                FullComboPercentage = 100d * fullCombos / scores.Length,
                MedianPlayerRank = Median(values.Select(value => (double)value.Candidate.Player.GlobalRank)),
                EarliestScoreDate = scores.Min(score => score.EndedAt),
                MostRecentScoreDate = scores.Max(score => score.EndedAt),
                Players = details,
            });
        }

        return Sort(results, query)
            .Take(query.MaximumResults)
            .ToArray();
    }

    private static bool MatchesScalarFilters(FarmScoreCandidate candidate, FarmFinderQuery query)
    {
        if (!InRange(candidate.Player.GlobalRank, query.MinimumGlobalRank, query.MaximumGlobalRank)
            || !InRange(candidate.Score.Pp, query.MinimumPp, query.MaximumPp))
            return false;
        if (query.RankedFrom is { } from &&
            (candidate.Beatmap.RankedAt is null || candidate.Beatmap.RankedAt.Value < from))
            return false;
        if (query.RankedTo is { } to &&
            (candidate.Beatmap.RankedAt is null || candidate.Beatmap.RankedAt.Value > to))
            return false;
        if (query.MapStatus != FarmMapStatus.Any
            && !candidate.Beatmap.Status.Equals(query.MapStatus.ToString(), StringComparison.OrdinalIgnoreCase))
            return false;
        if (string.IsNullOrWhiteSpace(query.TextSearch))
            return true;
        var text = query.TextSearch.Trim();
        return candidate.Beatmap.Artist.Contains(text, StringComparison.OrdinalIgnoreCase)
               || candidate.Beatmap.Title.Contains(text, StringComparison.OrdinalIgnoreCase)
               || candidate.Beatmap.Difficulty.Contains(text, StringComparison.OrdinalIgnoreCase)
               || candidate.Beatmap.Mapper.Contains(text, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBetter(FarmScore candidate, FarmScore existing) =>
        candidate.Pp > existing.Pp
        || candidate.Pp.Equals(existing.Pp) && candidate.Accuracy > existing.Accuracy
        || candidate.Pp.Equals(existing.Pp) && candidate.Accuracy.Equals(existing.Accuracy)
        && candidate.EndedAt > existing.EndedAt;

    private static double Median(IEnumerable<double> source)
    {
        var values = source.Order().ToArray();
        if (values.Length == 0)
            return 0;
        var middle = values.Length / 2;
        return values.Length % 2 == 0 ? (values[middle - 1] + values[middle]) / 2 : values[middle];
    }

    private static bool InRange<T>(T value, T? minimum, T? maximum)
        where T : struct, IComparable<T> =>
        (minimum is null || value.CompareTo(minimum.Value) >= 0)
        && (maximum is null || value.CompareTo(maximum.Value) <= 0);

    private static IOrderedEnumerable<FarmMapResult> Sort(
        IEnumerable<FarmMapResult> results,
        FarmFinderQuery query)
    {
        Func<FarmMapResult, object?> selector = query.SortField switch
        {
            FarmSortField.CohortPercentage => result => result.CohortPercentage,
            FarmSortField.EffectiveBpm => result => result.EffectiveBpm,
            FarmSortField.EffectiveLength => result => result.EffectiveLengthSeconds,
            FarmSortField.AveragePp => result => result.AveragePp,
            FarmSortField.StarRating => result => result.EffectiveStarRating,
            FarmSortField.MedianAccuracy => result => result.MedianAccuracy,
            FarmSortField.FcPercentage => result => result.FullComboPercentage,
            FarmSortField.RankedDate => result => result.Beatmap.RankedAt,
            FarmSortField.ArtistTitle => result => $"{result.Beatmap.Artist}\u001f{result.Beatmap.Title}",
            _ => result => result.UniquePlayers,
        };
        var comparer = Comparer<object?>.Create(CompareObjects);
        var ordered = query.SortDirection == FarmSortDirection.Ascending
            ? results.OrderBy(selector, comparer)
            : results.OrderByDescending(selector, comparer);
        return ordered.ThenBy(result => result.Beatmap.BeatmapId)
                      .ThenBy(result => result.NormalizedMods, StringComparer.Ordinal)
                      .ThenBy(result => result.ClockRate);
    }

    private static int CompareObjects(object? left, object? right)
    {
        if (ReferenceEquals(left, right)) return 0;
        if (left is null) return -1;
        if (right is null) return 1;
        if (left is string leftString && right is string rightString)
            return StringComparer.OrdinalIgnoreCase.Compare(leftString, rightString);
        return ((IComparable)left).CompareTo(right);
    }

    private static string NormalizeAcronym(string acronym, FarmFinderQuery query)
    {
        var value = acronym.Trim().ToUpperInvariant();
        if (query.TreatNightcoreAsDoubleTime && value == "NC")
            return "DT";
        return value;
    }

    private static IReadOnlyList<FarmMod> DifficultyModsForGroup(
        IReadOnlyList<FarmMod> actualMods,
        IReadOnlySet<string> wildcardMods,
        FarmFinderQuery query)
    {
        var result = new SortedDictionary<string, FarmMod>(StringComparer.Ordinal);
        foreach (var source in actualMods)
        {
            var acronym = NormalizeAcronym(source.NormalizedAcronym, query);
            if (acronym.Length == 0
                || acronym == "NM"
                || wildcardMods.Contains(acronym))
                continue;
            var settings = FarmFinderValidation.CanonicalJson(
                source.SettingsJson,
                "adjust_pitch");
            if (!result.TryGetValue(acronym, out var existing)
                || existing.SettingsJson == "{}" && settings != "{}")
                result[acronym] = new FarmMod(acronym, settings);
        }
        return result.Values.ToArray();
    }

    private readonly record struct GroupKey(long BeatmapId, string Signature, double ClockRate);
    private sealed record RatedGroup(
        KeyValuePair<GroupKey, Dictionary<long, Qualified>> Entry,
        double? CalculatedStars);
    private sealed record Qualified(
        FarmScoreCandidate Candidate,
        NormalizedMods Normalized,
        double EffectiveBpm,
        double EffectiveLength);
}
