using System.Text.Json;
using Kumori.Core;

namespace Kumori.Native;

public static class LazerReplayFrameDiagnostics
{
    private static readonly object Gate = new();

    public static string StatusPath => AppPaths.LazerReplayFrameStatusFile;

    public static void Update(Action<LazerReplayFrameStatus> mutate)
    {
        lock (Gate)
        {
            var status = Load();
            mutate(status);
            status.UpdatedAt = DateTimeOffset.UtcNow;
            Save(status);
        }
    }

    public static LazerReplayFrameStatus Load()
    {
        try
        {
            if (File.Exists(StatusPath))
            {
                return JsonSerializer.Deserialize<LazerReplayFrameStatus>(
                    File.ReadAllText(StatusPath)) ?? new LazerReplayFrameStatus();
            }
        }
        catch
        {
        }
        return new LazerReplayFrameStatus();
    }

    private static void Save(LazerReplayFrameStatus status)
    {
        string? tmp = null;
        try
        {
            Directory.CreateDirectory(AppPaths.StatusDir);
            var json = JsonSerializer.Serialize(status, new JsonSerializerOptions { WriteIndented = true });
            tmp = Path.Combine(
                AppPaths.StatusDir,
                $"{Path.GetFileName(StatusPath)}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");

            File.WriteAllText(tmp, json);
            File.Move(tmp, StatusPath, overwrite: true);
        }
        catch
        {
            // Diagnostics must never stop capture. The debug window can tolerate a stale file.
        }
        finally
        {
            try
            {
                if (tmp is not null && File.Exists(tmp))
                {
                    File.Delete(tmp);
                }
            }
            catch
            {
            }
        }
    }
}

public sealed record LazerReplayFrameStatus
{
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public bool Enabled { get; set; }
    public string State { get; set; } = "idle";
    public string? Detail { get; set; }
    public long FramesEmitted { get; set; }
    public long FramesBufferedForAttempt { get; set; }
    public long FramesStored { get; set; }
    public long? ActiveAttemptId { get; set; }
    public double? LastFrameMapTimeMs { get; set; }
    public double? LastFrameX { get; set; }
    public double? LastFrameY { get; set; }
    public bool LastFrameLeftPressed { get; set; }
    public bool LastFrameRightPressed { get; set; }
    public string? LastError { get; set; }
    public int? ProcessId { get; set; }
    public string? ProcessName { get; set; }
    public string? ProcessPath { get; set; }
    public string LocalReplayState { get; set; } = "idle";
    public string? LocalReplayPath { get; set; }
    public int LocalReplayFrames { get; set; }
    public string? LocalReplayError { get; set; }
}
