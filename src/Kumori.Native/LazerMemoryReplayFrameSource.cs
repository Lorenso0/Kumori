using System.ComponentModel;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Kumori.Core;
using Kumori.Tracking;

namespace Kumori.Native;

public sealed class LazerMemoryReplayFrameSource : ILazerReplayFrameSource, ILazerReplayFrameSnapshotSource, IAttemptAwareReplayFrameSource
{
    private static readonly string[] ProcessNames = ["osu!", "osu"];
    private static readonly TimeSpan ProcessSearchInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan TosuGameBaseHintInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan FinalTailDrainBudget = TimeSpan.FromMilliseconds(25);
    private const int MaximumFinalTailPasses = 16;
    private readonly TimeSpan _pollInterval;
    private readonly IReplayFrameStatusSink _status;
    private readonly string? _offsetsPath;
    private readonly object _readerGate = new();
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
        LazerMemoryOffsets? offsets = null;

        while (!cancellationToken.IsCancellationRequested)
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
                        Task.Delay(LazerMemoryReadPolicy.DiscoveryStepInterval, cancellationToken),
                        attemptStarted)
                    .WaitAsync(cancellationToken);
            }
            else
            {
                await Task.Delay(_pollInterval, cancellationToken);
            }

            var attemptGeneration = Volatile.Read(ref _attemptGeneration);

            if (offsets is null)
            {
                // Offset download/parse is prewarmed off the capture loop. A
                // song that starts before it completes must not perform file or
                // network I/O on the gameplay polling path.
                offsets = Volatile.Read(ref _replayDetectionOffsets);
                if (offsets is null)
                {
                    _status.Update(s =>
                    {
                        s.Enabled = true;
                        s.State = "lazer_memory_offsets_warming";
                        s.Detail = "Waiting for osu!lazer memory offsets; persisted replay recovery remains available.";
                        s.LastError = null;
                    });
                    await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
                    continue;
                }
                var loadedOffsets = offsets;
                _status.Update(s =>
                {
                    s.Enabled = true;
                    s.State = "lazer_memory_starting";
                    s.Detail = $"Loaded osu!lazer offsets {loadedOffsets.OsuVersion}.";
                    s.LastError = null;
                });
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
                tosuGameBaseHint = await TosuGameBaseLogHintReader.TryReadCurrentAsync(cancellationToken)
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
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(LazerMemoryReadPolicy.DiscoveryStepInterval, cancellationToken)
                .ConfigureAwait(false);
            if (Volatile.Read(ref _attemptActive) == 0)
                TryWarmGameBase();
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
        WarmReplayDetectionOffsets();
        if (Volatile.Read(ref _replayDetectionOffsets) is not null)
            return;
        if (Interlocked.Exchange(ref _replayDetectionOffsetsNetworkLoadStarted, 1) != 0)
            return;

        try
        {
            var offsets = await LazerMemoryOffsets.LoadAsync(_offsetsPath, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            Volatile.Write(ref _replayDetectionOffsets, offsets);
        }
        catch
        {
            Interlocked.Exchange(ref _replayDetectionOffsetsNetworkLoadStarted, 0);
            throw;
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

}

internal readonly record struct TosuGameBaseLogHint(int ProcessId, nint GameBase);

internal static class TosuGameBaseLogHintReader
{
    internal const int MaximumHeadBytes = 32 * 1024;
    internal const int MaximumTailBytes = 64 * 1024;

    internal static async Task<TosuGameBaseLogHint?> TryReadCurrentAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var path = CurrentLogPaths()
                .Select(candidate => new FileInfo(candidate))
                .Where(file => file.Exists)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Select(file => file.FullName)
                .FirstOrDefault();
            if (path is null)
                return null;

            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var length = stream.Length;
            if (length <= 0)
                return null;

            var headLength = checked((int)Math.Min(length, MaximumHeadBytes));
            var headBytes = new byte[headLength];
            var headRead = await ReadAtMostAsync(stream, headBytes, cancellationToken)
                .ConfigureAwait(false);

            byte[] tailBytes = [];
            var tailStart = Math.Max(headRead, length - MaximumTailBytes);
            if (tailStart < length)
            {
                stream.Seek(tailStart, SeekOrigin.Begin);
                tailBytes = new byte[checked((int)(length - tailStart))];
                var tailRead = await ReadAtMostAsync(stream, tailBytes, cancellationToken)
                    .ConfigureAwait(false);
                if (tailRead != tailBytes.Length)
                    Array.Resize(ref tailBytes, tailRead);
            }

            var head = Encoding.UTF8.GetString(headBytes, 0, headRead);
            var tail = tailBytes.Length == 0 ? string.Empty : Encoding.UTF8.GetString(tailBytes);
            var segmentsContiguous = tailStart <= headRead;
            if (segmentsContiguous && tail.Length > 0)
            {
                head += tail;
                tail = string.Empty;
            }
            return TosuGameBaseLogHintParser.TryParse(
                    head,
                    tail,
                    out var hint,
                    segmentsContiguous)
                ? hint
                : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private static IEnumerable<string> CurrentLogPaths()
    {
        // Vanilla tosu writes beside its executable. Kumori moves closed logs
        // to the canonical log directory at startup, so retain that as a
        // fallback for installations which configured the canonical path.
        yield return Path.Combine(AppPaths.TosuDir, "logs", "latest.log");
        yield return Path.Combine(AppPaths.TosuLogDir, "latest.log");
    }

    private static async Task<int> ReadAtMostAsync(
        Stream stream,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken)
                .ConfigureAwait(false);
            if (count == 0)
                break;
            read += count;
        }
        return read;
    }
}

internal static class TosuGameBaseLogHintParser
{
    private const string ClientMarker = "Starting regular data loop for client ";
    private const string GameBaseMarker = "GameBase address updated:";

    internal static bool TryParse(
        string head,
        string tail,
        out TosuGameBaseLogHint hint,
        bool segmentsContiguous = false)
    {
        int? currentProcessId = null;
        nint currentGameBase = 0;
        ParseSegment(head, ref currentProcessId, ref currentGameBase);
        if (!string.IsNullOrEmpty(tail))
        {
            if (segmentsContiguous)
            {
                ParseSegment(tail, ref currentProcessId, ref currentGameBase);
            }
            else
            {
                // A bounded head/tail read can omit the middle of a large log.
                // Never associate an orphan address update in the tail with a
                // process marker from the head across that unknown gap.
                int? tailProcessId = null;
                nint tailGameBase = 0;
                ParseSegment(tail, ref tailProcessId, ref tailGameBase);
                if (tailProcessId is not null)
                {
                    currentProcessId = tailProcessId;
                    currentGameBase = tailGameBase;
                }
                else
                {
                    // The skipped range may contain a newer client marker even
                    // when the visible tail has no GameBase line. Across any
                    // unknown gap, only a self-contained tail marker/address
                    // pair is safe to adopt.
                    currentProcessId = null;
                    currentGameBase = 0;
                }
            }
        }

        if (currentProcessId is not { } processId || currentGameBase == 0)
        {
            hint = default;
            return false;
        }

        hint = new TosuGameBaseLogHint(processId, currentGameBase);
        return true;
    }

    private static void ParseSegment(
        string segment,
        ref int? currentProcessId,
        ref nint currentGameBase)
    {
        using var lines = new StringReader(segment);
        while (lines.ReadLine() is { } line)
        {
            var clientMarker = line.IndexOf(ClientMarker, StringComparison.Ordinal);
            if (clientMarker >= 0)
            {
                var pidText = line.AsSpan(clientMarker + ClientMarker.Length).Trim();
                var digitCount = 0;
                while (digitCount < pidText.Length && char.IsAsciiDigit(pidText[digitCount]))
                    digitCount++;
                currentProcessId = int.TryParse(
                    pidText[..digitCount],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var processId)
                        ? processId
                        : null;
                currentGameBase = 0;
                continue;
            }

            if (currentProcessId is null ||
                line.IndexOf(GameBaseMarker, StringComparison.Ordinal) < 0)
                continue;

            // Any update supersedes the older address, including an explicit
            // transition to undefined.
            currentGameBase = 0;
            var arrow = line.LastIndexOf("=>", StringComparison.Ordinal);
            if (arrow < 0)
                continue;
            var addressText = line.AsSpan(arrow + 2).Trim();
            var tokenLength = 0;
            if (addressText.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                tokenLength = 2;
            while (tokenLength < addressText.Length && IsHex(addressText[tokenLength]))
                tokenLength++;
            var token = addressText[..tokenLength];
            if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                token = token[2..];
            if (token.Length == 0 ||
                !ulong.TryParse(token, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var address) ||
                address > long.MaxValue)
                continue;
            currentGameBase = (nint)(long)address;
        }
    }

    private static bool IsHex(char value) =>
        char.IsAsciiDigit(value) || value is >= 'a' and <= 'f' or >= 'A' and <= 'F';
}

internal readonly record struct LazerProcessCandidate(int ProcessId, DateTime StartTime);

internal static class LazerProcessSelectionPolicy
{
    internal static int? Select(
        IReadOnlyList<LazerProcessCandidate> candidates,
        int? preferredProcessId)
    {
        if (preferredProcessId is { } preferred &&
            candidates.Any(candidate => candidate.ProcessId == preferred))
            return preferred;

        return candidates
            .OrderByDescending(candidate => candidate.StartTime)
            .Select(candidate => (int?)candidate.ProcessId)
            .FirstOrDefault();
    }
}

internal static class LazerAttemptFrameBufferPolicy
{
    internal static bool BeginsNewGeneration(
        bool framesListChanged,
        long previousSequence,
        IReadOnlyList<LazerReplayFrame> frames) =>
        framesListChanged ||
        (previousSequence > 0 &&
         frames.Count > 0 &&
         frames[0].Sequence is { } firstSequence &&
         firstSequence <= previousSequence);

    internal static void Append(
        List<LazerReplayFrame> attemptFrames,
        IReadOnlyList<LazerReplayFrame> frames,
        bool attemptActive,
        bool beginsNewGeneration)
    {
        if (!attemptActive)
            return;
        if (beginsNewGeneration)
            attemptFrames.Clear();
        if (frames.Count > 0)
            attemptFrames.AddRange(frames);
    }
}

internal static class TosuGameBaseAdoptionPolicy
{
    internal static bool ShouldAdopt(
        nint candidate,
        bool vtableMatches,
        bool screenStackUsable) =>
        candidate.ToInt64() > 0x10000 && vtableMatches && screenStackUsable;
}

internal static class LazerMemoryReadPolicy
{
    internal static readonly TimeSpan CachedReadBudget = TimeSpan.FromMilliseconds(2);
    internal static readonly TimeSpan DiscoveryReadBudget = TimeSpan.FromMilliseconds(4);
    // 1 MiB every 16 ms is about 62.5 MiB/s at most. That is fast enough to
    // finish discovery during lazer startup/menu time while remaining a small,
    // time-bounded, below-normal-priority memory-read workload. The previous
    // 250 ms cadence could require several minutes for a multi-gigabyte lazer
    // process, longer than a complete beatmap.
    internal static readonly TimeSpan DiscoveryStepInterval = TimeSpan.FromMilliseconds(16);
    internal const int DiscoveryBytesPerStep = 1024 * 1024;

    internal static bool ShouldDiscover(nint gameBase) => gameBase == 0;

    internal static bool ShouldRearmDiscovery(nint gameBase, bool discoveryExhausted) =>
        gameBase == 0 && discoveryExhausted;

    internal static bool MayAttemptUnit(bool isFirst, bool budgetExpired) =>
        isFirst || !budgetExpired;

    internal static int FindAlignedPointerOffset(
        ReadOnlySpan<byte> buffer,
        long expected,
        int searchOffset)
    {
        var alignedSearchOffset = (Math.Max(0, searchOffset) + sizeof(long) - 1) & ~(sizeof(long) - 1);
        for (var offset = alignedSearchOffset; offset <= buffer.Length - sizeof(long); offset += sizeof(long))
        {
            if (BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset, sizeof(long))) == expected)
                return offset;
        }
        return -1;
    }
}

internal sealed class LazerReplayFrameMemoryReader
{
    private static readonly byte[] ScalingContainerTargetDrawSizePattern =
    [
        0x00, 0x00, 0x80, 0x44,
        0x00, 0x00, 0x40, 0x44,
    ];

    private const int ScoreReplayOffset = 0x10;
    private const int ReplayFramesOffset = 0x8;
    private const int ReplayFramePositionOffset = 0x20;
    private const int ReplayFrameActionsOffset = 0x18;
    private const int MaxFrameCount = 1_000_000;
    private const int MaxFramesPerTick = 64;
    private const int DiscoveryChunkBytes = 1024 * 1024;
    private const int CachedGameBaseInvalidationChecks = 3;
    private static readonly TimeSpan CachedGameBaseValidationInterval = TimeSpan.FromSeconds(1);
    private static readonly int[] ReplayFrameActionOffsets = [ReplayFrameActionsOffset, ReplayFrameActionsOffset + 0x8];
    private static readonly int[] BootstrapDeltas = [0x24, 0x28, 0x2c, 0x20, 0x30, 0x1c, 0x34];
    private static readonly int[] ReplayFrameTimeOffsetCandidates = [0x8, 0x10, 0x18, 0x28, 0x30];

    private readonly ProcessMemory _memory;
    private readonly LazerMemoryOffsets _offsets;
    private readonly int? _preferredReplayFrameTimeOffset;
    private readonly nint _preferredGameBase;
    private readonly byte[] _discoveryBuffer = new byte[DiscoveryChunkBytes];
    private MemoryRegion[]? _discoveryRegions;
    private int _discoveryRegionIndex;
    private long _discoveryRegionOffset;
    private int _discoveryChunkSearchOffset;
    private DateTimeOffset _nextDiscoveryStepAt;
    private DateTimeOffset _nextBootstrapCandidateRetryAt;
    private bool _discoveryExhausted;
    private DiscoveryPhase _discoveryPhase;
    private nint _fallbackVtableMarker;
    private int _fallbackMarkerResumeRegionIndex;
    private long _fallbackMarkerResumeRegionOffset;
    private int _fallbackMarkerResumeSearchOffset;
    private int _invalidCachedGameBasePolls;
    private int _bootstrapCandidateIndex;
    private DateTimeOffset _nextCachedGameBaseValidationAt;
    private readonly List<nint> _bootstrapCandidates = [];
    private nint _timeOffsetSearchFramesList;
    private nint _timeOffsetSearchItems;
    private int _timeOffsetSearchSize;
    private int _timeOffsetSampleCount;
    private int _timeOffsetSampleStep;
    private int _timeOffsetCandidateIndex;
    private int _timeOffsetSampleIndex;
    private double? _timeOffsetPrevious;
    private double? _timeOffsetFirst;
    private double? _timeOffsetLast;
    private int _timeOffsetSaneCount;
    private bool _timeOffsetCandidateInvalid;
    private int? _timeOffsetBestOffset;
    private double _timeOffsetBestScore = double.NegativeInfinity;
    private nint _failedTimeOffsetFramesList;
    private nint _failedTimeOffsetItems;
    private DateTimeOffset _nextTimeOffsetSearchAt;

    public LazerReplayFrameMemoryReader(
        ProcessMemory memory,
        LazerMemoryOffsets offsets,
        int? preferredReplayFrameTimeOffset = null,
        nint preferredGameBase = 0)
    {
        _memory = memory;
        _offsets = offsets;
        _preferredReplayFrameTimeOffset = preferredReplayFrameTimeOffset;
        _preferredGameBase = preferredGameBase;
    }

    public string? LastStatus { get; private set; }
    public nint LastFramesList { get; private set; }
    public bool FramesListChanged { get; private set; }
    public int? LastReplayFrameTimeOffset { get; private set; }
    public nint LastGameBase { get; private set; }

    public void ResetAttemptSearch()
    {
        ResetReplayFrameTimeOffsetSearch();
        _failedTimeOffsetFramesList = 0;
        _failedTimeOffsetItems = 0;
        _nextTimeOffsetSearchAt = DateTimeOffset.MinValue;
        if (LazerMemoryReadPolicy.ShouldRearmDiscovery(LastGameBase, _discoveryExhausted))
        {
            _discoveryExhausted = false;
            _discoveryPhase = DiscoveryPhase.BootstrapPattern;
            _fallbackVtableMarker = 0;
            _fallbackMarkerResumeRegionIndex = 0;
            _fallbackMarkerResumeRegionOffset = 0;
            _fallbackMarkerResumeSearchOffset = 0;
            ResetDiscoveryCursor();
            _nextDiscoveryStepAt = DateTimeOffset.MinValue;
            _nextBootstrapCandidateRetryAt = DateTimeOffset.MinValue;
        }
    }

    public void WarmGameBase() =>
        _ = FindGameBase(allowDiscovery: true, DeadlineFromNow(TimeSpan.FromMilliseconds(3)));

    public bool TryAdoptValidatedGameBase(nint candidate)
    {
        var vtableMatches = IsGameBase(candidate);
        var screenStackUsable = vtableMatches && HasUsableScreenStack(candidate);
        if (!TosuGameBaseAdoptionPolicy.ShouldAdopt(
                candidate,
                vtableMatches,
                screenStackUsable))
        {
            LastStatus = "tosu GameBase hint did not pass native vtable and ScreenStack validation";
            return false;
        }

        LastGameBase = candidate;
        _invalidCachedGameBasePolls = 0;
        _nextCachedGameBaseValidationAt = DateTimeOffset.UtcNow + CachedGameBaseValidationInterval;
        LastStatus = null;
        return true;
    }

    public IReadOnlyList<LazerReplayFrame> ReadFramesAfter(long lastSequence, nint previousFramesList)
    {
        LastStatus = null;
        LastFramesList = 0;
        FramesListChanged = false;
        var allowDiscovery = LazerMemoryReadPolicy.ShouldDiscover(LastGameBase);
        var deadline = DeadlineFromNow(
            allowDiscovery
                ? LazerMemoryReadPolicy.DiscoveryReadBudget
                : LazerMemoryReadPolicy.CachedReadBudget);
        var players = FindPlayers(allowDiscovery, deadline);
        if (players.Count == 0)
        {
            LastStatus ??= "player screen unavailable";
            return Array.Empty<LazerReplayFrame>();
        }

        for (var index = 0; index < players.Count; index++)
        {
            if (!LazerMemoryReadPolicy.MayAttemptUnit(index == 0, BudgetExpired(deadline)))
                break;
            try
            {
                var frames = ReadFramesAfter(players[index], lastSequence, previousFramesList, deadline: deadline);
                if (frames.Count > 0)
                {
                    return frames;
                }
            }
            catch (Win32Exception ex)
            {
                LastStatus = $"player candidate unreadable: {ex.Message}";
            }
            catch (Exception ex)
            {
                LastStatus = $"player candidate failed: {ex.Message}";
            }
        }

        LastStatus ??= $"no readable replay frames from {players.Count} player candidate(s)";
        return Array.Empty<LazerReplayFrame>();
    }

    public IReadOnlyList<LazerReplayFrame> ReadAllFrames()
    {
        LastStatus = null;
        LastFramesList = 0;
        FramesListChanged = false;
        var players = FindPlayers(allowDiscovery: false, long.MaxValue);
        if (players.Count == 0)
        {
            LastStatus ??= "player screen unavailable";
            return Array.Empty<LazerReplayFrame>();
        }

        foreach (var player in players)
        {
            try
            {
                var frames = ReadFramesAfter(
                    player,
                    lastSequence: 0,
                    previousFramesList: 0,
                    readAll: true,
                    deadline: long.MaxValue);
                if (frames.Count > 0)
                {
                    return frames;
                }
            }
            catch (Win32Exception ex)
            {
                LastStatus = $"player candidate unreadable: {ex.Message}";
            }
            catch (Exception ex)
            {
                LastStatus = $"player candidate failed: {ex.Message}";
            }
        }

        LastStatus ??= $"no readable replay frames from {players.Count} player candidate(s)";
        return Array.Empty<LazerReplayFrame>();
    }

    public bool IsWatchingReplay()
    {
        if (_offsets.PlayerDrawableRuleset < 0 || _offsets.DrawableRulesetReplayScore < 0)
        {
            LastStatus = "replay playback offsets unavailable";
            return false;
        }

        var deadline = DeadlineFromNow(LazerMemoryReadPolicy.CachedReadBudget);
        foreach (var player in FindPlayers(allowDiscovery: false, deadline))
        {
            if (BudgetExpired(deadline))
                break;
            try
            {
                var drawableRuleset = _memory.ReadIntPtr(player + _offsets.PlayerDrawableRuleset);
                if (!IsReadablePointer(drawableRuleset))
                    continue;

                var replayScore = _memory.ReadIntPtr(drawableRuleset + _offsets.DrawableRulesetReplayScore);
                if (IsReadablePointer(replayScore))
                    return true;
            }
            catch
            {
                // A screen transition can invalidate a candidate between reads.
            }
        }

        return false;
    }

    private IReadOnlyList<LazerReplayFrame> ReadFramesAfter(
        nint player,
        long lastSequence,
        nint previousFramesList,
        bool readAll = false,
        long deadline = long.MaxValue)
    {
        var score = _memory.ReadIntPtr(player + _offsets.PlayerScore);
        if (!IsReadablePointer(score))
        {
            LastStatus = "score unavailable";
            return Array.Empty<LazerReplayFrame>();
        }

        var replay = _memory.ReadIntPtr(score + ScoreReplayOffset);
        if (!IsReadablePointer(replay))
        {
            LastStatus = "replay unavailable";
            return Array.Empty<LazerReplayFrame>();
        }

        var framesList = _memory.ReadIntPtr(replay + ReplayFramesOffset);
        if (!IsReadablePointer(framesList))
        {
            LastStatus = "replay frames unavailable";
            return Array.Empty<LazerReplayFrame>();
        }
        LastFramesList = framesList;

        var (size, items) = ListItemsInfo(framesList);
        if (!IsReadablePointer(items) || size <= 0)
        {
            LastStatus = "replay frames empty";
            return Array.Empty<LazerReplayFrame>();
        }
        if (size > MaxFrameCount)
        {
            LastStatus = $"invalid replay frame count: {size}";
            return Array.Empty<LazerReplayFrame>();
        }

        var cachedTimeOffset = LastReplayFrameTimeOffset ?? _preferredReplayFrameTimeOffset;
        int? timeOffset;
        if (IsReplayFrameTimeOffsetUsable(items, size, cachedTimeOffset, deadline))
        {
            timeOffset = cachedTimeOffset;
            ResetReplayFrameTimeOffsetSearch();
            _failedTimeOffsetFramesList = 0;
            _failedTimeOffsetItems = 0;
        }
        else
        {
            timeOffset = FindReplayFrameTimeOffset(framesList, items, size, deadline);
        }
        if (timeOffset is null)
        {
            LastStatus = "replay frame time offset unavailable";
            return Array.Empty<LazerReplayFrame>();
        }
        LastReplayFrameTimeOffset = timeOffset;

        FramesListChanged = previousFramesList != 0 && previousFramesList != framesList;
        var effectiveLastSequence = FramesListChanged || lastSequence > size
            ? 0
            : lastSequence;
        var startIndex = Math.Clamp((int)Math.Max(0, effectiveLastSequence), 0, size);
        var endIndex = readAll ? size : Math.Min(size, startIndex + MaxFramesPerTick);
        var frames = new List<LazerReplayFrame>(Math.Max(0, endIndex - startIndex));
        for (var index = startIndex; index < endIndex; index++)
        {
            if (!readAll &&
                !LazerMemoryReadPolicy.MayAttemptUnit(index == startIndex, BudgetExpired(deadline)))
                break;
            try
            {
                if (ReadFrameAt(items, index, timeOffset.Value, deadline) is { } frame)
                {
                    frames.Add(frame);
                }
            }
            catch (Win32Exception)
            {
            }
            catch (AccessViolationException)
            {
            }
            catch (ArgumentOutOfRangeException)
            {
            }
        }

        LastStatus = frames.Count == 0 ? "no new sane replay frames" : null;
        return frames;
    }

    private IReadOnlyList<nint> FindPlayers(bool allowDiscovery, long deadline)
    {
        var gameBase = FindGameBase(allowDiscovery, deadline);
        if (!IsReadablePointer(gameBase))
        {
            LastStatus ??= "osu! game base unavailable";
            return Array.Empty<nint>();
        }

        var screenStack = _memory.ReadIntPtr(gameBase + _offsets.OsuGameScreenStack);
        if (!IsReadablePointer(screenStack))
        {
            LastStatus = "screen stack unavailable";
            return Array.Empty<nint>();
        }

        var stack = _memory.ReadIntPtr(screenStack + _offsets.ScreenStackStack);
        if (!IsReadablePointer(stack))
        {
            LastStatus = "screen stack list unavailable";
            return Array.Empty<nint>();
        }

        var count = _memory.ReadInt32(stack + 0x10);
        var items = _memory.ReadIntPtr(stack + 0x8);
        if (!IsReadablePointer(items) || count <= 0 || count > 128)
        {
            LastStatus = $"screen stack empty: stack=0x{stack.ToInt64():X}, count={count}, items=0x{items.ToInt64():X}";
            return Array.Empty<nint>();
        }

        var players = new List<nint>();
        for (var index = count - 1; index >= 0; index--)
        {
            if (!LazerMemoryReadPolicy.MayAttemptUnit(index == count - 1, BudgetExpired(deadline)) ||
                players.Count >= 4)
                break;
            var screen = _memory.ReadIntPtr(items + 0x10 + 0x8 * index);
            if (IsReadablePointer(screen) && LooksLikePlayer(screen))
            {
                LastStatus = null;
                players.Add(screen);
            }
        }

        if (players.Count == 0)
        {
            LastStatus = $"player screen unavailable; scanned {count} screen stack entries";
        }
        return players;
    }

    private nint FindGameBase(bool allowDiscovery, long deadline)
    {
        var now = DateTimeOffset.UtcNow;
        // Screen transitions can briefly make ScreenStack unreadable, while a
        // compacting GC can permanently move GameBase. Tolerate the former,
        // but eventually invalidate the cached object and retry the already
        // discovered bootstrap anchors instead of remaining stuck forever.
        if (LastGameBase != 0)
        {
            if (now < _nextCachedGameBaseValidationAt)
                return LastGameBase;
            _nextCachedGameBaseValidationAt = now + CachedGameBaseValidationInterval;

            if (!BudgetExpired(deadline) && HasUsableScreenStack(LastGameBase))
            {
                _invalidCachedGameBasePolls = 0;
                return LastGameBase;
            }

            if (++_invalidCachedGameBasePolls < CachedGameBaseInvalidationChecks)
            {
                LastStatus = "cached osu! game base is temporarily unavailable";
                return LastGameBase;
            }

            LastGameBase = 0;
            _invalidCachedGameBasePolls = 0;
            _nextBootstrapCandidateRetryAt = DateTimeOffset.MinValue;
            // If the previous bootstrap anchors no longer resolve after an
            // update, bounded discovery may run again. It remains subject to
            // the 1 MiB / 3 ms step budget below.
            _discoveryExhausted = false;
            _discoveryRegions = null;
            _discoveryRegionIndex = 0;
            _discoveryRegionOffset = 0;
            _discoveryChunkSearchOffset = 0;
            _discoveryPhase = DiscoveryPhase.BootstrapPattern;
            _fallbackVtableMarker = 0;
            _fallbackMarkerResumeRegionIndex = 0;
            _fallbackMarkerResumeRegionOffset = 0;
            _fallbackMarkerResumeSearchOffset = 0;
            _nextDiscoveryStepAt = now + TimeSpan.FromSeconds(1);
        }

        if (IsReadablePointer(_preferredGameBase)
            && !BudgetExpired(deadline)
            && HasUsableScreenStack(_preferredGameBase))
        {
            LastGameBase = _preferredGameBase;
            return _preferredGameBase;
        }

        if (_bootstrapCandidates.Count > 0 && now >= _nextBootstrapCandidateRetryAt)
        {
            _nextBootstrapCandidateRetryAt = now + TimeSpan.FromSeconds(1);
            var candidatesToTry = _bootstrapCandidates.Count;
            while (candidatesToTry-- > 0 && !BudgetExpired(deadline))
            {
                var index = _bootstrapCandidateIndex++ % _bootstrapCandidates.Count;
                var candidate = TryResolveBootstrapGameBase(_bootstrapCandidates[index]);
                if (candidate == 0)
                    continue;
                LastGameBase = candidate;
                LastStatus = null;
                return candidate;
            }
        }

        if (_discoveryExhausted)
        {
            LastStatus = _bootstrapCandidates.Count > 0
                ? "osu! game base bootstrap found; waiting for its object graph to become readable"
                : "osu! game base discovery exhausted without a usable bootstrap or vtable candidate";
            return 0;
        }

        if (!allowDiscovery)
        {
            LastStatus = _bootstrapCandidates.Count > 0
                ? "osu! game base bootstrap is not readable; waiting for menu prewarm"
                : "osu! game base is not prewarmed; bounded discovery is paused";
            return 0;
        }

        if (now < _nextDiscoveryStepAt)
        {
            LastStatus = "osu! game base discovery is waiting for its next bounded scan step";
            return 0;
        }
        _nextDiscoveryStepAt = now + LazerMemoryReadPolicy.DiscoveryStepInterval;

        _discoveryRegions ??= _memory.Regions()
            .Where(region => region.RegionSize > 0 && region.RegionSize <= 256 * 1024 * 1024)
            .OrderByDescending(region => region.Writable && region.Type == 0x20000)
            .ToArray();

        var stopwatch = Stopwatch.StartNew();
        var remainingBudget = LazerMemoryReadPolicy.DiscoveryBytesPerStep;
        while (_discoveryRegionIndex < _discoveryRegions.Length
               && remainingBudget > 0
               && stopwatch.Elapsed < TimeSpan.FromMilliseconds(3))
        {
            var region = _discoveryRegions[_discoveryRegionIndex];
            var remainingInRegion = region.RegionSize - _discoveryRegionOffset;
            if (remainingInRegion <= 0)
            {
                _discoveryRegionIndex++;
                _discoveryRegionOffset = 0;
                _discoveryChunkSearchOffset = 0;
                continue;
            }

            var readSize = (int)Math.Min(
                Math.Min(DiscoveryChunkBytes, remainingBudget),
                remainingInRegion);
            var chunkAddress = region.BaseAddress + checked((int)_discoveryRegionOffset);
            try
            {
                _memory.ReadBytes(chunkAddress, _discoveryBuffer, readSize);
            }
            catch
            {
                _discoveryRegionOffset += readSize;
                _discoveryChunkSearchOffset = 0;
                remainingBudget -= readSize;
                continue;
            }

            if (_discoveryPhase == DiscoveryPhase.BootstrapPattern)
            {
                var searchStart = _discoveryChunkSearchOffset;
                var buffer = _discoveryBuffer.AsSpan(0, readSize);
                while (searchStart <= buffer.Length - ScalingContainerTargetDrawSizePattern.Length)
                {
                    var relative = buffer[searchStart..].IndexOf(ScalingContainerTargetDrawSizePattern);
                    if (relative < 0)
                        break;
                    var patternOffset = searchStart + relative;
                    var patternAddress = chunkAddress + patternOffset;
                    _discoveryChunkSearchOffset = patternOffset + 1;
                    var gameBase = TryResolveBootstrapGameBase(patternAddress);
                    if (gameBase != 0)
                    {
                        LastGameBase = gameBase;
                        LastStatus = null;
                        return gameBase;
                    }
                    if (_bootstrapCandidates.Count < 64 && !_bootstrapCandidates.Contains(patternAddress))
                        _bootstrapCandidates.Add(patternAddress);
                    searchStart = _discoveryChunkSearchOffset;
                    if (BudgetExpired(deadline))
                    {
                        LastStatus = "osu! game base bootstrap discovery will resume within the current bounded chunk";
                        return 0;
                    }
                }
            }
            else if (_discoveryPhase == DiscoveryPhase.VtableMarker)
            {
                var markerOffset = FindPointerOffsetInDiscoveryBuffer(
                    readSize,
                    (nint)_offsets.GameBaseVtable,
                    _discoveryChunkSearchOffset);
                if (markerOffset >= 0)
                {
                    _fallbackVtableMarker = chunkAddress + markerOffset;
                    _discoveryChunkSearchOffset = markerOffset + sizeof(long);
                    _fallbackMarkerResumeRegionIndex = _discoveryRegionIndex;
                    _fallbackMarkerResumeRegionOffset = _discoveryRegionOffset;
                    _fallbackMarkerResumeSearchOffset = _discoveryChunkSearchOffset;
                    _discoveryPhase = DiscoveryPhase.GameBaseObject;
                    ResetDiscoveryCursor();
                    LastStatus = "osu! game base vtable marker found; bounded object discovery will continue";
                    return 0;
                }
            }
            else if (_discoveryPhase == DiscoveryPhase.GameBaseObject)
            {
                while (true)
                {
                    var candidateOffset = FindPointerOffsetInDiscoveryBuffer(
                        readSize,
                        _fallbackVtableMarker,
                        _discoveryChunkSearchOffset);
                    if (candidateOffset < 0)
                        break;
                    _discoveryChunkSearchOffset = candidateOffset + sizeof(long);
                    var candidate = chunkAddress + candidateOffset;
                    if (IsGameBase(candidate) && HasUsableScreenStack(candidate))
                    {
                        LastGameBase = candidate;
                        LastStatus = null;
                        return candidate;
                    }
                    if (BudgetExpired(deadline))
                    {
                        LastStatus = "osu! game base object fallback will resume within the current bounded chunk";
                        return 0;
                    }
                }
            }

            var overlap = _discoveryPhase == DiscoveryPhase.BootstrapPattern
                ? ScalingContainerTargetDrawSizePattern.Length - 1
                : 0;
            var advance = remainingInRegion <= readSize ? readSize : Math.Max(1, readSize - overlap);
            _discoveryRegionOffset += advance;
            _discoveryChunkSearchOffset = 0;
            remainingBudget -= readSize;
        }

        if (_discoveryRegionIndex >= _discoveryRegions.Length)
        {
            if (_discoveryPhase == DiscoveryPhase.BootstrapPattern && _offsets.GameBaseVtable > 0)
            {
                _discoveryPhase = DiscoveryPhase.VtableMarker;
                ResetDiscoveryCursor();
                LastStatus = "osu! game base bootstrap scan completed; bounded vtable fallback will continue";
            }
            else if (_discoveryPhase == DiscoveryPhase.GameBaseObject)
            {
                // A region can contain multiple values that look like the
                // vtable marker. Resume the bounded marker scan after the last
                // one instead of permanently negative-caching a false lead.
                _discoveryPhase = DiscoveryPhase.VtableMarker;
                _discoveryRegionIndex = _fallbackMarkerResumeRegionIndex;
                _discoveryRegionOffset = _fallbackMarkerResumeRegionOffset;
                _discoveryChunkSearchOffset = _fallbackMarkerResumeSearchOffset;
                _fallbackVtableMarker = 0;
                LastStatus = "osu! game base object was not found for the current vtable marker; bounded fallback will continue";
            }
            else
            {
                _discoveryExhausted = true;
                LastStatus = "osu! game base discovery completed without a usable candidate";
            }
        }
        else
        {
            LastStatus = $"osu! game base {_discoveryPhase switch
            {
                DiscoveryPhase.BootstrapPattern => "bootstrap",
                DiscoveryPhase.VtableMarker => "vtable-marker fallback",
                _ => "object fallback",
            }} discovery in progress ({_discoveryRegionIndex + 1}/{_discoveryRegions.Length} regions)";
        }
        return 0;
    }

    private void ResetDiscoveryCursor()
    {
        _discoveryRegionIndex = 0;
        _discoveryRegionOffset = 0;
        _discoveryChunkSearchOffset = 0;
    }

    private int FindPointerOffsetInDiscoveryBuffer(
        int readSize,
        nint value,
        int searchOffset)
        => LazerMemoryReadPolicy.FindAlignedPointerOffset(
            _discoveryBuffer.AsSpan(0, readSize),
            value.ToInt64(),
            searchOffset);

    private nint TryResolveBootstrapGameBase(nint patternAddress)
    {
        // osu!lazer has shifted this field relative to the ScalingContainer
        // anchor across releases. The candidate dereferences are constant-size;
        // the surrounding pattern search is incrementally budgeted above.
        foreach (var delta in BootstrapDeltas)
        {
            try
            {
                var externalLinkOpener = _memory.ReadIntPtr(patternAddress - delta);
                if (!IsReadablePointer(externalLinkOpener))
                    continue;
                var api = _memory.ReadIntPtr(externalLinkOpener + _offsets.ExternalLinkOpenerApi);
                if (!IsReadablePointer(api))
                    continue;
                var game = _memory.ReadIntPtr(api + _offsets.ApiAccessGame);
                if (IsReadablePointer(game) && HasUsableScreenStack(game))
                    return game;
            }
            catch
            {
                // A wrong delta can land on an unreadable field.
                continue;
            }
        }
        return 0;
    }

    private bool IsGameBase(nint address)
    {
        if (!IsReadablePointer(address) || _offsets.GameBaseVtable <= 0)
        {
            return false;
        }

        try
        {
            var vtable = _memory.ReadIntPtr(address);
            return IsReadablePointer(vtable) && _memory.ReadInt64(vtable) == _offsets.GameBaseVtable;
        }
        catch
        {
            return false;
        }
    }

    private bool HasUsableScreenStack(nint gameBase)
    {
        try
        {
            var screenStack = _memory.ReadIntPtr(gameBase + _offsets.OsuGameScreenStack);
            if (!IsReadablePointer(screenStack))
            {
                return false;
            }

            var stack = _memory.ReadIntPtr(screenStack + _offsets.ScreenStackStack);
            if (!IsReadablePointer(stack))
            {
                return false;
            }

            var count = _memory.ReadInt32(stack + 0x10);
            var items = _memory.ReadIntPtr(stack + 0x8);
            return IsReadablePointer(items) && count > 0 && count <= 128;
        }
        catch
        {
            return false;
        }
    }

    private bool LooksLikePlayer(nint address)
    {
        try
        {
            var score = _memory.ReadIntPtr(address + _offsets.PlayerScore);
            return IsReadablePointer(score);
        }
        catch
        {
            return false;
        }
    }

    private LazerReplayFrame? ReadFrameAt(
        nint items,
        int index,
        int replayFrameTimeOffset,
        long deadline)
    {
        var frame = ReadItem(items, index);
        if (!IsReadablePointer(frame))
        {
            return null;
        }

        var mapTimeMs = _memory.ReadDouble(frame + replayFrameTimeOffset);
        var x = _memory.ReadFloat(frame + ReplayFramePositionOffset);
        var y = _memory.ReadFloat(frame + ReplayFramePositionOffset + 0x4);
        if (!IsSaneReplayFrame(mapTimeMs, x, y))
        {
            return null;
        }

        var (leftPressed, rightPressed) = ReadActionsFromFrame(frame, deadline);
        return new LazerReplayFrame
        {
            MapTimeMs = mapTimeMs,
            X = x,
            Y = y,
            LeftPressed = leftPressed,
            RightPressed = rightPressed,
            Focused = true,
            Paused = false,
            Sequence = index + 1,
        };
    }

    private int? FindReplayFrameTimeOffset(nint framesList, nint items, int size, long deadline)
    {
        if (size < 2)
        {
            ResetReplayFrameTimeOffsetSearch();
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        if (_failedTimeOffsetFramesList == framesList
            && _failedTimeOffsetItems == items
            && now < _nextTimeOffsetSearchAt)
        {
            return null;
        }

        if (_timeOffsetSearchFramesList != framesList || _timeOffsetSearchItems != items)
        {
            BeginReplayFrameTimeOffsetSearch(framesList, items, size);
        }

        while (_timeOffsetCandidateIndex < ReplayFrameTimeOffsetCandidates.Length)
        {
            var offset = ReplayFrameTimeOffsetCandidates[_timeOffsetCandidateIndex];
            while (_timeOffsetSampleIndex < _timeOffsetSampleCount && !_timeOffsetCandidateInvalid)
            {
                if (BudgetExpired(deadline))
                {
                    // Retain both candidate and sample position. Restarting at
                    // candidate zero every 16 ms can permanently starve a valid
                    // later offset while consuming the full live-read budget.
                    return null;
                }

                var index = _timeOffsetSampleIndex * _timeOffsetSampleStep;
                double time;
                try
                {
                    var frame = ReadItem(items, index);
                    if (!IsReadablePointer(frame))
                    {
                        _timeOffsetCandidateInvalid = true;
                        continue;
                    }

                    time = _memory.ReadDouble(frame + offset);
                }
                catch
                {
                    _timeOffsetCandidateInvalid = true;
                    continue;
                }

                if (!double.IsFinite(time) || time <= -300_000 || time >= 12 * 60 * 60 * 1000)
                {
                    _timeOffsetCandidateInvalid = true;
                    continue;
                }

                if (_timeOffsetPrevious is not null && time < _timeOffsetPrevious - 0.001)
                {
                    _timeOffsetCandidateInvalid = true;
                    continue;
                }

                _timeOffsetFirst ??= time;
                _timeOffsetLast = time;
                _timeOffsetPrevious = time;
                _timeOffsetSaneCount++;
                _timeOffsetSampleIndex++;
            }

            if (!_timeOffsetCandidateInvalid && _timeOffsetSaneCount == _timeOffsetSampleCount)
            {
                var span = (_timeOffsetLast ?? 0) - (_timeOffsetFirst ?? 0);
                if (_timeOffsetSearchSize <= 1 || span > 0.001)
                {
                    var score = _timeOffsetSaneCount * 1000 + span;
                    if (score > _timeOffsetBestScore)
                    {
                        _timeOffsetBestOffset = offset;
                        _timeOffsetBestScore = score;
                    }
                }
            }

            AdvanceReplayFrameTimeOffsetCandidate();
        }

        var bestOffset = _timeOffsetBestOffset;
        ResetReplayFrameTimeOffsetSearch();
        if (bestOffset is null)
        {
            _failedTimeOffsetFramesList = framesList;
            _failedTimeOffsetItems = items;
            _nextTimeOffsetSearchAt = now + TimeSpan.FromMilliseconds(100);
        }
        return bestOffset;
    }

    private void BeginReplayFrameTimeOffsetSearch(nint framesList, nint items, int size)
    {
        ResetReplayFrameTimeOffsetSearch();
        _failedTimeOffsetFramesList = 0;
        _failedTimeOffsetItems = 0;
        _timeOffsetSearchFramesList = framesList;
        _timeOffsetSearchItems = items;
        _timeOffsetSearchSize = size;
        _timeOffsetSampleCount = Math.Min(size, 4);
        _timeOffsetSampleStep = Math.Max(1, size / _timeOffsetSampleCount);
    }

    private void AdvanceReplayFrameTimeOffsetCandidate()
    {
        _timeOffsetCandidateIndex++;
        _timeOffsetSampleIndex = 0;
        _timeOffsetPrevious = null;
        _timeOffsetFirst = null;
        _timeOffsetLast = null;
        _timeOffsetSaneCount = 0;
        _timeOffsetCandidateInvalid = false;
    }

    private void ResetReplayFrameTimeOffsetSearch()
    {
        _timeOffsetSearchFramesList = 0;
        _timeOffsetSearchItems = 0;
        _timeOffsetSearchSize = 0;
        _timeOffsetSampleCount = 0;
        _timeOffsetSampleStep = 0;
        _timeOffsetCandidateIndex = 0;
        _timeOffsetSampleIndex = 0;
        _timeOffsetPrevious = null;
        _timeOffsetFirst = null;
        _timeOffsetLast = null;
        _timeOffsetSaneCount = 0;
        _timeOffsetCandidateInvalid = false;
        _timeOffsetBestOffset = null;
        _timeOffsetBestScore = double.NegativeInfinity;
    }

    private bool IsReplayFrameTimeOffsetUsable(nint items, int size, int? offset, long deadline)
    {
        if (offset is not { } value || size < 2)
        {
            return false;
        }

        try
        {
            if (BudgetExpired(deadline))
                return false;
            var firstFrame = ReadItem(items, 0);
            var lastFrame = ReadItem(items, size - 1);
            if (!IsReadablePointer(firstFrame) || !IsReadablePointer(lastFrame))
            {
                return false;
            }

            var first = _memory.ReadDouble(firstFrame + value);
            var last = _memory.ReadDouble(lastFrame + value);
            return double.IsFinite(first)
                   && double.IsFinite(last)
                   && first > -300_000
                   && last < 12 * 60 * 60 * 1000
                   && last >= first - 0.001;
        }
        catch
        {
            return false;
        }
    }

    private static long DeadlineFromNow(TimeSpan budget) =>
        Stopwatch.GetTimestamp() + Math.Max(1, (long)(budget.TotalSeconds * Stopwatch.Frequency));

    private static bool BudgetExpired(long deadline) =>
        deadline != long.MaxValue && Stopwatch.GetTimestamp() >= deadline;

    private enum DiscoveryPhase
    {
        BootstrapPattern,
        VtableMarker,
        GameBaseObject,
    }

    private (bool LeftPressed, bool RightPressed) ReadActionsFromFrame(nint frame, long deadline)
    {
        foreach (var offset in ReplayFrameActionOffsets)
        {
            if (BudgetExpired(deadline))
                break;
            try
            {
                var actionsList = _memory.ReadIntPtr(frame + offset);
                var result = ReadActions(actionsList, deadline);
                if (result.Readable)
                {
                    return (result.LeftPressed, result.RightPressed);
                }
            }
            catch
            {
            }
        }

        return (false, false);
    }

    private (bool LeftPressed, bool RightPressed, bool Readable) ReadActions(nint actionsList, long deadline)
    {
        var leftPressed = false;
        var rightPressed = false;
        if (!IsReadablePointer(actionsList))
        {
            return (leftPressed, rightPressed, false);
        }

        var (size, items) = ListItemsInfo(actionsList);
        if (!IsReadablePointer(items) || size < 0 || size > 16)
        {
            return (leftPressed, rightPressed, size == 0);
        }

        for (var i = 0; i < size; i++)
        {
            if (BudgetExpired(deadline))
                return (leftPressed, rightPressed, false);
            var action = _memory.ReadInt32(items + 0x10 + 0x4 * i);
            if (action == 0)
            {
                leftPressed = true;
            }
            else if (action == 1)
            {
                rightPressed = true;
            }
        }

        return (leftPressed, rightPressed, true);
    }

    private (int Size, nint Items) ListItemsInfo(nint list)
    {
        var array = _memory.ReadIntPtr(list + 0x8);
        var size = _memory.ReadInt32(list + 0x10);
        if (!IsReadablePointer(array) || size < 0 || size > MaxFrameCount)
        {
            return (0, 0);
        }

        return (size, array);
    }

    private nint ReadItem(nint items, int index) => _memory.ReadIntPtr(items + 0x10 + 0x8 * index);

    private static bool IsReadablePointer(nint address) => address.ToInt64() > 0x10000;

    private static bool IsSaneReplayFrame(double time, double x, double y)
        => double.IsFinite(time)
           && double.IsFinite(x)
           && double.IsFinite(y)
           && time > -300_000
           && time < 12 * 60 * 60 * 1000
           && x > -10_000
           && x < 10_000
           && y > -10_000
           && y < 10_000;
}

internal sealed record LazerMemoryOffsets(
    string OsuVersion,
    long GameBaseVtable,
    int OsuGameScreenStack,
    int ScreenStackStack,
    int PlayerScore,
    int ExternalLinkOpenerApi,
    int ApiAccessGame,
    int PlayerDrawableRuleset,
    int DrawableRulesetReplayScore)
{
    private const string OfficialOffsetsUrl =
        "https://raw.githubusercontent.com/tosuapp/tosu/master/packages/tosu/src/assets/offsets.json";

    public static LazerMemoryOffsets Load(string? path, bool refreshOfficialCache = false)
    {
        path ??= EnsureDefaultOffsetsPath(refreshOfficialCache);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("osu!lazer offsets.json was not found.", path);
        }

        return Parse(File.ReadAllText(path));
    }

    public static LazerMemoryOffsets? LoadCached(string? path)
    {
        path ??= Path.Combine(AppPaths.CacheDir, "tosu", "offsets.json");
        if (!File.Exists(path))
            return null;
        try { return Parse(File.ReadAllText(path)); }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException)
        {
            return null;
        }
    }

    public static async Task<LazerMemoryOffsets> LoadAsync(
        string? path,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            if (!File.Exists(path))
                throw new FileNotFoundException("osu!lazer offsets.json was not found.", path);
            var explicitJson = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return Parse(explicitJson);
        }

        path = Path.Combine(AppPaths.CacheDir, "tosu", "offsets.json");
        if (File.Exists(path))
        {
            try
            {
                var cachedJson = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return Parse(cachedJson);
            }
            catch (Exception ex) when (
                ex is IOException or JsonException or InvalidDataException)
            {
                // Replace an unreadable cache only after a fresh response has
                // been downloaded and validated below.
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Kumori");
        var json = await http.GetStringAsync(OfficialOffsetsUrl, cancellationToken).ConfigureAwait(false);
        var offsets = Parse(json);
        cancellationToken.ThrowIfCancellationRequested();

        var isNew = !File.Exists(path);
        var temp = path + $".new-{Guid.NewGuid():N}";
        try
        {
            await File.WriteAllTextAsync(temp, json, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temp); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        if (isNew)
            CacheActivityLog.RecordAddition(path, "tosu-memory-offsets");
        return offsets;
    }

    private static LazerMemoryOffsets Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return new LazerMemoryOffsets(
            root.TryGetProperty("OsuVersion", out var version) ? version.GetString() ?? "unknown" : "unknown",
            GetInt64(root, "GameBaseVtable"),
            GetOffset(root, "osu.Game.OsuGame", "<ScreenStack>k__BackingField"),
            GetOffset(root, "osu.Framework.Screens.ScreenStack", "stack"),
            GetOffset(root, "osu.Game.Screens.Play.Player", "<Score>k__BackingField"),
            GetOffset(root, "osu.Game.Online.Chat.ExternalLinkOpener", "<api>k__BackingField"),
            GetOffset(root, "osu.Game.Online.API.APIAccess", "game"),
            GetOptionalOffset(root, "osu.Game.Screens.Play.Player", "<DrawableRuleset>k__BackingField"),
            GetOptionalOffset(root, "osu.Game.Rulesets.UI.DrawableRuleset", "<ReplayScore>k__BackingField"));
    }

    private static string EnsureDefaultOffsetsPath(bool refreshOfficialCache)
    {
        var path = Path.Combine(AppPaths.CacheDir, "tosu", "offsets.json");
        if (File.Exists(path) && !refreshOfficialCache)
            return path;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            var isNew = !File.Exists(path);
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Kumori");
            var json = http.GetStringAsync(OfficialOffsetsUrl).GetAwaiter().GetResult();
            _ = Parse(json); // validate before replacing the last known-good cache.

            var temp = path + ".new";
            File.WriteAllText(temp, json);
            File.Move(temp, path, overwrite: true);
            if (isNew) CacheActivityLog.RecordAddition(path, "tosu-memory-offsets");
        }
        catch when (File.Exists(path))
        {
            // A previous valid cache is safer than disabling replay capture
            // when the upstream response is unavailable or malformed.
        }

        return path;
    }

    private static int GetOffset(JsonElement root, string type, string field)
    {
        if (!root.TryGetProperty(type, out var typeElement) ||
            !typeElement.TryGetProperty(field, out var fieldElement) ||
            !fieldElement.TryGetInt32(out var offset))
        {
            throw new InvalidDataException($"Missing offset {type}.{field}.");
        }

        return offset;
    }

    private static int GetOptionalOffset(JsonElement root, string type, string field)
        => root.TryGetProperty(type, out var typeElement)
           && typeElement.TryGetProperty(field, out var fieldElement)
           && fieldElement.TryGetInt32(out var offset)
            ? offset
            : -1;

    private static long GetInt64(JsonElement root, string field)
    {
        if (!root.TryGetProperty(field, out var element) || !element.TryGetInt64(out var value))
        {
            throw new InvalidDataException($"Missing offset {field}.");
        }

        return value;
    }
}

public sealed class ProcessMemory : IDisposable
{
    private const int ProcessVmRead = 0x0010;
    private const int ProcessQueryInformation = 0x0400;
    private readonly nint _handle;
    private readonly byte[] _scalarBuffer = new byte[8];

    private ProcessMemory(nint handle)
    {
        _handle = handle;
    }

    public static ProcessMemory Open(Process process)
    {
        var handle = OpenProcess(ProcessVmRead | ProcessQueryInformation, false, process.Id);
        if (handle == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return new ProcessMemory(handle);
    }

    // ProcessMemory instances are intentionally serialized by their owners.
    // Reusing this scalar buffer avoids several short-lived arrays per frame.
    public int ReadInt32(nint address)
    {
        ReadScalar(address, 4);
        return BitConverter.ToInt32(_scalarBuffer, 0);
    }

    public long ReadInt64(nint address)
    {
        ReadScalar(address, 8);
        return BitConverter.ToInt64(_scalarBuffer, 0);
    }

    public float ReadFloat(nint address)
    {
        ReadScalar(address, 4);
        return BitConverter.ToSingle(_scalarBuffer, 0);
    }

    public double ReadDouble(nint address)
    {
        ReadScalar(address, 8);
        return BitConverter.ToDouble(_scalarBuffer, 0);
    }

    public nint ReadIntPtr(nint address)
    {
        ReadScalar(address, IntPtr.Size);
        return IntPtr.Size == 8
            ? (nint)BitConverter.ToInt64(_scalarBuffer, 0)
            : (nint)BitConverter.ToInt32(_scalarBuffer, 0);
    }

    public byte ReadByte(nint address)
    {
        ReadScalar(address, 1);
        return _scalarBuffer[0];
    }

    public byte[] ReadBytes(nint address, int count) => Read(address, count);

    public void ReadBytes(nint address, byte[] buffer, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if ((uint)count > (uint)buffer.Length)
            throw new ArgumentOutOfRangeException(nameof(count));
        if (!ReadProcessMemory(_handle, address, buffer, count, out var bytesRead) || bytesRead != count)
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private void ReadScalar(nint address, int count)
    {
        if (!ReadProcessMemory(_handle, address, _scalarBuffer, count, out var bytesRead) || bytesRead != count)
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public IEnumerable<MemoryRegion> Regions()
    {
        nint address = 0x10000;
        while (VirtualQueryEx(_handle, address, out var info, (nuint)Marshal.SizeOf<MemoryBasicInformation>()) != 0)
        {
            if (info.State == 0x1000 && (info.Protect & 0x100) == 0 && (info.Protect & 0x01) == 0)
            {
                bool writable = (info.Protect & (0x04 | 0x08 | 0x40 | 0x80)) != 0;
                bool executable = (info.Protect & (0x10 | 0x20 | 0x40 | 0x80)) != 0;
                yield return new MemoryRegion(info.BaseAddress, (long)info.RegionSize, writable, executable, unchecked((uint)info.Type));
            }

            var next = info.BaseAddress.ToInt64() + (long)info.RegionSize;
            if (next <= address.ToInt64())
            {
                yield break;
            }

            address = (nint)next;
        }
    }

    public nint FindPointer(nint baseAddress, long size, nint value)
    {
        const int chunkSize = 1024 * 1024;
        var needle = BitConverter.GetBytes(value.ToInt64());
        var remaining = size;
        var address = baseAddress;
        while (remaining > 0)
        {
            var readSize = (int)Math.Min(chunkSize, remaining);
            byte[] buffer;
            try
            {
                buffer = Read(address, readSize);
            }
            catch
            {
                return 0;
            }

            for (var i = 0; i <= buffer.Length - 8; i += 8)
            {
                if (buffer.AsSpan(i, 8).SequenceEqual(needle))
                {
                    return address + i;
                }
            }

            address += readSize;
            remaining -= readSize;
        }

        return 0;
    }

    public nint FindPattern(nint baseAddress, long size, IReadOnlyList<byte?> pattern)
        => FindPatterns(baseAddress, size, pattern, maxMatches: 1).FirstOrDefault();

    public IEnumerable<nint> FindPatterns(nint baseAddress, long size, IReadOnlyList<byte?> pattern, int maxMatches)
    {
        const int chunkSize = 1024 * 1024;
        var exactPattern = pattern.All(value => value.HasValue)
            ? pattern.Select(value => value!.Value).ToArray()
            : null;
        var remaining = size;
        var address = baseAddress;
        var overlap = Math.Max(0, pattern.Count - 1);
        byte[] previousTail = [];
        var matches = 0;

        while (remaining > 0)
        {
            var readSize = (int)Math.Min(chunkSize, remaining);
            byte[] current;
            try
            {
                current = Read(address, readSize);
            }
            catch
            {
                yield break;
            }

            var buffer = previousTail.Length == 0
                ? current
                : previousTail.Concat(current).ToArray();
            var bufferBase = address - previousTail.Length;
            if (exactPattern is not null)
            {
                var searchStart = 0;
                while (searchStart <= buffer.Length - exactPattern.Length)
                {
                    var found = buffer.AsSpan(searchStart).IndexOf(exactPattern);
                    if (found < 0)
                    {
                        break;
                    }

                    var index = searchStart + found;
                    yield return bufferBase + index;
                    matches++;
                    if (matches >= maxMatches)
                    {
                        yield break;
                    }
                    searchStart = index + 1;
                }
            }
            else
            {
                for (var i = 0; i <= buffer.Length - pattern.Count; i++)
                {
                    if (!Matches(buffer, i, pattern))
                    {
                        continue;
                    }

                    yield return bufferBase + i;
                    matches++;
                    if (matches >= maxMatches)
                    {
                        yield break;
                    }
                }
            }

            previousTail = current.Length > overlap
                ? current[^overlap..]
                : current;
            address += readSize;
            remaining -= readSize;
        }
    }

    private static bool Matches(byte[] buffer, int offset, IReadOnlyList<byte?> pattern)
    {
        for (var i = 0; i < pattern.Count; i++)
        {
            if (pattern[i] is { } expected && buffer[offset + i] != expected)
            {
                return false;
            }
        }

        return true;
    }

    private byte[] Read(nint address, int count)
    {
        var buffer = new byte[count];
        if (!ReadProcessMemory(_handle, address, buffer, count, out var bytesRead) || bytesRead != count)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return buffer;
    }

    public void Dispose()
    {
        if (_handle != 0)
        {
            CloseHandle(_handle);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(int desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(nint process, nint baseAddress, byte[] buffer, int size, out int bytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int VirtualQueryEx(nint process, nint address, out MemoryBasicInformation buffer, nuint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);
}

public readonly record struct MemoryRegion(nint BaseAddress, long RegionSize, bool Writable, bool Executable, uint Type);

[StructLayout(LayoutKind.Sequential)]
internal struct MemoryBasicInformation
{
    public nint BaseAddress;
    public nint AllocationBase;
    public int AllocationProtect;
    public nuint RegionSize;
    public int State;
    public int Protect;
    public int Type;
}
