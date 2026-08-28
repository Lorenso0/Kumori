using System.Text.Json;
using Kumori.Core.State;
using Kumori.Storage;
using Kumori.Tracking;
using Serilog;

namespace Kumori.Native;

public sealed class LazerReplayFrameCaptureService : IAttemptSink, IAsyncDisposable
{
    private static readonly TimeSpan HealthInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan FrameStatusInterval = TimeSpan.FromMilliseconds(250);
    private const int MinimumReplacementFrames = 60;
    private const double MinimumReplacementDurationMs = 1000;
    private const int MaxRecentFrames = 512;
    private const double RecentSeedWindowMs = 5000;
    private const int PersistenceChunkSize = 4096;

    private readonly AppStateStore _store;
    private readonly SqliteConnectionFactory _factory;
    private readonly ILazerReplayFrameSource _source;
    private readonly Func<long?> _currentAttemptId;
    private readonly IReplayFrameStatusSink _status;
    private readonly string _sourceName;
    private readonly OsuClientKind _clientKind;
    private readonly Func<string, Func<CancellationToken, Task>, Task>? _deferPersistence;
    private readonly Action<long>? _captureCommitted;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private List<LazerReplayFrame> _frames = new();
    private readonly List<LazerReplayFrame> _recentFrames = new();
    private readonly List<Task> _persistenceTasks = new();
    private readonly object _disposeGate = new();
    private Task? _readerTask;
    private Task? _healthTask;
    private Task? _disposeTask;
    private long? _activeAttemptId;
    private long _receivedFrames;
    private LazerReplayFrame? _lastReceivedFrame;
    private string? _lastError;
    private DateTimeOffset _lastFrameStatusAt = DateTimeOffset.MinValue;

    public LazerReplayFrameCaptureService(
        AppStateStore store,
        SqliteConnectionFactory factory,
        Func<long?> currentAttemptId,
        ILazerReplayFrameSource? source = null,
        IReplayFrameStatusSink? status = null,
        string sourceName = "lazer_replay_frame",
        OsuClientKind clientKind = OsuClientKind.Lazer,
        Func<string, Func<CancellationToken, Task>, Task>? deferPersistence = null,
        Action<long>? captureCommitted = null)
    {
        _store = store;
        _factory = factory;
        _currentAttemptId = currentAttemptId;
        _source = source ?? new TcpLazerReplayFrameSource();
        _status = status ?? new DelegatingReplayFrameStatusSink();
        _sourceName = string.IsNullOrWhiteSpace(sourceName) ? "lazer_replay_frame" : sourceName;
        _clientKind = clientKind;
        _deferPersistence = deferPersistence;
        _captureCommitted = captureCommitted;
    }

    public void Start()
    {
        _status.Update(s =>
        {
            s.Enabled = true;
            s.State = "starting";
            s.Detail = $"{_sourceName} capture service is starting.";
            s.FramesEmitted = 0;
            s.FramesBufferedForAttempt = 0;
            s.FramesStored = 0;
            s.ActiveAttemptId = null;
            s.LastFrameMapTimeMs = null;
            s.LastFrameX = null;
            s.LastFrameY = null;
            s.LastFrameLeftPressed = false;
            s.LastFrameRightPressed = false;
            s.LastError = null;
            s.ProcessId = null;
            s.ProcessName = null;
            s.ProcessPath = null;
            s.LocalReplayState = "idle";
            s.LocalReplayPath = null;
            s.LocalReplayFrames = 0;
            s.LocalReplayError = null;
        });
        PublishCaptureStatus(HealthLevel.Unknown, running: true, error: null);
        _readerTask = Task.Run(ReadLoopAsync);
        _healthTask = Task.Run(HealthLoopAsync);
    }

    public void StartAttempt(AttemptStart start)
    {
        bool acceptsLazerFamily = _clientKind == OsuClientKind.Lazer;
        if (start.ClientKind != _clientKind
            && !(acceptsLazerFamily && (start.ClientKind.IsLazerFamily()
                                        || start.ClientKind == OsuClientKind.Unknown)))
        {
            return;
        }
        long? attemptId = _currentAttemptId();
        lock (_gate)
        {
            if (_activeAttemptId is not null && _activeAttemptId == attemptId)
            {
                _status.Update(s =>
                {
                    s.ActiveAttemptId = _activeAttemptId;
                    s.FramesBufferedForAttempt = _frames.Count;
                    s.State = "attempt_armed";
                    s.Detail = $"Attempt {_activeAttemptId} is already armed for {_sourceName} frames.";
                });
                return;
            }
        }
        lock (_gate)
        {
            _activeAttemptId = attemptId;
            _frames.Clear();
            if (_source is not IAttemptAwareReplayFrameSource)
                _frames.AddRange(CurrentReplaySeedFrames());
        }
        try
        {
            // Arm the consumer before waking/resetting the source. An always-on
            // source can yield immediately from StartAttempt; doing this in the
            // opposite order created a narrow first-batch loss window.
            if (_source is IAttemptAwareReplayFrameSource attemptAware)
                attemptAware.StartAttempt(start);
        }
        catch
        {
            lock (_gate)
            {
                if (_activeAttemptId == attemptId)
                {
                    _activeAttemptId = null;
                    _frames.Clear();
                }
            }
            throw;
        }
        _status.Update(s =>
        {
            s.ActiveAttemptId = _activeAttemptId;
            s.FramesBufferedForAttempt = CurrentFrameCount();
            s.State = "attempt_armed";
            s.Detail = _activeAttemptId is null
                ? "Attempt started, but database attempt id is not available yet."
                : $"Attempt {_activeAttemptId} armed for {_sourceName} frames.";
        });
        PublishCaptureStatus(HealthLevel.Ok, running: true, error: null);
        Log.Debug("{Source} replay-frame capture armed for attempt {AttemptId}", _sourceName, _activeAttemptId);
    }

    public void Checkpoint(AttemptCheckpoint checkpoint)
    {
        if (_activeAttemptId is not null && _source is IAttemptAwareReplayFrameSource attemptAware)
            attemptAware.UpdateAttempt(checkpoint.Snapshot);
    }

    public void DiscardIfEmpty(AttemptDiscard discard)
    {
        if (_activeAttemptId is null) return;
        if (_source is IAttemptAwareReplayFrameSource attemptAware) attemptAware.EndAttempt();
        lock (_gate)
        {
            _activeAttemptId = null;
            _frames.Clear();
        }
        _status.Update(s =>
        {
            s.ActiveAttemptId = null;
            s.FramesBufferedForAttempt = 0;
            s.State = "attempt_discarded";
            s.Detail = $"Attempt discarded: {discard.Reason}.";
        });
    }

    public void Finalize(AttemptFinalization finalization)
    {
        lock (_gate)
        {
            if (_activeAttemptId is null)
                return;
        }

        // A finalizable source owns the only authoritative boundary snapshot.
        // Take that snapshot before allowing the next attempt to reset the source,
        // but leave every transform and storage operation to ProcessCapture.
        IReadOnlyList<LazerReplayFrame>? finalizedSourceSnapshot = null;
        try
        {
            if (_source is IFinalizableReplayFrameSource finalizable)
            {
                finalizedSourceSnapshot = finalizable.FinalizeAttemptSnapshot();
            }
            else
            {
                if (_source is IAttemptAwareReplayFrameSource attemptAware)
                    attemptAware.EndAttempt();
                if (_source is ILazerReplayFrameSnapshotSource snapshotSource)
                    finalizedSourceSnapshot = snapshotSource.ReadCurrentFramesSnapshot();
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not detach the final {Source} source snapshot", _sourceName);
            if (_source is IAttemptAwareReplayFrameSource attemptAware)
            {
                try { attemptAware.EndAttempt(); } catch { }
            }
        }

        List<LazerReplayFrame> bufferedFrames;
        long? attemptId;
        lock (_gate)
        {
            attemptId = _activeAttemptId;
            bufferedFrames = _frames;
            _frames = new List<LazerReplayFrame>();
            _activeAttemptId = null;
        }

        if (attemptId is null)
        {
            string? sourceDiagnostic = _status.Load().Detail;
            _status.Update(s =>
            {
                s.ActiveAttemptId = null;
                s.FramesBufferedForAttempt = 0;
                s.State = "finalized_without_frames";
                s.Detail = "Attempt finalized before a database attempt id was available.";
            });
            Log.Warning(
                "Lazer replay-frame capture finalized without frames for attempt {AttemptId}; active id missing: {MissingAttemptId}",
                attemptId,
                attemptId is null);
            return;
        }

        var capture = new DetachedCapture(
            attemptId.Value,
            bufferedFrames,
            finalizedSourceSnapshot,
            finalization,
            _status.Load().Detail);

        if (_deferPersistence is null)
        {
            ProcessCapture(capture, CancellationToken.None);
            return;
        }

        var detachedFrameCount = Math.Max(bufferedFrames.Count, finalizedSourceSnapshot?.Count ?? 0);
        _status.Update(s =>
        {
            s.ActiveAttemptId = null;
            s.FramesBufferedForAttempt = 0;
            s.State = "persistence_queued";
            s.Detail = $"Queued {detachedFrameCount} {_sourceName} frames for attempt {attemptId}; preparation and storage will run outside gameplay.";
        });
        try
        {
            var persistence = _deferPersistence(
                $"{_sourceName}-persistence-{attemptId.Value}",
                token => ProcessCaptureDeferred(capture, token));
            TrackPersistence(persistence);
        }
        catch (Exception ex)
        {
            // Never fall back to synchronous compression on the tracking thread.
            // A scheduling failure only happens during teardown, where preserving
            // input responsiveness is still more important than forcing a write.
            _status.Update(s =>
            {
                s.State = "persistence_schedule_failed";
                s.Detail = $"Could not queue {_sourceName} frames for attempt {attemptId}.";
                s.LastError = ex.Message;
            });
            Log.Warning(ex, "Could not defer {Source} persistence for attempt {AttemptId}", _sourceName, attemptId);
        }
    }

    private Task ProcessCaptureDeferred(DetachedCapture capture, CancellationToken token)
    {
        try
        {
            ProcessCapture(capture, token);
            return Task.CompletedTask;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _status.Update(s =>
            {
                s.State = "persistence_failed";
                s.Detail = $"Could not store {_sourceName} frames for attempt {capture.AttemptId}.";
                s.LastError = ex.Message;
            });
            throw;
        }
    }

    private void ProcessCapture(DetachedCapture capture, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var frames = capture.SourceFrames is { Count: > 0 }
            ? PreferSnapshot(capture.AttemptId, capture.BufferedFrames, capture.SourceFrames, capture.Finalization.Snapshot, token)
            : capture.BufferedFrames;

        token.ThrowIfCancellationRequested();
        if (frames.Count == 0)
        {
            _status.Update(s =>
            {
                s.ActiveAttemptId = null;
                s.FramesBufferedForAttempt = 0;
                s.State = "finalized_without_frames";
                s.Detail = $"Attempt {capture.AttemptId} finalized with no {_sourceName} frames. Last source diagnostic: {capture.SourceDiagnostic ?? "none"}";
            });
            Log.Warning("Replay-frame capture finalized without {Source} frames for attempt {AttemptId}", _sourceName, capture.AttemptId);
            return;
        }

        var replayFrames = PreserveReplayFrameOrder(frames, token);
        token.ThrowIfCancellationRequested();
        var samples = new Kumori.Core.Models.MovementSample[replayFrames.Count];
        for (var index = 0; index < replayFrames.Count; index++)
        {
            if ((index & 1023) == 0)
                token.ThrowIfCancellationRequested();
            samples[index] = LazerReplayFrameMapper.ToMovementSample(replayFrames[index]);
        }
        if (samples.Length == 0)
        {
            _status.Update(s =>
            {
                s.State = "finalized_without_samples";
                s.Detail = $"Attempt {capture.AttemptId} had {_sourceName} frames, but none normalized into samples.";
            });
            return;
        }

        var duration = samples[^1].MapTimeMs - samples[0].MapTimeMs;
        token.ThrowIfCancellationRequested();
        var existing = new MovementRepository(_factory).GetMetadata(capture.AttemptId, token);
        if (existing is not null &&
            !existing.Source.Equals(_sourceName, StringComparison.OrdinalIgnoreCase) &&
            (samples.Length < MinimumReplacementFrames || duration < MinimumReplacementDurationMs))
        {
            _status.Update(s =>
            {
                s.ActiveAttemptId = null;
                s.FramesBufferedForAttempt = 0;
                s.State = "finalized_lazer_rejected";
                s.Detail = $"Rejected {samples.Length} {_sourceName} frames over {duration:0.##}ms for attempt {capture.AttemptId}; preserved existing {existing.Source} capture with {existing.SampleCount} samples.";
            });
            Log.Warning(
                "Rejected {Count} lazer replay frames over {Duration}ms for attempt {AttemptId}; preserved existing {Source} movement with {ExistingCount} samples",
                samples.Length,
                duration,
                capture.AttemptId,
                existing.Source,
                existing.SampleCount);
            return;
        }

        token.ThrowIfCancellationRequested();
        var expectedEndMs = Math.Max(
            capture.Finalization.Snapshot.LiveTimeMs,
            capture.Finalization.Snapshot.DurationSeconds * 1000);
        var tailShortfallMs = Math.Max(0, expectedEndMs - samples[^1].MapTimeMs);
        var tailToleranceMs = _sourceName.Equals("stable_memory", StringComparison.OrdinalIgnoreCase)
            ? Math.Max(5000, expectedEndMs * 0.10)
            : Math.Max(2000, expectedEndMs * 0.05);
        var captureComplete = expectedEndMs <= 0 || tailShortfallMs <= tailToleranceMs;
        var estimatedDroppedSamples = captureComplete || duration <= 0
            ? 0
            : (int)Math.Ceiling((samples.Length - 1) / duration * tailShortfallMs);
        var calibrationJson = JsonSerializer.Serialize(new
        {
            source = _sourceName,
            frame_count = replayFrames.Count,
            replay_exact = captureComplete,
            tail_complete = captureComplete,
            expected_end_ms = expectedEndMs,
            captured_end_ms = samples[^1].MapTimeMs,
            tail_shortfall_ms = tailShortfallMs,
            estimated_dropped_samples = estimatedDroppedSamples,
            note = captureComplete
                ? $"Frames supplied by {_sourceName}; Kumori preserves lazer replay frame order without movement normalization."
                : $"Frames supplied by {_sourceName}, but the capture ended {tailShortfallMs:0.##}ms before the finalized attempt.",
        });

        // Compression is bounded into chunks above. Persistence remains
        // cancellable through its final pre-commit boundary so a new play rolls
        // back an in-progress replacement instead of competing with gameplay.
        token.ThrowIfCancellationRequested();
        var movementStore = new MovementCaptureStore(_factory);
        movementStore.Start(capture.AttemptId);
        for (var offset = 0; offset < samples.Length; offset += PersistenceChunkSize)
        {
            token.ThrowIfCancellationRequested();
            var count = Math.Min(PersistenceChunkSize, samples.Length - offset);
            movementStore.AddSamples(new ArraySegment<Kumori.Core.Models.MovementSample>(samples, offset, count));
        }
        token.ThrowIfCancellationRequested();
        movementStore.Complete(estimatedDroppedSamples, _sourceName, calibrationJson, token);
        try
        {
            _captureCommitted?.Invoke(capture.AttemptId);
        }
        catch (Exception ex)
        {
            // The capture is already durable. A notification failure must not
            // make the coordinator retry persistence or duplicate accounting.
            Log.Warning(ex, "Post-commit replay capture notification failed for attempt {AttemptId}", capture.AttemptId);
        }
        _status.Update(s =>
        {
            s.ActiveAttemptId = null;
            s.FramesBufferedForAttempt = 0;
            s.FramesStored += samples.Length;
            s.State = captureComplete ? "stored" : "stored_incomplete";
            s.Detail = captureComplete
                ? $"Stored {samples.Length} {_sourceName} replay-frame samples for attempt {capture.AttemptId}."
                : $"Stored {samples.Length} frames for attempt {capture.AttemptId}, but the tail is short by {tailShortfallMs:0.##}ms.";
        });
        PublishCaptureStatus(HealthLevel.Ok, running: true, error: null);

        Log.Information("Stored {Count} {Source} replay frames for attempt {AttemptId}", samples.Length, _sourceName, capture.AttemptId);
        if (!captureComplete)
        {
            Log.Warning(
                "Incomplete lazer replay capture for attempt {AttemptId}: captured through {CapturedEnd}ms, expected about {ExpectedEnd}ms; estimated {DroppedSamples} missing samples",
                capture.AttemptId,
                samples[^1].MapTimeMs,
                expectedEndMs,
                estimatedDroppedSamples);
        }
    }

    private sealed record DetachedCapture(
        long AttemptId,
        List<LazerReplayFrame> BufferedFrames,
        IReadOnlyList<LazerReplayFrame>? SourceFrames,
        AttemptFinalization Finalization,
        string? SourceDiagnostic);

    private void TrackPersistence(Task task)
    {
        lock (_gate)
            _persistenceTasks.Add(task);
        _ = task.ContinueWith(
            completed =>
            {
                lock (_gate)
                    _persistenceTasks.Remove(completed);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private List<LazerReplayFrame> PreferSnapshot(
        long attemptId,
        List<LazerReplayFrame> bufferedFrames,
        IReadOnlyList<LazerReplayFrame> sourceFrames,
        AttemptSnapshot snapshot,
        CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            var snapshotFrames = PreserveReplayFrameOrder(sourceFrames, token).ToList();
            if (snapshotFrames.Count == 0)
            {
                return bufferedFrames;
            }

            var bufferedDuration = DurationMs(bufferedFrames);
            var snapshotDuration = DurationMs(snapshotFrames);
            var targetMapTime = Math.Max(snapshot.LiveTimeMs, snapshot.DurationSeconds * 1000) * 0.95;
            var snapshotLooksBetter = snapshotFrames.Count > bufferedFrames.Count
                                      && snapshotDuration >= bufferedDuration
                                      && (targetMapTime <= 0 || snapshotFrames[^1].MapTimeMs >= targetMapTime || snapshotDuration > bufferedDuration + 1000);

            if (!snapshotLooksBetter)
            {
                return bufferedFrames;
            }

            Log.Information(
                "Replacing buffered lazer replay frames for attempt {AttemptId} with final memory snapshot: buffered {BufferedCount}/{BufferedDuration:0.##}ms, snapshot {SnapshotCount}/{SnapshotDuration:0.##}ms",
                attemptId,
                bufferedFrames.Count,
                bufferedDuration,
                snapshotFrames.Count,
                snapshotDuration);
            _status.Update(s =>
            {
                s.State = "final_snapshot";
                s.Detail = $"Recovered {snapshotFrames.Count} lazer replay frames from final memory snapshot for attempt {attemptId}.";
                s.FramesBufferedForAttempt = snapshotFrames.Count;
                s.LastError = null;
            });
            return snapshotFrames;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not read final lazer replay frame snapshot for attempt {AttemptId}", attemptId);
            return bufferedFrames;
        }
    }

    private static double DurationMs(IReadOnlyList<LazerReplayFrame> frames)
        => frames.Count == 0 ? 0 : frames[^1].MapTimeMs - frames[0].MapTimeMs;

    internal static IReadOnlyList<LazerReplayFrame> PreserveReplayFrameOrder(IReadOnlyList<LazerReplayFrame> frames)
        => PreserveReplayFrameOrder(frames, CancellationToken.None);

    private static IReadOnlyList<LazerReplayFrame> PreserveReplayFrameOrder(
        IReadOnlyList<LazerReplayFrame> frames,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var indexed = new (LazerReplayFrame Frame, int Index)[frames.Count];
        for (var index = 0; index < frames.Count; index++)
        {
            if ((index & 1023) == 0)
                token.ThrowIfCancellationRequested();
            indexed[index] = (frames[index], index);
        }

        token.ThrowIfCancellationRequested();
        Array.Sort(indexed, static (left, right) =>
        {
            var sequence = (left.Frame.Sequence ?? long.MaxValue)
                .CompareTo(right.Frame.Sequence ?? long.MaxValue);
            return sequence != 0 ? sequence : left.Index.CompareTo(right.Index);
        });
        token.ThrowIfCancellationRequested();

        var ordered = new LazerReplayFrame[indexed.Length];
        for (var index = 0; index < indexed.Length; index++)
        {
            if ((index & 1023) == 0)
                token.ThrowIfCancellationRequested();
            ordered[index] = indexed[index].Frame;
        }
        return ordered;
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            await foreach (var frame in _source.ReadFramesAsync(_cts.Token))
            {
                long? activeAttemptId;
                int bufferedFrameCount;
                var shouldUpdateStatus = false;
                lock (_gate)
                {
                    _receivedFrames++;
                    RememberRecentFrame(frame);
                    if (_activeAttemptId is not null)
                    {
                        if (_frames.Count > 0 && IsNewReplaySequence(_frames[^1], frame))
                        {
                            _frames.Clear();
                        }
                        _frames.Add(frame);
                    }

                    activeAttemptId = _activeAttemptId;
                    bufferedFrameCount = _frames.Count;
                    var now = DateTimeOffset.UtcNow;
                    if (now - _lastFrameStatusAt >= FrameStatusInterval)
                    {
                        _lastFrameStatusAt = now;
                        shouldUpdateStatus = true;
                    }
                }

                if (shouldUpdateStatus)
                {
                    _status.Update(s =>
                    {
                        s.ActiveAttemptId = activeAttemptId;
                        s.FramesBufferedForAttempt = bufferedFrameCount;
                        s.LastFrameMapTimeMs = frame.MapTimeMs;
                        s.LastFrameX = frame.X;
                        s.LastFrameY = frame.Y;
                        s.LastFrameLeftPressed = frame.LeftPressed;
                        s.LastFrameRightPressed = frame.RightPressed;
                        s.FramesEmitted = _receivedFrames;
                    });
                    PublishCaptureStatus(HealthLevel.Ok, running: true, error: null);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            _status.Update(s =>
            {
                s.State = "source_failed";
                s.Detail = "Lazer replay-frame source stopped with an error.";
                s.LastError = ex.Message;
            });
            PublishCaptureStatus(HealthLevel.Error, running: false, error: ex.Message);
            Log.Warning(ex, "Lazer replay-frame source failed");
        }
    }

    private IReadOnlyList<LazerReplayFrame> CurrentReplaySeedFrames()
    {
        if (_recentFrames.Count == 0)
        {
            return Array.Empty<LazerReplayFrame>();
        }

        var firstTime = _recentFrames[0].MapTimeMs;
        if (firstTime > RecentSeedWindowMs)
        {
            return Array.Empty<LazerReplayFrame>();
        }

        return _recentFrames.ToArray();
    }

    private int CurrentFrameCount()
    {
        lock (_gate)
        {
            return _frames.Count;
        }
    }

    private void RememberRecentFrame(LazerReplayFrame frame)
    {
        if (_lastReceivedFrame is { } previous &&
            IsNewReplaySequence(previous, frame))
        {
            _recentFrames.Clear();
        }

        _recentFrames.Add(frame);
        while (_recentFrames.Count > MaxRecentFrames)
        {
            _recentFrames.RemoveAt(0);
        }

        _lastReceivedFrame = frame;
    }

    private static bool IsNewReplaySequence(LazerReplayFrame previous, LazerReplayFrame current)
    {
        if (previous.Sequence is { } previousSequence &&
            current.Sequence is { } currentSequence &&
            currentSequence <= previousSequence)
        {
            return true;
        }

        return current.MapTimeMs + 1000 < previous.MapTimeMs;
    }

    private async Task HealthLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(HealthInterval, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            // App's companion monitor already owns the single process-table
            // scan. Reuse its cached result instead of performing a second
            // system-wide enumeration every second during gameplay.
            var running = _store.Current.Companions.OsuRunning;
            var lazerDetail = _lastError is not null
                ? $"lazer frame source failed: {_lastError}"
                : running
                    ? _receivedFrames > 0
                        ? $"lazer frame bridge active ({_receivedFrames} frames)"
                        : "lazer frame bridge waiting"
                    : "osu!lazer not running";

            _store.Update(s => s with
            {
                Tracking = s.Tracking with
                {
                    OsuRunning = running,
                    Health = _lastError is null ? s.Tracking.Health : HealthLevel.Degraded,
                    // tosu not being reachable is the more actionable problem - only surface
                    // memory-reader detail once tosu itself is actually connected.
                    Detail = s.Tracking.TosuConnected ? lazerDetail : (s.Tracking.Detail ?? "tosu not running"),
                },
                Capture = s.Capture with
                {
                    Running = _lastError is null,
                    Health = _lastError is null
                        ? _receivedFrames > 0 ? HealthLevel.Ok : HealthLevel.Unknown
                        : HealthLevel.Error,
                    Error = _lastError,
                    FramesReceived = _receivedFrames,
                    FramesBuffered = CurrentFrameCount(),
                    LastFrameMapTimeMs = _lastReceivedFrame?.MapTimeMs,
                },
            });
        }
    }

    private void PublishCaptureStatus(HealthLevel health, bool running, string? error)
    {
        lock (_gate)
        {
            PublishCaptureStatusLocked(health, running, error);
        }
    }

    private void PublishCaptureStatusLocked(HealthLevel health, bool running, string? error)
    {
        _store.Update(s => s with
        {
            Capture = s.Capture with
            {
                Running = running,
                Source = _sourceName,
                Health = health,
                Error = error,
                FramesReceived = _receivedFrames,
                FramesBuffered = _frames.Count,
                FramesStored = _status.Load().FramesStored,
                LastFrameMapTimeMs = _lastReceivedFrame?.MapTimeMs,
            },
        });
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
        {
            // Teardown sources can include native process and pipe cleanup.
            // Start it away from the caller so even a synchronous native wait
            // cannot freeze WPF's shutdown dispatcher. Repeated shutdown paths
            // share exactly one teardown operation.
            _disposeTask ??= Task.Run(disposeCoreAsync);
            return new ValueTask(_disposeTask);
        }
    }

    private async Task disposeCoreAsync()
    {
        _cts.Cancel();
        foreach (var task in new[] { _readerTask, _healthTask })
        {
            if (task is not null)
            {
                try { await task; } catch { }
            }
        }
        Task[] persistence;
        lock (_gate)
            persistence = _persistenceTasks.ToArray();
        if (persistence.Length > 0)
        {
            try { await Task.WhenAll(persistence); } catch { }
        }
        if (_source is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
        }
        _cts.Dispose();
    }
}
