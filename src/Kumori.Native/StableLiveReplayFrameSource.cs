using System.Diagnostics;
using Kumori.Tracking;
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
            path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Kumori.StableFrameBridge", "bin", "Debug", "net10.0", "win-x86", "Kumori.StableFrameBridge.exe"));
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
        using (var currentProcess = Process.GetCurrentProcess())
        {
            start.ArgumentList.Add("--parent-pid");
            start.ArgumentList.Add(currentProcess.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            start.ArgumentList.Add("--parent-start-utc-ticks");
            start.ArgumentList.Add(currentProcess.StartTime.ToUniversalTime().Ticks.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        }
        if (!string.IsNullOrWhiteSpace(gameFolder))
        {
            start.ArgumentList.Add("--game-folder");
            start.ArgumentList.Add(gameFolder);
        }
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
