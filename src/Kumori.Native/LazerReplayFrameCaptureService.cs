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

    private readonly AppStateStore _store;
    private readonly MovementCaptureStore _movementStore;
    private readonly MovementRepository _movementRepository;
    private readonly ILazerReplayFrameSource _source;
    private readonly Func<long?> _currentAttemptId;
    private readonly IReplayFrameStatusSink _status;
    private readonly string _sourceName;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private readonly List<LazerReplayFrame> _frames = new();
    private readonly List<LazerReplayFrame> _recentFrames = new();
    private Task? _readerTask;
    private Task? _healthTask;
    private long? _activeAttemptId;
    private bool _storeStarted;
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
        string sourceName = "lazer_replay_frame")
    {
        _store = store;
        _movementStore = new MovementCaptureStore(factory);
        _movementRepository = new MovementRepository(factory);
        _currentAttemptId = currentAttemptId;
        _source = source ?? new TcpLazerReplayFrameSource();
        _status = status ?? new DelegatingReplayFrameStatusSink();
        _sourceName = string.IsNullOrWhiteSpace(sourceName) ? "lazer_replay_frame" : sourceName;
    }

    public void Start()
    {
        _status.Update(s =>
        {
            s.Enabled = true;
            s.State = "starting";
            s.Detail = $"{_sourceName} capture service is starting.";
            s.LastError = null;
        });
        PublishCaptureStatus(HealthLevel.Unknown, running: true, error: null);
        _readerTask = Task.Run(ReadLoopAsync);
        _healthTask = Task.Run(HealthLoopAsync);
    }

    public void StartAttempt(AttemptStart start)
    {
        lock (_gate)
        {
            var attemptId = _currentAttemptId();
            if (_activeAttemptId == attemptId && _frames.Count > 0)
            {
                _status.Update(s =>
                {
                    s.ActiveAttemptId = _activeAttemptId;
                    s.FramesBufferedForAttempt = _frames.Count;
                    s.State = "attempt_armed";
                    s.Detail = $"Attempt {_activeAttemptId} is already armed for lazer replay frames.";
                });
                return;
            }

            _activeAttemptId = attemptId;
            _storeStarted = false;
            _frames.Clear();
            _frames.AddRange(CurrentReplaySeedFrames());
        }
        _status.Update(s =>
        {
            s.ActiveAttemptId = _activeAttemptId;
            s.FramesBufferedForAttempt = CurrentFrameCount();
            s.State = "attempt_armed";
            s.Detail = _activeAttemptId is null
                ? "Attempt started, but database attempt id is not available yet."
                : $"Attempt {_activeAttemptId} armed for lazer replay frames.";
        });
        PublishCaptureStatus(HealthLevel.Ok, running: true, error: null);
        Log.Debug("Lazer replay-frame capture armed for attempt {AttemptId}", _activeAttemptId);
    }

    public void Checkpoint(AttemptCheckpoint checkpoint)
    {
    }

    public void DiscardIfEmpty(AttemptDiscard discard)
    {
        lock (_gate)
        {
            _activeAttemptId = null;
            _storeStarted = false;
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
        List<LazerReplayFrame> frames;
        long? attemptId;
        lock (_gate)
        {
            attemptId = _activeAttemptId;
            frames = _frames.ToList();
            _activeAttemptId = null;
            _frames.Clear();
        }

        if (attemptId is null || frames.Count == 0)
        {
            _status.Update(s =>
            {
                s.ActiveAttemptId = null;
                s.FramesBufferedForAttempt = 0;
                s.State = "finalized_without_frames";
                s.Detail = attemptId is null
                    ? "Attempt finalized before a database attempt id was available."
                    : $"Attempt {attemptId} finalized with no lazer frames.";
            });
            Log.Warning(
                "Lazer replay-frame capture finalized without frames for attempt {AttemptId}; active id missing: {MissingAttemptId}",
                attemptId,
                attemptId is null);
            return;
        }

        // Detach the attempt before inspecting the final replay snapshot. Finalization is
        // synchronous with tracking, so waiting here used to let a fast retry reset the
        // shared buffer and replace this attempt's frames with those from the next play.
        // The memory source exposes the complete current replay list at this boundary;
        // for streaming sources we retain the already-buffered frames instead.
        frames = PreferFinalSnapshot(attemptId.Value, frames, finalization.Snapshot);

        var replayFrames = PreserveReplayFrameOrder(frames);
        var samples = replayFrames
            .Select(LazerReplayFrameMapper.ToMovementSample)
            .ToArray();
        if (samples.Length == 0)
        {
            _status.Update(s =>
            {
                s.State = "finalized_without_samples";
                s.Detail = $"Attempt {attemptId} had lazer frames, but none normalized into samples.";
            });
            return;
        }

        var duration = samples[^1].MapTimeMs - samples[0].MapTimeMs;
        var existing = _movementRepository.GetMetadata(attemptId.Value);
        if (existing is not null &&
            !existing.Source.Equals(_sourceName, StringComparison.OrdinalIgnoreCase) &&
            (samples.Length < MinimumReplacementFrames || duration < MinimumReplacementDurationMs))
        {
            _status.Update(s =>
            {
                s.ActiveAttemptId = null;
                s.FramesBufferedForAttempt = 0;
                s.State = "finalized_lazer_rejected";
                s.Detail = $"Rejected {samples.Length} lazer frames over {duration:0.##}ms for attempt {attemptId}; preserved existing {existing.Source} capture with {existing.SampleCount} samples.";
            });
            Log.Warning(
                "Rejected {Count} lazer replay frames over {Duration}ms for attempt {AttemptId}; preserved existing {Source} movement with {ExistingCount} samples",
                samples.Length,
                duration,
                attemptId,
                existing.Source,
                existing.SampleCount);
            return;
        }

        if (!_storeStarted)
        {
            _movementStore.Start(attemptId.Value);
            _storeStarted = true;
        }
        var expectedEndMs = Math.Max(
            finalization.Snapshot.LiveTimeMs,
            finalization.Snapshot.DurationSeconds * 1000);
        var tailShortfallMs = Math.Max(0, expectedEndMs - samples[^1].MapTimeMs);
        var tailToleranceMs = Math.Max(2000, expectedEndMs * 0.05);
        var captureComplete = expectedEndMs <= 0 || tailShortfallMs <= tailToleranceMs;
        var estimatedDroppedSamples = captureComplete || duration <= 0
            ? 0
            : (int)Math.Ceiling((samples.Length - 1) / duration * tailShortfallMs);
        _movementStore.AddSamples(samples);
        _movementStore.Complete(
            droppedSamples: estimatedDroppedSamples,
            source: _sourceName,
            calibrationJson: JsonSerializer.Serialize(new
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
            }));
        _storeStarted = false;
        _status.Update(s =>
        {
            s.ActiveAttemptId = null;
            s.FramesBufferedForAttempt = 0;
            s.FramesStored += samples.Length;
            s.State = captureComplete ? "stored" : "stored_incomplete";
            s.Detail = captureComplete
                ? $"Stored {samples.Length} lazer replay-frame samples for attempt {attemptId}."
                : $"Stored {samples.Length} frames for attempt {attemptId}, but the tail is short by {tailShortfallMs:0.##}ms.";
        });
        PublishCaptureStatus(HealthLevel.Ok, running: true, error: null);

        Log.Information("Stored {Count} lazer replay frames for attempt {AttemptId}", samples.Length, attemptId);
        if (!captureComplete)
        {
            Log.Warning(
                "Incomplete lazer replay capture for attempt {AttemptId}: captured through {CapturedEnd}ms, expected about {ExpectedEnd}ms; estimated {DroppedSamples} missing samples",
                attemptId,
                samples[^1].MapTimeMs,
                expectedEndMs,
                estimatedDroppedSamples);
        }
    }

    private List<LazerReplayFrame> PreferFinalSnapshot(long attemptId, List<LazerReplayFrame> bufferedFrames, AttemptSnapshot snapshot)
    {
        if (_source is not ILazerReplayFrameSnapshotSource snapshotSource)
        {
            return bufferedFrames;
        }

        try
        {
            var snapshotFrames = PreserveReplayFrameOrder(snapshotSource.ReadCurrentFramesSnapshot())
                .ToList();
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
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not read final lazer replay frame snapshot for attempt {AttemptId}", attemptId);
            return bufferedFrames;
        }
    }

    private static double DurationMs(IReadOnlyList<LazerReplayFrame> frames)
        => frames.Count == 0 ? 0 : frames[^1].MapTimeMs - frames[0].MapTimeMs;

    internal static IReadOnlyList<LazerReplayFrame> PreserveReplayFrameOrder(IReadOnlyList<LazerReplayFrame> frames)
        => frames
            .Select((Frame, Index) => new { Frame, Index })
            .OrderBy(x => x.Frame.Sequence ?? long.MaxValue)
            .ThenBy(x => x.Index)
            .Select(x => x.Frame)
            .ToArray();

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

            var running = OsuProcessDetector.IsRunning();
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

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        foreach (var task in new[] { _readerTask, _healthTask })
        {
            if (task is not null)
            {
                try { await task; } catch { }
            }
        }
        if (_source is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
        }
        _cts.Dispose();
    }
}
