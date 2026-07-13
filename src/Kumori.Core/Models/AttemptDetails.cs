namespace Kumori.Core.Models;

/// <summary>
/// Full inspector model for a single attempt. Loaded on selection only —
/// never as part of the history list query.
/// </summary>
public sealed record AttemptDetails
{
    public AttemptSummary Summary { get; init; } = new();

    // Hit results (attempts row)
    public int N300 { get; init; }
    public int N100 { get; init; }
    public int N50 { get; init; }
    public int Geki { get; init; }
    public int Katu { get; init; }
    public int SliderBreaks { get; init; }
    public double UnstableRate { get; init; }
    public double FcPp { get; init; }
    public double MaxPp { get; init; }
    public double DurationSeconds { get; init; }
    public string? TerminationEvidence { get; init; }
    public int Key1Count { get; init; }
    public int Key2Count { get; init; }
    public string Key1Binding { get; init; } = "Z";
    public string Key2Binding { get; init; } = "X";

    // Slider tick / tail detail (attempts row).
    public int LargeTickHits { get; init; }
    public int LargeTickMisses { get; init; }
    public int SmallTickHits { get; init; }
    public int SmallTickMisses { get; init; }
    public int SliderTailHits { get; init; }
    public int SliderTailMisses { get; init; }

    // Difficulty snapshot (attempts + beatmaps rows).
    public double? BaseStars { get; init; }
    public double? AdjustedStars { get; init; }
    public string Mapper { get; init; } = "";
    public double? BeatmapAr { get; init; }
    public double? BeatmapCs { get; init; }
    public double? BeatmapOd { get; init; }
    public double? BeatmapHp { get; init; }
    public double? Bpm { get; init; }
    public int BeatmapMaxCombo { get; init; }

    public IReadOnlyList<ModEntry> Mods { get; init; } = Array.Empty<ModEntry>();
    public TimingSummary? Timing { get; init; }
    public IReadOnlyList<JudgementEvent> Events { get; init; } = Array.Empty<JudgementEvent>();
    public InputSummary? Input { get; init; }
    public MovementSummary? Movement { get; init; }
    public string? LocalBeatmapPath { get; init; }
    public string? LocalMediaDirectory { get; init; }
    public string ClientKind { get; init; } = "unknown";
    public bool ResultRecoveredFromReplay { get; init; }
    public string? ResultRecoverySource { get; init; }
    public bool ResultRecoverySimulationCompleted { get; init; }

    /// <summary>
    /// Per-attempt captured map values from attempt_context.beatmap_json, keyed
    /// by "ar"/"cs"/"od"/"hp"/"stars"/"bpm". Each pair is (original, converted).
    /// </summary>
    public IReadOnlyDictionary<string, DifficultyPair> CapturedDifficulty { get; init; }
        = new Dictionary<string, DifficultyPair>();
}

/// <summary>An (original, mod-converted) pair for a single difficulty stat.</summary>
public readonly record struct DifficultyPair(double? Original, double? Converted);

/// <summary>attempt_movement summary row.</summary>
public sealed record MovementSummary
{
    public bool Available { get; init; }
    public string? Source { get; init; }
    public double SampleRate { get; init; }
    public int SampleCount { get; init; }
    public int DroppedSamples { get; init; }
}

public sealed record ModEntry(string Acronym, string SettingsJson);

/// <summary>attempt_timing row; Offsets decoded from the zlib JSON blob on demand.</summary>
public sealed record TimingSummary
{
    public int HitCount { get; init; }
    public int EarlyCount { get; init; }
    public int LateCount { get; init; }
    public double Mean { get; init; }
    public double Median { get; init; }
    public double Deviation { get; init; }
    public IReadOnlyList<double> Offsets { get; init; } = Array.Empty<double>();
}

/// <summary>
/// attempt_events row. Semantics (from tosu_stats.py): hit_100/hit_50 are one
/// row per packet increase with cumulative Value and Delta in data; miss and
/// slider_break are per-increment rows; combo/pp_peak track running peaks.
/// </summary>
public sealed record JudgementEvent
{
    public long Id { get; init; }
    public string EventType { get; init; } = "";
    public long? MapTimeMs { get; init; }
    public double? Value { get; init; }
    public string DataJson { get; init; } = "{}";
}

public sealed record InputSummary
{
    public int Key1Presses { get; init; }
    public int Key2Presses { get; init; }
    public int Alternations { get; init; }
    public int SimultaneousPresses { get; init; }
    public double Key1HoldMs { get; init; }
    public double Key2HoldMs { get; init; }
    public int PeakKps { get; init; }
    public double AverageKps { get; init; }
}

public sealed record AttemptTrendSummary
{
    public long Id { get; init; }
    public double Accuracy { get; init; }
    public int N100 { get; init; }
    public int N50 { get; init; }
    public int Misses { get; init; }
    public int SliderBreaks { get; init; }
    public double? MeanOffset { get; init; }
}
