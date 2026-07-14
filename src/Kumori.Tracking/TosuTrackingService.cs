using System.Diagnostics;
using Kumori.Core.State;
using Serilog;

namespace Kumori.Tracking;

/// <summary>
/// Owns the live tosu connection and publishes TrackingStatus to the app
/// state store. Runs entirely on background threads/tasks; the GUI observes
/// the store. (Session/attempt tracking builds on top of this in the next
/// Phase 3 increment — this service currently provides connection health and
/// beatmap detection.)
/// </summary>
public sealed class TosuTrackingService : IAsyncDisposable
{
    private static readonly TimeSpan StaleThreshold = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HealthTickInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan StatePublishInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan MediaIdleDelay = TimeSpan.FromSeconds(2);
    private const int MaxPendingMediaMaps = 8;
    private const int MaxRememberedMediaMaps = 512;

    private readonly AppStateStore _store;
    private readonly WebSocketPacketSource _source;
    private readonly TosuClient _client;
    private readonly AttemptTracker? _attemptTracker;
    private readonly SessionTracker? _sessionTracker;
    private readonly IProfileTelemetrySink? _profileTelemetry;
    private readonly string _primaryMediaMirror;
    private readonly IReadOnlyList<string> _fallbackMediaMirrors;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _trackingGate = new();
    private readonly object _statePublishGate = new();
    private readonly object _mediaGate = new();
    private Task? _runTask;
    private Task? _healthTask;
    private Task? _statePublishTask;
    private Task? _mediaCacheTask;
    private volatile bool _connected;
    private readonly HashSet<string> _cachedMediaKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _cachedMediaOrder = new();
    private readonly Queue<string> _pendingMediaOrder = new();
    private readonly Dictionary<string, TosuSnapshot> _pendingMedia = new(StringComparer.OrdinalIgnoreCase);
    private TosuSnapshot? _pendingStateSnapshot;
    private string? _mediaInFlightKey;
    private CancellationTokenSource? _mediaWorkCts;
    private volatile bool _gameplayActive;
    private long _stateEpoch;
    private OsuClientKind _lastObservedClientKind;

    public event Action<OsuClientKind>? ClientKindObserved;

    public TosuTrackingService(
        AppStateStore store,
        Uri? uri = null,
        AttemptTracker? attemptTracker = null,
        SessionTracker? sessionTracker = null,
        IProfileTelemetrySink? profileTelemetry = null,
        string primaryMediaMirror = "https://api.rai.moe",
        IReadOnlyList<string>? fallbackMediaMirrors = null,
        bool recordPackets = false,
        IReplayPlaybackDetector? replayPlaybackDetector = null)
    {
        _store = store;
        _client = new TosuClient(replayPlaybackDetector);
        _source = new WebSocketPacketSource(uri, recordPackets);
        _attemptTracker = attemptTracker;
        _sessionTracker = sessionTracker;
        _profileTelemetry = profileTelemetry;
        _primaryMediaMirror = string.IsNullOrWhiteSpace(primaryMediaMirror) ? "https://api.rai.moe" : primaryMediaMirror;
        _fallbackMediaMirrors = fallbackMediaMirrors ?? Array.Empty<string>();
        _source.Connected += OnConnected;
        _source.Disconnected += OnDisconnected;
        _client.SnapshotReceived += OnSnapshot;
    }

    public void Start()
    {
        _runTask = Task.Run(() => _client.RunAsync(_source, _cts.Token));
        _healthTask = Task.Run(HealthLoopAsync);
        _statePublishTask = Task.Run(PublishStateLoopAsync);
    }

    private void OnConnected()
    {
        long epoch;
        lock (_statePublishGate)
        {
            _connected = true;
            epoch = Interlocked.Increment(ref _stateEpoch);
        }
        _store.Update(s => Volatile.Read(ref _stateEpoch) == epoch && _connected
            ? s with
            {
                Tracking = s.Tracking with
                {
                    TosuConnected = true,
                    Health = HealthLevel.Ok,
                    Detail = null,
                },
            }
            : s);
    }

    private void OnDisconnected(string? reason)
    {
        long epoch;
        lock (_statePublishGate)
        {
            _connected = false;
            _pendingStateSnapshot = null;
            epoch = Interlocked.Increment(ref _stateEpoch);
        }
        _store.Update(s => Volatile.Read(ref _stateEpoch) == epoch
            ? s with
            {
                Tracking = s.Tracking with
                {
                    TosuConnected = false,
                    CurrentBeatmap = null,
                    Health = HealthLevel.Error,
                    Detail = reason is null ? "tosu not reachable" : $"tosu: {reason}",
                },
            }
            : s);
    }

    private void OnSnapshot(TosuSnapshot snapshot)
    {
        // This must be the first snapshot-side action. Packet recording is
        // developer-only, but even its below-normal worker must be excluded
        // from gameplay before any persistence or UI consumer sees the frame.
        _source.SetGameplayActive(snapshot.IsPlaying);

        try
        {
            // Profile data is independent of gameplay state. Persist it before
            // the attempt tracker opens an attempt so that it becomes its baseline.
            _profileTelemetry?.Ingest(snapshot);
            if (snapshot.ClientKind != OsuClientKind.Unknown
                && snapshot.ClientKind != _lastObservedClientKind)
            {
                ClientKindObserved?.Invoke(snapshot.ClientKind);
                _lastObservedClientKind = snapshot.ClientKind;
            }
            QueueMediaCache(snapshot);
            if (snapshot.IsStandardMode)
            {
                lock (_trackingGate)
                {
                    _sessionTracker?.Ingest(TrackingFrameMapper.ToSessionFrame(snapshot));
                    _attemptTracker?.Ingest(TrackingFrameMapper.ToAttemptFrame(snapshot));
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Tracking persistence failed for a tosu packet; continuing with later packets");
            long epoch;
            lock (_statePublishGate)
            {
                _pendingStateSnapshot = null;
                epoch = Interlocked.Increment(ref _stateEpoch);
            }
            _store.Update(s => Volatile.Read(ref _stateEpoch) == epoch
                ? s with
                {
                    Tracking = s.Tracking with
                    {
                        Health = HealthLevel.Error,
                        Detail = $"tracking save failed: {ex.Message}",
                    },
                }
                : s);
            return;
        }

        lock (_statePublishGate)
        {
            _pendingStateSnapshot = snapshot;
        }
    }

    private static TosuTelemetry ToTelemetry(TosuSnapshot snapshot) => new()
    {
        ReceivedAt = DateTimeOffset.UtcNow,
        State = snapshot.State,
        IsPlaying = snapshot.IsPlaying,
        IsResults = snapshot.IsResults,
        IsStandardMode = snapshot.IsStandardMode,
        Artist = snapshot.Artist,
        Title = snapshot.Title,
        Mapper = snapshot.Mapper,
        Difficulty = snapshot.Difficulty,
        BeatmapId = snapshot.BeatmapId,
        BeatmapSetId = snapshot.BeatmapSetId,
        Checksum = snapshot.Checksum,
        LiveTimeMs = snapshot.LiveTimeMs,
        Score = snapshot.Score,
        Grade = snapshot.Grade,
        Accuracy = snapshot.Play.Accuracy,
        Combo = snapshot.Play.Combo,
        MaxCombo = snapshot.BeatmapStats.MaxCombo,
        Progress = snapshot.Play.Progress ?? 0,
        Health = snapshot.Play.Health,
        Pp = snapshot.Pp,
        FcPp = snapshot.FcPp,
        MaxPp = snapshot.MaxPp,
        ModsKey = snapshot.ModsKey,
        Hit300 = snapshot.Play.Hit300,
        Hit100 = snapshot.Play.Hit100,
        Hit50 = snapshot.Play.Hit50,
        Miss = snapshot.Play.Miss,
        Geki = snapshot.Play.Geki,
        Katu = snapshot.Play.Katu,
        SliderBreaks = snapshot.Play.SliderBreak,
        LargeTickHits = snapshot.Play.LargeTickHit,
        LargeTickMisses = snapshot.Play.LargeTickMiss,
        SmallTickHits = snapshot.Play.SmallTickHit,
        SmallTickMisses = snapshot.Play.SmallTickMiss,
        SliderTailHits = snapshot.Play.SliderTailHit,
        SliderTailMisses = snapshot.Play.SliderTailMiss,
        UnstableRate = snapshot.Play.UnstableRate,
    };

    private async Task PublishStateLoopAsync()
    {
        using var timer = new PeriodicTimer(StatePublishInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(_cts.Token))
            {
                TosuSnapshot snapshot;
                long epoch;
                lock (_statePublishGate)
                {
                    if (!_connected || _pendingStateSnapshot is not { } pending)
                    {
                        continue;
                    }

                    snapshot = pending;
                    _pendingStateSnapshot = null;
                    epoch = _stateEpoch;
                }
                // AppStateStore invokes subscribers synchronously. Keep those
                // callbacks outside the service gate while the epoch check
                // still prevents a stale publish from racing a disconnect.
                _store.Update(s => Volatile.Read(ref _stateEpoch) == epoch && _connected
                    ? s with
                    {
                        Tracking = s.Tracking with
                        {
                            TosuConnected = true,
                            CurrentBeatmap = snapshot.BeatmapDisplay,
                            LastPacketAgeSeconds = 0,
                            Health = HealthLevel.Ok,
                            Detail = snapshot.IsPlaying ? "playing" : snapshot.State,
                            LatestTelemetry = ToTelemetry(snapshot),
                        },
                    }
                    : s);
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            // Normal service shutdown.
        }
    }

    private void QueueMediaCache(TosuSnapshot snapshot)
    {
        // The transition packet already canceled any idle media worker and
        // queued this map. Continuous gameplay packets need no media lock,
        // dictionary lookup, or key construction at all.
        if (snapshot.IsPlaying && _gameplayActive)
            return;

        CancellationTokenSource? mediaWorkToCancel = null;
        string? droppedMediaKey = null;
        lock (_mediaGate)
        {
            var wasPlaying = _gameplayActive;
            _gameplayActive = snapshot.IsPlaying;
            if (_gameplayActive && !wasPlaying)
            {
                // A retry or new map always wins over optional media work.
                mediaWorkToCancel = _mediaWorkCts;
            }

            if (snapshot.IsStandardMode
                && snapshot.ClientKind != OsuClientKind.Stable
                && snapshot.Media is not null)
            {
                var key = MediaKey(snapshot);
                if (!_cachedMediaKeys.Contains(key)
                    && !string.Equals(_mediaInFlightKey, key, StringComparison.OrdinalIgnoreCase))
                {
                    if (_pendingMedia.ContainsKey(key))
                    {
                        _pendingMedia[key] = snapshot;
                    }
                    else
                    {
                        while (_pendingMedia.Count >= MaxPendingMediaMaps
                               && _pendingMediaOrder.TryDequeue(out var droppedKey))
                        {
                            if (_pendingMedia.Remove(droppedKey))
                            {
                                droppedMediaKey = droppedKey;
                                break;
                            }
                        }

                        _pendingMedia[key] = snapshot;
                        _pendingMediaOrder.Enqueue(key);
                    }
                }
            }

            if (!_gameplayActive)
            {
                TryStartMediaCacheWorkerUnderLock();
            }
        }

        if (droppedMediaKey is not null)
            Log.Warning("Dropping deferred media cache request for {CacheKey}; queue is full", droppedMediaKey);
        if (mediaWorkToCancel is not null)
        {
            // Never run cancellation callbacks synchronously on the packet
            // thread (or while holding _mediaGate).
            _ = CancelMediaWorkAsync(mediaWorkToCancel);
        }
    }

    private void TryStartMediaCacheWorkerUnderLock()
    {
        if (_gameplayActive
            || _mediaCacheTask is not null
            || _pendingMedia.Count == 0
            || _cts.IsCancellationRequested)
        {
            return;
        }

        var workerCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        _mediaWorkCts = workerCts;
        _mediaCacheTask = Task.Run(() => MediaCacheLoopAsync(workerCts));
    }

    private async Task MediaCacheLoopAsync(CancellationTokenSource workerCts)
    {
        TosuSnapshot? inFlightSnapshot = null;
        string? inFlightKey = null;
        // Stable owns durable, directly-addressable files in its Songs folder.
        // AttemptSqliteSink persists those paths for later replay analysis, so
        // copying the map, audio, background and samples into Kumori is wasteful.
        try
        {
            while (true)
            {
                await Task.Delay(MediaIdleDelay, workerCts.Token);

                lock (_mediaGate)
                {
                    if (_gameplayActive)
                    {
                        return;
                    }

                    while (_pendingMediaOrder.TryDequeue(out var key))
                    {
                        if (!_pendingMedia.Remove(key, out var candidate))
                        {
                            continue;
                        }
                        if (_cachedMediaKeys.Contains(key))
                        {
                            continue;
                        }

                        inFlightKey = key;
                        inFlightSnapshot = candidate;
                        _mediaInFlightKey = key;
                        break;
                    }
                }

                if (inFlightSnapshot?.Media is not { } media || inFlightKey is null)
                {
                    return;
                }

                var cached = TosuMediaCache.Cache(
                    media,
                    _primaryMediaMirror,
                    _fallbackMediaMirrors,
                    workerCts.Token);
                workerCts.Token.ThrowIfCancellationRequested();

                lock (_mediaGate)
                {
                    MarkMediaCachedUnderLock(inFlightKey);
                    _mediaInFlightKey = null;
                    inFlightKey = null;
                    inFlightSnapshot = null;
                }

                if (cached is not null)
                {
                    _store.Update(s => s with
                    {
                        Media = s.Media with
                        {
                            BeatmapFile = cached.BeatmapFile,
                            Audio = cached.AudioFile,
                            Background = cached.BackgroundFile,
                            Mirror = _primaryMediaMirror,
                            LastError = null,
                        },
                    });
                }
            }
        }
        catch (OperationCanceledException) when (workerCts.IsCancellationRequested)
        {
            // Gameplay resumed or the service is shutting down. Requeue the
            // interrupted map so a later idle window can finish it.
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Unexpected tosu media cache failure for {Beatmap}", inFlightSnapshot?.BeatmapDisplay);
            _store.Update(s => s with
            {
                Media = s.Media with
                {
                    Mirror = _primaryMediaMirror,
                    LastError = ex.Message,
                },
            });
        }
        finally
        {
            var shouldRestart = false;
            lock (_mediaGate)
            {
                if (inFlightKey is not null
                    && inFlightSnapshot is not null
                    && !_pendingMedia.ContainsKey(inFlightKey)
                    && !_cachedMediaKeys.Contains(inFlightKey))
                {
                    _pendingMedia[inFlightKey] = inFlightSnapshot;
                    _pendingMediaOrder.Enqueue(inFlightKey);
                }

                if (string.Equals(_mediaInFlightKey, inFlightKey, StringComparison.OrdinalIgnoreCase))
                {
                    _mediaInFlightKey = null;
                }
                if (ReferenceEquals(_mediaWorkCts, workerCts))
                {
                    _mediaWorkCts = null;
                    _mediaCacheTask = null;
                }
                shouldRestart = !_gameplayActive;
            }

            workerCts.Dispose();
            if (shouldRestart)
            {
                lock (_mediaGate)
                {
                    TryStartMediaCacheWorkerUnderLock();
                }
            }
        }
    }

    private static async Task CancelMediaWorkAsync(CancellationTokenSource workerCts)
    {
        try
        {
            await workerCts.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // The worker completed between capture and cancellation.
        }
    }

    private static string MediaKey(TosuSnapshot snapshot) =>
        snapshot.Checksum ?? snapshot.BeatmapIdentity;

    private void MarkMediaCachedUnderLock(string key)
    {
        if (!_cachedMediaKeys.Add(key))
            return;

        _cachedMediaOrder.Enqueue(key);
        while (_cachedMediaKeys.Count > MaxRememberedMediaMaps
               && _cachedMediaOrder.TryDequeue(out var expired))
        {
            _cachedMediaKeys.Remove(expired);
        }
    }

    /// <summary>Degrades health when packets stop arriving while connected.</summary>
    private async Task HealthLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(HealthTickInterval, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            if (_client.LastPacketMonoTime is not { } last)
            {
                continue;
            }
            var now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
            var age = now - last;
            if (age > StaleThreshold.TotalSeconds)
            {
                long epoch;
                lock (_statePublishGate)
                {
                    if (!_connected)
                    {
                        continue;
                    }
                    epoch = _stateEpoch;
                }
                _store.Update(s => Volatile.Read(ref _stateEpoch) == epoch
                                   && _connected
                                   && s.Tracking.Health != HealthLevel.Degraded
                    ? s with
                    {
                        Tracking = s.Tracking with
                        {
                            LastPacketAgeSeconds = age,
                            Health = HealthLevel.Degraded,
                            Detail = $"no packets for {age:0}s",
                        },
                    }
                    : s);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        lock (_trackingGate)
        {
            _attemptTracker?.EndInterrupted();
            if (_sessionTracker?.HasSession == true && _client.LastSnapshot is { } snapshot)
            {
                _sessionTracker.EndClean(snapshot.WallTime, snapshot.MonoTime);
            }
        }
        Task? mediaTask;
        CancellationTokenSource? mediaWorkCts;
        lock (_mediaGate)
        {
            mediaWorkCts = _mediaWorkCts;
            mediaTask = _mediaCacheTask;
        }
        if (mediaWorkCts is not null)
        {
            await CancelMediaWorkAsync(mediaWorkCts);
        }
        foreach (var task in new[] { _runTask, _healthTask, _statePublishTask, mediaTask })
        {
            if (task is not null)
            {
                try { await task; } catch { /* cancellation */ }
            }
        }
        await _source.DisposeAsync();
        _cts.Dispose();
    }

    /// <summary>Ends the live attempt and session when osu! closes or the user ends a session.</summary>
    public bool EndSession(string evidence = "session_ended")
    {
        var snapshot = _client.LastSnapshot;
        if (snapshot is null)
        {
            return false;
        }

        lock (_trackingGate)
        {
            _attemptTracker?.EndInterrupted(evidence);
            if (_sessionTracker?.HasSession == true)
            {
                _sessionTracker.EndClean(snapshot.WallTime, snapshot.MonoTime);
                return true;
            }
        }

        return false;
    }

    public void NotifyOsuStopped()
    {
        _client.ResetReplayPlaybackState();
        var snapshot = _client.LastSnapshot;
        if (snapshot is null)
        {
            return;
        }

        lock (_trackingGate)
        {
            _attemptTracker?.EndInterrupted("osu_stopped");
            _sessionTracker?.EndInterrupted(snapshot.WallTime, snapshot.MonoTime);
        }
    }
}
