using System.Text.Json;

namespace Kumori.ReplayViewer;

internal sealed record PreparedReplayAnalysis(
    int Version,
    long AttemptId,
    IReadOnlyList<PreparedReplayJudgement> Judgements,
    IReadOnlyList<PreparedReplayFrame> Frames,
    PreparedReplaySimulationSummary? Summary = null)
{
    // Version 6 is produced by the scoring-safe simulation rate. Older files
    // may have silently skipped short replay inputs while running at 100x.
    public const int CurrentVersion = 6;

    public static string PathFor(string contractPath) => contractPath + ".analysis.json";

    public static PreparedReplayAnalysis? Load(string contractPath)
    {
        string path = PathFor(contractPath);
        if (!File.Exists(path))
            return null;

        var value = JsonSerializer.Deserialize<PreparedReplayAnalysis>(File.ReadAllText(path), ViewerContract.JsonOptions);
        return value?.Version == CurrentVersion ? value : null;
    }

    public void Save(string contractPath)
    {
        string path = PathFor(contractPath);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, ViewerContract.JsonOptions));
        File.Move(temporary, path, true);
    }
}

internal sealed record PreparedReplaySimulationSummary(
    int N300,
    int N100,
    int N50,
    int Misses,
    double Accuracy,
    long Score,
    int AchievedCombo,
    int SliderBreaks,
    int LargeTickHits,
    int LargeTickMisses,
    int SmallTickHits,
    int SmallTickMisses,
    int SliderTailHits,
    int SliderTailMisses,
    double UnstableRate,
    IReadOnlyList<double> TimingOffsets,
    double Pp,
    double FcPp,
    double MaxPp,
    double BaseStars,
    double AdjustedStars,
    double ApproachRate,
    double AdjustedApproachRate,
    double CircleSize,
    double AdjustedCircleSize,
    double OverallDifficulty,
    double AdjustedOverallDifficulty,
    double DrainRate,
    double AdjustedDrainRate,
    double Bpm,
    double AdjustedBpm,
    double ClockRate,
    int MaxCombo,
    int CircleCount,
    int SliderCount,
    int SpinnerCount);

internal sealed record PreparedReplayJudgement(
    KumoriTimelineMarkerKind Kind,
    double EventTime,
    double RootStartTime,
    double RootEndTime,
    double ObjectStartTime,
    string ObjectType,
    float X,
    float Y,
    double Radius,
    double TimeOffset,
    int ComboBefore,
    int ComboAfter,
    double FrameStartTime = double.NaN,
    double FrameEndTime = double.NaN);

internal sealed record PreparedReplayFrame(
    double Time,
    float X,
    float Y,
    bool HasAction,
    bool Pressed,
    bool Released);
