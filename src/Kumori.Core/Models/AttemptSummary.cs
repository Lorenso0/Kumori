namespace Kumori.Core.Models;

/// <summary>GUI-facing row model for the history list (attempts joined with beatmaps).</summary>
public sealed record AttemptSummary
{
    public long Id { get; init; }
    public long SessionId { get; init; }
    public string StartedAt { get; init; } = "";
    public string? EndedAt { get; init; }
    public string Outcome { get; init; } = "active";
    public string? Grade { get; init; }
    public double Accuracy { get; init; }
    public long Score { get; init; }
    public double Pp { get; init; }
    public int Combo { get; init; }
    public int BeatmapMaxCombo { get; init; }
    public int Misses { get; init; }
    public int Key1Count { get; init; }
    public int Key2Count { get; init; }
    public string ModsKey { get; init; } = "NM";
    public IReadOnlyList<ModEntry> Mods { get; init; } = Array.Empty<ModEntry>();
    public string Artist { get; init; } = "";
    public string Title { get; init; } = "";
    public string Difficulty { get; init; } = "";
    public string Mapper { get; init; } = "";
    public double? Stars { get; init; }
    public double? AdjustedStars { get; init; }
    public double Progress { get; init; }
    public long? OsuBeatmapId { get; init; }
    public long? BeatmapSetId { get; init; }
    public string? Checksum { get; init; }
    public bool IsPersonalBest { get; init; }
    public bool IsMultiplayer { get; init; }
    public bool HasMovement { get; init; }
    public string? PlayerName { get; init; }
    public string? SharedByPlayerName { get; init; }
    public string? ImportedAt { get; init; }
    public string? LocalBeatmapPath { get; init; }
    public string? LocalBackgroundPath { get; init; }
    public bool IsImported => !string.IsNullOrWhiteSpace(SharedByPlayerName);
}
