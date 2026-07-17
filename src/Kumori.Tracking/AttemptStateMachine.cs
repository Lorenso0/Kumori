namespace Kumori.Tracking;

/// <summary>
/// Pure port of the legacy attempt-boundary logic. No I/O: emits decisions; the executor
/// (future attempt tracker) starts/finalizes attempts and owns snapshots.
///
/// Preserved semantics:
/// - Start attempt on playing with none active.
/// - Playing + identity changed → Finalize("abandoned","beatmap_changed") + start.
/// - Playing + restart (same map, prevLive > 1500 && curLive + 1000 &lt; prevLive)
///   OR re-entering play from a non-play state →
///   RetryBoundary (executor: discard-if-empty else finalize
///   "retried"/"fresh_gameplay_same_map") + start.
/// - Results with a genuinely different identity → StaleResultsIgnored
///   (a delayed results packet must never finalize the next attempt).
/// - Results otherwise → Finalize "failed" if grade == "F" else "completed",
///   evidence "results_screen".
/// - Leaving play (non-results) starts a 2.0 s grace window
///   (STATE_TRANSITION_GRACE_SECONDS); executor must capture the last real
///   gameplay snapshot on GraceStarted. If the non-play state persists past
///   the deadline: Finalize "failed" if hp &lt;= 0 else "quit", evidence
///   "state_transition:{pendingState}->{state}". Returning to play cancels
///   the grace window (transient/garbled packets never end an attempt).
/// </summary>
public sealed class AttemptStateMachine
{
    public const double StateTransitionGraceSeconds = 2.0;
    private static readonly HashSet<string> PlayStates = new() { "play", "playing", "gameplay" };

    public enum Kind
    {
        None,
        StartAttempt,
        FinalizeAndStart,   // abandoned/beatmap_changed
        RetryBoundary,      // discard-if-empty else finalize retried + start
        Finalize,           // results or grace expiry
        GraceStarted,       // capture boundary snapshot now
        StaleResultsIgnored,
    }

    public sealed record Decision(Kind Kind, string? Outcome = null, string? Evidence = null);

    public readonly record struct PacketView
    {
        public PacketView() { }

        public double MonoTime { get; init; }
        public string State { get; init; } = "";          // normalized
        public bool IsPlaying { get; init; }
        public bool IsResults { get; init; }
        public string Identity { get; init; } = "unknown";
        public long LiveTimeMs { get; init; }
        public string? Grade { get; init; }               // results packets
        public double Health { get; init; } = 1;          // play.healthBar.normal
    }

    public string? AttemptIdentity { get; private set; }
    public bool HasAttempt => AttemptIdentity is not null;

    private string _lastState = "";
    private long _lastLiveTimeMs;
    private double _lastHealth = 1;
    private double _pendingHealth = 1;
    private double? _closeDeadline;
    private string _pendingQuitState = "";

    /// <summary>Executor calls this after acting on StartAttempt-producing decisions.</summary>
    public void AttemptStarted(string identity, long liveTimeMs = 0)
    {
        AttemptIdentity = identity;
        _lastLiveTimeMs = liveTimeMs;
        _closeDeadline = null;
        _pendingQuitState = "";
    }

    public void AttemptCleared() => AttemptIdentity = null;

    public IReadOnlyList<Decision> Ingest(PacketView p)
        => TryIngest(p, out var decision) ? [decision!] : [];

    /// <summary>
    /// Allocation-free packet path. State transitions create a decision only
    /// at an attempt boundary; ordinary gameplay packets return false.
    /// </summary>
    public bool TryIngest(PacketView p, out Decision? decision)
    {
        decision = null;
        if (p.IsPlaying)
        {
            _closeDeadline = null;
            var restarted = HasAttempt
                && p.Identity == AttemptIdentity
                && ((_lastLiveTimeMs > 1500
                   && p.LiveTimeMs + 1000 < _lastLiveTimeMs)
                  // A very early retry can rewind before the ordinary
                  // 1.5-second boundary. Require both a meaningful amount
                  // of elapsed play and a return to the map origin so
                  // normal clock jitter cannot split the attempt.
                  || (_lastLiveTimeMs >= 500
                        && p.LiveTimeMs <= 100
                        && p.LiveTimeMs + 250 < _lastLiveTimeMs));

            if (!HasAttempt)
            {
                decision = new Decision(Kind.StartAttempt);
            }
            else if (p.Identity != AttemptIdentity)
            {
                decision = new Decision(Kind.FinalizeAndStart, "abandoned", "beatmap_changed");
            }
            else if (restarted || !PlayStates.Contains(_lastState))
            {
                decision = new Decision(Kind.RetryBoundary, "retried", "fresh_gameplay_same_map");
            }
            _pendingQuitState = "";
        }
        else if (p.IsResults && HasAttempt)
        {
            if (p.Identity is not ("" or "unknown")
                && AttemptIdentity is not (null or "" or "unknown")
                && p.Identity != AttemptIdentity)
            {
                decision = new Decision(Kind.StaleResultsIgnored);
                return true; // Python: early return — do NOT update last state
            }
            var outcome = string.Equals(p.Grade, "F", StringComparison.OrdinalIgnoreCase)
                ? "failed"
                : "completed";
            decision = new Decision(Kind.Finalize, outcome, "results_screen");
        }
        else if (HasAttempt && (PlayStates.Contains(_lastState) || _closeDeadline is not null))
        {
            if (_closeDeadline is null)
            {
                _closeDeadline = p.MonoTime + StateTransitionGraceSeconds;
                _pendingQuitState = _lastState;
                // Python uses _pending_quit_data (= last_data, the packet
                // BEFORE this transition) for the hp check — not this packet.
                _pendingHealth = _lastHealth;
                decision = new Decision(Kind.GraceStarted);
            }
            else if (p.MonoTime >= _closeDeadline)
            {
                var outcome = _pendingHealth <= 0 ? "failed" : "quit";
                decision = new Decision(
                    Kind.Finalize, outcome,
                    $"state_transition:{_pendingQuitState}->{p.State}");
                _closeDeadline = null;
                _pendingQuitState = "";
            }
        }

        _lastState = p.State;
        _lastHealth = p.Health;
        if (p.IsPlaying)
        {
            _lastLiveTimeMs = p.LiveTimeMs;
        }
        return decision is not null;
    }
}
