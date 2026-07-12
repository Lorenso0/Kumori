using System.IO;
using System.Text.Json;
using Kumori.Core;

namespace Kumori.App;

internal static class StableReplayFrameDiagnostics
{
    private static readonly object Gate = new();
    public static string StatusPath => AppPaths.StableReplayFrameStatusFile;

    public static void Update(Action<StableReplayFrameStatus> mutate)
    {
        lock (Gate)
        {
            var status = Load();
            mutate(status);
            status.UpdatedAt = DateTimeOffset.UtcNow;
            Save(status);
        }
    }

    public static StableReplayFrameStatus Load()
    {
        try
        {
            if (File.Exists(StatusPath))
                return JsonSerializer.Deserialize<StableReplayFrameStatus>(File.ReadAllText(StatusPath)) ?? new();
        }
        catch { }
        return new StableReplayFrameStatus();
    }

    private static void Save(StableReplayFrameStatus status)
    {
        string? temporary = null;
        try
        {
            Directory.CreateDirectory(AppPaths.StatusDir);
            temporary = Path.Combine(AppPaths.StatusDir, $"{Path.GetFileName(StatusPath)}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
            File.WriteAllText(temporary, JsonSerializer.Serialize(status, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, StatusPath, true);
        }
        catch { }
        finally
        {
            try { if (temporary is not null && File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }
}

internal sealed record StableReplayFrameStatus
{
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public bool Enabled { get; set; }
    public string State { get; set; } = "idle";
    public string? Detail { get; set; }
    public long? ActiveAttemptId { get; set; }
    public string? GameFolder { get; set; }
    public string? BeatmapPath { get; set; }
    public string? ExpectedChecksum { get; set; }
    public string? CandidateReplayPath { get; set; }
    public int CandidatesChecked { get; set; }
    public int FramesDecoded { get; set; }
    public int FramesStored { get; set; }
    public string? LastError { get; set; }
    public string LiveState { get; set; } = "idle";
    public string? LiveDetail { get; set; }
    public long LiveFramesReceived { get; set; }
    public long LiveFramesBuffered { get; set; }
    public long LiveFramesStored { get; set; }
    public string? LiveSnapshotPath { get; set; }
    public string? ComparisonReportPath { get; set; }
    public string? ComparisonSummary { get; set; }
}

internal sealed class StableCaptureStatusSink : Kumori.Native.IReplayFrameStatusSink
{
    private readonly object gate = new();
    private Kumori.Native.LazerReplayFrameStatus status = new();
    public void Update(Action<Kumori.Native.LazerReplayFrameStatus> mutate)
    {
        lock (gate)
        {
            mutate(status);
            StableReplayFrameDiagnostics.Update(target =>
            {
                target.Enabled = status.Enabled;
                target.LiveState = status.State;
                target.LiveDetail = status.Detail;
                target.LiveFramesReceived = status.FramesEmitted;
                target.LiveFramesBuffered = status.FramesBufferedForAttempt;
                target.LiveFramesStored = status.FramesStored;
                const string snapshotMarker = "offline replay analysis: ";
                int snapshotIndex = status.Detail?.IndexOf(snapshotMarker, StringComparison.OrdinalIgnoreCase) ?? -1;
                if (snapshotIndex >= 0)
                    target.LiveSnapshotPath = status.Detail![(snapshotIndex + snapshotMarker.Length)..].Trim();
                if (status.LastError is not null) target.LastError = status.LastError;
            });
        }
    }
    public Kumori.Native.LazerReplayFrameStatus Load() { lock (gate) return status; }
}
