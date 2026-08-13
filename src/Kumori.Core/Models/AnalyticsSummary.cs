namespace Kumori.Core.Models;

public sealed record AnalyticsSummary
{
    public long Attempts { get; init; }
    public long Completed { get; init; }
    public long Failed { get; init; }
    public double AverageAccuracy { get; init; }
    public double BestPp { get; init; }
    public long TotalScore { get; init; }
    public double TotalDurationSeconds { get; init; }
    public long ZTotal { get; init; }
    public long XTotal { get; init; }
    public long TotalMisses { get; init; }
    public string Key1Binding { get; init; } = "Z";
    public string Key2Binding { get; init; } = "X";
    public string? LastSyncedAt { get; init; }
    public AccountChangeSummary? LatestAccountChange { get; init; }
    public IReadOnlyList<DailyAttemptTrend> Daily { get; init; } = Array.Empty<DailyAttemptTrend>();
}

public sealed record DailyAttemptTrend
{
    public string Day { get; init; } = "";
    public long Attempts { get; init; }
    public long Completed { get; init; }
    public double AverageAccuracy { get; init; }
    public double BestPp { get; init; }
    public double TotalDurationSeconds { get; init; }
    public long ZTotal { get; init; }
    public long XTotal { get; init; }
    public long TotalMisses { get; init; }
    public long DistinctMaps { get; init; }
    public long TotalScore { get; init; }
    public double? PpChange { get; init; }
    /// <summary>Positive when global ranks were gained; negative when ranks were lost.</summary>
    public long? RankChange { get; init; }
}

public sealed record DailyProgressReport
{
    public required DailyAttemptTrend Summary { get; init; }
    public string PlayerName { get; init; } = "";
    public DailyAccountProgress? Account { get; init; }
    public DailyMapHighlight? MostPlayedMap { get; init; }
    public DailyPlayHighlight? BestPlay { get; init; }
    public IReadOnlyList<DailyModCombinationUsage> MostUsedModCombinations { get; init; }
        = Array.Empty<DailyModCombinationUsage>();
}

public sealed record DailyAccountProgress
{
    public long PlayerId { get; init; }
    public string PlayerName { get; init; } = "";
    public string? CountryCode { get; init; }
    public double? OldTotalPp { get; init; }
    public double? NewTotalPp { get; init; }
    public long? OldGlobalRank { get; init; }
    public long? NewGlobalRank { get; init; }
    public long? OldCountryRank { get; init; }
    public long? NewCountryRank { get; init; }
    public long? OldPlayCount { get; init; }
    public long? NewPlayCount { get; init; }
}

public sealed record DailyMapHighlight
{
    public long BeatmapId { get; init; }
    public long BeatmapSetId { get; init; }
    public string Artist { get; init; } = "";
    public string Title { get; init; } = "";
    public string Difficulty { get; init; } = "";
    public long Plays { get; init; }
    public double? Stars { get; init; }
    public double? Ar { get; init; }
    public double? Od { get; init; }
    public double? Cs { get; init; }
    public double? Bpm { get; init; }
}

public sealed record DailyPlayHighlight
{
    public long BeatmapId { get; init; }
    public long BeatmapSetId { get; init; }
    public string Artist { get; init; } = "";
    public string Title { get; init; } = "";
    public string Difficulty { get; init; } = "";
    public double Pp { get; init; }
    public double Accuracy { get; init; }
    public long Combo { get; init; }
    public long MaxCombo { get; init; }
    public long N100 { get; init; }
    public long N50 { get; init; }
    public long Misses { get; init; }
    public long SliderBreaks { get; init; }
    public string ModsKey { get; init; } = "NM";
    public double? BaseStars { get; init; }
    public double? AdjustedStars { get; init; }
    public double? BaseAr { get; init; }
    public double? AdjustedAr { get; init; }
    public double? BaseOd { get; init; }
    public double? AdjustedOd { get; init; }
    public double? BaseCs { get; init; }
    public double? AdjustedCs { get; init; }
    public double? BaseBpm { get; init; }
    public double? Bpm { get; init; }
    public bool UsedBpmAdjust { get; init; }
}

public sealed record DailyModCombinationUsage
{
    public string ModsKey { get; init; } = "NM";
    public double? Bpm { get; init; }
    public long Plays { get; init; }
}

public sealed record AccountChangeSummary
{
    public double? OldTotalPp { get; init; }
    public double? NewTotalPp { get; init; }
    public long? OldGlobalRank { get; init; }
    public long? NewGlobalRank { get; init; }
    public double? OldAccuracy { get; init; }
    public double? NewAccuracy { get; init; }
    public long? OldPlayCount { get; init; }
    public long? NewPlayCount { get; init; }
}
