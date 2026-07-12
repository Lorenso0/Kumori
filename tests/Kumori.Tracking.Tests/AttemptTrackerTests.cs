using Kumori.Tracking;
using Xunit;
using static Kumori.Tracking.AttemptStateMachine;

namespace Kumori.Tracking.Tests;

public class AttemptTrackerTests
{
    private readonly RecordingAttemptSink _sink = new();
    private readonly AttemptTracker _tracker;

    public AttemptTrackerTests()
    {
        _tracker = new AttemptTracker(_sink);
    }

    [Fact]
    public void CompletedAttempt_StartsCheckpointsAndFinalizes()
    {
        _tracker.Ingest(Play(0, live: 0));
        _tracker.Ingest(Play(1.2, live: 1200, score: 10_000, n300: 50, progress: 0.5));
        _tracker.Ingest(Play(3.5, live: 3500, score: 50_000, n300: 300, progress: 1));
        _tracker.Ingest(Results(3.8, grade: "A", score: 50_000, n300: 300, progress: 1));

        Assert.Single(_sink.Starts);
        var final = Assert.Single(_sink.Finals);
        Assert.Equal("completed", final.Outcome);
        Assert.Equal("results_screen", final.Evidence);
        Assert.Equal(1, final.Snapshot.Progress);
        Assert.True(final.Snapshot.DurationSeconds >= 3.5);
        Assert.Contains(_sink.Checkpoints, c => c.Events.Any(e => e.EventType == "checkpoint"));
    }

    [Fact]
    public void WatchedReplay_IsNeverStarted()
    {
        _tracker.Ingest(Play(0, live: 0) with { IsWatchedReplay = true });

        Assert.Empty(_sink.Starts);
        Assert.Empty(_sink.Checkpoints);
        Assert.Empty(_sink.Finals);
    }

    [Fact]
    public void WatchedReplayAfterAttemptStart_DiscardsTheAttempt()
    {
        _tracker.Ingest(Play(0, live: 0));
        _tracker.Ingest(Play(1, live: 1000, score: 10_000, n300: 20) with { IsWatchedReplay = true });

        var discard = Assert.Single(_sink.Discards);
        Assert.Equal("watched_replay", discard.Reason);
        Assert.Empty(_sink.Finals);
    }

    [Fact]
    public void AutoMod_IsNeverStarted()
    {
        _tracker.Ingest(Play(0, live: 0) with { HasAutoMod = true });

        Assert.Empty(_sink.Starts);
        Assert.Empty(_sink.Checkpoints);
        Assert.Empty(_sink.Finals);
    }

    [Fact]
    public void BlankResultsGrade_PreservesPreviousGrade()
    {
        var play = Play(3.5, live: 3500, score: 50_000, n300: 300, progress: 1) with
        {
            Grade = "A",
            Packet = Play(3.5, live: 3500, score: 50_000, n300: 300, progress: 1).Packet with { Grade = "A" },
        };

        _tracker.Ingest(Play(0, live: 0));
        _tracker.Ingest(play);
        _tracker.Ingest(Results(3.8, grade: "", score: 50_000, n300: 300, progress: 1));

        Assert.Equal("A", Assert.Single(_sink.Finals).Snapshot.Grade);
    }

    [Fact]
    public void ResultsWithoutPerformanceValues_PreserveLatestGameplayPerformance()
    {
        _tracker.Ingest(Play(0, live: 0));
        _tracker.Ingest(Play(3.5, live: 3500, score: 50_000, n300: 300, progress: 1) with
        {
            Pp = 321.45,
            FcPp = 400.5,
            MaxPp = 350.25,
        });
        _tracker.Ingest(Results(3.8, score: 50_000, n300: 300, progress: 1));

        var snapshot = Assert.Single(_sink.Finals).Snapshot;
        Assert.Equal(321.45, snapshot.Pp);
        Assert.Equal(400.5, snapshot.FcPp);
        Assert.Equal(350.25, snapshot.MaxPp);
    }

    [Fact]
    public void StableResultModReset_DoesNotDiscardGameplayMods()
    {
        var classic = new AttemptMod("CL");
        var doubleTime = new AttemptMod("DT");
        _tracker.Ingest(Play(0, live: 0) with
        {
            ClientKind = OsuClientKind.Stable,
            ModsKey = "DTCL",
            Mods = [doubleTime, classic],
        });
        _tracker.Ingest(Play(3.5, live: 3500, score: 50_000, n300: 100, progress: 1) with
        {
            ClientKind = OsuClientKind.Stable,
            ModsKey = "DTCL",
            Mods = [doubleTime, classic],
        });
        _tracker.Ingest(Results(3.8, score: 50_000, n300: 100, progress: 1) with
        {
            ClientKind = OsuClientKind.Stable,
            ModsKey = "CL",
            Mods = [classic],
        });

        var final = Assert.Single(_sink.Finals).Snapshot;
        Assert.Equal("DTCL", final.ModsKey);
        Assert.Equal(["DT", "CL"], final.Mods.Select(mod => mod.Acronym));
    }

    [Fact]
    public void ResultsScreenRichHitCounts_AreFinalized()
    {
        _tracker.Ingest(Play(0, live: 0));
        _tracker.Ingest(Play(3.5, live: 3500, score: 50_000, n300: 300, progress: 1));
        _tracker.Ingest(Results(3.8, score: 50_000, n300: 300, progress: 1) with
        {
            Play = Results(3.8, score: 50_000, n300: 300, progress: 1).Play with
            {
                Geki = 2,
                Katu = 3,
                LargeTickHit = 74,
                LargeTickMiss = 5,
                SmallTickHit = 6,
                SmallTickMiss = 7,
                SliderTailHit = 84,
                SliderTailMiss = 8,
            },
        });

        var snapshot = Assert.Single(_sink.Finals).Snapshot;
        Assert.Equal(2, snapshot.Geki);
        Assert.Equal(3, snapshot.Katu);
        Assert.Equal(74, snapshot.LargeTickHits);
        Assert.Equal(5, snapshot.LargeTickMisses);
        Assert.Equal(6, snapshot.SmallTickHits);
        Assert.Equal(7, snapshot.SmallTickMisses);
        Assert.Equal(84, snapshot.SliderTailHits);
        Assert.Equal(8, snapshot.SliderTailMisses);
    }

    [Fact]
    public void FailedResults_UsesGradeF()
    {
        _tracker.Ingest(Play(0));
        _tracker.Ingest(Play(3.2, live: 3200, score: 5000, miss: 10, progress: 0.4));
        _tracker.Ingest(Results(3.3, grade: "F", score: 5000, miss: 10, progress: 0.4));

        Assert.Equal("failed", Assert.Single(_sink.Finals).Outcome);
    }

    [Fact]
    public void BlankCompletedGrade_IsCalculatedFromJudgementCounts()
    {
        _tracker.Ingest(Play(1.0, live: 1_000, score: 10_000, n300: 100, n100: 5));
        _tracker.Ingest(Results(4.0, grade: "", score: 50_000, n300: 100, n100: 5, progress: 1));

        Assert.Equal("S", Assert.Single(_sink.Finals).Snapshot.Grade);
    }

    [Fact]
    public void BlankFailedGrade_IsCalculatedAsF()
    {
        _tracker.Ingest(Play(1.0, live: 1_000, score: 10_000, n300: 10));
        _tracker.Ingest(Results(4.0, grade: "", score: 10_000, n300: 10, miss: 1, progress: 1) with
        {
            Packet = Results(4.0, grade: "", score: 10_000, n300: 10, miss: 1, progress: 1).Packet with { Grade = "F" },
            Grade = "",
        });

        Assert.Equal("F", Assert.Single(_sink.Finals).Snapshot.Grade);
    }

    [Fact]
    public void RetryChain_DiscardsEmptyPulseThenFinalizesRealRetry()
    {
        _tracker.Ingest(Play(0, live: 0));
        _tracker.Ingest(Menu(0.2));
        _tracker.Ingest(Play(0.4, live: 0));

        Assert.Single(_sink.Discards);
        Assert.Empty(_sink.Finals);
        Assert.Equal(2, _sink.Starts.Count);

        _tracker.Ingest(Play(1.0, live: 1000, score: 1000, n300: 10, progress: 0.2));
        _tracker.Ingest(Play(4.2, live: 4200, score: 12_000, n300: 80, progress: 0.7));
        _tracker.Ingest(Menu(4.3));
        _tracker.Ingest(Play(4.6, live: 0, score: 0, n300: 0));

        var final = Assert.Single(_sink.Finals);
        Assert.Equal("retried", final.Outcome);
        Assert.Equal("fresh_gameplay_same_map", final.Evidence);
        Assert.Equal(3, _sink.Starts.Count);
    }

    [Fact]
    public void QuitAfterGrace_FinalizesWithPendingGameplaySnapshot()
    {
        _tracker.Ingest(Play(0));
        _tracker.Ingest(Play(4.0, live: 4000, score: 20_000, n300: 100, hp: 0.8, progress: 0.6));
        _tracker.Ingest(Menu(4.1));
        _tracker.Ingest(Menu(6.2, state: "songselect"));

        var final = Assert.Single(_sink.Finals);
        Assert.Equal("quit", final.Outcome);
        Assert.Equal("state_transition:play->songselect", final.Evidence);
        Assert.Equal(20_000, final.Snapshot.Score);
        Assert.Equal(0.6, final.Snapshot.Progress);
    }

    [Fact]
    public void SustainedPlayWithoutScoreCounters_IsNotDiscarded()
    {
        _tracker.Ingest(Play(0));
        _tracker.Ingest(Play(14, live: 14_000));
        _tracker.Ingest(Menu(14.1));
        _tracker.Ingest(Menu(16.2));

        var final = Assert.Single(_sink.Finals);
        Assert.Equal("quit", final.Outcome);
        Assert.Empty(_sink.Discards);
        Assert.Equal(0, final.Snapshot.Score);
    }

    [Fact]
    public void ZeroHealthAtGraceStart_FinalizesAsFailed()
    {
        _tracker.Ingest(Play(0));
        _tracker.Ingest(Play(4.0, live: 4000, score: 20_000, miss: 5, hp: 0, progress: 0.4));
        _tracker.Ingest(Menu(4.1));
        _tracker.Ingest(Menu(6.2));

        Assert.Equal("failed", Assert.Single(_sink.Finals).Outcome);
    }

    [Fact]
    public void TransientMenu_ReturningToPlayUsesRetryBoundary()
    {
        _tracker.Ingest(Play(0));
        _tracker.Ingest(Play(4.0, live: 4000, score: 30_000, n300: 120, progress: 0.6));
        _tracker.Ingest(Menu(4.1));
        _tracker.Ingest(Play(4.5, live: 4100, score: 0, n300: 0));

        var final = Assert.Single(_sink.Finals);
        Assert.Equal("retried", final.Outcome);
        Assert.Equal("fresh_gameplay_same_map", final.Evidence);
    }

    [Fact]
    public void MapSwitch_FinalizesAbandonedAndStartsNext()
    {
        _tracker.Ingest(Play(0, id: "mapA"));
        _tracker.Ingest(Play(4.0, id: "mapA", live: 4000, score: 10_000, n300: 90, progress: 0.5));
        _tracker.Ingest(Play(4.1, id: "mapB", live: 0));

        var final = Assert.Single(_sink.Finals);
        Assert.Equal("abandoned", final.Outcome);
        Assert.Equal("beatmap_changed", final.Evidence);
        Assert.Equal("mapA", final.Snapshot.Identity);
        Assert.Equal(2, _sink.Starts.Count);
        Assert.Equal("mapB", _sink.Starts.Last().Identity);
    }

    [Fact]
    public void StaleResults_DoNothingAndDoNotOverwriteActiveAttempt()
    {
        _tracker.Ingest(Play(0, id: "mapB"));
        _tracker.Ingest(Play(4.0, id: "mapB", live: 4000, score: 10_000, n300: 50));
        _tracker.Ingest(Results(4.2, id: "mapA", score: 99_999, n300: 999));
        _tracker.Ingest(Results(4.4, id: "mapB", score: 10_000, n300: 50));

        var final = Assert.Single(_sink.Finals);
        Assert.Equal("mapB", final.Snapshot.Identity);
        Assert.Equal(10_000, final.Snapshot.Score);
    }

    [Fact]
    public void NearCompleteQuit_IsPromotedToCompleted()
    {
        _tracker.Ingest(Play(0));
        _tracker.Ingest(Play(4.0, live: 4000, score: 40_000, n300: 220, progress: 0.995));
        _tracker.Ingest(Menu(4.1));
        _tracker.Ingest(Menu(6.2));

        var final = Assert.Single(_sink.Finals);
        Assert.Equal("completed", final.Outcome);
        Assert.Equal("state_transition:play->songselect:complete_progress", final.Evidence);
        Assert.Equal(1, final.Snapshot.Progress);
    }

    [Fact]
    public void NonStandardPacketsAreIgnored()
    {
        _tracker.Ingest(Play(0) with { IsStandardMode = false });
        Assert.Empty(_sink.Starts);
    }

    [Fact]
    public void CriticalJudgementsAreCapturedBeforeOneSecondCheckpoint()
    {
        _tracker.Ingest(Play(0));
        _tracker.Ingest(Play(0.1, score: 1000, n300: 10));
        _tracker.Ingest(Play(0.2, score: 1000, n300: 10, miss: 2));

        var missEvents = _sink.Checkpoints
            .SelectMany(c => c.Events)
            .Where(e => e.EventType == "miss")
            .ToList();
        Assert.Equal(new double[] { 1, 2 }, missEvents.Select(e => e.Value));
    }

    [Fact]
    public void MidPlayAttachment_UsesObservedMapTimeAndKeepsTheResult()
    {
        _tracker.Ingest(Play(0, live: 90_000, score: 500_000, n300: 500, progress: 0.98));
        _tracker.Ingest(Results(0.2, score: 510_000, n300: 510, progress: 1));

        var final = Assert.Single(_sink.Finals);
        Assert.Equal("completed", final.Outcome);
        Assert.True(final.Snapshot.DurationSeconds >= 90);
    }

    [Fact]
    public void EndInterrupted_FinalizesAValidActiveAttempt()
    {
        _tracker.Ingest(Play(0));
        _tracker.Ingest(Play(4, live: 4000, score: 20_000, n300: 100, progress: 0.5));

        _tracker.EndInterrupted("app_closed");

        var final = Assert.Single(_sink.Finals);
        Assert.Equal("abandoned", final.Outcome);
        Assert.Equal("app_closed", final.Evidence);
    }

    private static AttemptTracker.Frame Play(
        double t,
        string id = "mapA",
        long live = 0,
        int score = 0,
        double n300 = 0,
        double n100 = 0,
        double n50 = 0,
        double miss = 0,
        double hp = 1,
        double progress = 0) => new()
        {
            WallTime = 1_000 + t,
            Packet = new PacketView
            {
                MonoTime = t,
                State = "play",
                IsPlaying = true,
                Identity = id,
                LiveTimeMs = live,
                Health = hp,
            },
            Score = score,
            Play = new JudgementCapture.PlayValues
            {
                Hit300 = n300,
                Hit100 = n100,
                Hit50 = n50,
                Miss = miss,
                Health = hp,
                Progress = progress,
                Accuracy = n300 + n100 + n50 + miss == 0
                ? 0
                : (300 * n300 + 100 * n100 + 50 * n50) / (300 * (n300 + n100 + n50 + miss)),
            },
        };

    private static AttemptTracker.Frame Menu(
        double t,
        string state = "songselect") => new()
        {
            WallTime = 1_000 + t,
            Packet = new PacketView
            {
                MonoTime = t,
                State = state,
                Identity = "mapA",
            },
        };

    private static AttemptTracker.Frame Results(
        double t,
        string id = "mapA",
        string grade = "A",
        int score = 0,
        double n300 = 0,
        double n100 = 0,
        double n50 = 0,
        double miss = 0,
        double progress = 1) => new()
        {
            WallTime = 1_000 + t,
            Packet = new PacketView
            {
                MonoTime = t,
                State = "resultscreen",
                IsResults = true,
                Identity = id,
                Grade = grade,
            },
            Score = score,
            Grade = grade,
            Play = new JudgementCapture.PlayValues
            {
                Hit300 = n300,
                Hit100 = n100,
                Hit50 = n50,
                Miss = miss,
                Progress = progress,
            },
        };

    private sealed class RecordingAttemptSink : IAttemptSink
    {
        public List<AttemptStart> Starts { get; } = new();
        public List<AttemptCheckpoint> Checkpoints { get; } = new();
        public List<AttemptDiscard> Discards { get; } = new();
        public List<AttemptFinalization> Finals { get; } = new();

        public void StartAttempt(AttemptStart start) => Starts.Add(start);
        public void Checkpoint(AttemptCheckpoint checkpoint) => Checkpoints.Add(checkpoint);
        public void DiscardIfEmpty(AttemptDiscard discard) => Discards.Add(discard);
        public void Finalize(AttemptFinalization finalization) => Finals.Add(finalization);
    }
}
