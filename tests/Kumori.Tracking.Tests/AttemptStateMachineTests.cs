using Kumori.Tracking;
using Xunit;
using static Kumori.Tracking.AttemptStateMachine;

namespace Kumori.Tracking.Tests;

public class AttemptStateMachineTests
{
    private static PacketView Play(
        double t, string id = "mapA", long live = 0, double hp = 1) => new()
    {
        MonoTime = t, State = "play", IsPlaying = true,
        Identity = id, LiveTimeMs = live, Health = hp,
    };

    private static PacketView Menu(double t, string state = "songselect") => new()
    {
        MonoTime = t, State = state,
    };

    private static PacketView Results(double t, string? grade = "S", string id = "mapA") => new()
    {
        MonoTime = t, State = "resultscreen", IsResults = true,
        Identity = id, Grade = grade,
    };

    private static AttemptStateMachine InPlay(string id = "mapA", long live = 0)
    {
        var m = new AttemptStateMachine();
        Assert.Contains(m.Ingest(Play(0, id, live)), d => d.Kind == Kind.StartAttempt);
        m.AttemptStarted(id, live);
        return m;
    }

    [Fact]
    public void PlayWithNoAttempt_Starts()
    {
        var m = new AttemptStateMachine();
        var d = Assert.Single(m.Ingest(Play(0)));
        Assert.Equal(Kind.StartAttempt, d.Kind);
    }

    [Fact]
    public void MapChangeMidPlay_AbandonsAndStarts()
    {
        var m = InPlay("mapA", 5000);
        var d = Assert.Single(m.Ingest(Play(1, "mapB")));
        Assert.Equal(Kind.FinalizeAndStart, d.Kind);
        Assert.Equal("abandoned", d.Outcome);
        Assert.Equal("beatmap_changed", d.Evidence);
    }

    [Fact]
    public void TimeRewind_SameMap_IsRetryBoundary()
    {
        var m = InPlay("mapA");
        m.Ingest(Play(1, live: 20000)); // establish prev live > 1500
        var d = Assert.Single(m.Ingest(Play(2, live: 500))); // 500+1000 < 20000
        Assert.Equal(Kind.RetryBoundary, d.Kind);
        Assert.Equal("fresh_gameplay_same_map", d.Evidence);
    }

    [Fact]
    public void SmallTimeJitter_IsNotRetry()
    {
        var m = InPlay("mapA");
        m.Ingest(Play(1, live: 20000));
        Assert.Empty(m.Ingest(Play(2, live: 19500))); // within 1000ms slack
    }

    [Fact]
    public void TransientMenuPacket_DoesNotFinalize()
    {
        var m = InPlay("mapA");
        m.Ingest(Play(1, live: 5000));
        var grace = Assert.Single(m.Ingest(Menu(2)));
        Assert.Equal(Kind.GraceStarted, grace.Kind);
        // Back in play within the 2s window: grace cancelled, but re-entering
        // play from non-play is a retry boundary (matches Python: last_state
        // was non-play, so the attempt restarts).
        var d = Assert.Single(m.Ingest(Play(3, live: 5100)));
        Assert.Equal(Kind.RetryBoundary, d.Kind);
    }

    [Fact]
    public void PersistedQuit_FinalizesAsQuit_AfterGrace()
    {
        var m = InPlay("mapA");
        m.Ingest(Play(1, live: 5000, hp: 0.8));
        Assert.Contains(m.Ingest(Menu(2)), d => d.Kind == Kind.GraceStarted);
        Assert.Empty(m.Ingest(Menu(3.5)));                    // still inside window
        var d = Assert.Single(m.Ingest(Menu(4.1)));           // past 2 + 2.0
        Assert.Equal(Kind.Finalize, d.Kind);
        Assert.Equal("quit", d.Outcome);
        Assert.Equal("state_transition:play->songselect", d.Evidence);
    }

    [Fact]
    public void PersistedQuit_WithZeroHealthAtBoundary_IsFailed()
    {
        var m = InPlay("mapA");
        m.Ingest(Play(1, live: 5000, hp: 0));  // last gameplay packet: dead
        m.Ingest(Menu(2));
        var d = Assert.Single(m.Ingest(Menu(4.1)));
        Assert.Equal("failed", d.Outcome);     // hp from pending snapshot, not current packet
    }

    [Fact]
    public void ResultsScreen_CompletesOrFails()
    {
        var m = InPlay("mapA");
        var done = Assert.Single(m.Ingest(Results(1, "A")));
        Assert.Equal(Kind.Finalize, done.Kind);
        Assert.Equal("completed", done.Outcome);
        Assert.Equal("results_screen", done.Evidence);

        var m2 = InPlay("mapA");
        Assert.Equal("failed", Assert.Single(m2.Ingest(Results(1, "F"))).Outcome);
    }

    [Fact]
    public void StaleResultsForOtherMap_Ignored()
    {
        var m = InPlay("mapB");
        var d = Assert.Single(m.Ingest(Results(1, "S", id: "mapA")));
        Assert.Equal(Kind.StaleResultsIgnored, d.Kind);
        Assert.True(m.HasAttempt); // next attempt untouched
    }

    [Fact]
    public void ResultsWithUnknownIdentity_StillFinalizes()
    {
        var m = InPlay("mapA");
        var d = Assert.Single(m.Ingest(Results(1, "S", id: "unknown")));
        Assert.Equal(Kind.Finalize, d.Kind);
    }

    [Fact]
    public void NoAttempt_MenuAndResults_DoNothing()
    {
        var m = new AttemptStateMachine();
        Assert.Empty(m.Ingest(Menu(0)));
        Assert.Empty(m.Ingest(Results(1)));
    }
}
