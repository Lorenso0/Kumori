using System.Text.Json;
using Kumori.Core;

namespace Kumori.Native;

public static class LazerReplayFrameDiagnostics
{
    private static readonly object Gate = new();
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(1);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly System.Threading.Timer FlushTimer;
    private static LazerReplayFrameStatus current = LoadFromDisk();
    private static bool dirty;
    private static int flushActive;

    static LazerReplayFrameDiagnostics()
    {
        FlushTimer = new System.Threading.Timer(
            static _ => FlushPending(force: false),
            null,
            FlushInterval,
            FlushInterval);
        AppDomain.CurrentDomain.ProcessExit += static (_, _) => FlushPending(force: true);
    }

    public static string StatusPath => AppPaths.LazerReplayFrameStatusFile;

    public static void Update(Action<LazerReplayFrameStatus> mutate)
    {
        lock (Gate)
        {
            mutate(current);
            current.UpdatedAt = DateTimeOffset.UtcNow;
            dirty = true;
        }
    }

    public static LazerReplayFrameStatus Load()
    {
        lock (Gate)
        {
            // The status object is mutable. Never let a caller mutate the
            // process-wide snapshot without going through Update().
            return current with { };
        }
    }

    private static LazerReplayFrameStatus LoadFromDisk()
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

    private static void FlushPending(bool force)
    {
        if (Interlocked.Exchange(ref flushActive, 1) != 0)
            return;

        try
        {
            LazerReplayFrameStatus snapshot;
            lock (Gate)
            {
                if (!dirty)
                    return;
                if (!force && current.ActiveAttemptId is not null)
                    return;
                snapshot = current with { };
                dirty = false;
            }

            if (!Save(snapshot))
            {
                lock (Gate)
                    dirty = true;
            }
        }
        finally
        {
            Volatile.Write(ref flushActive, 0);
        }
    }

    private static bool Save(LazerReplayFrameStatus status)
    {
        string? tmp = null;
        try
        {
            Directory.CreateDirectory(AppPaths.StatusDir);
            var json = JsonSerializer.Serialize(status, JsonOptions);
            tmp = Path.Combine(
                AppPaths.StatusDir,
                $"{Path.GetFileName(StatusPath)}.{Environment.ProcessId}.tmp");

            File.WriteAllText(tmp, json);
            File.Move(tmp, StatusPath, overwrite: true);
            return true;
        }
        catch
        {
            // Diagnostics must never stop capture. The debug window can tolerate a stale file.
            return false;
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
