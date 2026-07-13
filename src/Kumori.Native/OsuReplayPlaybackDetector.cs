using System.Diagnostics;
using Kumori.Tracking;

namespace Kumori.Native;

/// <summary>
/// Reads the replay state already maintained by osu!. Official tosu currently
/// reads the same state internally but does not publish it through v2.
/// </summary>
public sealed class OsuReplayPlaybackDetector : IReplayPlaybackDetector
{
    private static readonly byte?[] StableReplayPattern =
    [
        0x55, 0x8B, 0xEC, 0x80, 0x3D, null, null, null, null, 0x00, 0x75, 0x26, 0x80, 0x3D,
    ];

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);
    private readonly object gate = new();
    private readonly LazerMemoryReplayFrameSource lazer;
    private DateTimeOffset lastCheck;
    private OsuClientKind lastKind;
    private bool lastResult;
    private int? stableProcessId;
    private nint stableReplayFlag;

    public OsuReplayPlaybackDetector(LazerMemoryReplayFrameSource? lazer = null)
    {
        this.lazer = lazer ?? new LazerMemoryReplayFrameSource();
        this.lazer.WarmReplayDetectionOffsets();
    }

    public bool IsWatchingReplay(OsuClientKind clientKind)
    {
        if (clientKind == OsuClientKind.Unknown)
            return false;

        lock (gate)
        {
            var now = DateTimeOffset.UtcNow;
            if (clientKind == lastKind && now - lastCheck < PollInterval)
                return lastResult;

            lastKind = clientKind;
            lastCheck = now;
            lastResult = clientKind switch
            {
                OsuClientKind.Lazer => detectLazer(),
                OsuClientKind.Stable => detectStable(),
                _ => false,
            };
            return lastResult;
        }
    }

    private bool detectLazer()
    {
        try { return lazer.IsWatchingReplay(); }
        catch { return false; }
    }

    private bool detectStable()
    {
        Process? process = null;
        try
        {
            if (stableProcessId is { } cachedId)
            {
                try
                {
                    process = Process.GetProcessById(cachedId);
                    if (process.HasExited)
                    {
                        process.Dispose();
                        process = null;
                    }
                }
                catch
                {
                    process = null;
                }
            }

            process ??= findStableProcess();
            if (process is null)
            {
                resetStable();
                return false;
            }

            if (stableProcessId != process.Id)
            {
                stableProcessId = process.Id;
                stableReplayFlag = 0;
            }

            using var memory = ProcessMemory.Open(process);
            if (stableReplayFlag == 0)
                stableReplayFlag = findStableReplayFlag(memory);
            if (stableReplayFlag == 0)
                return false;

            return memory.ReadBytes(stableReplayFlag, 1)[0] == 1;
        }
        catch
        {
            stableReplayFlag = 0;
            return false;
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static Process? findStableProcess()
    {
        foreach (var name in new[] { "osu!", "osu" })
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                try
                {
                    var directory = Path.GetDirectoryName(process.MainModule?.FileName);
                    if (!string.IsNullOrWhiteSpace(directory)
                        && (Directory.Exists(Path.Combine(directory, "Songs"))
                            || Directory.Exists(Path.Combine(directory, "Data", "r"))))
                    {
                        return process;
                    }
                }
                catch
                {
                }

                process.Dispose();
            }
        }

        return null;
    }

    private static nint findStableReplayFlag(ProcessMemory memory)
    {
        foreach (var region in memory.Regions().Where(region => region.Executable))
        {
            var signature = memory.FindPattern(region.BaseAddress, region.RegionSize, StableReplayPattern);
            if (signature == 0)
                continue;

            var address = unchecked((uint)memory.ReadInt32(signature + 0x46));
            if (address >= 0x10000)
                return (nint)address;
        }

        return 0;
    }

    private void resetStable()
    {
        stableProcessId = null;
        stableReplayFlag = 0;
    }
}
