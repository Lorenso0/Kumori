using Kumori.ReplayViewer;
using osu.Game.Rulesets.Objects;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets.Osu.Objects;
using osu.Game.Rulesets.Osu.Replays;
using osuTK;
using Xunit;

namespace Kumori.Core.Tests;

public class MissAnalysisBuilderTests
{
    [Fact]
    public void Build_IncludesBadHitKindsInOrder()
    {
        var model = MissAnalysisBuilder.Build(Contract([
            Event(3000, "hit_100"),
            Event(1000, "miss"),
            Event(2000, "slider_break"),
            Event(4000, "hit_50"),
            Event(5000, "300"),
        ]), Objects(), Frames(Frame(1000, 256, 192)));

        Assert.Equal(["Miss", "Slider break", "100", "50"], model.Entries.Select(e => e.Label));
    }

    [Fact]
    public void Build_MatchesNearestHitObjectByTime()
    {
        var model = MissAnalysisBuilder.Build(Contract([
            Event(2100, "miss"),
        ]), Objects(
            Circle(1000, 50, 50),
            Circle(2050, 300, 180),
            Circle(4000, 450, 300)), Frames(Frame(2100, 300, 180)));

        var entry = Assert.Single(model.Entries);
        Assert.Equal(new Vector2(300, 180), entry.TargetPosition);
        Assert.Equal("Circle", entry.ObjectType);
        Assert.Equal(new Vector2(50, 50), entry.PreviousPosition);
        Assert.Equal(new Vector2(450, 300), entry.NextPosition);
    }

    [Fact]
    public void Build_SlicesReplayFramesAroundEvent()
    {
        var model = MissAnalysisBuilder.Build(Contract([
            Event(1000, "miss"),
        ]), Objects(Circle(1000, 256, 192)), Frames(
            Frame(200, 0, 0),
            Frame(350, 10, 10),
            Frame(1000, 256, 192),
            Frame(1450, 300, 200),
            Frame(1600, 400, 300)));

        var entry = Assert.Single(model.Entries);
        Assert.Equal([200, 350, 1000, 1450, 1600], entry.ReplayFrames.Select(s => (int)s.Time));
        Assert.Equal(0, entry.DistanceFromTarget);
    }

    [Fact]
    public void Build_CalculatesEarlyAndLateTapOffsetsFromReplayPresses()
    {
        var model = MissAnalysisBuilder.Build(Contract([
            Event(1000, "100"),
            Event(2000, "50"),
            Event(3000, "slider_break"),
        ]), Objects(
            Circle(1000, 100, 100),
            Circle(2000, 200, 200),
            Circle(3000, 300, 300)), Frames(
            Frame(970, 100, 100, OsuAction.LeftButton),
            Frame(1010, 100, 100),
            Frame(2042, 200, 200, OsuAction.RightButton),
            Frame(2070, 200, 200),
            Frame(3015, 300, 300, OsuAction.LeftButton)));

        Assert.Equal(-30, model.Entries[0].TapOffsetMs);
        Assert.Equal(42, model.Entries[1].TapOffsetMs);
        Assert.Equal(15, model.Entries[2].TapOffsetMs);
    }

    [Fact]
    public void Build_NumbersJudgementsWhenObjectGeometryIsUnavailable()
    {
        var model = MissAnalysisBuilder.Build(Contract([
            Event(1000, "miss"),
            Event(1100, "miss"),
            Event(1200, "100"),
            Event(1300, "100"),
            Event(1400, "slider_break"),
        ]), Objects(), Frames(Frame(1000, 256, 192)));

        Assert.Equal(
            ["Miss 1", "Miss 2", "100 1", "100 2", "Slider break 1"],
            model.Entries.Select(entry => entry.ObjectType));
    }

    [Fact]
    public void BuildFromJudgements_UsesExactLazerTimingAndSource()
    {
        HitCircle circle = Circle(1000, 120, 80);
        var model = MissAnalysisBuilder.BuildFromJudgements(
            Objects(circle),
            Frames(Frame(965, 120, 80, OsuAction.LeftButton)),
            [new ReplayJudgementSnapshot(circle, KumoriTimelineMarkerKind.Ok, 965, -35, 12, 13)]);

        MissAnalysisEntry entry = Assert.Single(model.Entries);
        Assert.Equal(AnalysisDataSource.Lazer, entry.Source);
        Assert.Equal("Circle", entry.ObjectType);
        Assert.Equal(-35, entry.InputOffsetMs);
        Assert.True(entry.ExactTiming);
        Assert.Equal(12, entry.ComboBefore);
        Assert.Equal(13, entry.ComboAfter);
    }

    [Fact]
    public void BuildFromJudgements_NamesSliderComponentsAndDeduplicates()
    {
        var tick = new SliderTick { StartTime = 1500, Position = new Vector2(220, 160) };
        var snapshot = new ReplayJudgementSnapshot(tick, KumoriTimelineMarkerKind.SliderBreak, 1500, 0, 20, 0);

        var model = MissAnalysisBuilder.BuildFromJudgements(
            Objects(),
            Frames(Frame(1500, 220, 160, OsuAction.LeftButton)),
            [snapshot, snapshot]);

        MissAnalysisEntry entry = Assert.Single(model.Entries);
        Assert.Equal("Slider tick", entry.ObjectType);
        Assert.Equal(KumoriTimelineMarkerKind.SliderBreak, entry.Kind);
    }

    [Fact]
    public void BuildFromJudgements_ExcludesSyntheticMissesBeyondCaptureCoverage()
    {
        HitCircle captured = Circle(1000, 120, 80);
        HitCircle syntheticTail = Circle(5000, 300, 200);

        var model = MissAnalysisBuilder.BuildFromJudgements(
            Objects(captured, syntheticTail),
            Frames(Frame(1000, 120, 80)),
            [
                new ReplayJudgementSnapshot(captured, KumoriTimelineMarkerKind.Miss, 1100, 100, 1, 0),
                new ReplayJudgementSnapshot(syntheticTail, KumoriTimelineMarkerKind.Miss, 5100, 100, 1, 0),
            ],
            analysisCoverageEnd: 1200);

        Assert.Equal(1000, Assert.Single(model.Entries).TargetStartTime);
    }

    [Fact]
    public void Build_MarksContractEventsAsInferred()
    {
        var model = MissAnalysisBuilder.Build(
            Contract([Event(1000, "miss")]),
            Objects(Circle(1000, 256, 192)),
            Frames(Frame(1000, 256, 192)));

        Assert.Equal(AnalysisDataSource.Inferred, Assert.Single(model.Entries).Source);
    }

    [Fact]
    public void Build_UsesPlayableAnalysisForFallbackObjectGeometry()
    {
        var analysis = new BeatmapAnalysis("", "", "", 9, 8, 4,
        [
            new HitObjectAnalysis("Slider", 12969, 13200, 14, 191, 45, 600, 0, 1, 1, []),
        ], new HitWindowAnalysis(20, 50, 100, 150));

        var model = MissAnalysisBuilder.Build(
            Contract([Event(13008, "miss")]),
            analysis,
            Frames(Frame(13008, 30, 191, OsuAction.LeftButton)));

        MissAnalysisEntry entry = Assert.Single(model.Entries);
        Assert.Equal("Slider", entry.ObjectType);
        Assert.Equal(new Vector2(14, 191), entry.TargetPosition);
        Assert.Equal(45, entry.TargetRadius);
        Assert.NotNull(entry.DistanceFromTarget);
    }

    [Fact]
    public void Build_MatchesLateMissToResolvedCircleInsteadOfFollowingSlider()
    {
        var analysis = new BeatmapAnalysis("", "", "", 9, 8, 4,
        [
            new HitObjectAnalysis("HitCircle", 12806, 12806, 159, 274, 31, 600, 0, 0, 0, []),
            new HitObjectAnalysis("Slider", 12969, 13132, 14, 193, 31, 600, 0, 1, 1, []),
        ], new HitWindowAnalysis(20, 50, 100, 150));

        var model = MissAnalysisBuilder.Build(
            Contract([Event(13008, "miss")]),
            analysis,
            Frames(Frame(12806, 120, 274), Frame(13008, 14, 193, OsuAction.LeftButton)));

        MissAnalysisEntry entry = Assert.Single(model.Entries);
        Assert.Equal("Circle", entry.ObjectType);
        Assert.Equal(12806, entry.EventTime);
        Assert.Equal(new Vector2(159, 274), entry.TargetPosition);
        Assert.Equal(12969, entry.NextTime);
    }

    [Fact]
    public void BuildFromPrepared_UsesExactLazerObjectIdentityAndPosition()
    {
        var analysis = new BeatmapAnalysis("", "", "", 9, 8, 4,
        [
            new HitObjectAnalysis("HitCircle", 12806, 12806, 159, 274, 31, 600, 0, 0, 0, []),
            new HitObjectAnalysis("Slider", 12969, 13132, 14, 193, 31, 600, 0, 1, 1, []),
        ], new HitWindowAnalysis(20, 50, 100, 150));
        var judgement = new PreparedReplayJudgement(
            KumoriTimelineMarkerKind.Miss,
            12906,
            12806,
            12806,
            12806,
            "Circle",
            159,
            274,
            31,
            100,
            60,
            0);

        var model = MissAnalysisBuilder.BuildFromPrepared(
            analysis,
            Frames(Frame(12806, 120, 274)),
            [judgement]);

        MissAnalysisEntry entry = Assert.Single(model.Entries);
        Assert.Equal(AnalysisDataSource.Lazer, entry.Source);
        Assert.Equal(12806, entry.EventTime);
        Assert.Equal("Circle", entry.ObjectType);
        Assert.Equal(new Vector2(159, 274), entry.TargetPosition);
        Assert.Equal(60, entry.ComboBefore);
    }

    [Fact]
    public void BuildFromPrepared_ExcludesJudgementsBeyondCaptureCoverage()
    {
        var analysis = new BeatmapAnalysis("", "", "", 9, 8, 4, [], new HitWindowAnalysis(20, 50, 100, 150));
        PreparedReplayJudgement judgement(double time) => new(
            KumoriTimelineMarkerKind.Miss,
            time + 100,
            time,
            time,
            time,
            "Circle",
            256,
            192,
            31,
            100,
            1,
            0);

        var model = MissAnalysisBuilder.BuildFromPrepared(
            analysis,
            Frames(Frame(1000, 256, 192)),
            [judgement(1000), judgement(5000)],
            analysisCoverageEnd: 1200);

        Assert.Equal(1000, Assert.Single(model.Entries).TargetStartTime);
    }

    [Fact]
    public void BuildFromPrepared_AnchorsContiguousPathToOwnedInput()
    {
        var analysis = new BeatmapAnalysis("", "", "", 9, 8, 4,
        [
            new HitObjectAnalysis("HitCircle", 1000, 1000, 200, 200, 31, 600, 0, 0, 0, []),
        ], new HitWindowAnalysis(20, 50, 100, 150));
        var judgement = new PreparedReplayJudgement(
            KumoriTimelineMarkerKind.Miss,
            1100,
            1000,
            1000,
            1000,
            "Circle",
            200,
            200,
            31,
            100,
            1,
            0,
            800,
            1120);
        PreparedReplayFrame[] preparedFrames =
        [
            new(900, 150, 200, false, false, false),
            new(920, 160, 200, true, true, false),
            // A capture/seek discontinuity inside the same time window.
            new(940, 450, 20, false, false, false),
            new(1080, 190, 200, false, false, false),
            new(1100, 198, 200, false, false, false),
        ];

        var model = MissAnalysisBuilder.BuildFromPrepared(
            analysis,
            Frames(Frame(1100, 198, 200, OsuAction.LeftButton)),
            [judgement],
            preparedFrames);

        MissAnalysisEntry entry = Assert.Single(model.Entries);
        Assert.Equal([900, 920], entry.ReplayFrames.Select(frame => (int)frame.Time));
        Assert.Equal(920, entry.InputFrame?.Time);
    }

    [Fact]
    public void BuildFromPrepared_HeatmapKeepsOnlyFinalLocalApproach()
    {
        var analysis = new BeatmapAnalysis("", "", "", 9, 8, 4,
        [
            new HitObjectAnalysis("HitCircle", 1000, 1000, 200, 200, 31, 600, 0, 0, 0, []),
        ], new HitWindowAnalysis(20, 50, 100, 150));
        var judgement = new PreparedReplayJudgement(
            KumoriTimelineMarkerKind.Miss, 1100, 1000, 1000, 1000,
            "Circle", 200, 200, 31, 100, 1, 0, 700, 1120);
        PreparedReplayFrame[] preparedFrames =
        [
            new(800, 20, 200, false, false, false),
            new(900, 120, 200, false, false, false),
            new(1020, 150, 200, false, false, false),
            new(1060, 175, 200, false, false, false),
            new(1100, 190, 200, true, true, false),
        ];

        var model = MissAnalysisBuilder.BuildFromPrepared(
            analysis,
            Frames(Frame(1100, 190, 200, OsuAction.LeftButton)),
            [judgement],
            preparedFrames);

        Assert.Equal([1020, 1060, 1100], Assert.Single(model.Entries).ReplayFrames.Select(frame => (int)frame.Time));
    }

    [Fact]
    public void Build_UsesNearestSliderComponentForSliderBreak()
    {
        var analysis = new BeatmapAnalysis("", "", "", 9, 8, 4,
        [
            new HitObjectAnalysis("Slider", 1000, 2000, 100, 100, 45, 600, 0, 1, 1,
            [
                new NestedObjectAnalysis("SliderTick", 1490, 300, 210),
                new NestedObjectAnalysis("SliderTailCircle", 2000, 420, 250),
            ]),
        ], new HitWindowAnalysis(20, 50, 100, 150));

        var model = MissAnalysisBuilder.Build(
            Contract([Event(1500, "slider_break")]),
            analysis,
            Frames(Frame(1500, 305, 210, OsuAction.LeftButton)));

        MissAnalysisEntry entry = Assert.Single(model.Entries);
        Assert.Equal("Slider tick", entry.ObjectType);
        Assert.Equal(new Vector2(300, 210), entry.TargetPosition);
        Assert.Equal(1490, entry.TargetStartTime);
    }

    private static ViewerContract Contract(
        IEnumerable<JudgementEventContract> events,
        IEnumerable<Kumori.ReplayViewer.MovementSample>? samples = null) => new()
        {
            ContractVersion = ViewerContract.CurrentVersion,
            Attempt = new AttemptContract(),
            BeatmapPath = "map.osu",
            Samples = samples?.ToList() ?? [Sample(1000, 256, 192)],
            JudgementEvents = events.ToList(),
        };

    private static JudgementEventContract Event(int time, string kind) => new()
    {
        MapTimeMs = time,
        Kind = kind,
        Delta = 1,
    };

    private static Kumori.ReplayViewer.MovementSample Sample(double time, double x, double y) => new()
    {
        MapTimeMs = time,
        X = x,
        Y = y,
    };

    private static IReadOnlyList<HitObject> Objects(params HitObject[] objects) => objects;

    private static IEnumerable<OsuReplayFrame> Frames(params OsuReplayFrame[] frames) => frames;

    private static OsuReplayFrame Frame(double time, float x, float y, params OsuAction[] actions)
        => new(time, new Vector2(x, y), actions);

    private static HitCircle Circle(double time, float x, float y) => new()
    {
        StartTime = time,
        Position = new Vector2(x, y),
    };
}
