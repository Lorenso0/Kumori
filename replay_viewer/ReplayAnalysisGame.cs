using System.Diagnostics;
using osu.Framework.Allocation;
using osu.Framework.Audio;
using osu.Framework.Graphics;
using osu.Framework.Platform;
using osu.Framework.Screens;
using osu.Game;
using osu.Game.Beatmaps;
using osu.Game.Rulesets.Judgements;
using osu.Game.Rulesets.Difficulty;
using osu.Game.Rulesets.Objects;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets.Osu.Difficulty;
using osu.Game.Rulesets.Osu.Objects;
using osu.Game.Rulesets.Osu.Replays;
using osu.Game.Rulesets.Scoring;
using osu.Game.Scoring;
using osu.Game.Screens;
using osu.Game.Screens.Play;
using osu.Game.Utils;
using osuTK;

namespace Kumori.ReplayViewer;

internal partial class ReplayAnalysisGame : OsuGameBase
{
    private readonly ViewerContract contract;
    private readonly string contractPath;
    private GameHost gameHost = null!;
    private Score sourceScore = null!;
    private readonly Stopwatch watchdog = Stopwatch.StartNew();
    private bool finishing;

    public Exception? Failure { get; private set; }
    public bool Succeeded { get; private set; }

    public ReplayAnalysisGame(ViewerContract contract, string contractPath)
    {
        this.contract = contract;
        this.contractPath = contractPath;
    }

    protected override void LoadComplete()
    {
        try
        {
            base.LoadComplete();
            var audio = Dependencies.Get<AudioManager>();
            // Preparation runs the real ruleset to obtain exact judgements,
            // but it is not a user-facing playback session.
            audio.Volume.Value = 0;
            audio.VolumeTrack.Value = 0;
            audio.VolumeSample.Value = 0;
            gameHost = Dependencies.Get<GameHost>();
            var ruleset = new OsuRuleset();
            var workingBeatmap = new KumoriWorkingBeatmap(contract.BeatmapPath, contract.MediaDirectory, contract.MediaPaths, audio, gameHost);
            // Headless hosts do not run the normal music-controller track
            // preparation path used by the desktop game.
            workingBeatmap.LoadTrack();
            Beatmap.Value = workingBeatmap;
            Ruleset.Value = ruleset.RulesetInfo;

            Score score = ReplayScoreFactory.Create(contract, ruleset, workingBeatmap, disableHidden: false);
            sourceScore = score;
            SelectedMods.Value = score.ScoreInfo.Mods;
            var player = new ReplaySimulationPlayer(score, ruleset, workingBeatmap, complete, fail)
            {
                RelativeSizeAxes = Axes.Both,
            };
            var stack = new OsuScreenStack { RelativeSizeAxes = Axes.Both };
            Add(stack);
            stack.Push(player);
        }
        catch (Exception ex)
        {
            fail(ex);
        }
    }

    private void complete(
        IReadOnlyList<PreparedReplayJudgement> judgements,
        PreparedReplaySimulationSummary summary)
    {
        if (finishing)
            return;
        finishing = true;
        try
        {
            new PreparedReplayAnalysis(
                PreparedReplayAnalysis.CurrentVersion,
                contract.Attempt.Id,
                judgements,
                prepareFrames(),
                summary).Save(contractPath);
            Succeeded = true;
            NativeViewerLog.Write($"Prepared exact replay analysis with {judgements.Count} bad judgements.");
        }
        catch (Exception ex)
        {
            Failure = ex;
        }
        finally
        {
            gameHost.Exit();
        }
    }

    private IReadOnlyList<PreparedReplayFrame> prepareFrames()
    {
        List<PreparedReplayFrame> frames = [];
        bool previousAction = false;

        foreach (OsuReplayFrame frame in sourceScore.Replay.Frames.OfType<OsuReplayFrame>().OrderBy(frame => frame.Time))
        {
            bool hasAction = frame.Actions.Count > 0;
            var prepared = new PreparedReplayFrame(
                frame.Time,
                frame.Position.X,
                frame.Position.Y,
                hasAction,
                hasAction && !previousAction,
                !hasAction && previousAction);

            // Input transitions can share a timestamp. Keep the final state at
            // that instant so a zero-duration transition cannot create a
            // false stroke in the heatmap.
            if (frames.Count > 0 && Math.Abs(frames[^1].Time - prepared.Time) < 0.001)
            {
                PreparedReplayFrame prior = frames[^1];
                frames[^1] = prepared with
                {
                    Pressed = prior.Pressed || prepared.Pressed,
                    Released = prior.Released || prepared.Released,
                };
            }
            else
                frames.Add(prepared);

            previousAction = hasAction;
        }

        return frames;
    }

    private void fail(Exception ex)
    {
        if (finishing)
            return;
        finishing = true;
        Failure = ex;
        NativeViewerLog.Error(ex, "Replay analysis preparation failed");
        if (gameHost != null)
            gameHost.Exit();
    }

    protected override void Update()
    {
        base.Update();
        if (!finishing && watchdog.Elapsed > TimeSpan.FromSeconds(30))
            fail(new TimeoutException("Replay analysis host did not complete within 30 seconds."));
    }
}

internal partial class ReplaySimulationPlayer : ReplayPlayer
{
    private readonly Score sourceScore;
    private readonly OsuRuleset ruleset;
    private readonly WorkingBeatmap workingBeatmap;
    private readonly Action<IReadOnlyList<PreparedReplayJudgement>, PreparedReplaySimulationSummary> completed;
    private readonly Action<Exception> failed;
    private readonly List<PreparedReplayJudgement> badJudgements = [];
    private readonly HashSet<(double Root, double Object, KumoriTimelineMarkerKind Kind)> seen = [];
    private readonly Dictionary<HitResult, int> resultCounts = [];
    private readonly List<double> timingOffsets = [];
    private int sliderTailMisses;
    private readonly Stopwatch stopwatch = Stopwatch.StartNew();
    private bool finished;

    public ReplaySimulationPlayer(
        Score score,
        OsuRuleset ruleset,
        WorkingBeatmap workingBeatmap,
        Action<IReadOnlyList<PreparedReplayJudgement>, PreparedReplaySimulationSummary> completed,
        Action<Exception> failed)
        : base(score, new PlayerConfiguration
        {
            ShowResults = false,
            AllowRestart = false,
            AllowUserInteraction = false,
            AllowSkipping = false,
            ShowLeaderboard = false,
        })
    {
        sourceScore = score;
        this.ruleset = ruleset;
        this.workingBeatmap = workingBeatmap;
        this.completed = completed;
        this.failed = failed;
    }

    protected override IReadOnlyDependencyContainer CreateChildDependencies(IReadOnlyDependencyContainer parent)
    {
        Mods.Value = sourceScore.ScoreInfo.Mods;
        Ruleset.Value = sourceScore.ScoreInfo.Ruleset;
        return base.CreateChildDependencies(parent);
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();
        if (!LoadedBeatmapSuccessfully)
        {
            finishWithError(new InvalidOperationException("The beatmap could not be loaded for replay analysis."));
            return;
        }

        ScoreProcessor.NewJudgement += collect;
        if (GameplayClockContainer is MasterGameplayClockContainer master)
        {
            master.UserPlaybackRate.MaxValue = 100;
            master.UserPlaybackRate.Value = 100;
        }
        GameplayClockContainer.Start();
    }

    protected override void Update()
    {
        base.Update();
        if (finished)
            return;

        if (ScoreProcessor.HasCompleted.Value)
        {
            finished = true;
            ScoreProcessor.NewJudgement -= collect;
            completed(badJudgements, createSummary());
        }
        else if (stopwatch.Elapsed > TimeSpan.FromSeconds(30))
        {
            finishWithError(new TimeoutException("Replay analysis exceeded 30 seconds."));
        }
    }

    private void collect(JudgementResult result)
    {
        resultCounts[result.Type] = resultCounts.GetValueOrDefault(result.Type) + 1;
        if (result.Type == HitResult.Miss && result.HitObject is SliderTailCircle)
            sliderTailMisses++;
        if (result.Type is HitResult.Great or HitResult.Ok or HitResult.Meh
            && result.HitObject is HitCircle or SliderHeadCircle)
            timingOffsets.Add(result.TimeOffset);

        if (KumoriTimelineMarkers.KindFromJudgement(result) is not { } kind)
            return;

        HitObject root = DrawableRuleset.Objects.FirstOrDefault(candidate =>
            ReferenceEquals(candidate, result.HitObject) || candidate.NestedHitObjects.Contains(result.HitObject))
            ?? result.HitObject;
        if (!seen.Add((root.StartTime, result.HitObject.StartTime, kind)))
            return;

        Vector2 position = result.HitObject is OsuHitObject osu
            ? osu.StackedPosition
            : new Vector2(256, 192);
        double radius = result.HitObject is OsuHitObject target ? target.Radius : OsuHitObject.OBJECT_RADIUS;
        (double frameStart, double frameEnd) = frameBounds(result.HitObject, root, result.TimeAbsolute);
        badJudgements.Add(new PreparedReplayJudgement(
            kind,
            result.TimeAbsolute,
            root.StartTime,
            root.GetEndTime(),
            result.HitObject.StartTime,
            objectType(result.HitObject, root),
            position.X,
            position.Y,
            radius,
            result.TimeOffset,
            result.ComboAtJudgement,
            result.ComboAfterJudgement,
            frameStart,
            frameEnd));
    }

    private PreparedReplaySimulationSummary createSummary()
    {
        double unstableRate = 0;
        if (timingOffsets.Count > 0)
        {
            double mean = timingOffsets.Average();
            unstableRate = Math.Sqrt(timingOffsets.Average(value => Math.Pow(value - mean, 2))) * 10;
        }

        var score = sourceScore.ScoreInfo.DeepClone();
        score.BeatmapInfo = workingBeatmap.BeatmapInfo;
        ScoreProcessor.PopulateScore(score);

        DifficultyAttributes baseDifficulty = ruleset.CreateDifficultyCalculator(workingBeatmap).Calculate();
        DifficultyAttributes adjustedDifficulty = ruleset.CreateDifficultyCalculator(workingBeatmap).Calculate(score.Mods);
        var adjustedOsuDifficulty = (OsuDifficultyAttributes)adjustedDifficulty;
        var performance = ruleset.CreatePerformanceCalculator();
        double pp = performance.Calculate(score, adjustedDifficulty).Total;
        double fcPp = performance.Calculate(createFullComboProjection(score, adjustedDifficulty.MaxCombo), adjustedDifficulty).Total;
        double maxPp = performance.Calculate(createPerfectProjection(score, adjustedDifficulty.MaxCombo), adjustedDifficulty).Total;

        var baseMapDifficulty = workingBeatmap.BeatmapInfo.Difficulty;
        var displayDifficulty = ruleset.GetAdjustedDisplayDifficulty(workingBeatmap.BeatmapInfo, score.Mods);
        double clockRate = ModUtils.CalculateRateWithMods(score.Mods);
        double beatLength = workingBeatmap.Beatmap.GetMostCommonBeatLength();
        double bpm = beatLength > 0 ? 60000 / beatLength : 0;

        return new PreparedReplaySimulationSummary(
            resultCounts.GetValueOrDefault(HitResult.ComboBreak),
            resultCounts.GetValueOrDefault(HitResult.LargeTickHit),
            resultCounts.GetValueOrDefault(HitResult.LargeTickMiss),
            resultCounts.GetValueOrDefault(HitResult.SmallTickHit),
            resultCounts.GetValueOrDefault(HitResult.SmallTickMiss),
            resultCounts.GetValueOrDefault(HitResult.SliderTailHit),
            sliderTailMisses,
            unstableRate,
            timingOffsets,
            pp,
            fcPp,
            maxPp,
            baseDifficulty.StarRating,
            adjustedDifficulty.StarRating,
            baseMapDifficulty.ApproachRate,
            displayDifficulty.ApproachRate,
            baseMapDifficulty.CircleSize,
            displayDifficulty.CircleSize,
            baseMapDifficulty.OverallDifficulty,
            displayDifficulty.OverallDifficulty,
            baseMapDifficulty.DrainRate,
            displayDifficulty.DrainRate,
            bpm,
            bpm * clockRate,
            clockRate,
            adjustedDifficulty.MaxCombo,
            adjustedOsuDifficulty.HitCircleCount,
            adjustedOsuDifficulty.SliderCount,
            adjustedOsuDifficulty.SpinnerCount);
    }

    private static ScoreInfo createFullComboProjection(ScoreInfo actual, int maxCombo)
    {
        ScoreInfo projected = actual.DeepClone();
        int misses = projected.Statistics.GetValueOrDefault(HitResult.Miss);
        projected.Statistics[HitResult.Great] = projected.Statistics.GetValueOrDefault(HitResult.Great) + misses;
        projected.Statistics[HitResult.Miss] = 0;
        int missedComboTicks = projected.Statistics.GetValueOrDefault(HitResult.LargeTickMiss);
        projected.Statistics[HitResult.LargeTickHit] =
            projected.Statistics.GetValueOrDefault(HitResult.LargeTickHit) + missedComboTicks;
        projected.Statistics[HitResult.LargeTickMiss] = 0;
        projected.MaxCombo = maxCombo;
        projected.Accuracy = accuracyFrom(projected.Statistics);
        // A legacy score value describes the original miss/combo state and
        // must not influence the hypothetical full-combo projection.
        projected.IsLegacyScore = false;
        projected.LegacyTotalScore = null;
        return projected;
    }

    private static ScoreInfo createPerfectProjection(ScoreInfo actual, int maxCombo)
    {
        ScoreInfo projected = actual.DeepClone();
        projected.Statistics = new Dictionary<HitResult, int>(projected.MaximumStatistics);
        projected.MaxCombo = maxCombo;
        projected.Accuracy = 1;
        projected.IsLegacyScore = false;
        projected.LegacyTotalScore = null;
        return projected;
    }

    private static double accuracyFrom(IReadOnlyDictionary<HitResult, int> statistics)
    {
        int great = statistics.GetValueOrDefault(HitResult.Great);
        int ok = statistics.GetValueOrDefault(HitResult.Ok);
        int meh = statistics.GetValueOrDefault(HitResult.Meh);
        int miss = statistics.GetValueOrDefault(HitResult.Miss);
        int total = great + ok + meh + miss;
        return total == 0 ? 1 : (great * 300d + ok * 100d + meh * 50d) / (total * 300d);
    }

    private (double Start, double End) frameBounds(HitObject judged, HitObject root, double resultTime)
    {
        HitObject[] roots = DrawableRuleset.Objects.OrderBy(candidate => candidate.StartTime).ToArray();
        HitObject[] siblings = root.NestedHitObjects.OrderBy(candidate => candidate.StartTime).ToArray();
        HitObject[] sequence = siblings.Contains(judged) ? siblings : roots;
        int index = Array.IndexOf(sequence, sequence == siblings ? judged : root);
        double targetTime = judged.StartTime;
        double start = targetTime - 500;
        double end = Math.Max(Math.Max(targetTime, judged.GetEndTime()) + 80, resultTime + 20);

        if (index > 0)
        {
            double previousEnd = sequence[index - 1].GetEndTime();
            start = previousEnd < targetTime ? Math.Max(start, previousEnd) : targetTime - 180;
        }

        if (index >= 0 && index + 1 < sequence.Length)
            end = Math.Min(end, sequence[index + 1].StartTime - 0.001);

        // A miss resolves after its object time. Preserve that final cursor
        // position unless ownership has already passed to the next object.
        return (start, Math.Max(targetTime, end));
    }

    private void finishWithError(Exception ex)
    {
        if (finished)
            return;
        finished = true;
        ScoreProcessor.NewJudgement -= collect;
        failed(ex);
    }

    private static string objectType(HitObject judged, HitObject root) => judged switch
    {
        SliderHeadCircle => "Slider head",
        SliderTick => "Slider tick",
        SliderRepeat => "Slider repeat",
        SliderTailCircle => "Slider tail",
        HitCircle => "Circle",
        Spinner => "Spinner",
        _ when root is Slider => "Slider",
        _ => judged.GetType().Name,
    };
}
