using System.ComponentModel;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Kumori.Core;
using Kumori.Tracking;

namespace Kumori.Native;

public sealed class LazerMemoryReplayFrameSource : ILazerReplayFrameSource, ILazerReplayFrameSnapshotSource, IAttemptAwareReplayFrameSource, IDisposable, IAsyncDisposable
{
    private static readonly string[] ProcessNames = ["osu!", "osu"];
    private static readonly TimeSpan ProcessSearchInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan TosuGameBaseHintInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan FinalTailDrainBudget = TimeSpan.FromMilliseconds(25);
    internal static readonly TimeSpan OffsetRefreshInterval = TimeSpan.FromHours(6);
    internal static readonly TimeSpan OffsetRefreshRetryInterval = TimeSpan.FromMinutes(15);
    private const int MaximumFinalTailPasses = 16;
    private readonly TimeSpan _pollInterval;
    private readonly IReplayFrameStatusSink _status;
    private readonly string? _offsetsPath;
    private readonly object _readerGate = new();
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly List<LazerReplayFrame> _attemptFrames = new();
    private long _lastSequence;
    private nint _lastFramesList;
    private nint _lastGameBase;
    private int? _lastProcessId;
    private int? _lastReplayFrameTimeOffset;
    private LazerMemoryOffsets? _replayDetectionOffsets;
    private int _replayDetectionOffsetsNetworkLoadStarted;
    private int _attemptActive;
    private long _attemptGeneration;
    private TaskCompletionSource _attemptStarted = NewAttemptSignal();
    private Process? _cachedProcess;
    private ProcessMemory? _cachedMemory;
    private LazerReplayFrameMemoryReader? _cachedReader;
    private LazerMemoryOffsets? _cachedReaderOffsets;
    private DateTimeOffset _nextProcessSearchAt;
    private DateTimeOffset _nextTosuGameBaseHintAt;
    private string? _cachedProcessName;
    private string? _cachedProcessPath;
    private int _disposed;

    public LazerMemoryReplayFrameSource(
        TimeSpan? pollInterval = null,
        IReplayFrameStatusSink? status = null,
        string? offsetsPath = null)
    {
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(16);
        _status = status ?? new DelegatingReplayFrameStatusSink();
        _offsetsPath = offsetsPath;
        WarmReplayDetectionOffsets();
    }

    public async IAsyncEnumerable<LazerReplayFrame> ReadFramesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0)
            yield break;

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCts.Token);
        var effectiveToken = linkedCts.Token;
        LazerMemoryOffsets? offsets = null;

        while (!effectiveToken.IsCancellationRequested)
        {
            // Keep the reader alive before tosu announces the attempt. osu!lazer
            // can enter Player before the first usable telemetry packet arrives;
            // pausing here used to make GameBase discovery start too late and an
            // entire short map could finish without a single captured frame.
            // Idle work is still one bounded, below-normal-priority slice per
            // interval. Once Player appears, frames are emitted into the capture
            // service's small rolling pre-attempt buffer.
            if (Volatile.Read(ref _attemptActive) == 0)
            {
                Task attemptStarted;
                lock (_readerGate)
                    attemptStarted = _attemptStarted.Task;
                await Task.WhenAny(
                        Task.Delay(LazerMemoryReadPolicy.DiscoveryStepInterval, effectiveToken),
                        attemptStarted)
                    .WaitAsync(effectiveToken);
            }
            else
            {
                await Task.Delay(_pollInterval, effectiveToken);
            }

            var attemptGeneration = Volatile.Read(ref _attemptGeneration);

            var publishedOffsets = Volatile.Read(ref _replayDetectionOffsets);
            if (publishedOffsets is not null && publishedOffsets != offsets)
            {
                // Offset refresh happens on its own background task. Observe
                // the immutable replacement here; GetReaderLocked will rebuild
                // the reader on the next bounded memory poll.
                offsets = publishedOffsets;
                var loadedOffsets = offsets;
                _status.Update(s =>
                {
                    s.Enabled = true;
                    s.State = "lazer_memory_starting";
                    s.Detail = $"Loaded osu!lazer offsets {loadedOffsets.OsuVersion}.";
                    s.LastError = null;
                });
            }
            if (offsets is null)
            {
                // Offset download/parse is prewarmed off the capture loop. A
                // song that starts before it completes must not perform file or
                // network I/O on the gameplay polling path.
                _status.Update(s =>
                {
                    s.Enabled = true;
                    s.State = "lazer_memory_offsets_warming";
                    s.Detail = "Waiting for osu!lazer memory offsets; persisted replay recovery remains available.";
                    s.LastError = null;
                });
                await Task.Delay(TimeSpan.FromMilliseconds(100), effectiveToken);
                continue;
            }

            // Vanilla tosu has already resolved GameBase by the time its normal
            // data loop becomes usable. Reuse that diagnostic as an untrusted
            // hint so we do not have to walk several gigabytes of managed heap.
            // The bounded file read is deliberately outside _readerGate and is
            // never reachable from StartAttempt/the telemetry callback.
            TosuGameBaseLogHint? tosuGameBaseHint = null;
            var now = DateTimeOffset.UtcNow;
            bool gameBaseMissing;
            lock (_readerGate)
                gameBaseMissing = _lastGameBase == 0 && (_cachedReader?.LastGameBase ?? 0) == 0;
            if (gameBaseMissing && now >= _nextTosuGameBaseHintAt)
            {
                _nextTosuGameBaseHintAt = now + TosuGameBaseHintInterval;
                tosuGameBaseHint = await TosuGameBaseLogHintReader.TryReadCurrentAsync(effectiveToken)
                    .ConfigureAwait(false);
            }

            IReadOnlyList<LazerReplayFrame> frames = Array.Empty<LazerReplayFrame>();
            string? waitingStatus = null;
            bool processChanged = false;
            bool processAvailable = false;

            // Cross-process memory polling can consume up to the strict 2 ms
            // budget each tick. Run that CPU/RPM slice below normal priority
            // and restore the shared worker thread before any await/yield.
            try
            {
                using (new BackgroundThreadPriorityScope())
                {
                    lock (_readerGate)
                    {
                        if (Volatile.Read(ref _attemptGeneration) != attemptGeneration)
                        {
                            continue;
                        }

                        var reader = GetReaderLocked(
                            offsets,
                            out processChanged,
                            tosuGameBaseHint?.ProcessId);
                        if (reader is not null)
                        {
                            processAvailable = true;
                            if (tosuGameBaseHint is { } hint && _lastProcessId == hint.ProcessId)
                                reader.TryAdoptValidatedGameBase(hint.GameBase);
                            frames = reader.ReadFramesAfter(_lastSequence, _lastFramesList);
                            CaptureReaderStateLocked(reader, frames);
                            waitingStatus = reader.LastStatus;
                        }
                    }
                }
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 5)
            {
                lock (_readerGate)
                {
                    ResetCachedPointersLocked(closeProcess: true);
                    _nextProcessSearchAt = DateTimeOffset.UtcNow + ProcessSearchInterval;
                }
                _status.Update(s =>
                {
                    s.State = "lazer_memory_access_denied";
                    s.Detail = "Could not read osu!lazer memory. Run the tool with matching elevation if osu!lazer is elevated.";
                    s.LastError = ex.Message;
                });
                continue;
            }
            catch (Exception ex)
            {
                lock (_readerGate)
                {
                    ResetCachedPointersLocked(closeProcess: true);
                    _nextProcessSearchAt = DateTimeOffset.UtcNow + ProcessSearchInterval;
                }
                _status.Update(s =>
                {
                    s.State = "lazer_memory_error";
                    s.Detail = "osu!lazer memory reader failed; waiting for the next poll.";
                    s.LastError = ex.Message;
                });
                continue;
            }

            if (!processAvailable)
            {
                _status.Update(s =>
                {
                    s.State = "osu_lazer_not_running";
                    s.Detail = "osu!lazer process was not found.";
                    s.LastError = null;
                    s.ProcessId = null;
                    s.ProcessName = null;
                    s.ProcessPath = null;
                });
                continue;
            }

            if (processChanged)
            {
                var processId = _lastProcessId;
                var processName = _cachedProcessName;
                var processPath = _cachedProcessPath;
                _status.Update(s =>
                {
                    s.ProcessId = processId;
                    s.ProcessName = processName;
                    s.ProcessPath = processPath;
                });
            }

            if (frames.Count == 0)
            {
                _status.Update(s =>
                {
                    s.State = "lazer_memory_waiting";
                    s.Detail = waitingStatus ?? "osu!lazer is running; replay frames are not available yet.";
                    s.LastError = null;
                });
                continue;
            }
            var lastFrame = frames[^1];
            _status.Update(s =>
            {
                s.State = "lazer_memory_frame";
                s.Detail = $"Reading replay frames from osu!lazer memory ({frames.Count} frame batch).";
                s.FramesEmitted += frames.Count;
                s.LastFrameMapTimeMs = lastFrame.MapTimeMs;
                s.LastFrameX = lastFrame.X;
                s.LastFrameY = lastFrame.Y;
                s.LastFrameLeftPressed = lastFrame.LeftPressed;
                s.LastFrameRightPressed = lastFrame.RightPressed;
                s.LastError = null;
            });

            foreach (var frame in frames)
            {
                yield return frame;
            }
        }
    }

    /// <summary>
    /// Keeps native replay detection warm when movement capture is disabled.
    /// Every scan remains byte/time bounded and runs below normal priority.
    /// </summary>
    public async Task PrewarmGameBaseAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCts.Token);
        var effectiveToken = linkedCts.Token;
        try
        {
            while (!effectiveToken.IsCancellationRequested)
            {
                await Task.Delay(LazerMemoryReadPolicy.DiscoveryStepInterval, effectiveToken)
                    .ConfigureAwait(false);
                if (Volatile.Read(ref _attemptActive) == 0)
                    TryWarmGameBase();
            }
        }
        catch (OperationCanceledException) when (
            _lifetimeCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
        }
    }

    public IReadOnlyList<LazerReplayFrame> ReadCurrentFramesSnapshot()
    {
        lock (_readerGate)
            return _attemptFrames.ToArray();
    }

    internal void WarmReplayDetectionOffsets()
    {
        if (Volatile.Read(ref _replayDetectionOffsets) is not null)
            return;

        // Constructors and packet-side replay detection may only touch the
        // small existing cache. A missing cache must never trigger network or
        // file replacement work outside the gameplay-idle coordinator.
        var offsets = LazerMemoryOffsets.LoadCached(_offsetsPath);
        if (offsets is not null)
            Volatile.Write(ref _replayDetectionOffsets, offsets);
    }

    public async Task EnsureReplayDetectionOffsetsAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        WarmReplayDetectionOffsets();
        if (Interlocked.Exchange(ref _replayDetectionOffsetsNetworkLoadStarted, 1) != 0)
            return;

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCts.Token);
        var effectiveToken = linkedCts.Token;
        try
        {
            // An explicit path is a diagnostic/development override and must
            // never be replaced from the public tosu URL.
            if (!string.IsNullOrWhiteSpace(_offsetsPath))
            {
                if (Volatile.Read(ref _replayDetectionOffsets) is null)
                {
                    var explicitOffsets = await LazerMemoryOffsets.LoadAsync(_offsetsPath, effectiveToken)
                        .ConfigureAwait(false);
                    effectiveToken.ThrowIfCancellationRequested();
                    Volatile.Write(ref _replayDetectionOffsets, explicitOffsets);
                }
                return;
            }

            while (!effectiveToken.IsCancellationRequested)
            {
                var nextCheck = OffsetRefreshInterval;
                try
                {
                    var current = Volatile.Read(ref _replayDetectionOffsets);
                    if (current is null)
                    {
                        var loaded = await LazerMemoryOffsets.LoadAsync(null, effectiveToken)
                            .ConfigureAwait(false);
                        effectiveToken.ThrowIfCancellationRequested();
                        Volatile.Write(ref _replayDetectionOffsets, loaded);
                    }
                    else
                    {
                        var refresh = await LazerMemoryOffsets.RefreshCachedAsync(
                                current,
                                cancellationToken: effectiveToken)
                            .ConfigureAwait(false);
                        effectiveToken.ThrowIfCancellationRequested();
                        if (refresh.Updated)
                            Volatile.Write(ref _replayDetectionOffsets, refresh.Offsets);
                    }
                }
                catch (Exception ex) when (!effectiveToken.IsCancellationRequested)
                {
                    // Keep a validated last-known-good cache. A transient
                    // network or malformed upstream response must not stop live
                    // capture, and the shorter retry interval heals it later.
                    nextCheck = OffsetRefreshRetryInterval;
                    if (Volatile.Read(ref _replayDetectionOffsets) is null)
                    {
                        _status.Update(s =>
                        {
                            s.Enabled = true;
                            s.State = "lazer_memory_offsets_warming";
                            s.Detail = "Could not load osu!lazer offsets; retrying in the background.";
                            s.LastError = ex.Message;
                        });
                    }
                }

                await Task.Delay(nextCheck, effectiveToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (
            _lifetimeCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            Interlocked.Exchange(ref _replayDetectionOffsetsNetworkLoadStarted, 0);
        }
    }

    private void TryWarmGameBase()
    {
        try
        {
            var offsets = Volatile.Read(ref _replayDetectionOffsets)
                          ?? LazerMemoryOffsets.LoadCached(_offsetsPath);
            if (offsets is null)
                return;
            Volatile.Write(ref _replayDetectionOffsets, offsets);

            using (new BackgroundThreadPriorityScope())
                lock (_readerGate)
                {
                    var reader = GetReaderLocked(offsets, out _);
                    if (reader is null)
                        return;
                    reader.WarmGameBase();
                    if (reader.LastGameBase != 0)
                        _lastGameBase = reader.LastGameBase;
                }
        }
        catch
        {
            // Prewarming is opportunistic. Live capture reports actionable
            // access and offset errors through the normal status path.
        }
    }

    internal bool IsWatchingReplay()
    {
        var offsets = Volatile.Read(ref _replayDetectionOffsets);
        if (offsets is null)
        {
            offsets = LazerMemoryOffsets.LoadCached(_offsetsPath);
            if (offsets is not null)
                Volatile.Write(ref _replayDetectionOffsets, offsets);
        }
        if (offsets is null)
            return false;
        if (offsets.PlayerDrawableRuleset < 0 || offsets.DrawableRulesetReplayScore < 0)
            return false;

        lock (_readerGate)
        {
            try
            {
                var reader = GetReaderLocked(offsets, out _);
                if (reader is null)
                    return false;
                // Official tosu offsets describe the exact managed layout of
                // ppy's published client. The Kumori-branded fork intentionally
                // keeps the public version for tosu compatibility, but a local
                // publish can lay out managed fields differently. Do not let its
                // unreliable ReplayScore offset veto genuine plays. Official
                // lazer and every other build retain the existing behaviour.
                if (IsKumoriCustomClient(_cachedProcessPath))
                    return false;
                var replay = reader.IsWatchingReplay();
                if (reader.LastGameBase != 0)
                    _lastGameBase = reader.LastGameBase;
                return replay;
            }
            catch
            {
                ResetCachedPointersLocked(closeProcess: true);
                _nextProcessSearchAt = DateTimeOffset.UtcNow + ProcessSearchInterval;
                return false;
            }
        }
    }

    internal static bool IsKumoriCustomProduct(string? productName, string? fileDescription) =>
        string.Equals(productName, "Kumori", StringComparison.OrdinalIgnoreCase)
        || string.Equals(fileDescription, "Kumori", StringComparison.OrdinalIgnoreCase);

    private static bool IsKumoriCustomClient(string? processPath)
    {
        if (string.IsNullOrWhiteSpace(processPath))
            return false;
        try
        {
            var version = FileVersionInfo.GetVersionInfo(processPath);
            return IsKumoriCustomProduct(version.ProductName, version.FileDescription);
        }
        catch
        {
            return false;
        }
    }

    public void StartAttempt(AttemptStart start)
    {
        lock (_readerGate)
        {
            Interlocked.Increment(ref _attemptGeneration);
            _cachedReader?.ResetAttemptSearch();
            // The always-on reader may already be inside the current replay
            // list. Rewind the cursor so all frames that existed before tosu's
            // StartAttempt packet are emitted again into this attempt.
            _lastSequence = 0;
            _lastFramesList = 0;
            _attemptFrames.Clear();
            Volatile.Write(ref _attemptActive, 1);
            _attemptStarted.TrySetResult();
        }
    }

    public void UpdateAttempt(AttemptSnapshot snapshot) { }

    public void EndAttempt()
    {
        lock (_readerGate)
        {
            Interlocked.Increment(ref _attemptGeneration);
            // Drain only the frames appended since the previous 16 ms poll.
            // Retain the in-process attempt snapshot so finalization never has
            // to reread and recreate the entire replay list at the song boundary.
            if (_cachedReader is not null)
            {
                try
                {
                    var drain = Stopwatch.StartNew();
                    for (var pass = 0;
                         pass < MaximumFinalTailPasses && drain.Elapsed < FinalTailDrainBudget;
                         pass++)
                    {
                        var tail = _cachedReader.ReadFramesAfter(_lastSequence, _lastFramesList);
                        CaptureReaderStateLocked(_cachedReader, tail);
                        if (tail.Count == 0)
                            break;
                    }
                }
                catch
                {
                    // The normal capture buffer remains authoritative if the
                    // final incremental tail read races a screen transition.
                }
            }
            Volatile.Write(ref _attemptActive, 0);
            _attemptStarted = NewAttemptSignal();
        }
    }

    private static TaskCompletionSource NewAttemptSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void CaptureReaderStateLocked(
        LazerReplayFrameMemoryReader reader,
        IReadOnlyList<LazerReplayFrame> frames)
    {
        var previousSequence = _lastSequence;
        var beginsNewGeneration = LazerAttemptFrameBufferPolicy.BeginsNewGeneration(
            reader.FramesListChanged,
            previousSequence,
            frames);
        if (reader.LastGameBase != 0)
            _lastGameBase = reader.LastGameBase;
        if (reader.LastReplayFrameTimeOffset is { } timeOffset)
            _lastReplayFrameTimeOffset = timeOffset;
        if (beginsNewGeneration)
            _lastSequence = 0;
        if (reader.LastFramesList != 0)
            _lastFramesList = reader.LastFramesList;
        foreach (var frame in frames)
            _lastSequence = Math.Max(_lastSequence, frame.Sequence ?? _lastSequence);
        LazerAttemptFrameBufferPolicy.Append(
            _attemptFrames,
            frames,
            Volatile.Read(ref _attemptActive) != 0,
            beginsNewGeneration);
    }

    private void ResetCachedPointersLocked(bool closeProcess)
    {
        _lastSequence = 0;
        _lastFramesList = 0;
        _lastGameBase = 0;
        _lastReplayFrameTimeOffset = null;
        _lastProcessId = null;
        _cachedReader = null;
        _cachedReaderOffsets = null;
        if (!closeProcess)
            return;

        _cachedMemory?.Dispose();
        _cachedMemory = null;
        _cachedProcess?.Dispose();
        _cachedProcess = null;
        _cachedProcessName = null;
        _cachedProcessPath = null;
    }

    private LazerReplayFrameMemoryReader? GetReaderLocked(
        LazerMemoryOffsets offsets,
        out bool processChanged,
        int? preferredProcessId = null)
    {
        processChanged = false;
        if (_cachedProcess is not null)
        {
            try
            {
                if (_cachedProcess.HasExited)
                    ResetCachedPointersLocked(closeProcess: true);
            }
            catch
            {
                ResetCachedPointersLocked(closeProcess: true);
            }
        }

        // A current managed-tosu log identifies the exact osu! process for
        // which its GameBase was resolved. Prefer it over a newer unrelated
        // process, but only after normal likely-lazer filtering succeeds.
        if (_cachedProcess is not null
            && preferredProcessId is { } preferred
            && _cachedProcess.Id != preferred)
        {
            var hintedProcess = FindProcess(preferred);
            if (hintedProcess?.Id == preferred)
            {
                ResetCachedPointersLocked(closeProcess: true);
                try
                {
                    AttachProcessLocked(hintedProcess);
                }
                catch
                {
                    hintedProcess.Dispose();
                    throw;
                }
                processChanged = true;
            }
            else
            {
                hintedProcess?.Dispose();
            }
        }

        if (_cachedProcess is null)
        {
            var now = DateTimeOffset.UtcNow;
            if (now < _nextProcessSearchAt)
                return null;
            _nextProcessSearchAt = now + ProcessSearchInterval;

            var process = FindProcess(preferredProcessId);
            if (process is null)
                return null;

            try
            {
                AttachProcessLocked(process);
                processChanged = true;
            }
            catch
            {
                process.Dispose();
                throw;
            }
        }

        if (_cachedReader is null || _cachedReaderOffsets != offsets)
        {
            _cachedReaderOffsets = offsets;
            _cachedReader = new LazerReplayFrameMemoryReader(
                _cachedMemory!,
                offsets,
                _lastReplayFrameTimeOffset,
                _lastGameBase);
        }

        return _cachedReader;
    }

    private void AttachProcessLocked(Process process)
    {
        var processId = process.Id;
        var memory = ProcessMemory.Open(process);
        string processName;
        string? processPath;
        try
        {
            processName = process.ProcessName;
            processPath = SafeProcessPath(process);
        }
        catch
        {
            memory.Dispose();
            throw;
        }

        _cachedMemory = memory;
        _cachedProcess = process;
        _lastProcessId = processId;
        _cachedProcessName = processName;
        _cachedProcessPath = processPath;
    }

    private static string? SafeProcessPath(Process process)
    {
        try { return process.MainModule?.FileName; }
        catch { return null; }
    }

    private static Process? FindProcess(int? preferredProcessId = null)
    {
        var processes = new Dictionary<int, Process>();
        foreach (var name in ProcessNames)
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                if (!IsLikelyLazer(process))
                {
                    process.Dispose();
                    continue;
                }
                if (!processes.TryAdd(process.Id, process))
                {
                    process.Dispose();
                }
            }
        }

        var candidates = processes.Values
            .Select(process => new LazerProcessCandidate(
                process.Id,
                SafeStartTime(process)))
            .ToArray();
        var selectedId = LazerProcessSelectionPolicy.Select(candidates, preferredProcessId);
        Process? selected = null;
        foreach (var process in processes.Values)
        {
            if (process.Id == selectedId)
                selected = process;
            else
                process.Dispose();
        }
        return selected;
    }

    private static DateTime SafeStartTime(Process process)
    {
        try { return process.StartTime; }
        catch { return DateTime.MinValue; }
    }

    private static bool IsLikelyLazer(Process process)
    {
        try
        {
            string? directory = Path.GetDirectoryName(process.MainModule?.FileName);
            // Stable installations own Songs and Data/r next to osu!.exe.
            // Lazer may use the same process name, so never attach its 64-bit
            // reader to an obvious stable installation.
            return directory is not null
                   && !Directory.Exists(Path.Combine(directory, "Songs"))
                   && !Directory.Exists(Path.Combine(directory, "Data", "r"));
        }
        catch { return false; }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _lifetimeCts.Cancel();
        lock (_readerGate)
        {
            Interlocked.Increment(ref _attemptGeneration);
            Volatile.Write(ref _attemptActive, 0);
            _attemptStarted.TrySetResult();
            ResetCachedPointersLocked(closeProcess: true);
            _attemptFrames.Clear();
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
