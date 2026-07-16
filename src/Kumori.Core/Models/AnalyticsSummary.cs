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
    public double? PpChange { get; init; }
    /// <summary>Positive when global ranks were gained; negative when ranks were lost.</summary>
    public long? RankChange { get; init; }
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
