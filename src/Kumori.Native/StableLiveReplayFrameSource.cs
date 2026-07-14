using System.Diagnostics;
using Kumori.Tracking;
#if STABLE_FRAME_BRIDGE
using Microsoft.Diagnostics.Runtime;
#endif
#if !STABLE_FRAME_BRIDGE
using Serilog;
#endif

namespace Kumori.Native;

internal static class StableGraphDiscoveryPolicy
{
    internal const int MaximumAttempts = 3;
    internal const int MaximumDepth = 10;
    internal const int MaximumObjects = 25_000;
    internal const int MaximumObjectsPerPoll = 256;
    internal const int MaximumHeapObjects = 5_000_000;
    internal const int MaximumReferencesPerPoll = 1_024;
    internal const int MaximumCandidateOperationsPerPoll = 128;
    internal const int MaximumTailFramesPerPoll = 64;
    internal static readonly TimeSpan MaximumPollDuration = TimeSpan.FromMilliseconds(3);
    internal static readonly TimeSpan ActivePollInterval = TimeSpan.FromMilliseconds(16);
    internal static readonly TimeSpan IdlePollInterval = TimeSpan.FromMilliseconds(250);
    internal static readonly TimeSpan RediscoveryCooldown = TimeSpan.FromSeconds(2);

    internal static bool HasBudget(
        TimeSpan elapsed,
        int objectsProcessed,
        int referencesProcessed,
        int candidateOperations)
        => elapsed < MaximumPollDuration
           && objectsProcessed < MaximumObjectsPerPoll
           && referencesProcessed < MaximumReferencesPerPoll
           && candidateOperations < MaximumCandidateOperationsPerPoll;

    internal static bool AttemptsExhausted(int attempts) => attempts >= MaximumAttempts;

    internal static bool CanRetryNegativeCache(DateTimeOffset now, DateTimeOffset retryAt) =>
        now >= retryAt;

    internal static TimeSpan PollInterval(bool discoveryInProgress, bool hasFrames)
        => discoveryInProgress || hasFrames ? ActivePollInterval : IdlePollInterval;

    internal static TimeSpan FinalizationDrainDelay(TimeSpan pollInterval)
        => TimeSpan.FromMilliseconds(Math.Max(32, pollInterval.TotalMilliseconds * 2));

    internal static bool ShouldCaptureDiagnosticSnapshot(string diagnostic)
        => diagnostic.Contains("replay list not matched", StringComparison.Ordinal)
           || (diagnostic.StartsWith("stable typed heap fallback", StringComparison.Ordinal)
               && (diagnostic.Contains("did not find a populated replay list", StringComparison.Ordinal)
                   || diagnostic.Contains("reached object cap", StringComparison.Ordinal)
                   || diagnostic.Contains("failed:", StringComparison.Ordinal)));

    internal static bool IsReplayFrameFieldShape(
        int totalFields,
        int floatFields,
        int integerFields,
        int booleanFields)
        => totalFields <= 12
           && floatFields == 2
           && integerFields >= 2
           && booleanFields is >= 4 and <= 8;

    internal static IReadOnlyList<uint> DiscoveryRoots(uint rulesetAddress, uint gameplayAddress)
    {
        if (rulesetAddress < 0x10000)
            return [];
        if (gameplayAddress < 0x10000 || gameplayAddress == rulesetAddress)
            return [rulesetAddress];
        // The ruleset is the authoritative owner. The historical +0x64 child
        // remains a useful shortcut on stable builds where that field still
        // points at gameplay, but it must never be the only traversal root.
        return [rulesetAddress, gameplayAddress];
    }
}

#if !STABLE_FRAME_BRIDGE
/// <summary>
/// Reads osu!stable's own in-progress List&lt;ReplayFrame&gt; from the CLR heap.
/// Stable obfuscates its game types on every update, so discovery deliberately
/// uses the invariant replay-frame shape (time, X, Y and pButtonState) rather
/// than an obfuscated type name. No desktop cursor or keyboard state is used.
/// </summary>
public sealed class StableLiveReplayFrameSource : ILazerReplayFrameSource, ILazerReplayFrameSnapshotSource, IAttemptAwareReplayFrameSource, IFinalizableReplayFrameSource, IAsyncDisposable
{
    private readonly object gate = new();
    private readonly SemaphoreSlim deliveryGate = new(1, 1);
    private readonly SemaphoreSlim sourceSignal = new(0, 1);
    private readonly TimeSpan pollInterval;
    private readonly IReplayFrameStatusSink status;
    private AttemptStart? attempt;
    private Process? bridge;
    private List<LazerReplayFrame> latest = [];
    private long attemptGeneration;

    public StableLiveReplayFrameSource(TimeSpan? pollInterval = null, IReplayFrameStatusSink? status = null)
    {
        this.pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(16);
        this.status = status ?? new DelegatingReplayFrameStatusSink();
        scheduleDiagnosticAttemptSignal(active: false, expectedGeneration: attemptGeneration);
    }

    public void StartAttempt(AttemptStart start)
    {
        deliveryGate.Wait();
        try
        {
            lock (gate)
            {
                attemptGeneration++;
                // Reuse the bridge prelaunched in menus. Process/CLR startup
                // and attach discovery must not begin on the first gameplay
                // packet. Dead-bridge detection and every lifecycle operation
                // belong to the source loop, never this packet callback.
                attempt = start;
                latest = [];
            }
        }
        finally { deliveryGate.Release(); }
        scheduleDiagnosticAttemptSignal(active: true, expectedGeneration: attemptGeneration);
        signalSourceLoop();
    }

    public void UpdateAttempt(AttemptSnapshot snapshot) { }

    public void EndAttempt()
    {
        deliveryGate.Wait();
        try
        {
            lock (gate)
            {
                attemptGeneration++;
                attempt = null;
                latest = [];
            }
        }
        finally { deliveryGate.Release(); }
        scheduleDiagnosticAttemptSignal(active: false, expectedGeneration: attemptGeneration);
        // Empty/pre-play attempts can be discarded immediately after the next
        // real play has already been detected. Keep the expensive CLR bridge
        // attachment warm here; StartAttempt changes the generation so stale
        // output cannot be attributed to the next attempt. Finalized real plays
        // still detach the bridge below to reset its replay-list ownership.
    }

    public IReadOnlyList<LazerReplayFrame> FinalizeAttemptSnapshot()
    {
        // Give the source loop enough time to consume frames already emitted by
        // the x86 bridge and one final bridge poll. This happens after gameplay
        // has ended, before the delivery barrier seals the attempt generation.
        Thread.Sleep(StableGraphDiscoveryPolicy.FinalizationDrainDelay(pollInterval));
        IReadOnlyList<LazerReplayFrame> snapshot;
        Process? stoppedBridge;
        deliveryGate.Wait();
        try
        {
            lock (gate)
            {
                // The delivery barrier guarantees that no accepted frame can
                // mutate this list after ownership is transferred.
                snapshot = latest;
                latest = [];
                attemptGeneration++;
                attempt = null;
                stoppedBridge = detachBridgeLocked();
            }
        }
        finally { deliveryGate.Release(); }
        stopBridgeInBackground(stoppedBridge);
        scheduleDiagnosticAttemptSignal(active: false, expectedGeneration: attemptGeneration);
        return snapshot;
    }

    public IReadOnlyList<LazerReplayFrame> ReadCurrentFramesSnapshot()
    {
        lock (gate)
        {
            return latest.ToArray();
        }
    }

    public async IAsyncEnumerable<LazerReplayFrame> ReadFramesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Process? expiredBridge = null;
            Process? currentBridge;
            AttemptStart? currentAttempt;
            long currentGeneration;
            var needsBridge = false;
            lock (gate)
            {
                currentAttempt = attempt;
                if (bridge is not null && hasExited(bridge))
                {
                    expiredBridge = detachBridgeLocked();
                }
                needsBridge = bridge is null;
            }
            stopBridgeInBackground(expiredBridge);

            if (needsBridge)
            {
                // Process creation and image loading can page in executable
                // data. Keep it outside all packet-visible locks and in Windows
                // background mode even when an attempt began before prewarm.
                Process? launched;
                using (new BackgroundThreadPriorityScope())
                    launched = startBridge(currentAttempt?.GameFolder);

                Process? redundantBridge = null;
                lock (gate)
                {
                    if (bridge is null)
                        bridge = launched;
                    else
                        redundantBridge = launched;
                }
                stopBridgeInBackground(redundantBridge);
            }

            lock (gate)
            {
                currentAttempt = attempt;
                currentGeneration = attemptGeneration;
                currentBridge = bridge;
            }
            if (currentAttempt is null || currentBridge is null)
            {
                await sourceSignal.WaitAsync(TimeSpan.FromSeconds(1), cancellationToken);
                continue;
            }

            string? line;
            try { line = await currentBridge.StandardOutput.ReadLineAsync(cancellationToken); }
            catch { line = null; }
            if (line is null)
            {
                Process? failedBridge = null;
                lock (gate)
                {
                    if (attemptGeneration == currentGeneration && ReferenceEquals(bridge, currentBridge))
                        failedBridge = detachBridgeLocked();
                }
                stopBridgeInBackground(failedBridge);
                await Task.Delay(pollInterval, cancellationToken);
                continue;
            }
            if (!LazerReplayFrameJson.TryParse(line, out LazerReplayFrame frame))
                continue;
            await deliveryGate.WaitAsync(cancellationToken);
            try
            {
                var accepted = false;
                lock (gate)
                {
                    // Killing a bridge can still complete its pending stdout read.
                    // Reject that line unless it belongs to the exact process and
                    // attempt generation captured before ReadLineAsync was awaited.
                    if (attempt is not null
                        && attemptGeneration == currentGeneration
                        && ReferenceEquals(bridge, currentBridge))
                    {
                        if (latest.Count > 0 && (frame.Sequence ?? 0) <= (latest[^1].Sequence ?? 0))
                            latest.Clear();
                        latest.Add(frame);
                        accepted = true;
                    }
                }
                if (accepted)
                {
                    // Keep the delivery barrier until the consumer advances the
                    // iterator. Attempt transitions take the same barrier, so a
                    // frame accepted for generation N cannot be delivered after
                    // generation N+1 has started.
                    yield return frame;
                }
            }
            finally { deliveryGate.Release(); }
        }
    }

    private static bool hasExited(Process process)
    {
        try { return process.HasExited; }
        catch { return true; }
    }

    private void signalSourceLoop()
    {
        try { sourceSignal.Release(); }
        catch (SemaphoreFullException) { }
        catch (ObjectDisposedException) { }
    }

    private void scheduleDiagnosticAttemptSignal(bool active, long? expectedGeneration)
    {
        _ = Task.Run(() =>
        {
            try
            {
                lock (gate)
                {
                    if (expectedGeneration is { } generation
                        && (attemptGeneration != generation || (attempt is not null) != active))
                        return;
                }

                string path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Kumori", "runtime", "debug", "stable-memory-attempt-active.signal");
                if (active)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllText(path, DateTimeOffset.UtcNow.ToString("O"));
                }
                else
                {
                    try { File.Delete(path); } catch { }
                }
            }
            catch
            {
                // This signal only gates an explicitly armed diagnostic dump.
                // Capture itself must never affect attempt tracking.
            }
        });
    }

    private Process? startBridge(string? gameFolder)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "stable-frame-bridge", "Kumori.StableFrameBridge.exe");
        if (!File.Exists(path))
            path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Kumori.StableFrameBridge", "bin", "Debug", "net8.0", "win-x86", "Kumori.StableFrameBridge.exe"));
        if (!File.Exists(path))
        {
            status.Update(s => { s.State = "stable_bridge_missing"; s.Detail = $"Stable frame bridge was not found at {path}."; s.LastError = s.Detail; });
            return null;
        }
        var start = new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        if (!string.IsNullOrWhiteSpace(gameFolder)) start.ArgumentList.Add(gameFolder);
        Process? process = Process.Start(start);
        if (process is not null)
        {
            try { process.PriorityClass = ProcessPriorityClass.BelowNormal; }
            catch { }
            process.ErrorDataReceived += (_, e) =>
            {
                if (string.IsNullOrWhiteSpace(e.Data)) return;
                Log.Debug("Stable frame bridge: {Diagnostic}", e.Data);
                status.Update(s => { s.State = "stable_memory_diagnostic"; s.Detail = e.Data; s.LastError = null; });
            };
            process.BeginErrorReadLine();
        }
        return process;
    }

    private Process? detachBridgeLocked()
    {
        var detached = bridge;
        bridge = null;
        return detached;
    }

    private static void stopBridge(Process? process)
    {
        if (process is null) return;
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        process.Dispose();
    }

    private static void stopBridgeInBackground(Process? process)
    {
        if (process is null) return;
        try
        {
            _ = Task.Run(() =>
            {
                using var priority = new BackgroundThreadPriorityScope();
                stopBridge(process);
            });
        }
        catch
        {
            // Scheduling failure is teardown-only in practice. Relinquish the
            // handle without putting process termination on a packet callback.
            process.Dispose();
        }
    }

    public ValueTask DisposeAsync()
    {
        Process? stoppedBridge;
        deliveryGate.Wait();
        try
        {
            lock (gate)
            {
                attemptGeneration++;
                attempt = null;
                latest = [];
                stoppedBridge = detachBridgeLocked();
            }
        }
        finally { deliveryGate.Release(); }
        stopBridge(stoppedBridge);
        scheduleDiagnosticAttemptSignal(active: false, expectedGeneration: attemptGeneration);
        sourceSignal.Dispose();
        return ValueTask.CompletedTask;
    }
}
#endif

#if STABLE_FRAME_BRIDGE
public sealed class StableClrReplayReader : IDisposable
{
    private static readonly object AttachGate = new();
    private static readonly byte?[] RulesetPattern = [0x7d, 0x15, 0xa1, null, null, null, null, 0x85, 0xc0];
    private static StableAttachDiscovery? attachDiscovery;
    private static int? unsupportedAttachProcessId;
    private static DateTimeOffset unsupportedAttachUntil;
    private static DateTimeOffset nextAttachSearchAt;
    public static string LastAttachDiagnostic { get; private set; } = "not attempted";
    public static TimeSpan AttachPollInterval
    {
        get
        {
            lock (AttachGate)
                return attachDiscovery is null ? TimeSpan.FromMilliseconds(250) : TimeSpan.FromMilliseconds(5);
        }
    }
    private readonly DataTarget target;
    private readonly ClrRuntime runtime;
    private readonly ProcessMemory memory;
    private readonly uint rulesetSlot;
    private ulong listAddress;
    private string? timeField;
    private string? xField;
    private string? yField;
    private string? buttonsField;
    private const int MaximumUnreadableFrameRetries = 3;
    private int discoveryAttempts;
    private DateTimeOffset nextDiscoveryAt;
    private bool rediscoveryDisabled;
    private StableGraphDiscovery? graphDiscovery;
    private StableHeapDiscovery? heapDiscovery;
    private int emittedListCount;
    private int unreadableFrameIndex = -1;
    private int unreadableFrameRetries;
    private double lastEmittedTime = double.NegativeInfinity;
    public string LastDiagnostic { get; private set; } = "not scanned";
    public TimeSpan PollInterval => StableGraphDiscoveryPolicy.PollInterval(
        graphDiscovery is not null || heapDiscovery is not null,
        listAddress != 0);

    private StableClrReplayReader(DataTarget target, ClrRuntime runtime, ProcessMemory memory, uint rulesetSlot)
    {
        this.target = target;
        this.runtime = runtime;
        this.memory = memory;
        this.rulesetSlot = rulesetSlot;
    }

    public static StableClrReplayReader? TryAttach(string? expectedGameFolder)
    {
        lock (AttachGate)
        {
            if (attachDiscovery is null)
            {
                var now = DateTimeOffset.UtcNow;
                if (now < nextAttachSearchAt)
                    return null;

                Process? process = findStableProcess(expectedGameFolder);
                if (process is null)
                {
                    LastAttachDiagnostic = "osu!stable process not found";
                    nextAttachSearchAt = now + TimeSpan.FromSeconds(1);
                    return null;
                }

                if (unsupportedAttachProcessId == process.Id && now < unsupportedAttachUntil)
                {
                    LastAttachDiagnostic = $"stable ruleset signature negative-cached for process {process.Id}";
                    process.Dispose();
                    nextAttachSearchAt = now + TimeSpan.FromSeconds(1);
                    return null;
                }
                if (unsupportedAttachProcessId != process.Id || now >= unsupportedAttachUntil)
                {
                    unsupportedAttachProcessId = null;
                    unsupportedAttachUntil = DateTimeOffset.MinValue;
                }

                ProcessMemory? memory = null;
                try
                {
                    memory = ProcessMemory.Open(process);
                    attachDiscovery = new StableAttachDiscovery(process, memory, RulesetPattern);
                    memory = null;
                }
                catch (Exception ex)
                {
                    memory?.Dispose();
                    process.Dispose();
                    LastAttachDiagnostic = $"{ex.GetType().Name}: {ex.Message}";
                    nextAttachSearchAt = now + TimeSpan.FromSeconds(1);
                    return null;
                }
            }

            StableAttachScanResult scan = attachDiscovery.ScanStep();
            LastAttachDiagnostic = attachDiscovery.Diagnostic;
            if (scan.State == StableAttachScanState.Pending)
                return null;

            int processId = attachDiscovery.ProcessId;
            if (scan.State == StableAttachScanState.Exhausted)
            {
                unsupportedAttachProcessId = processId;
                // Stable is managed: a method not yet JITted can introduce the
                // signature later, so this PID-level negative cache expires.
                unsupportedAttachUntil = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
                attachDiscovery.Dispose();
                attachDiscovery = null;
                nextAttachSearchAt = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(1);
                return null;
            }
            if (scan.State == StableAttachScanState.Aborted)
            {
                attachDiscovery.Dispose();
                attachDiscovery = null;
                nextAttachSearchAt = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(1);
                return null;
            }

            ProcessMemory ownedMemory = attachDiscovery.TakeMemory();
            attachDiscovery.Dispose();
            attachDiscovery = null;
            DataTarget? target = null;
            try
            {
                target = DataTarget.AttachToProcess(processId, suspend: false);
                ClrInfo? clr = target.ClrVersions.FirstOrDefault();
                if (clr is null)
                    throw new InvalidOperationException("Stable CLR runtime unavailable.");
                ClrRuntime runtime = clr.CreateRuntime();
                // The bridge is normally attached in stable's menus. Flush the
                // DAC cache once as part of that prewarm. The normal gameplay
                // graph path never flushes it; only the isolated typed-heap
                // fallback refreshes after proving the cached graph insufficient.
                runtime.FlushCachedData();
                LastAttachDiagnostic = $"attached via stable ruleset slot 0x{scan.RulesetSlot:x8}";
                return new StableClrReplayReader(target, runtime, ownedMemory, scan.RulesetSlot);
            }
            catch (Exception ex)
            {
                LastAttachDiagnostic = $"{ex.GetType().Name}: {ex.Message}";
                target?.Dispose();
                ownedMemory.Dispose();
                nextAttachSearchAt = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(1);
                return null;
            }
        }
    }

    private enum StableAttachScanState { Pending, Found, Exhausted, Aborted }

    private readonly record struct StableAttachScanResult(StableAttachScanState State, uint RulesetSlot = 0);

    /// <summary>
    /// Carries the executable/JIT-region cursor between bridge polls. Each poll
    /// reads at most 64 KiB, validates at most eight candidates, and spends at
    /// most a few milliseconds scanning before yielding back to the bridge.
    /// </summary>
    private sealed class StableAttachDiscovery : IDisposable
    {
        private const int MaximumBytesPerStep = 64 * 1024;
        private const int MaximumCandidatesPerStep = 8;
        private const int MaximumCandidates = 128;
        private static readonly TimeSpan MaximumScanTimePerStep = TimeSpan.FromMilliseconds(3);
        private readonly Process process;
        private ProcessMemory? memory;
        private readonly IReadOnlyList<byte?> pattern;
        private readonly MemoryRegion[] regions;
        private readonly byte[] buffer;
        private int regionIndex;
        private long regionOffset;
        private int bufferedCount;
        private nint bufferedAddress;
        private int scanOffset;
        private int candidateCount;
        private long totalBytesRead;

        public StableAttachDiscovery(Process process, ProcessMemory memory, IReadOnlyList<byte?> pattern)
        {
            this.process = process;
            this.memory = memory;
            this.pattern = pattern;
            // Include every executable allocation, including private CLR JIT
            // regions; limiting discovery to image modules misses stable builds.
            regions = memory.Regions().Where(region => region.Executable).ToArray();
            buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(MaximumBytesPerStep);
            Diagnostic = $"scanning stable executable/JIT regions for process {process.Id}";
        }

        public int ProcessId => process.Id;
        public string Diagnostic { get; private set; }

        public StableAttachScanResult ScanStep()
        {
            try { if (process.HasExited) return abort("stable process exited during attach discovery"); }
            catch { return abort("stable process became unavailable during attach discovery"); }
            if (memory is null)
                return abort("stable process memory is unavailable");

            var timer = Stopwatch.StartNew();
            int bytesThisStep = 0;
            int candidatesThisStep = 0;
            while (timer.Elapsed < MaximumScanTimePerStep
                   && bytesThisStep < MaximumBytesPerStep
                   && candidatesThisStep < MaximumCandidatesPerStep)
            {
                if (bufferedCount == 0)
                {
                    if (regionIndex >= regions.Length)
                    {
                        Diagnostic = $"stable ruleset signature unavailable after bounded scan ({totalBytesRead} bytes, {candidateCount} candidates)";
                        return new StableAttachScanResult(StableAttachScanState.Exhausted);
                    }

                    MemoryRegion region = regions[regionIndex];
                    long remaining = region.RegionSize - regionOffset;
                    if (remaining <= 0)
                    {
                        regionIndex++;
                        regionOffset = 0;
                        continue;
                    }

                    int readSize = (int)Math.Min(remaining, MaximumBytesPerStep - bytesThisStep);
                    try
                    {
                        memory.ReadBytes(region.BaseAddress + (nint)regionOffset, buffer, readSize);
                    }
                    catch
                    {
                        regionIndex++;
                        regionOffset = 0;
                        continue;
                    }
                    bufferedAddress = region.BaseAddress + (nint)regionOffset;
                    bufferedCount = readSize;
                    scanOffset = 0;
                    bytesThisStep += readSize;
                    totalBytesRead += readSize;
                }

                int lastStart = bufferedCount - pattern.Count;
                while (scanOffset <= lastStart)
                {
                    if ((scanOffset & 0xff) == 0 && timer.Elapsed >= MaximumScanTimePerStep)
                        return pending();
                    if (!matches(buffer, scanOffset, pattern))
                    {
                        scanOffset++;
                        continue;
                    }
                    if (candidatesThisStep >= MaximumCandidatesPerStep
                        || bytesThisStep + sizeof(int) > MaximumBytesPerStep)
                        return pending();
                    if (++candidateCount > MaximumCandidates)
                    {
                        Diagnostic = $"stable ruleset candidate cap reached after {totalBytesRead} bytes";
                        return new StableAttachScanResult(StableAttachScanState.Exhausted);
                    }

                    candidatesThisStep++;
                    bytesThisStep += sizeof(int);
                    nint signature = bufferedAddress + scanOffset++;
                    try
                    {
                        uint rulesetSlot = unchecked((uint)memory.ReadInt32(signature - 0x0b));
                        if (rulesetSlot >= 0x10000)
                            return new StableAttachScanResult(StableAttachScanState.Found, rulesetSlot);
                    }
                    catch { }
                }

                finishBufferedRegionChunk();
            }
            return pending();
        }

        private StableAttachScanResult pending()
        {
            Diagnostic = $"scanning stable executable/JIT regions {regionIndex + 1}/{Math.Max(1, regions.Length)} ({totalBytesRead} bytes, {candidateCount} candidates)";
            return new StableAttachScanResult(StableAttachScanState.Pending);
        }

        private StableAttachScanResult abort(string diagnostic)
        {
            Diagnostic = diagnostic;
            return new StableAttachScanResult(StableAttachScanState.Aborted);
        }

        private void finishBufferedRegionChunk()
        {
            MemoryRegion region = regions[regionIndex];
            if (regionOffset + bufferedCount >= region.RegionSize)
            {
                regionIndex++;
                regionOffset = 0;
            }
            else
            {
                int overlap = Math.Max(0, pattern.Count - 1);
                regionOffset += Math.Max(1, bufferedCount - overlap);
            }
            bufferedCount = 0;
            scanOffset = 0;
        }

        public ProcessMemory TakeMemory()
        {
            ProcessMemory result = memory ?? throw new ObjectDisposedException(nameof(StableAttachDiscovery));
            memory = null;
            return result;
        }

        public void Dispose()
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
            memory?.Dispose();
            memory = null;
            process.Dispose();
        }
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

    private enum StableGraphScanState { Pending, Found, Exhausted }

    private readonly record struct StableReplayListCandidate(
        ulong Address,
        int Count,
        double LastTime,
        string Time,
        string X,
        string Y,
        string Buttons,
        string Type,
        string Layout);

    private readonly record struct StableGraphScanResult(
        StableGraphScanState State,
        StableReplayListCandidate Candidate,
        string Diagnostic);

    public IReadOnlyList<LazerReplayFrame> ReadReplayFrames()
    {
        if (listAddress != 0)
        {
            try
            {
                return readNewFrames(listAddress);
            }
            catch
            {
                ScheduleRediscovery("stable replay list became unreadable");
                return [];
            }
        }

        if (heapDiscovery is not null)
            return advanceHeapDiscovery();
        if (graphDiscovery is not null)
            return advanceGraphDiscovery();
        var now = DateTimeOffset.UtcNow;
        if (rediscoveryDisabled)
        {
            if (!StableGraphDiscoveryPolicy.CanRetryNegativeCache(now, nextDiscoveryAt))
                return [];
            rediscoveryDisabled = false;
            discoveryAttempts = 0;
            LastDiagnostic = "stable replay discovery cooldown elapsed; bounded discovery resumed";
        }
        if (StableGraphDiscoveryPolicy.AttemptsExhausted(discoveryAttempts)
            || now < nextDiscoveryAt)
            return [];

        uint rulesetAddress;
        try { rulesetAddress = unchecked((uint)memory.ReadInt32((nint)(rulesetSlot + 4))); }
        catch
        {
            LastDiagnostic = "stable ruleset pointer could not be read";
            return [];
        }
        if (rulesetAddress < 0x10000)
        {
            LastDiagnostic = "waiting for stable gameplay ruleset";
            return [];
        }

        // The bridge process, CLR attachment and ruleset signature scan are
        // prewarmed while stable is in its menus. Stable's current replay list
        // is weak/native-owned and is not reliably reachable from the ruleset;
        // traversing that graph also races compacting GC. Refresh and scan the
        // typed heap directly on the isolated bridge worker instead.
        discoveryAttempts++;
        heapDiscovery = new StableHeapDiscovery(this, discoveryAttempts);
        LastDiagnostic = heapDiscovery.Diagnostic;
        return [];
    }

    private IReadOnlyList<LazerReplayFrame> advanceGraphDiscovery()
    {
        StableGraphDiscovery discovery = graphDiscovery!;
        StableGraphScanResult scan = discovery.ScanStep();
        LastDiagnostic = scan.Diagnostic;
        if (scan.State == StableGraphScanState.Pending)
            return [];

        graphDiscovery = null;
        discovery.Dispose();
        if (scan.State == StableGraphScanState.Exhausted)
        {
            // Some stable builds keep the active replay list behind a weak or
            // native-owned reference that is not reachable from the ruleset
            // roots. Fall back to a typed heap walk on the bridge's dedicated,
            // below-normal worker. The scan never runs on Kumori or osu!'s
            // gameplay threads and retains the same strict type/value checks.
            heapDiscovery = new StableHeapDiscovery(this, discoveryAttempts);
            LastDiagnostic += "; starting bounded typed heap fallback";
            return [];
        }

        return lockCandidate(scan.Candidate, "graph");
    }

    private IReadOnlyList<LazerReplayFrame> advanceHeapDiscovery()
    {
        StableHeapDiscovery discovery = heapDiscovery!;
        StableHeapScanResult scan = discovery.ScanStep();
        LastDiagnostic = scan.Diagnostic;
        if (scan.State == StableHeapScanState.Pending)
            return [];

        heapDiscovery = null;
        discovery.Dispose();
        if (scan.State == StableHeapScanState.Exhausted)
        {
            if (StableGraphDiscoveryPolicy.AttemptsExhausted(discoveryAttempts))
            {
                rediscoveryDisabled = true;
                nextDiscoveryAt = DateTimeOffset.UtcNow + StableGraphDiscoveryPolicy.RediscoveryCooldown;
                LastDiagnostic += "; cooling down before bounded rediscovery";
            }
            else
            {
                nextDiscoveryAt = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(250);
            }
            return [];
        }

        return lockCandidate(scan.Candidate, "typed heap fallback");
    }

    private IReadOnlyList<LazerReplayFrame> lockCandidate(
        StableReplayListCandidate best,
        string source)
    {
        listAddress = best.Address;
        timeField = best.Time;
        xField = best.X;
        yField = best.Y;
        buttonsField = best.Buttons;
        emittedListCount = 0;
        unreadableFrameIndex = -1;
        unreadableFrameRetries = 0;
        lastEmittedTime = double.NegativeInfinity;
        LastDiagnostic = $"locked stable replay-frame layout via {source} (type={best.Type}; fields={best.Layout})";
        return readNewFrames(best.Address);
    }

    private enum StableHeapScanState { Pending, Found, Exhausted }

    private readonly record struct StableHeapScanResult(
        StableHeapScanState State,
        StableReplayListCandidate Candidate,
        string Diagnostic);

    /// <summary>
    /// Background fallback for stable builds whose replay list is not strongly
    /// reachable from the ruleset object graph. Only List&lt;T&gt; instances whose
    /// T has the invariant replay-frame primitive shape are value-validated.
    /// </summary>
    private sealed class StableHeapDiscovery : IDisposable
    {
        private readonly StableClrReplayReader reader;
        private readonly int graphAttempt;
        private readonly CancellationTokenSource cancellation = new();
        private readonly Task<StableHeapScanResult> scanTask;
        private int objectsSeen;
        private int shapedListsSeen;
        private int populatedListsSeen;

        public StableHeapDiscovery(StableClrReplayReader reader, int graphAttempt)
        {
            this.reader = reader;
            this.graphAttempt = graphAttempt;
            scanTask = Task.Factory.StartNew(
                scan,
                cancellation.Token,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            Diagnostic = pendingDiagnostic();
        }

        public string Diagnostic { get; private set; }

        public StableHeapScanResult ScanStep()
        {
            if (!scanTask.IsCompleted)
            {
                Diagnostic = pendingDiagnostic();
                return new StableHeapScanResult(StableHeapScanState.Pending, default, Diagnostic);
            }

            try { return scanTask.GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { return exhausted(cancelled: true); }
            catch (Exception ex)
            {
                Diagnostic = $"stable typed heap fallback failed: {ex.GetType().Name}: {ex.Message}";
                return new StableHeapScanResult(StableHeapScanState.Exhausted, default, Diagnostic);
            }
        }

        private StableHeapScanResult scan()
        {
            try { Thread.CurrentThread.Priority = ThreadPriority.BelowNormal; }
            catch { }

            // The bridge attaches and prewarms in stable's menus. ClrMD caches
            // heap segments at that point, so a later whole-heap enumeration
            // otherwise walks the pre-attempt object map and cannot see the
            // replay list allocated for gameplay. Refresh only here, on the
            // bridge's isolated below-normal worker, after the bounded graph
            // search has failed and before no other CLR operation is active.
            reader.runtime.FlushCachedData();
            cancellation.Token.ThrowIfCancellationRequested();

            var matches = new List<StableReplayListCandidate>();
            var metadataRejectedListTypes = new HashSet<ulong>();
            using IEnumerator<ClrObject> objects = reader.runtime.Heap.EnumerateObjects().GetEnumerator();
            while (!cancellation.IsCancellationRequested
                   && objectsSeen < StableGraphDiscoveryPolicy.MaximumHeapObjects)
            {
                bool moved;
                ClrObject obj = default;
                try
                {
                    moved = objects.MoveNext();
                    if (moved)
                        obj = objects.Current;
                }
                catch
                {
                    moved = false;
                }
                if (!moved)
                    break;

                Interlocked.Increment(ref objectsSeen);
                ClrType? type;
                try { type = obj.IsNull ? null : obj.Type; }
                catch { continue; }
                if (!isGenericList(type)
                    || metadataRejectedListTypes.Contains(type!.MethodTable))
                    continue;

                Interlocked.Increment(ref shapedListsSeen);
                var candidate = new StableListCandidateDiscovery(reader, obj, depth: 0);
                StableListCandidateStep step;
                do
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    var timer = Stopwatch.StartNew();
                    int operations = 0;
                    step = candidate.ScanStep(timer, ref operations);
                }
                while (step == StableListCandidateStep.Pending);

                if (candidate.Populated)
                    Interlocked.Increment(ref populatedListsSeen);
                if (candidate.Populated && !candidate.MetadataShaped)
                    metadataRejectedListTypes.Add(type.MethodTable);
                if (step == StableListCandidateStep.Matched)
                    matches.Add(candidate.Result);
            }

            cancellation.Token.ThrowIfCancellationRequested();
            if (matches.Count == 0)
                return exhausted(cancelled: false);

            // Active recording is the replay-frame list that continues growing.
            // Recheck all exact-shape matches after a short observation window
            // instead of accidentally locking a cached, already-complete replay.
            Thread.Sleep(100);
            StableReplayListCandidate best = matches
                .Select(candidate => (Candidate: candidate, Growth: currentCount(candidate) - candidate.Count))
                .OrderByDescending(item => item.Growth > 0)
                .ThenByDescending(item => item.Growth)
                .ThenByDescending(item => item.Candidate.Address)
                .First().Candidate;
            Diagnostic = $"stable typed heap fallback matched replay list after {objectsSeen} objects (scan attempt {graphAttempt}/{StableGraphDiscoveryPolicy.MaximumAttempts}, shaped-lists={shapedListsSeen}, populated={populatedListsSeen}, matches={matches.Count})";
            return new StableHeapScanResult(StableHeapScanState.Found, best, Diagnostic);
        }

        private int currentCount(StableReplayListCandidate candidate)
        {
            try
            {
                ClrObject list = reader.runtime.Heap.GetObject(candidate.Address);
                return tryRead(list, "_size", out int size) ? size : candidate.Count;
            }
            catch { return candidate.Count; }
        }

        private StableHeapScanResult exhausted(bool cancelled)
        {
            string reason = cancelled
                ? "was cancelled"
                : objectsSeen >= StableGraphDiscoveryPolicy.MaximumHeapObjects
                    ? $"reached object cap {StableGraphDiscoveryPolicy.MaximumHeapObjects}"
                    : "did not find a populated replay list";
            Diagnostic = $"stable typed heap fallback {reason} (scan attempt {graphAttempt}/{StableGraphDiscoveryPolicy.MaximumAttempts}, objects={objectsSeen}, shaped-lists={shapedListsSeen}, populated={populatedListsSeen})";
            return new StableHeapScanResult(StableHeapScanState.Exhausted, default, Diagnostic);
        }

        private string pendingDiagnostic()
            => $"stable typed heap fallback in background (scan attempt {graphAttempt}/{StableGraphDiscoveryPolicy.MaximumAttempts}, objects={Volatile.Read(ref objectsSeen)}, shaped-lists={Volatile.Read(ref shapedListsSeen)}, populated={Volatile.Read(ref populatedListsSeen)})";

        public void Dispose()
        {
            try { cancellation.Cancel(); }
            catch { }
        }
    }

    /// <summary>
    /// Resumable breadth-first CLR graph traversal. Both reference enumeration
    /// and replay-list validation retain their cursor across calls, so each
    /// bridge poll cooperatively stops at the same three-millisecond deadline.
    /// </summary>
    private sealed class StableGraphDiscovery : IDisposable
    {
        private readonly StableClrReplayReader reader;
        private readonly uint rulesetAddress;
        private readonly uint gameplayAddress;
        private readonly int attempt;
        private readonly LinkedList<(ulong Address, int Depth)> pending = new();
        private readonly HashSet<ulong> scheduled = new();
        private IEnumerator<ClrObject>? referenceEnumerator;
        private int referenceDepth;
        private StableListCandidateDiscovery? candidateDiscovery;
        private StableReplayListCandidate best;
        private int reachableSeen;
        private int listsSeen;
        private int populatedLists;
        private int frameShapedLists;
        private int metadataFilteredLists;
        private int metadataPrioritizedLists;
        private long referencesSeen;
        private bool objectCapReached;

        public StableGraphDiscovery(
            StableClrReplayReader reader,
            uint rulesetAddress,
            uint gameplayAddress,
            int attempt)
        {
            this.reader = reader;
            this.rulesetAddress = rulesetAddress;
            this.gameplayAddress = gameplayAddress;
            this.attempt = attempt;
            foreach (uint root in StableGraphDiscoveryPolicy.DiscoveryRoots(rulesetAddress, gameplayAddress))
            {
                pending.AddLast((root, 0));
                scheduled.Add(root);
            }
            Diagnostic = pendingDiagnostic();
        }

        public string Diagnostic { get; private set; }

        public StableGraphScanResult ScanStep()
        {
            var timer = Stopwatch.StartNew();
            int objectsThisPoll = 0;
            int referencesThisPoll = 0;
            int candidateOperationsThisPoll = 0;
            while (StableGraphDiscoveryPolicy.HasBudget(
                       timer.Elapsed,
                       objectsThisPoll,
                       referencesThisPoll,
                       candidateOperationsThisPoll))
            {
                if (candidateDiscovery is not null)
                {
                    StableListCandidateStep candidateStep = candidateDiscovery.ScanStep(
                        timer,
                        ref candidateOperationsThisPoll);
                    if (candidateStep == StableListCandidateStep.Pending)
                        return pendingResult();

                    StableListCandidateDiscovery completed = candidateDiscovery;
                    candidateDiscovery = null;
                    if (completed.Populated)
                        populatedLists++;
                    if (completed.FrameShaped)
                        frameShapedLists++;
                    if (candidateStep == StableListCandidateStep.Matched)
                    {
                        // Breadth-first traversal reaches the replay list closest
                        // to the live gameplay root first. Its shape validation is
                        // deliberately strict; locking it immediately avoids
                        // spending the first half of a short map searching for a
                        // merely larger duplicate elsewhere in the object graph.
                        best = completed.Result;
                        return complete();
                    }
                    beginReferences(completed.Object, completed.Depth);
                    continue;
                }

                if (referenceEnumerator is not null)
                {
                    if (scheduled.Count >= StableGraphDiscoveryPolicy.MaximumObjects)
                    {
                        objectCapReached = true;
                        endReferences();
                        continue;
                    }
                    bool moved;
                    ClrObject child = default;
                    try
                    {
                        moved = referenceEnumerator.MoveNext();
                        if (moved)
                            child = referenceEnumerator.Current;
                    }
                    catch
                    {
                        moved = false;
                    }
                    referencesThisPoll++;
                    referencesSeen++;
                    if (!moved)
                    {
                        endReferences();
                        continue;
                    }
                    if (child.IsNull || child.Address == 0 || scheduled.Contains(child.Address))
                        continue;
                    scheduled.Add(child.Address);
                    if (isStructurallyReplayList(child.Type))
                    {
                        // A List<T> exposes T through its _items field metadata.
                        // Put replay-frame-shaped lists at the front before the
                        // broad gameplay graph can bury them behind thousands of
                        // unrelated UI and ruleset objects.
                        pending.AddFirst((child.Address, referenceDepth));
                        metadataPrioritizedLists++;
                    }
                    else
                    {
                        pending.AddLast((child.Address, referenceDepth));
                    }
                    continue;
                }

                if (pending.Count == 0)
                    return complete();

                (ulong address, int depth) = pending.First!.Value;
                pending.RemoveFirst();
                objectsThisPoll++;
                ClrObject obj;
                try { obj = reader.runtime.Heap.GetObject(address); }
                catch { continue; }
                if (obj.IsNull || obj.Type is null)
                    continue;

                reachableSeen++;
                if (obj.Type.Name?.StartsWith("System.Collections.Generic.List<", StringComparison.Ordinal) == true)
                {
                    listsSeen++;
                    if (tryGetReplayFrameShape(obj.Type, out bool replayFrameShape) && !replayFrameShape)
                    {
                        // The generic element metadata is authoritative for the
                        // field kinds. Skip the expensive 64-item value sample
                        // for lists that cannot possibly contain replay frames,
                        // while still traversing them for nested references.
                        metadataFilteredLists++;
                        beginReferences(obj, depth);
                        continue;
                    }
                    candidateDiscovery = new StableListCandidateDiscovery(reader, obj, depth);
                    continue;
                }
                beginReferences(obj, depth);
            }
            return pendingResult();
        }

        private void beginReferences(ClrObject obj, int depth)
        {
            if (depth >= StableGraphDiscoveryPolicy.MaximumDepth || obj.Type is null || obj.Type.IsString)
                return;
            try
            {
                referenceEnumerator = obj
                    .EnumerateReferences(carefully: true, considerDependantHandles: false)
                    .GetEnumerator();
                referenceDepth = depth + 1;
            }
            catch
            {
                referenceEnumerator = null;
            }
        }

        private void endReferences()
        {
            try { referenceEnumerator?.Dispose(); }
            catch { }
            referenceEnumerator = null;
        }

        private StableGraphScanResult pendingResult()
        {
            Diagnostic = pendingDiagnostic();
            return new StableGraphScanResult(StableGraphScanState.Pending, default, Diagnostic);
        }

        private StableGraphScanResult complete()
        {
            string cap = objectCapReached ? $", object-cap={StableGraphDiscoveryPolicy.MaximumObjects}" : "";
            string metadata = $", metadata-filtered={metadataFilteredLists}, metadata-prioritized={metadataPrioritizedLists}";
            if (best.Address != 0)
            {
                Diagnostic = $"stable replay graph discovery completed in bounded slices (attempt {attempt}/{StableGraphDiscoveryPolicy.MaximumAttempts}, roots={scheduledRootDescription()}, reachable={reachableSeen}, references={referencesSeen}, lists={listsSeen}, populated={populatedLists}, frame-shaped={frameShapedLists}{metadata}{cap})";
                return new StableGraphScanResult(StableGraphScanState.Found, best, Diagnostic);
            }

            Diagnostic = $"attached to stable ruleset 0x{rulesetAddress:x8}, gameplay=0x{gameplayAddress:x8}; replay list not matched in bounded discovery attempt {attempt}/{StableGraphDiscoveryPolicy.MaximumAttempts} (roots={scheduledRootDescription()}, reachable={reachableSeen}, references={referencesSeen}, lists={listsSeen}, populated={populatedLists}, frame-shaped={frameShapedLists}{metadata}{cap})";
            return new StableGraphScanResult(StableGraphScanState.Exhausted, default, Diagnostic);
        }

        private string pendingDiagnostic()
            => $"stable replay graph discovery {attempt}/{StableGraphDiscoveryPolicy.MaximumAttempts} in progress (roots={scheduledRootDescription()}, reachable={reachableSeen}, queued={pending.Count}, references={referencesSeen}, lists={listsSeen}, populated={populatedLists}, frame-shaped={frameShapedLists}, metadata-filtered={metadataFilteredLists}, metadata-prioritized={metadataPrioritizedLists}; slice<={StableGraphDiscoveryPolicy.MaximumPollDuration.TotalMilliseconds:0}ms/{StableGraphDiscoveryPolicy.MaximumObjectsPerPoll} objects)";

        private string scheduledRootDescription()
            => gameplayAddress >= 0x10000 && gameplayAddress != rulesetAddress
                ? "ruleset+child"
                : "ruleset";

        public void Dispose()
        {
            endReferences();
            candidateDiscovery = null;
            pending.Clear();
            scheduled.Clear();
        }
    }

    private static bool isStructurallyReplayList(ClrType? type)
        => type is not null
           && type.Name?.StartsWith("System.Collections.Generic.List<", StringComparison.Ordinal) == true
           && tryGetReplayFrameShape(type, out bool replayFrameShape)
           && replayFrameShape;

    private static bool isGenericList(ClrType? type)
        => type?.Name?.StartsWith("System.Collections.Generic.List<", StringComparison.Ordinal) == true;

    private static bool tryGetReplayFrameShape(ClrType listType, out bool replayFrameShape)
    {
        replayFrameShape = false;
        try
        {
            ClrType? elementType = listType.GetFieldByName("_items")?.Type?.ComponentType;
            if (elementType is null)
                return false;

            ClrInstanceField[] fields = elementType.Fields.ToArray();
            int floats = fields.Count(field => field.ElementType == ClrElementType.Float);
            int integers = fields.Count(field =>
                field.ElementType is ClrElementType.Int32 or ClrElementType.UInt32
                || field.Type?.IsEnum == true);
            int booleans = fields.Count(field => field.ElementType == ClrElementType.Boolean);
            replayFrameShape = StableGraphDiscoveryPolicy.IsReplayFrameFieldShape(
                fields.Length,
                floats,
                integers,
                booleans);
            return true;
        }
        catch
        {
            // Missing metadata must preserve the old value-based fallback.
            return false;
        }
    }

    private enum StableListCandidateStep { Pending, NotMatched, Matched }

    /// <summary>
    /// Incremental validation for one List&lt;T&gt;. Sampling object addresses and
    /// reading candidate frame fields are CLR operations too, so they share the
    /// graph poll's deadline instead of hiding an unbounded inner loop.
    /// </summary>
    private sealed class StableListCandidateDiscovery
    {
        private enum Phase
        {
            ReadList,
            ReadItems,
            ReadSampleAddresses,
            MaterializeSamples,
            PrepareFields,
            ReadIntValues,
            ReadFloatValues,
            ComputeIntValidity,
            ComputeFloatValidity,
            MatchFields,
            ReadTailAddress,
            ReadTailObject,
            ReadTailTime,
            Completed,
        }

        private readonly StableClrReplayReader reader;
        private readonly ClrObject list;
        private Phase phase;
        private int size;
        private ClrArray array;
        private int[] sampleIndices = [];
        private ulong[] sampleAddresses = [];
        private readonly List<ClrObject> samples = [];
        private int sampleIndex;
        private ClrType? frameType;
        private ClrInstanceField[] allFields = [];
        private ClrInstanceField[] intFields = [];
        private ClrInstanceField[] floatFields = [];
        private int[][] intValues = [];
        private float[][] floatValues = [];
        private bool[] intReadable = [];
        private bool[] floatReadable = [];
        private bool[] timeValid = [];
        private bool[] buttonsValid = [];
        private bool[] coordinateValid = [];
        private int fieldIndex;
        private int fieldSampleIndex;
        private int validityIndex;
        private int timeIndex;
        private int buttonsIndex;
        private int xIndex;
        private int yIndex;
        private string selectedTime = "";
        private string selectedButtons = "";
        private string selectedX = "";
        private string selectedY = "";
        private ulong tailAddress;
        private ClrObject tailObject;
        private StableListCandidateStep completedStep;

        public StableListCandidateDiscovery(StableClrReplayReader reader, ClrObject list, int depth)
        {
            this.reader = reader;
            this.list = list;
            Depth = depth;
        }

        public ClrObject Object => list;
        public int Depth { get; }
        public bool Populated { get; private set; }
        public bool MetadataShaped { get; private set; }
        public bool FrameShaped { get; private set; }
        public StableReplayListCandidate Result { get; private set; }

        public StableListCandidateStep ScanStep(Stopwatch timer, ref int operations)
        {
            if (phase == Phase.Completed)
                return completedStep;

            while (timer.Elapsed < StableGraphDiscoveryPolicy.MaximumPollDuration
                   && operations < StableGraphDiscoveryPolicy.MaximumCandidateOperationsPerPoll)
            {
                switch (phase)
                {
                    case Phase.ReadList:
                        operations++;
                        if (!tryRead(list, "_size", out size) || size < 2 || size > 1_000_000)
                            return complete(StableListCandidateStep.NotMatched);
                        Populated = true;
                        phase = Phase.ReadItems;
                        break;

                    case Phase.ReadItems:
                        operations++;
                        if (!tryReadObject(list, "_items", out ClrObject items) || items.IsNull || !items.IsArray)
                            return complete(StableListCandidateStep.NotMatched);
                        array = items.AsArray();
                        int count = Math.Min(size, 64);
                        sampleIndices = new int[count];
                        sampleAddresses = new ulong[count];
                        for (int index = 0; index < count; index++)
                        {
                            sampleIndices[index] = count == size
                                ? index
                                : (int)Math.Round(index * (size - 1d) / (count - 1d));
                        }
                        phase = Phase.ReadSampleAddresses;
                        break;

                    case Phase.ReadSampleAddresses:
                        if (sampleIndex >= sampleIndices.Length)
                        {
                            sampleIndex = 0;
                            phase = Phase.MaterializeSamples;
                            break;
                        }
                        operations++;
                        try { sampleAddresses[sampleIndex] = reader.readObjectAddressAt(array, sampleIndices[sampleIndex]); }
                        catch { return complete(StableListCandidateStep.NotMatched); }
                        sampleIndex++;
                        break;

                    case Phase.MaterializeSamples:
                        if (sampleIndex >= sampleAddresses.Length)
                        {
                            if (samples.Count < 2)
                                return complete(StableListCandidateStep.NotMatched);
                            phase = Phase.PrepareFields;
                            break;
                        }
                        operations++;
                        ulong address = sampleAddresses[sampleIndex++];
                        if (address == 0)
                            break;
                        try
                        {
                            ClrObject sample = reader.runtime.Heap.GetObject(address);
                            if (!sample.IsNull)
                                samples.Add(sample);
                        }
                        catch { return complete(StableListCandidateStep.NotMatched); }
                        break;

                    case Phase.PrepareFields:
                        operations++;
                        try
                        {
                            frameType = samples[0].Type;
                            if (frameType is null || samples.Any(sample => sample.Type != frameType))
                                return complete(StableListCandidateStep.NotMatched);
                            allFields = frameType.Fields.ToArray();
                        }
                        catch
                        {
                            return complete(StableListCandidateStep.NotMatched);
                        }
                        floatFields = allFields.Where(field => field.ElementType == ClrElementType.Float).ToArray();
                        intFields = allFields.Where(field => field.ElementType is ClrElementType.Int32 or ClrElementType.UInt32 || field.Type?.IsEnum == true).ToArray();
                        int booleans = allFields.Count(field => field.ElementType == ClrElementType.Boolean);
                        // Stable's replay frame is a small leaf object: X/Y,
                        // input flags, button-state enum and timestamp.
                        if (!StableGraphDiscoveryPolicy.IsReplayFrameFieldShape(
                                allFields.Length,
                                floatFields.Length,
                                intFields.Length,
                                booleans))
                            return complete(StableListCandidateStep.NotMatched);
                        MetadataShaped = true;

                        intValues = new int[intFields.Length][];
                        floatValues = new float[floatFields.Length][];
                        intReadable = new bool[intFields.Length];
                        floatReadable = new bool[floatFields.Length];
                        timeValid = new bool[intFields.Length];
                        buttonsValid = new bool[intFields.Length];
                        coordinateValid = new bool[floatFields.Length];
                        for (int index = 0; index < intFields.Length; index++)
                            intValues[index] = new int[samples.Count];
                        for (int index = 0; index < floatFields.Length; index++)
                            floatValues[index] = new float[samples.Count];
                        Array.Fill(intReadable, true);
                        Array.Fill(floatReadable, true);
                        phase = Phase.ReadIntValues;
                        break;

                    case Phase.ReadIntValues:
                        if (fieldIndex >= intFields.Length)
                        {
                            fieldIndex = 0;
                            fieldSampleIndex = 0;
                            phase = Phase.ReadFloatValues;
                            break;
                        }
                        operations++;
                        try
                        {
                            intValues[fieldIndex][fieldSampleIndex] = samples[fieldSampleIndex]
                                .ReadField<int>(intFields[fieldIndex].Name!);
                            if (++fieldSampleIndex >= samples.Count)
                            {
                                fieldIndex++;
                                fieldSampleIndex = 0;
                            }
                        }
                        catch
                        {
                            intReadable[fieldIndex] = false;
                            fieldIndex++;
                            fieldSampleIndex = 0;
                        }
                        break;

                    case Phase.ReadFloatValues:
                        if (fieldIndex >= floatFields.Length)
                        {
                            validityIndex = 0;
                            phase = Phase.ComputeIntValidity;
                            break;
                        }
                        operations++;
                        try
                        {
                            floatValues[fieldIndex][fieldSampleIndex] = samples[fieldSampleIndex]
                                .ReadField<float>(floatFields[fieldIndex].Name!);
                            if (++fieldSampleIndex >= samples.Count)
                            {
                                fieldIndex++;
                                fieldSampleIndex = 0;
                            }
                        }
                        catch
                        {
                            floatReadable[fieldIndex] = false;
                            fieldIndex++;
                            fieldSampleIndex = 0;
                        }
                        break;

                    case Phase.ComputeIntValidity:
                        if (validityIndex >= intFields.Length)
                        {
                            validityIndex = 0;
                            phase = Phase.ComputeFloatValidity;
                            break;
                        }
                        operations++;
                        if (intReadable[validityIndex])
                        {
                            timeValid[validityIndex] = isTimeSeries(intValues[validityIndex]);
                            buttonsValid[validityIndex] = intValues[validityIndex].All(value => (value & ~0x1f) == 0);
                        }
                        validityIndex++;
                        break;

                    case Phase.ComputeFloatValidity:
                        if (validityIndex >= floatFields.Length)
                        {
                            phase = Phase.MatchFields;
                            break;
                        }
                        operations++;
                        coordinateValid[validityIndex] = floatReadable[validityIndex]
                            && floatValues[validityIndex].All(value => float.IsFinite(value) && value is >= -10_000 and <= 10_000);
                        validityIndex++;
                        break;

                    case Phase.MatchFields:
                        if (timeIndex >= intFields.Length)
                            return complete(StableListCandidateStep.NotMatched);
                        operations++;
                        int currentTime = timeIndex;
                        int currentButtons = buttonsIndex;
                        int currentX = xIndex;
                        int currentY = yIndex;
                        advanceFieldCombination();
                        if (currentTime == currentButtons || currentX == currentY
                            || !timeValid[currentTime] || !buttonsValid[currentButtons]
                            || !coordinateValid[currentX] || !coordinateValid[currentY])
                            break;
                        selectedTime = intFields[currentTime].Name!;
                        selectedButtons = intFields[currentButtons].Name!;
                        selectedX = floatFields[currentX].Name!;
                        selectedY = floatFields[currentY].Name!;
                        FrameShaped = true;
                        phase = Phase.ReadTailAddress;
                        break;

                    case Phase.ReadTailAddress:
                        operations++;
                        try { tailAddress = reader.readObjectAddressAt(array, size - 1); }
                        catch { return complete(StableListCandidateStep.NotMatched); }
                        if (tailAddress == 0)
                            return complete(StableListCandidateStep.NotMatched);
                        phase = Phase.ReadTailObject;
                        break;

                    case Phase.ReadTailObject:
                        operations++;
                        try { tailObject = reader.runtime.Heap.GetObject(tailAddress); }
                        catch { return complete(StableListCandidateStep.NotMatched); }
                        if (tailObject.IsNull)
                            return complete(StableListCandidateStep.NotMatched);
                        phase = Phase.ReadTailTime;
                        break;

                    case Phase.ReadTailTime:
                        operations++;
                        int lastTime;
                        try { lastTime = tailObject.ReadField<int>(selectedTime); }
                        catch { return complete(StableListCandidateStep.NotMatched); }
                        string layout = string.Join(",", allFields.Select(field => $"{field.ElementType}@{field.Offset}"));
                        Result = new StableReplayListCandidate(
                            list.Address,
                            size,
                            lastTime,
                            selectedTime,
                            selectedX,
                            selectedY,
                            selectedButtons,
                            frameType?.Name ?? "unknown",
                            layout);
                        return complete(StableListCandidateStep.Matched);

                    case Phase.Completed:
                        return completedStep;
                }
            }
            return StableListCandidateStep.Pending;
        }

        private void advanceFieldCombination()
        {
            if (++yIndex < floatFields.Length)
                return;
            yIndex = 0;
            if (++xIndex < floatFields.Length)
                return;
            xIndex = 0;
            if (++buttonsIndex < intFields.Length)
                return;
            buttonsIndex = 0;
            timeIndex++;
        }

        private static bool isTimeSeries(IReadOnlyList<int> values)
        {
            int orderedPairs = 0;
            int minimum = int.MaxValue;
            int maximum = int.MinValue;
            for (int index = 0; index < values.Count; index++)
            {
                int value = values[index];
                minimum = Math.Min(minimum, value);
                maximum = Math.Max(maximum, value);
                if (index + 1 < values.Count && value <= values[index + 1])
                    orderedPairs++;
            }
            return orderedPairs >= values.Count - 2 && (long)maximum - minimum >= 10;
        }

        private StableListCandidateStep complete(StableListCandidateStep result)
        {
            completedStep = result;
            phase = Phase.Completed;
            return result;
        }
    }

    private void ScheduleRediscovery(string reason)
    {
        graphDiscovery?.Dispose();
        graphDiscovery = null;
        heapDiscovery?.Dispose();
        heapDiscovery = null;
        listAddress = 0;
        timeField = null;
        xField = null;
        yField = null;
        buttonsField = null;
        emittedListCount = 0;
        unreadableFrameIndex = -1;
        unreadableFrameRetries = 0;
        lastEmittedTime = double.NegativeInfinity;
        rediscoveryDisabled = true;
        nextDiscoveryAt = DateTimeOffset.UtcNow + StableGraphDiscoveryPolicy.RediscoveryCooldown;
        LastDiagnostic = $"{reason}; cooling down before bounded rediscovery";
    }

    private IReadOnlyList<LazerReplayFrame> readNewFrames(ulong address)
    {
        var timer = Stopwatch.StartNew();
        ClrObject list = runtime.Heap.GetObject(address);
        if (list.IsNull || !tryRead(list, "_size", out int size) || size < 0 || size > 1_000_000)
            return [];
        if (size < emittedListCount)
        {
            ScheduleRediscovery("stable replay list rotated");
            return [];
        }
        if (size == emittedListCount)
            return [];
        if (!tryReadObject(list, "_items", out ClrObject items) || items.IsNull || !items.IsArray)
            return [];

        var startIndex = emittedListCount;
        var batchCount = Math.Min(size - startIndex, StableGraphDiscoveryPolicy.MaximumTailFramesPerPoll);
        ulong[] addresses = readObjectAddresses(items.AsArray(), startIndex, batchCount);
        if (addresses.Length != batchCount)
            return [];
        var result = new List<LazerReplayFrame>(addresses.Length);
        var consumedCount = 0;
        for (var offset = 0; offset < addresses.Length; offset++)
        {
            // emittedListCount is the resumable cursor. Always allow one frame
            // to make progress, then yield once this poll's live-read budget is
            // consumed; the remaining tail is picked up on the next poll.
            if (offset > 0 && timer.Elapsed >= StableGraphDiscoveryPolicy.MaximumPollDuration)
                break;
            ulong frameAddress = addresses[offset];
            ClrObject frame = frameAddress == 0 ? default : runtime.Heap.GetObject(frameAddress);
            var time = 0;
            var x = 0f;
            var y = 0f;
            var buttons = 0;
            var readable = frameAddress != 0
                && !frame.IsNull
                && tryRead(frame, timeField!, out time)
                && tryRead(frame, xField!, out x)
                && tryRead(frame, yField!, out y)
                && tryRead(frame, buttonsField!, out buttons);
            if (!readable)
            {
                var absoluteIndex = startIndex + offset;
                if (unreadableFrameIndex != absoluteIndex)
                {
                    unreadableFrameIndex = absoluteIndex;
                    unreadableFrameRetries = 1;
                    break;
                }
                if (++unreadableFrameRetries < MaximumUnreadableFrameRetries)
                    break;

                // A permanently invalid slot must not stall the entire tail.
                unreadableFrameIndex = -1;
                unreadableFrameRetries = 0;
                consumedCount++;
                continue;
            }

            unreadableFrameIndex = -1;
            unreadableFrameRetries = 0;
            consumedCount++;
            if (time < -200_000 || time > 86_400_000
                || x is < -10_000 or > 10_000 || y is < -10_000 or > 10_000
                || (buttons & ~0x1f) != 0
                || time < lastEmittedTime)
                continue;

            lastEmittedTime = time;
            result.Add(new LazerReplayFrame
            {
                MapTimeMs = time,
                MonotonicMs = time,
                X = x,
                Y = y,
                LeftPressed = (buttons & 0x05) != 0,
                RightPressed = (buttons & 0x0a) != 0,
                Focused = true,
                Sequence = startIndex + offset + 1,
            });
        }
        emittedListCount = startIndex + consumedCount;
        return result;
    }

    private ulong[] readObjectAddresses(ClrArray array, int start, int count)
    {
        if (target.DataReader.PointerSize == 4)
            return (array.ReadValues<uint>(start, count) ?? []).Select(value => (ulong)value).ToArray();
        return array.ReadValues<ulong>(start, count) ?? [];
    }

    private ulong readObjectAddressAt(ClrArray array, int index)
        => target.DataReader.PointerSize == 4
            ? array.ReadValues<uint>(index, 1)?[0] ?? 0
            : array.ReadValues<ulong>(index, 1)?[0] ?? 0;

    private static bool tryRead<T>(ClrObject obj, string field, out T value) where T : unmanaged
    {
        try { value = obj.ReadField<T>(field); return true; }
        catch { value = default; return false; }
    }

    private static bool tryReadObject(ClrObject obj, string field, out ClrObject value)
    {
        try { value = obj.ReadObjectField(field); return true; }
        catch { value = default; return false; }
    }

    private static Process? findStableProcess(string? expectedGameFolder)
    {
        foreach (string name in new[] { "osu!", "osu" })
        {
            Process[] processes = Process.GetProcessesByName(name);
            Process? match = null;
            foreach (Process process in processes)
            {
                try
                {
                    string? directory = Path.GetDirectoryName(process.MainModule?.FileName);
                    bool expected = !string.IsNullOrWhiteSpace(expectedGameFolder) && directory is not null
                        && Path.GetFullPath(directory).Equals(Path.GetFullPath(expectedGameFolder), StringComparison.OrdinalIgnoreCase);
                    bool stable = directory is not null && Directory.Exists(Path.Combine(directory, "Songs"));
                    if (!process.HasExited && (expected || stable))
                    {
                        match = process;
                        break;
                    }
                }
                catch { }
            }
            foreach (Process process in processes)
                if (!ReferenceEquals(process, match)) process.Dispose();
            if (match is not null)
                return match;
        }
        return null;
    }

    public void Dispose()
    {
        graphDiscovery?.Dispose();
        graphDiscovery = null;
        heapDiscovery?.Dispose();
        heapDiscovery = null;
        memory.Dispose();
        target.Dispose();
    }
}
#endif
