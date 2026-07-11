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

    private readonly AppStateStore _store;
    private readonly WebSocketPacketSource _source;
    private readonly TosuClient _client = new();
    private readonly AttemptTracker? _attemptTracker;
    private readonly SessionTracker? _sessionTracker;
    private readonly string _primaryMediaMirror;
    private readonly IReadOnlyList<string> _fallbackMediaMirrors;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _trackingGate = new();
    private Task? _runTask;
    private Task? _healthTask;
    private volatile bool _connected;
    private readonly HashSet<string> _cachedMediaKeys = new(StringComparer.OrdinalIgnoreCase);

    public TosuTrackingService(
        AppStateStore store,
        Uri? uri = null,
        AttemptTracker? attemptTracker = null,
        SessionTracker? sessionTracker = null,
        string primaryMediaMirror = "https://api.rai.moe",
        IReadOnlyList<string>? fallbackMediaMirrors = null,
        bool recordPackets = false)
    {
        _store = store;
        _source = new WebSocketPacketSource(uri, recordPackets);
        _attemptTracker = attemptTracker;
        _sessionTracker = sessionTracker;
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
    }

    private void OnConnected()
    {
        _connected = true;
        _store.Update(s => s with
        {
            Tracking = s.Tracking with
            {
                TosuConnected = true,
                Health = HealthLevel.Ok,
                Detail = null,
            },
        });
    }

    private void OnDisconnected(string? reason)
    {
        _connected = false;
        _store.Update(s => s with
        {
            Tracking = s.Tracking with
            {
                TosuConnected = false,
                CurrentBeatmap = null,
                Health = HealthLevel.Error,
                Detail = reason is null ? "tosu not reachable" : $"tosu: {reason}",
            },
        });
    }

    private void OnSnapshot(TosuSnapshot snapshot)
    {
        try
        {
            if (snapshot.IsStandardMode)
            {
                CacheMedia(snapshot);
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
            _store.Update(s => s with
            {
                Tracking = s.Tracking with
                {
                    Health = HealthLevel.Error,
                    Detail = $"tracking save failed: {ex.Message}",
                },
            });
            return;
        }

        _store.Update(s => s with
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
        });
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

    private void CacheMedia(TosuSnapshot snapshot)
    {
        if (snapshot.Media is not { } media)
        {
            return;
        }

        var key = snapshot.Checksum ?? snapshot.BeatmapId?.ToString() ?? snapshot.BeatmapIdentity;
        lock (_cachedMediaKeys)
        {
            if (!_cachedMediaKeys.Add(key))
            {
                return;
            }
        }

        _ = Task.Run(() =>
        {
            try
            {
                var cached = TosuMediaCache.Cache(media, _primaryMediaMirror, _fallbackMediaMirrors);
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
            catch (Exception ex)
            {
                Log.Warning(ex, "Unexpected tosu media cache failure for {Beatmap}", snapshot.BeatmapDisplay);
                _store.Update(s => s with
                {
                    Media = s.Media with
                    {
                        Mirror = _primaryMediaMirror,
                        LastError = ex.Message,
                    },
                });
            }
        });
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
            if (!_connected || _client.LastPacketMonoTime is not { } last)
            {
                continue;
            }
            var now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
            var age = now - last;
            if (age > StaleThreshold.TotalSeconds)
            {
                _store.Update(s => s.Tracking.Health == HealthLevel.Degraded
                    ? s // already degraded: keep snapshot identity, no notify
                    : s with
                    {
                        Tracking = s.Tracking with
                        {
                            LastPacketAgeSeconds = age,
                            Health = HealthLevel.Degraded,
                            Detail = $"no packets for {age:0}s",
                        },
                    });
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
        foreach (var task in new[] { _runTask, _healthTask })
        {
            if (task is not null)
            {
                try { await task; } catch { /* cancellation */ }
            }
        }
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
