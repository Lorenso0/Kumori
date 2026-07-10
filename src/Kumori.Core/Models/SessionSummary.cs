namespace Kumori.Core.Models;

public sealed record SessionSummary
{
    public long Id { get; init; }
    public string StartedAt { get; init; } = "";
    public string? EndedAt { get; init; }
    public double ActiveSeconds { get; init; }
    public string? PlayerName { get; init; }
    public bool Interrupted { get; init; }
    public bool Legacy { get; init; }
    public int AttemptCount { get; init; }
    public int CompletedCount { get; init; }
    public int ZCount { get; init; }
    public int XCount { get; init; }
    public string Key1Binding { get; init; } = "Z";
    public string Key2Binding { get; init; } = "X";
    public double BestPp { get; init; }
    public int TotalMisses { get; init; }
    public double AverageUr { get; init; }
    public double AccountPpGain { get; init; }
}
