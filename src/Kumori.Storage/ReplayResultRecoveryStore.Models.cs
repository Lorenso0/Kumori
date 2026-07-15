namespace Kumori.Storage;

/// <summary>Result values which are stored in an osu! replay header.</summary>
public sealed record ReplayResultData(
    long Score,
    double Accuracy,
    string? Grade,
    int Combo,
    int N300,
    int N100,
    int N50,
    int Misses,
    int Geki,
    int Katu,
    int LargeTickHits = 0,
    int LargeTickMisses = 0,
    int SmallTickHits = 0,
    int SmallTickMisses = 0,
    int SliderTailHits = 0,
    int SliderTailMisses = 0);

public sealed record ReplayResultRecoveryOutcome(
    bool AttemptReady,
    bool Applied,
    IReadOnlyList<string> RecoveredFields)
{
    public static ReplayResultRecoveryOutcome NotReady { get; } = new(false, false, []);
    public static ReplayResultRecoveryOutcome NoChanges { get; } = new(true, false, []);
}

