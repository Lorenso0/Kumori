using System.Text.Json;
using System.Numerics;

namespace Kumori.FarmFinder;

public enum ModRequirement
{
    Ignore,
    Required,
    Excluded,
    Wildcard,
}

public enum ModMatchMode
{
    ContainsRequired,
    Exact,
}

public enum FarmMapStatus
{
    Any,
    Ranked,
    Approved,
    Loved,
}

public enum FarmSortField
{
    UniquePlayers,
    CohortPercentage,
    EffectiveBpm,
    EffectiveLength,
    AveragePp,
    MedianPp = AveragePp,
    StarRating,
    MedianAccuracy,
    FcPercentage,
    RankedDate,
    ArtistTitle,
}

public enum FarmSortDirection
{
    Ascending,
    Descending,
}

public enum FarmFinderProgressPhase
{
    SearchingCache,
    DiscoveringPlayers,
    FetchingScores,
    AggregatingResults,
    CalculatingStars,
    Completed,
}

public sealed record FarmModFilter(string Acronym, ModRequirement Requirement);

public sealed record FarmMod(string Acronym, string SettingsJson = "{}")
{
    public string NormalizedAcronym => Acronym.Trim().ToUpperInvariant();
}

public sealed record FarmFinderQuery
{
    public int? MinimumGlobalRank { get; init; }
    public int? MaximumGlobalRank { get; init; }
    public double? MinimumPp { get; init; }
    public double? MaximumPp { get; init; }
    public double? MinimumEffectiveBpm { get; init; }
    public double? MaximumEffectiveBpm { get; init; }
    public double? MinimumEffectiveLengthSeconds { get; init; }
    public double? MaximumEffectiveLengthSeconds { get; init; }
    public double? MinimumStarRating { get; init; }
    public double? MaximumStarRating { get; init; }
    public int MinimumUniquePlayers { get; init; } = 1;
    public FarmMapStatus MapStatus { get; init; } = FarmMapStatus.Any;
    public string TextSearch { get; init; } = string.Empty;
    public DateTimeOffset? RankedFrom { get; init; }
    public DateTimeOffset? RankedTo { get; init; }
    public IReadOnlyList<FarmModFilter> Mods { get; init; } = [];
    public IReadOnlyList<string> ExactModScope { get; init; } = [];
    public ModMatchMode ModMatchMode { get; init; } = ModMatchMode.ContainsRequired;
    public bool TreatNightcoreAsDoubleTime { get; init; } = true;
    public bool HiddenWildcard { get; init; }
    public FarmSortField SortField { get; init; } = FarmSortField.UniquePlayers;
    public FarmSortDirection SortDirection { get; init; } = FarmSortDirection.Descending;
    public int MaximumResults { get; init; } = 500;
}

public sealed record FarmPlayer(
    long UserId,
    string Username,
    int GlobalRank,
    double TotalPp,
    DateTimeOffset RankUpdatedAt,
    DateTimeOffset? ScoresUpdatedAt = null);

public sealed record FarmBeatmap(
    long BeatmapId,
    long BeatmapSetId,
    string Artist,
    string Title,
    string Difficulty,
    string Mapper,
    double BaseBpm,
    int HitLengthSeconds,
    int TotalLengthSeconds,
    double StarRating,
    string Status,
    DateTimeOffset? RankedAt,
    string CoverUrl);

public sealed record FarmScore(
    long ScoreId,
    long UserId,
    long BeatmapId,
    double Pp,
    double Accuracy,
    int MissCount,
    int MaxCombo,
    bool IsFullCombo,
    DateTimeOffset EndedAt,
    IReadOnlyList<FarmMod> ActualMods,
    string CanonicalModSignature,
    double ClockRate);

public sealed record FarmScoreCandidate(FarmPlayer Player, FarmScore Score, FarmBeatmap Beatmap);

public sealed record FarmScoreDetail(
    long UserId,
    string Username,
    int GlobalRank,
    long ScoreId,
    double Pp,
    double Accuracy,
    int MissCount,
    int MaxCombo,
    bool IsFullCombo,
    DateTimeOffset ScoreDate,
    IReadOnlyList<FarmMod> ActualMods)
{
    /// <summary>Difficulty id for the played beatmap when available.</summary>
    public long? BeatmapId { get; init; }

    public string ModsText => ActualMods.Count == 0
        ? "NM"
        : string.Concat(ActualMods.Select(mod => mod.NormalizedAcronym));
    public string PlayerUrl => $"https://osu.ppy.sh/users/{UserId}";
    public string ScoreUrl => $"https://osu.ppy.sh/scores/{ScoreId}";
    public string? BeatmapUrl => BeatmapId is { } id
        ? $"https://osu.ppy.sh/beatmaps/{id}"
        : null;
}

public sealed record FarmMapResult
{
    public required FarmBeatmap Beatmap { get; init; }
    public required string NormalizedMods { get; init; }
    public IReadOnlyList<string> ModAcronyms { get; init; } = [];
    public required double ClockRate { get; init; }
    public required int UniquePlayers { get; init; }
    public required double CohortPercentage { get; init; }
    public required double AveragePp { get; init; }
    public required double MinimumPp { get; init; }
    public required double MaximumPp { get; init; }
    public required double EffectiveBpm { get; init; }
    public required double EffectiveLengthSeconds { get; init; }
    public double? AdjustedStarRating { get; init; }
    public required double MedianAccuracy { get; init; }
    public required double AverageMissCount { get; init; }
    public required int FullComboCount { get; init; }
    public required double FullComboPercentage { get; init; }
    public required double MedianPlayerRank { get; init; }
    public required DateTimeOffset EarliestScoreDate { get; init; }
    public required DateTimeOffset MostRecentScoreDate { get; init; }
    public required IReadOnlyList<FarmScoreDetail> Players { get; init; }

    public string BeatmapUrl => $"https://osu.ppy.sh/beatmaps/{Beatmap.BeatmapId}";
    public string EffectiveBpmText => $"{EffectiveBpm:0.#} BPM";
    public string BaseBpmText => $"Base: {Beatmap.BaseBpm:0.#} BPM";
    public string EffectiveLengthText => TimeSpan.FromSeconds(Math.Round(EffectiveLengthSeconds)).ToString(@"m\:ss");
    public string BaseLengthText => $"Base hit length: {TimeSpan.FromSeconds(Beatmap.HitLengthSeconds):m\\:ss}";
    public string PpRangeText => $"{MinimumPp:0.##}–{MaximumPp:0.##}pp";
    public bool HasCalculatedStarRating =>
        AdjustedStarRating is > 0 && double.IsFinite(AdjustedStarRating.Value);
    public double EffectiveStarRating =>
        HasCalculatedStarRating ? AdjustedStarRating!.Value : Beatmap.StarRating;
    public string EffectiveStarRatingText => HasCalculatedStarRating
        ? $"{EffectiveStarRating:0.##} ★"
        : $"{Beatmap.StarRating:0.##} ★ base";
}

public sealed record CoverageSummary(
    int AvailablePlayers,
    int ScannedPlayers,
    int CachedPlayers,
    int FetchedPlayers,
    int FailedPlayers,
    int ScoresExamined,
    int MatchingScores,
    int ResultingMaps,
    DateTimeOffset? LastUpdatedAt,
    int? CoveredMinimumRank,
    int? CoveredMaximumRank,
    IReadOnlyList<CountryCoverageGap>? CountryGaps = null)
{
    public bool IsPartial => FailedPlayers > 0
                             || CoveredMinimumRank is null
                             || CoveredMaximumRank is null
                             || CountryGaps is { Count: > 0 };
}

public sealed record CountryCoverage(
    string CountryCode,
    int CoveredThroughGlobalRank,
    int RequestedMaximumRank,
    bool IsComplete,
    bool HitApiLimit);

public sealed record CountryCoverageGap(
    string CountryCode,
    int CoveredThroughGlobalRank,
    int RequestedMaximumRank);

public sealed record FarmFinderSearchResult(
    IReadOnlyList<FarmMapResult> Results,
    CoverageSummary Coverage);

public sealed record FarmCacheDownloadProgress(
    long BytesReceived,
    long TotalBytes,
    string Text,
    double BytesPerSecond = 0,
    TimeSpan? EstimatedRemaining = null,
    string? Detail = null);

public sealed record FarmCacheInstallResult(
    long BytesInstalled,
    string Sha256,
    int SchemaVersion,
    DateTimeOffset GeneratedAt,
    bool PreviousCacheRetained);

public sealed record FarmFinderProgress(
    int Current,
    int Total,
    string Text,
    int PlayersLoadedFromCache = 0,
    int PlayersFetched = 0,
    int ScoresExamined = 0,
    int MatchingScores = 0,
    int ResultCount = 0,
    DateTimeOffset? RateLimitedUntil = null,
    int PlayersFailed = 0,
    FarmFinderProgressPhase Phase = FarmFinderProgressPhase.SearchingCache,
    string SourceName = "");

public sealed record FarmStarRatingProgress(
    int Completed,
    int Total);

public sealed record RankingPage(
    IReadOnlyList<FarmPlayer> Players,
    string? NextCursorJson,
    int? Total);

public sealed record PlayerScoresPayload(
    FarmPlayer Player,
    IReadOnlyList<FarmScore> Scores,
    IReadOnlyList<FarmBeatmap> Beatmaps);

public sealed record IndexJob(
    long Id,
    int MinimumRank,
    int MaximumRank,
    string Status,
    string? CursorJson,
    int PlayersTotal,
    int PlayersCompleted,
    int PlayersFailed,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt);

public sealed record RankedModDescriptor(string Acronym, string Name);

public sealed record RankedModEvaluation(
    bool IsEligible,
    string Acronym,
    string CanonicalSettingsJson,
    double? ClockRate = null);

public sealed record NormalizedMods(
    IReadOnlyList<string> Acronyms,
    string Signature,
    double ClockRate);

public sealed record ModNormalizationOptions(
    bool TreatNightcoreAsDoubleTime,
    bool HiddenWildcard,
    IReadOnlySet<string>? WildcardMods = null);

public sealed record OsuApiCredentials(long ClientId, string ClientSecret)
{
    public bool IsConfigured => ClientId > 0 && !string.IsNullOrWhiteSpace(ClientSecret);
}

public sealed class OsuApiAuthenticationException(string message) : Exception(message);

public sealed class OsuApiRateLimitException(string message) : Exception(message);

public static class OsuApiLimits
{
    public const int PerformanceRankingPageSize = 50;
    public const int MaximumPerformanceRankingEntries = 10_000;
}

public static class FarmFinderValidation
{
    public static IReadOnlyList<string> Validate(FarmFinderQuery query)
    {
        var errors = new List<string>();
        ValidateRange(query.MinimumGlobalRank, query.MaximumGlobalRank, "Global rank", errors);
        ValidateRange(query.MinimumPp, query.MaximumPp, "PP", errors);
        ValidateRange(query.MinimumEffectiveBpm, query.MaximumEffectiveBpm, "BPM", errors);
        ValidateRange(query.MinimumEffectiveLengthSeconds, query.MaximumEffectiveLengthSeconds, "Hit length", errors);
        ValidateRange(query.MinimumStarRating, query.MaximumStarRating, "Star rating", errors);
        if (query.MinimumUniquePlayers < 1)
            errors.Add("Minimum unique players must be at least 1.");
        if (query.MaximumResults is < 1 or > 5_000)
            errors.Add("Maximum results must be between 1 and 5,000.");
        if (query.RankedFrom is { } from && query.RankedTo is { } to && from > to)
            errors.Add("Ranked-from date must be before ranked-to date.");
        return errors;
    }

    public static IReadOnlyList<string> ValidateIndexUpdate(FarmFinderQuery query)
    {
        var errors = Validate(query).ToList();
        if (query.MinimumGlobalRank is null || query.MaximumGlobalRank is null)
        {
            errors.Add("Both global-rank bounds are required to update a rank range.");
        }
        else
        {
            if (query.MinimumGlobalRank < 1)
                errors.Add("Global rank minimum must be at least 1 for an index update.");
        }
        return errors;
    }

    private static void ValidateRange<T>(T? minimum, T? maximum, string name, ICollection<string> errors)
        where T : struct, INumber<T>
    {
        if (minimum is { } min && min < T.Zero)
            errors.Add($"{name} minimum cannot be negative.");
        if (maximum is { } max && max < T.Zero)
            errors.Add($"{name} maximum cannot be negative.");
        if (minimum is { } lower && maximum is { } upper && lower > upper)
            errors.Add($"{name} minimum must be less than or equal to its maximum.");
    }

    public static string CanonicalJson(string? json, params string[] ignoredProperties)
    {
        if (string.IsNullOrWhiteSpace(json))
            return "{}";
        try
        {
            using var document = JsonDocument.Parse(json);
            var ignored = new HashSet<string>(ignoredProperties, StringComparer.OrdinalIgnoreCase);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
                WriteCanonical(document.RootElement, writer, ignored);
            return System.Text.Encoding.UTF8.GetString(stream.ToArray());
        }
        catch (JsonException)
        {
            return json.Trim();
        }
    }

    private static void WriteCanonical(JsonElement element, Utf8JsonWriter writer, ISet<string> ignored)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject()
                                                .Where(property => !ignored.Contains(property.Name))
                                                .OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(property.Value, writer, ignored);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var child in element.EnumerateArray())
                    WriteCanonical(child, writer, ignored);
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}
