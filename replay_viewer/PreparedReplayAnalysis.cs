using System.Text.Json;

namespace Kumori.ReplayViewer;

internal sealed record PreparedReplayAnalysis(
    int Version,
    long AttemptId,
    IReadOnlyList<PreparedReplayJudgement> Judgements,
    IReadOnlyList<PreparedReplayFrame> Frames)
{
    public const int CurrentVersion = 2;

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
