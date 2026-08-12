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
    public string? PlayerName { get; init; }
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
    public OsuClientKind ClientKind { get; init; }
    public string? GameFolder { get; init; }
    public string? BeatmapFile { get; init; }
    public string? SongsFolder { get; init; }
    public string? BeatmapFolder { get; init; }
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
    public string? PlayerName { get; init; }
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
    public string ModsKey { get; init; } = "NM";
    public IReadOnlyList<AttemptMod> Mods { get; init; } = Array.Empty<AttemptMod>();
    public bool ModsAreAuthoritativeResult { get; init; }
}

public sealed class AttemptTracker
{
    public const double WriteIntervalSeconds = 1.0;
    public const double MinimumAttemptSeconds = 3.0;
    public const int MinimumConfigurableAttemptSeconds = 1;
    public const int MaximumConfigurableAttemptSeconds = 300;

    private static readonly HashSet<string> PromotableOutcomes = new()
    {
        "retried", "quit", "abandoned",
    };

    private readonly IAttemptSink _sink;
    private readonly AttemptStateMachine _machine;
    private readonly JudgementCapture _judgements;
    private readonly Func<int> _minimumAttemptSecondsProvider;

    private Frame? _lastFrame;
    private Frame? _pendingQuitFrame;
    private double _lastCheckpointMonoTime;
    private double _attemptStartedMonoTime;
    private long _attemptStartMapTimeMs;
    private int _attemptOrdinal;
    private AttemptSnapshot? _latestSnapshot;
    private IReadOnlyList<AttemptMod> _attemptMods = Array.Empty<AttemptMod>();
    private string _attemptModsKey = "NM";
    private bool _modsAreAuthoritativeResult;
    private int _attemptMinimumSeconds = (int)MinimumAttemptSeconds;
    private double? _lastTrustedAccuracy;
    private bool _placeholderAccuracyDetected;

    public AttemptTracker(
        IAttemptSink sink,
        AttemptStateMachine? machine = null,
        JudgementCapture? judgements = null,
        Func<int>? minimumAttemptSecondsProvider = null)
    {
        _sink = sink;
        _machine = machine ?? new AttemptStateMachine();
        _judgements = judgements ?? new JudgementCapture();
        _minimumAttemptSecondsProvider = minimumAttemptSecondsProvider ?? (() => (int)MinimumAttemptSeconds);
    }

    public readonly record struct Frame
    {
        public Frame() { }

        public AttemptStateMachine.PacketView Packet { get; init; } = new();
        public JudgementCapture.PlayValues Play { get; init; } = new();
        public double WallTime { get; init; }
        public bool IsStandardMode { get; init; } = true;
        public OsuClientKind ClientKind { get; init; }
        public string? GameFolder { get; init; }
        public string? BeatmapFile { get; init; }
        public string? SongsFolder { get; init; }
        public string? BeatmapFolder { get; init; }
        public int Score { get; init; }
        public string? Grade { get; init; }
        public string? Artist { get; init; }
        public string? Title { get; init; }
        public string? Mapper { get; init; }
        public string? Difficulty { get; init; }
        public long? BeatmapId { get; init; }
        public long? BeatmapSetId { get; init; }
        public string? Checksum { get; init; }
        public string? PlayerName { get; init; }
        public BeatmapStats BeatmapStats { get; init; } = new();
        public string ModsKey { get; init; } = "NM";
        public IReadOnlyList<AttemptMod> Mods { get; init; } = Array.Empty<AttemptMod>();
        public bool ModsAreAuthoritativeResult { get; init; }
        public double Pp { get; init; }
        public double FcPp { get; init; }
        public double MaxPp { get; init; }
        public bool IsWatchedReplay { get; init; }
        public bool HasAutoMod { get; init; }
    }

    public void Ingest(Frame frame)
    {
        if (frame.IsWatchedReplay)
        {
            DiscardSuppressedAttempt(frame, "watched_replay");
            return;
        }

        if (frame.HasAutoMod)
        {
            DiscardSuppressedAttempt(frame, "auto_mod");
            return;
        }

        if (!frame.IsStandardMode)
        {
            return;
        }

        if (_machine.TryIngest(frame.Packet, out var decision)
            && decision!.Kind == AttemptStateMachine.Kind.StaleResultsIgnored)
        {
            return;
        }

        if (decision is not null)
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

    private void DiscardSuppressedAttempt(Frame frame, string reason)
    {
        if (!_machine.HasAttempt)
        {
            return;
        }

        var snapshot = _latestSnapshot ?? Snapshot(frame);
        Log.Information(
            "Discarding suppressed attempt {Ordinal}: {Reason}; duration={DurationSeconds:0.00}s score={Score} progress={Progress:P1}",
            _attemptOrdinal,
            reason,
            snapshot.DurationSeconds,
            snapshot.Score,
            snapshot.Progress);
        _sink.DiscardIfEmpty(new AttemptDiscard(reason, snapshot, _attemptOrdinal));
        _machine.AttemptCleared();
        _pendingQuitFrame = null;
        ClearAttempt(decrementOrdinal: true);
    }

    private static Frame MergeFinalFrame(Frame? previous, Frame result)
    {
        if (previous is null)
        {
            return result;
        }

        var prior = previous.Value;
        var play = result.Play;
        return result with
        {
            Packet = result.Packet with
            {
                LiveTimeMs = result.Packet.LiveTimeMs > 0
                    ? result.Packet.LiveTimeMs
                    : prior.Packet.LiveTimeMs,
            },
            Play = play with
            {
                Hit300 = play.Hit300 > 0 ? play.Hit300 : prior.Play.Hit300,
                Hit100 = play.Hit100 > 0 ? play.Hit100 : prior.Play.Hit100,
                Hit50 = play.Hit50 > 0 ? play.Hit50 : prior.Play.Hit50,
                Miss = play.Miss > 0 ? play.Miss : prior.Play.Miss,
                Geki = play.Geki > 0 ? play.Geki : prior.Play.Geki,
                Katu = play.Katu > 0 ? play.Katu : prior.Play.Katu,
                SliderBreak = play.SliderBreak > 0 ? play.SliderBreak : prior.Play.SliderBreak,
                LargeTickHit = play.LargeTickHit > 0 ? play.LargeTickHit : prior.Play.LargeTickHit,
                LargeTickMiss = play.LargeTickMiss > 0 ? play.LargeTickMiss : prior.Play.LargeTickMiss,
                SmallTickHit = play.SmallTickHit > 0 ? play.SmallTickHit : prior.Play.SmallTickHit,
                SmallTickMiss = play.SmallTickMiss > 0 ? play.SmallTickMiss : prior.Play.SmallTickMiss,
                SliderTailHit = play.SliderTailHit > 0 ? play.SliderTailHit : prior.Play.SliderTailHit,
                SliderTailMiss = play.SliderTailMiss > 0 ? play.SliderTailMiss : prior.Play.SliderTailMiss,
                Combo = play.Combo > 0 ? play.Combo : prior.Play.Combo,
                PpPeak = play.PpPeak > 0 ? play.PpPeak : prior.Play.PpPeak,
                PpCurrent = play.PpCurrent > 0 ? play.PpCurrent : prior.Play.PpCurrent,
                Accuracy = play.Accuracy > 0 ? play.Accuracy : prior.Play.Accuracy,
                Health = play.Health > 0 ? play.Health : prior.Play.Health,
                UnstableRate = play.UnstableRate > 0 ? play.UnstableRate : prior.Play.UnstableRate,
                Progress = play.Progress ?? prior.Play.Progress,
            },
            Score = result.Score > 0 ? result.Score : prior.Score,
            Grade = !string.IsNullOrWhiteSpace(result.Grade) ? result.Grade : prior.Grade,
            Pp = result.Pp > 0 ? result.Pp : prior.Pp,
            FcPp = result.FcPp > 0 ? result.FcPp : prior.FcPp,
            MaxPp = result.MaxPp > 0 ? result.MaxPp : prior.MaxPp,
        };
    }

    private void StartAttempt(Frame frame)
    {
        _attemptOrdinal++;
        _attemptMinimumSeconds = Math.Clamp(
            _minimumAttemptSecondsProvider(),
            MinimumConfigurableAttemptSeconds,
            MaximumConfigurableAttemptSeconds);
        _lastTrustedAccuracy = null;
        _placeholderAccuracyDetected = false;
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
        _attemptMods = frame.Mods;
        _attemptModsKey = frame.ModsKey;
        _modsAreAuthoritativeResult = frame.ModsAreAuthoritativeResult;
        _sink.StartAttempt(new AttemptStart
        {
            Identity = frame.Packet.Identity,
            StartedMonoTime = frame.Packet.MonoTime,
            WallTime = frame.WallTime,
            LiveTimeMs = frame.Packet.LiveTimeMs,
            Ordinal = _attemptOrdinal,
            PlayerName = frame.PlayerName,
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
            ClientKind = frame.ClientKind,
            GameFolder = frame.GameFolder,
            BeatmapFile = frame.BeatmapFile,
            SongsFolder = frame.SongsFolder,
            BeatmapFolder = frame.BeatmapFolder,
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
        double observedDurationSeconds = Math.Max(0, frame.Packet.MonoTime - _attemptStartedMonoTime);
        // Do not discard a sustained play solely because the telemetry source
        // temporarily cannot expose its score counters. osu!lazer updates can
        // leave tosu able to report state transitions while its GameBase reader
        // is recovering, which previously turned real plays into
        // `invalid_final_attempt` records. The short-attempt guard and the
        // retry/pre-play discard path still filter spurious transitions. A
        // mid-play attachment with no score or judgement evidence must itself
        // remain observable for the minimum duration; otherwise one stale
        // gameplay/results packet can manufacture a long phantom attempt from
        // the inherited map clock.
        if (snapshot.DurationSeconds < _attemptMinimumSeconds
            || (!HasJudgement(snapshot) && observedDurationSeconds < _attemptMinimumSeconds))
        {
            Log.Information(
                "Discarding attempt {Ordinal}: {Reason}; duration={DurationSeconds:0.00}s observed={ObservedDurationSeconds:0.00}s score={Score} hits={N300}/{N100}/{N50}/{Misses} progress={Progress:P1} outcome={Outcome} evidence={Evidence}",
                _attemptOrdinal,
                "invalid_final_attempt",
                snapshot.DurationSeconds,
                observedDurationSeconds,
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

        if (_placeholderAccuracyDetected && !evidence.Contains("accuracy_placeholder_guard", StringComparison.Ordinal))
            evidence = $"{evidence}:accuracy_placeholder_guard";

        if (string.IsNullOrWhiteSpace(snapshot.Grade) &&
            OsuGradeCalculator.Calculate(snapshot, outcome) is { } calculatedGrade)
        {
            snapshot = snapshot with { Grade = calculatedGrade };
            Log.Debug("Calculated missing osu! grade {Grade} for attempt {Ordinal}", calculatedGrade, _attemptOrdinal);
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
        RememberAttemptMods(frame);
        var mapElapsed = Math.Max(0, (frame.Packet.LiveTimeMs - _attemptStartMapTimeMs) / 1000.0);
        var duration = Math.Max(0, Math.Max(frame.Packet.MonoTime - _attemptStartedMonoTime, mapElapsed));
        var snapshot = new AttemptSnapshot
        {
            Identity = frame.Packet.Identity,
            MonoTime = frame.Packet.MonoTime,
            WallTime = frame.WallTime,
            LiveTimeMs = frame.Packet.LiveTimeMs,
            PlayerName = frame.PlayerName,
            DurationSeconds = duration,
            Score = frame.Score,
            Accuracy = TrustedAccuracy(frame),
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
            ModsKey = _attemptModsKey,
            Mods = _attemptMods,
            ModsAreAuthoritativeResult = _modsAreAuthoritativeResult,
        };
        _latestSnapshot = snapshot;
        return snapshot;
    }

    private double TrustedAccuracy(Frame frame)
    {
        var accuracy = frame.Play.Accuracy;
        if (!double.IsFinite(accuracy))
        {
            _placeholderAccuracyDetected = true;
            return LastTrustedAccuracyOrZero();
        }
        accuracy = Math.Clamp(accuracy, 0, 100);
        var impossiblePerfect = accuracy >= 99.999999
            && (frame.Play.Hit100 > 0 || frame.Play.Hit50 > 0 || frame.Play.Miss > 0);
        if (impossiblePerfect)
        {
            if (!_placeholderAccuracyDetected)
            {
                Log.Warning(
                    "Ignoring placeholder 100% accuracy for attempt {Ordinal}; hits={N100}/{N50}/{Misses}",
                    _attemptOrdinal,
                    frame.Play.Hit100,
                    frame.Play.Hit50,
                    frame.Play.Miss);
            }
            _placeholderAccuracyDetected = true;
            return LastTrustedAccuracyOrZero();
        }
        if (accuracy > 0)
            _lastTrustedAccuracy = accuracy;
        return accuracy;
    }

    private double LastTrustedAccuracyOrZero() =>
        _lastTrustedAccuracy is >= 0 and < 99.999999 ? _lastTrustedAccuracy.Value : 0;

    private void ClearAttempt(bool decrementOrdinal)
    {
        _latestSnapshot = null;
        _attemptStartedMonoTime = 0;
        _attemptStartMapTimeMs = 0;
        _lastCheckpointMonoTime = 0;
        _attemptMods = Array.Empty<AttemptMod>();
        _attemptModsKey = "NM";
        _modsAreAuthoritativeResult = false;
        if (decrementOrdinal)
        {
            _attemptOrdinal = Math.Max(0, _attemptOrdinal - 1);
        }
    }

    private void RememberAttemptMods(Frame frame)
    {
        var incomingHasGameplayMod = frame.Mods.Any(mod =>
            !mod.Acronym.Equals("CL", StringComparison.OrdinalIgnoreCase));
        var rememberedHasGameplayMod = _attemptMods.Any(mod =>
            !mod.Acronym.Equals("CL", StringComparison.OrdinalIgnoreCase));

        // lazer ScoreInfo.ModsJson preserves custom mods, while tosu's
        // positional menu mapping does not. Once BPM has been observed during
        // this attempt, a later FR/empty transition packet cannot represent a
        // real mod change and must not replace the authoritative settings.
        if (frame.ClientKind == OsuClientKind.Lazer
            && !frame.ModsAreAuthoritativeResult
            && _attemptMods.Any(mod => mod.Acronym.Equals("BPM", StringComparison.OrdinalIgnoreCase))
            && !frame.Mods.Any(mod => mod.Acronym.Equals("BPM", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        // osu!stable resets play.mods on the result transition. Once a real
        // gameplay mod has been observed, never downgrade that attempt to CL-only.
        if (frame.ClientKind == OsuClientKind.Stable && rememberedHasGameplayMod && !incomingHasGameplayMod)
            return;

        if (frame.Mods.Count > 0 || !frame.ModsKey.Equals("NM", StringComparison.OrdinalIgnoreCase))
        {
            _attemptMods = frame.Mods;
            _attemptModsKey = frame.ModsKey;
            _modsAreAuthoritativeResult = frame.ModsAreAuthoritativeResult;
        }
    }

    private static bool HasJudgement(AttemptSnapshot snapshot) =>
        snapshot.Score > 0
        || snapshot.N300 > 0
        || snapshot.N100 > 0
        || snapshot.N50 > 0
        || snapshot.Misses > 0;
}
