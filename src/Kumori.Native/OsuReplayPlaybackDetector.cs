using System.Buffers;
using System.Diagnostics;
using Kumori.Tracking;

namespace Kumori.Native;

/// <summary>
/// Reads the replay state already maintained by osu!. Official tosu currently
/// reads the same state internally but does not publish it through v2.
/// </summary>
public sealed class OsuReplayPlaybackDetector : IReplayPlaybackDetector, IDisposable
{
    private static readonly byte?[] StableReplayPattern =
    [
        0x55, 0x8B, 0xEC, 0x80, 0x3D, null, null, null, null, 0x00, 0x75, 0x26, 0x80, 0x3D,
    ];

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);
    private readonly object gate = new();
    private readonly LazerMemoryReplayFrameSource lazer;
    private readonly CancellationTokenSource lifetimeCts = new();
    private DateTimeOffset lastCheck;
    private OsuClientKind lastKind;
    private bool lastResult;
    private int? stableProcessId;
    private nint stableReplayFlag;
    private Process? stableProcess;
    private ProcessMemory? stableMemory;
    private int? stableUnsupportedProcessId;
    private DateTimeOffset stableUnsupportedUntil;
    private DateTimeOffset nextStableProcessSearch;
    private bool stableDiscoveryRunning;
    private bool stableCheckRunning;
    private long stableCheckGeneration;
    private bool stableLastResult;
    private bool lazerCheckRunning;
    private long lazerCheckGeneration;
    private bool lazerLastResult;
    private long replayStateGeneration;
    private bool disposed;

    public OsuReplayPlaybackDetector(LazerMemoryReplayFrameSource? lazer = null)
    {
        this.lazer = lazer ?? new LazerMemoryReplayFrameSource();
        this.lazer.WarmReplayDetectionOffsets();
    }

    public bool IsWatchingReplay(OsuClientKind clientKind)
    {
        if (clientKind == OsuClientKind.Unknown)
            return false;

        // Never make the websocket packet thread wait for a background RPM
        // probe or process-lifecycle transition. A busy detector returns its
        // last published value and catches up on a later packet.
        if (!Monitor.TryEnter(gate))
            return Volatile.Read(ref lastResult);
        try
        {
            if (disposed)
                return false;

            var now = DateTimeOffset.UtcNow;
            if (clientKind == lastKind && now - lastCheck < PollInterval)
                return lastResult;

            lastKind = clientKind;
            lastCheck = now;
            lastResult = clientKind.IsLazerFamily()
                ? detectLazer()
                : clientKind == OsuClientKind.Stable
                    ? detectStable()
                    : false;
            return lastResult;
        }
        finally
        {
            Monitor.Exit(gate);
        }
    }

    public void ResetAfterGameplay(OsuClientKind clientKind)
    {
        lock (gate)
        {
            if (disposed)
                return;

            replayStateGeneration++;
            lastCheck = DateTimeOffset.MinValue;
            lastKind = clientKind;
            lastResult = false;
            if (clientKind.IsLazerFamily() || clientKind == OsuClientKind.Unknown)
                lazerLastResult = false;
            if (clientKind is OsuClientKind.Stable or OsuClientKind.Unknown)
                stableLastResult = false;
        }
    }

    private bool detectLazer()
    {
        if (!lazerCheckRunning)
        {
            lazerCheckRunning = true;
            lazerCheckGeneration = replayStateGeneration;
            if (!ThreadPool.QueueUserWorkItem(
                    static detector => detector.detectLazerBackground(),
                    this,
                    preferLocal: false))
            {
                lazerCheckRunning = false;
            }
        }
        return lazerLastResult;
    }

    private void detectLazerBackground()
    {
        using var priority = new BackgroundThreadPriorityScope();
        var result = false;
        try
        {
            result = lazer.IsWatchingReplay();
        }
        catch
        {
        }
        finally
        {
            lock (gate)
            {
                if (!disposed && lazerCheckGeneration == replayStateGeneration)
                {
                    lazerLastResult = result;
                    if (lastKind.IsLazerFamily())
                        lastResult = result;
                }
                lazerCheckRunning = false;
            }
        }
    }

    private bool detectStable()
    {
        if (!stableCheckRunning)
        {
            stableCheckRunning = true;
            stableCheckGeneration = replayStateGeneration;
            if (!ThreadPool.QueueUserWorkItem(
                    static detector => detector.detectStableBackground(),
                    this,
                    preferLocal: false))
            {
                stableCheckRunning = false;
            }
        }
        return stableLastResult;
    }

    private void detectStableBackground()
    {
        using var priority = new BackgroundThreadPriorityScope();
        var result = false;
        try
        {
            lock (gate)
                result = detectStableCore();
        }
        catch
        {
        }
        finally
        {
            lock (gate)
            {
                if (!disposed && stableCheckGeneration == replayStateGeneration)
                {
                    stableLastResult = result;
                    if (lastKind == OsuClientKind.Stable)
                        lastResult = result;
                }
                stableCheckRunning = false;
            }
        }
    }

    private bool detectStableCore()
    {
        try
        {
            if (stableProcess is not null)
            {
                try
                {
                    if (stableProcess.HasExited)
                        resetStable();
                }
                catch
                {
                    resetStable();
                }
            }

            if (stableProcess is not null && stableMemory is not null && stableReplayFlag != 0)
            {
                // Even this cached one-byte ReadProcessMemory call stays off the
                // websocket packet thread; a paged-out target address must never
                // delay rhythm-game input processing.
                return stableMemory.ReadByte(stableReplayFlag) == 1;
            }

            startStableDiscoveryLocked(DateTimeOffset.UtcNow);
            return false;
        }
        catch
        {
            resetStable();
            nextStableProcessSearch = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
            return false;
        }
    }

    private void startStableDiscoveryLocked(DateTimeOffset now)
    {
        if (disposed || stableDiscoveryRunning || now < nextStableProcessSearch)
            return;

        stableDiscoveryRunning = true;
        nextStableProcessSearch = now + TimeSpan.FromSeconds(1);
        try
        {
            var worker = new Thread(discoverStable)
            {
                IsBackground = true,
                Name = "Kumori stable replay-state discovery",
                Priority = ThreadPriority.BelowNormal,
            };
            worker.Start();
        }
        catch
        {
            stableDiscoveryRunning = false;
            nextStableProcessSearch = now + TimeSpan.FromSeconds(30);
        }
    }

    private void discoverStable()
    {
        using var priority = new BackgroundThreadPriorityScope();
        Process? process = null;
        ProcessMemory? memory = null;
        try
        {
            process = findStableProcess();
            if (process is null)
                return;

            lock (gate)
            {
                // An exhausted executable image cannot acquire this signature
                // immediately. Stable uses JIT code, so expire the negative
                // cache in case the relevant method is compiled later.
                if (stableUnsupportedProcessId == process.Id)
                {
                    if (DateTimeOffset.UtcNow < stableUnsupportedUntil)
                        return;
                    stableUnsupportedProcessId = null;
                }
            }

            memory = ProcessMemory.Open(process);
            nint replayFlag = findStableReplayFlagIncrementally(
                memory,
                process,
                lifetimeCts.Token);
            bool exited;
            try { exited = process.HasExited; }
            catch { exited = true; }

            lock (gate)
            {
                if (disposed || exited || stableReplayFlag != 0)
                    return;

                if (replayFlag == 0)
                {
                    stableUnsupportedProcessId = process.Id;
                    stableUnsupportedUntil = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
                    return;
                }

                stableProcess = process;
                stableProcessId = process.Id;
                stableMemory = memory;
                stableReplayFlag = replayFlag;
                stableUnsupportedProcessId = null;
                process = null;
                memory = null;
            }
        }
        catch
        {
            lock (gate)
                nextStableProcessSearch = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        }
        finally
        {
            memory?.Dispose();
            process?.Dispose();
            lock (gate)
                stableDiscoveryRunning = false;
        }
    }

    private static Process? findStableProcess()
    {
        foreach (var name in new[] { "osu!", "osu" })
        {
            Process[] processes = Process.GetProcessesByName(name);
            Process? match = null;
            foreach (var process in processes)
            {
                try
                {
                    var directory = Path.GetDirectoryName(process.MainModule?.FileName);
                    if (!string.IsNullOrWhiteSpace(directory)
                        && (Directory.Exists(Path.Combine(directory, "Songs"))
                            || Directory.Exists(Path.Combine(directory, "Data", "r"))))
                    {
                        match = process;
                        break;
                    }
                }
                catch
                {
                }
            }

            foreach (Process process in processes)
                if (!ReferenceEquals(process, match)) process.Dispose();
            if (match is not null)
                return match;
        }

        return null;
    }

    private static nint findStableReplayFlagIncrementally(
        ProcessMemory memory,
        Process process,
        CancellationToken cancellationToken)
    {
        const int maximumBytesPerStep = 64 * 1024;
        const int maximumCandidatesPerStep = 8;
        const int maximumCandidates = 128;
        const int candidateReadBytes = sizeof(int);
        int chunkSize = maximumBytesPerStep - (maximumCandidatesPerStep * candidateReadBytes);
        int overlap = StableReplayPattern.Length - 1;
        int candidates = 0;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(chunkSize);

        try
        {
            foreach (var region in memory.Regions().Where(region => region.Executable))
            {
                if (cancellationToken.IsCancellationRequested)
                    return 0;
                long offset = 0;
                while (offset < region.RegionSize)
                {
                    if (cancellationToken.IsCancellationRequested)
                        return 0;
                    try { if (process.HasExited) return 0; }
                    catch { return 0; }

                    int readSize = (int)Math.Min(chunkSize, region.RegionSize - offset);
                    try { memory.ReadBytes(region.BaseAddress + (nint)offset, buffer, readSize); }
                    catch { break; }

                    var step = Stopwatch.StartNew();
                    int candidatesThisStep = 0;
                    for (int index = 0; index <= readSize - StableReplayPattern.Length; index++)
                    {
                        if ((index & 0xff) == 0 && cancellationToken.IsCancellationRequested)
                            return 0;
                        // Retain the current buffer/index while yielding the worker.
                        // No step monopolizes CPU for more than a few milliseconds.
                        if ((index & 0xff) == 0 && step.Elapsed >= TimeSpan.FromMilliseconds(3))
                        {
                            Thread.Sleep(2);
                            step.Restart();
                            candidatesThisStep = 0;
                        }
                        if (!matches(buffer, index, StableReplayPattern))
                            continue;

                        if (++candidates > maximumCandidates)
                            return 0;
                        if (++candidatesThisStep > maximumCandidatesPerStep)
                        {
                            Thread.Sleep(2);
                            step.Restart();
                            candidatesThisStep = 1;
                        }

                        try
                        {
                            nint signature = region.BaseAddress + (nint)offset + index;
                            uint address = unchecked((uint)memory.ReadInt32(signature + 0x46));
                            if (address >= 0x10000)
                                return (nint)address;
                        }
                        catch { }
                    }

                    if (offset + readSize >= region.RegionSize)
                        break;
                    offset += Math.Max(1, readSize - overlap);
                    Thread.Sleep(2);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return 0;
    }

    private static bool matches(byte[] buffer, int offset, IReadOnlyList<byte?> pattern)
    {
        for (int index = 0; index < pattern.Count; index++)
        {
            if (pattern[index] is { } expected && buffer[offset + index] != expected)
                return false;
        }
        return true;
    }

    private void resetStable()
    {
        stableProcessId = null;
        stableReplayFlag = 0;
        stableUnsupportedProcessId = null;
        stableUnsupportedUntil = DateTimeOffset.MinValue;
        stableMemory?.Dispose();
        stableMemory = null;
        stableProcess?.Dispose();
        stableProcess = null;
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
                return;
            disposed = true;
            replayStateGeneration++;
            lifetimeCts.Cancel();
            lastResult = false;
            lazerLastResult = false;
            stableLastResult = false;
            resetStable();
        }
        lazer.Dispose();
    }
}
