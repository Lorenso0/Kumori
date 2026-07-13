namespace Kumori.Core.Models;

public sealed record MapSummary
{
    public string MapKey { get; init; } = "";
    public long LastAttemptId { get; init; }
    public long? OsuBeatmapId { get; init; }
    public long? BeatmapSetId { get; init; }
    public string? Checksum { get; init; }
    public string Artist { get; init; } = "";
    public string Title { get; init; } = "";
    public string Difficulty { get; init; } = "";
    public string Mapper { get; init; } = "";
    public string LastStartedAt { get; init; } = "";
    public int PlayCount { get; init; }
    public int CompletedCount { get; init; }
    public double BestPp { get; init; }
    public double BestAccuracy { get; init; }
    public int BestCombo { get; init; }
    public double AverageAccuracy { get; init; }
    public double AveragePp { get; init; }
    public double AverageCombo { get; init; }
    public double? Stars { get; init; }
}
