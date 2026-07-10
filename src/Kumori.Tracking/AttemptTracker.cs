using Serilog;

namespace Kumori.Tracking;

public interface IAttemptSink
{
    void StartAttempt(AttemptStart start);
    void Checkpoint(AttemptCheckpoint checkpoint);
    void DiscardIfEmpty(AttemptDiscard discard);
    void Finalize(AttemptFinalization finalization);
}

public sealed record AttemptStart
{
    public string Identity { get; init; } = "unknown";
    public double StartedMonoTime { get; init; }
    public double WallTime { get; init; }
    public long LiveTimeMs { get; init; }
    public int Ordinal { get; init; }
    public string? Artist { get; init; }
    public string? Title { get; init; }
    public string? Mapper { get; init; }
    public string? Difficulty { get; init; }
    public long? BeatmapId { get; init; }
    public long? BeatmapSetId { get; init; }
    public string? Checksum { get; init; }
    public BeatmapStats BeatmapStats { get; init; } = new();
    public string ModsKey { get; init; } = "NM";
    public IReadOnlyList<AttemptMod> Mods { get; init; } = Array.Empty<AttemptMod>();
}

public sealed record AttemptMod(string Acronym, string SettingsJson = "{}");

public sealed record AttemptCheckpoint(
    AttemptSnapshot Snapshot,
    IReadOnlyList<JudgementCapture.CapturedEvent> Events,
    bool Forced);

public sealed record AttemptDiscard(
    string Reason,
    AttemptSnapshot Snapshot,
    int Ordinal);

public sealed record AttemptFinalization(
    string Outcome,
    string Evidence,
    AttemptSnapshot Snapshot,
    int Ordinal);

public sealed record AttemptSnapshot
{
    public string Identity { get; init; } = "unknown";
    public double MonoTime { get; init; }
    public double WallTime { get; init; }
    public long LiveTimeMs { get; init; }
    public double DurationSeconds { get; init; }
    public int Score { get; init; }
    public double Accuracy { get; init; }
    public string? Grade { get; init; }
    public double Pp { get; init; }
    public double FcPp { get; init; }
    public double MaxPp { get; init; }
    public double Combo { get; init; }
    public double N300 { get; init; }
    public double N100 { get; init; }
    public double N50 { get; init; }
    public double Misses { get; init; }
    public double Geki { get; init; }
    public double Katu { get; init; }
    public double SliderBreaks { get; init; }
    public double LargeTickHits { get; init; }
    public double LargeTickMisses { get; init; }
    public double SmallTickHits { get; init; }
    public double SmallTickMisses { get; init; }
    public double SliderTailHits { get; init; }
    public double SliderTailMisses { get; init; }
    public double UnstableRate { get; init; }
    public double Progress { get; init; }
    public IReadOnlyList<double> TimingOffsets { get; init; } = Array.Empty<double>();
    public BeatmapStats BeatmapStats { get; init; } = new();
}

public sealed class AttemptTracker
{
    public const double WriteIntervalSeconds = 1.0;
    public const double MinimumAttemptSeconds = 3.0;

    private static readonly HashSet<string> PromotableOutcomes = new()
    {
        "retried", "quit", "abandoned",
    };

    private readonly IAttemptSink _sink;
    private readonly AttemptStateMachine _machine;
    private readonly JudgementCapture _judgements;

    private Frame? _lastFrame;
    private Frame? _pendingQuitFrame;
    private double _lastCheckpointMonoTime;
    private double _attemptStartedMonoTime;
    private long _attemptStartMapTimeMs;
    private int _attemptOrdinal;
    private AttemptSnapshot? _latestSnapshot;

    public AttemptTracker(
        IAttemptSink sink,
        AttemptStateMachine? machine = null,
        JudgementCapture? judgements = null)
    {
        _sink = sink;
        _machine = machine ?? new AttemptStateMachine();
        _judgements = judgements ?? new JudgementCapture();
    }

    public sealed record Frame
    {
        public AttemptStateMachine.PacketView Packet { get; init; } = new();
        public JudgementCapture.PlayValues Play { get; init; } = new();
        public double WallTime { get; init; }
        public bool IsStandardMode { get; init; } = true;
        public int Score { get; init; }
        public string? Grade { get; init; }
        public string? Artist { get; init; }
        public string? Title { get; init; }
        public string? Mapper { get; init; }
        public string? Difficulty { get; init; }
        public long? BeatmapId { get; init; }
        public long? BeatmapSetId { get; init; }
        public string? Checksum { get; init; }
        public BeatmapStats BeatmapStats { get; init; } = new();
        public string ModsKey { get; init; } = "NM";
        public IReadOnlyList<AttemptMod> Mods { get; init; } = Array.Empty<AttemptMod>();
        public double Pp { get; init; }
        public double FcPp { get; init; }
        public double MaxPp { get; init; }
    }

    public void Ingest(Frame frame)
    {
        if (!frame.IsStandardMode)
        {
            return;
        }

        var decisions = _machine.Ingest(frame.Packet);
        if (decisions.Any(d => d.Kind == AttemptStateMachine.Kind.StaleResultsIgnored))
        {
            return;
        }

        foreach (var decision in decisions)
        {
            switch (decision.Kind)
            {
                case AttemptStateMachine.Kind.StartAttempt:
                    StartAttempt(frame);
                    break;
                case AttemptStateMachine.Kind.FinalizeAndStart:
                    Finalize(decision.Outcome!, decision.Evidence!, BoundaryFrame(frame), results: false);
                    _machine.AttemptCleared();
                    StartAttempt(frame);
                    break;
                case AttemptStateMachine.Kind.RetryBoundary:
                    if (!DiscardEmpty(BoundaryFrame(frame)))
                    {
                        Finalize(decision.Outcome!, decision.Evidence!, BoundaryFrame(frame), results: false);
                    }
                    _machine.AttemptCleared();
                    StartAttempt(frame);
                    break;
                case AttemptStateMachine.Kind.GraceStarted:
                    _pendingQuitFrame = _lastFrame;
                    break;
                case AttemptStateMachine.Kind.Finalize:
                    var snapshotFrame = decision.Evidence == "results_screen"
                        ? MergeFinalFrame(_lastFrame, frame)
                        : _pendingQuitFrame ?? _lastFrame ?? frame;
                    Finalize(decision.Outcome!, decision.Evidence!, snapshotFrame, decision.Evidence == "results_screen");
                    _machine.AttemptCleared();
                    _pendingQuitFrame = null;
                    break;
            }
        }

        if (_machine.HasAttempt && frame.Packet.IsPlaying)
        {
            CaptureCritical(frame);
            Checkpoint(frame, force: false);
        }

        _lastFrame = frame;
    }

    /// <summary>
    /// Persists the most recent valid gameplay snapshot when tracking is stopped
    /// before osu! supplies a normal result, retry, or quit boundary.
    /// </summary>
    public void EndInterrupted(string evidence = "tracker_stopped")
    {
        if (!_machine.HasAttempt || _latestSnapshot is null)
        {
            return;
        }

        if (_lastFrame is { } frame)
        {
            Finalize("abandoned", evidence, frame, results: false);
        }

        _machine.AttemptCleared();
        _pendingQuitFrame = null;
    }

    private Frame BoundaryFrame(Frame current) => _pendingQuitFrame ?? _lastFrame ?? current;

    private static Frame MergeFinalFrame(Frame? previous, Frame result)
    {
        if (previous is null)
        {
            return result;
        }

        var play = result.Play;
        return result with
        {
            Packet = result.Packet with
            {
                LiveTimeMs = result.Packet.LiveTimeMs > 0
                    ? result.Packet.LiveTimeMs
                    : previous.Packet.LiveTimeMs,
            },
            Play = play with
            {
                Hit300 = play.Hit300 > 0 ? play.Hit300 : previous.Play.Hit300,
                Hit100 = play.Hit100 > 0 ? play.Hit100 : previous.Play.Hit100,
                Hit50 = play.Hit50 > 0 ? play.Hit50 : previous.Play.Hit50,
                Miss = play.Miss > 0 ? play.Miss : previous.Play.Miss,
                Geki = play.Geki > 0 ? play.Geki : previous.Play.Geki,
                Katu = play.Katu > 0 ? play.Katu : previous.Play.Katu,
                SliderBreak = play.SliderBreak > 0 ? play.SliderBreak : previous.Play.SliderBreak,
                LargeTickHit = play.LargeTickHit > 0 ? play.LargeTickHit : previous.Play.LargeTickHit,
                LargeTickMiss = play.LargeTickMiss > 0 ? play.LargeTickMiss : previous.Play.LargeTickMiss,
                SmallTickHit = play.SmallTickHit > 0 ? play.SmallTickHit : previous.Play.SmallTickHit,
                SmallTickMiss = play.SmallTickMiss > 0 ? play.SmallTickMiss : previous.Play.SmallTickMiss,
                SliderTailHit = play.SliderTailHit > 0 ? play.SliderTailHit : previous.Play.SliderTailHit,
                SliderTailMiss = play.SliderTailMiss > 0 ? play.SliderTailMiss : previous.Play.SliderTailMiss,
                Combo = play.Combo > 0 ? play.Combo : previous.Play.Combo,
                PpPeak = play.PpPeak > 0 ? play.PpPeak : previous.Play.PpPeak,
                PpCurrent = play.PpCurrent > 0 ? play.PpCurrent : previous.Play.PpCurrent,
                Accuracy = play.Accuracy > 0 ? play.Accuracy : previous.Play.Accuracy,
                Health = play.Health > 0 ? play.Health : previous.Play.Health,
                UnstableRate = play.UnstableRate > 0 ? play.UnstableRate : previous.Play.UnstableRate,
                Progress = play.Progress ?? previous.Play.Progress,
            },
            Score = result.Score > 0 ? result.Score : previous.Score,
            Grade = !string.IsNullOrWhiteSpace(result.Grade) ? result.Grade : previous.Grade,
            Pp = result.Pp > 0 ? result.Pp : previous.Pp,
            FcPp = result.FcPp > 0 ? result.FcPp : previous.FcPp,
            MaxPp = result.MaxPp > 0 ? result.MaxPp : previous.MaxPp,
        };
    }

    private void StartAttempt(Frame frame)
    {
        _attemptOrdinal++;
        _attemptStartedMonoTime = frame.Packet.MonoTime;
        // Tracking can attach after a play has already started (for example,
        // after startup or a websocket reconnect). Treat the map clock as
        // elapsed time from the map's beginning so a legitimate near-finished
        // play is not discarded merely because Kumori observed its last few
        // seconds.
        _attemptStartMapTimeMs = 0;
        _lastCheckpointMonoTime = 0;
        _latestSnapshot = Snapshot(frame);
        _pendingQuitFrame = null;
        _judgements.Reset();
        _sink.StartAttempt(new AttemptStart
        {
            Identity = frame.Packet.Identity,
            StartedMonoTime = frame.Packet.MonoTime,
            WallTime = frame.WallTime,
            LiveTimeMs = frame.Packet.LiveTimeMs,
            Ordinal = _attemptOrdinal,
            Artist = frame.Artist,
            Title = frame.Title,
            Mapper = frame.Mapper,
            Difficulty = frame.Difficulty,
            BeatmapId = frame.BeatmapId,
            BeatmapSetId = frame.BeatmapSetId,
            Checksum = frame.Checksum,
            BeatmapStats = frame.BeatmapStats,
            ModsKey = frame.ModsKey,
            Mods = frame.Mods,
        });
        _machine.AttemptStarted(frame.Packet.Identity, frame.Packet.LiveTimeMs);
    }

    private bool DiscardEmpty(Frame frame)
    {
        var snapshot = _latestSnapshot ?? Snapshot(frame);
        if (HasJudgement(snapshot))
        {
            return false;
        }

        Log.Information(
            "Discarding attempt {Ordinal}: {Reason}; duration={DurationSeconds:0.00}s score={Score} hits={N300}/{N100}/{N50}/{Misses} progress={Progress:P1}",
            _attemptOrdinal,
            "empty_preplay",
            snapshot.DurationSeconds,
            snapshot.Score,
            snapshot.N300,
            snapshot.N100,
            snapshot.N50,
            snapshot.Misses,
            snapshot.Progress);
        _sink.DiscardIfEmpty(new AttemptDiscard("empty_preplay", snapshot, _attemptOrdinal));
        ClearAttempt(decrementOrdinal: true);
        return true;
    }

    private void Finalize(string outcome, string evidence, Frame frame, bool results)
    {
        if (_latestSnapshot is null)
        {
            return;
        }

        Checkpoint(frame, force: true);
        var snapshot = _latestSnapshot ?? Snapshot(frame);
        if (snapshot.DurationSeconds < MinimumAttemptSeconds || !HasJudgement(snapshot))
        {
            Log.Information(
                "Discarding attempt {Ordinal}: {Reason}; duration={DurationSeconds:0.00}s score={Score} hits={N300}/{N100}/{N50}/{Misses} progress={Progress:P1} outcome={Outcome} evidence={Evidence}",
                _attemptOrdinal,
                "invalid_final_attempt",
                snapshot.DurationSeconds,
                snapshot.Score,
                snapshot.N300,
                snapshot.N100,
                snapshot.N50,
                snapshot.Misses,
                snapshot.Progress,
                outcome,
                evidence);
            _sink.DiscardIfEmpty(new AttemptDiscard("invalid_final_attempt", snapshot, _attemptOrdinal));
            ClearAttempt(decrementOrdinal: true);
            return;
        }

        if (PromotableOutcomes.Contains(outcome) && snapshot.Progress >= 0.99)
        {
            outcome = "completed";
            evidence = $"{evidence}:complete_progress";
            snapshot = snapshot with { Progress = 1 };
            _latestSnapshot = snapshot;
        }

        _sink.Finalize(new AttemptFinalization(outcome, evidence, snapshot, _attemptOrdinal));
        ClearAttempt(decrementOrdinal: false);
    }

    private void CaptureCritical(Frame frame)
    {
        var events = _judgements.CaptureCritical(frame.Play);
        if (events.Count > 0)
        {
            _sink.Checkpoint(new AttemptCheckpoint(Snapshot(frame), events, Forced: false));
        }
    }

    private void Checkpoint(Frame frame, bool force)
    {
        if (!force && frame.Packet.MonoTime - _lastCheckpointMonoTime < WriteIntervalSeconds)
        {
            _latestSnapshot = Snapshot(frame);
            return;
        }

        _lastCheckpointMonoTime = frame.Packet.MonoTime;
        var snapshot = Snapshot(frame);
        var events = _judgements.Capture(frame.Play, includeCheckpoint: true);
        _sink.Checkpoint(new AttemptCheckpoint(snapshot, events, force));
    }

    private AttemptSnapshot Snapshot(Frame frame)
    {
        var mapElapsed = Math.Max(0, (frame.Packet.LiveTimeMs - _attemptStartMapTimeMs) / 1000.0);
        var duration = Math.Max(0, Math.Max(frame.Packet.MonoTime - _attemptStartedMonoTime, mapElapsed));
        var snapshot = new AttemptSnapshot
        {
            Identity = frame.Packet.Identity,
            MonoTime = frame.Packet.MonoTime,
            WallTime = frame.WallTime,
            LiveTimeMs = frame.Packet.LiveTimeMs,
            DurationSeconds = duration,
            Score = frame.Score,
            Accuracy = frame.Play.Accuracy,
            Grade = frame.Grade ?? frame.Packet.Grade,
            Pp = frame.Pp,
            FcPp = frame.FcPp,
            MaxPp = frame.MaxPp,
            Combo = frame.Play.Combo,
            N300 = frame.Play.Hit300,
            N100 = frame.Play.Hit100,
            N50 = frame.Play.Hit50,
            Misses = frame.Play.Miss,
            Geki = frame.Play.Geki,
            Katu = frame.Play.Katu,
            SliderBreaks = frame.Play.SliderBreak,
            LargeTickHits = frame.Play.LargeTickHit,
            LargeTickMisses = frame.Play.LargeTickMiss,
            SmallTickHits = frame.Play.SmallTickHit,
            SmallTickMisses = frame.Play.SmallTickMiss,
            SliderTailHits = frame.Play.SliderTailHit,
            SliderTailMisses = frame.Play.SliderTailMiss,
            UnstableRate = frame.Play.UnstableRate,
            Progress = frame.Play.Progress ?? 0,
            TimingOffsets = frame.Play.HitErrors,
            BeatmapStats = frame.BeatmapStats,
        };
        _latestSnapshot = snapshot;
        return snapshot;
    }

    private void ClearAttempt(bool decrementOrdinal)
    {
        _latestSnapshot = null;
        _attemptStartedMonoTime = 0;
        _attemptStartMapTimeMs = 0;
        _lastCheckpointMonoTime = 0;
        if (decrementOrdinal)
        {
            _attemptOrdinal = Math.Max(0, _attemptOrdinal - 1);
        }
    }

    private static bool HasJudgement(AttemptSnapshot snapshot) =>
        snapshot.Score > 0
        || snapshot.N300 > 0
        || snapshot.N100 > 0
        || snapshot.N50 > 0
        || snapshot.Misses > 0;
}
