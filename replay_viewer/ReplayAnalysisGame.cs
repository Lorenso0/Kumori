using System.Diagnostics;
using osu.Framework.Allocation;
using osu.Framework.Audio;
using osu.Framework.Graphics;
using osu.Framework.Platform;
using osu.Framework.Screens;
using osu.Framework.Testing;
using osu.Game;
using osu.Game.Beatmaps;
using osu.Game.Input.Handlers;
using osu.Game.Replays;
using osu.Game.Rulesets.Judgements;
using osu.Game.Rulesets.Difficulty;
using osu.Game.Rulesets.Objects;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets.Osu.Difficulty;
using osu.Game.Rulesets.Osu.Objects;
using osu.Game.Rulesets.Osu.Replays;
using osu.Game.Rulesets.Osu.UI;
using osu.Game.Rulesets.Scoring;
using osu.Game.Rulesets.UI;
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
            (double firstHitTime, double lastHitTime) = workingBeatmap.Beatmap.CalculatePlayableBounds();
            var player = new ReplaySimulationPlayer(
                score,
                ruleset,
                workingBeatmap,
                firstHitTime,
                lastHitTime,
                contract.ResolveAnalysisCoverageEnd(lastHitTime),
                complete,
                fail)
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

internal sealed class TransitionAccurateOsuReplayInputHandler
    : OsuFramedReplayInputHandler
{
    private readonly HashSet<double> transitionFrames = [];

    public TransitionAccurateOsuReplayInputHandler(Replay replay)
        : base(replay)
    {
        OsuReplayFrame[] frames = replay.Frames.OfType<OsuReplayFrame>().ToArray();
        for (int index = 1; index < frames.Length; index++)
        {
            if (!frames[index].Actions.Except(frames[index - 1].Actions).Any())
                continue;
            // Hold each newly pressed state through one nested drawable update.
            // Releases continue through the standard 60fps frame-stable path.
            transitionFrames.Add(frames[index].Time);
        }
    }

    protected override double AllowedImportantTimeSpan => double.MaxValue;

    // Cursor-only frames interpolate normally. Only button state changes must
    // force a nested drawable update; this preserves short taps at 100x
    // without serialising every held movement sample through the host.
    protected override bool IsImportant(OsuReplayFrame frame)
        => transitionFrames.Contains(frame.Time);
}

internal partial class ReplaySimulationPlayer : ReplayPlayer
{
    private const double simulation_playback_rate = 100;
    private readonly Score sourceScore;
    private readonly OsuRuleset ruleset;
    private readonly WorkingBeatmap workingBeatmap;
    private readonly double firstHitTime;
    private readonly double lastHitTime;
    private readonly double? coverageEnd;
    private readonly Action<IReadOnlyList<PreparedReplayJudgement>, PreparedReplaySimulationSummary> completed;
    private readonly Action<Exception> failed;
    private readonly List<PreparedReplayJudgement> badJudgements = [];
    private readonly HashSet<(double Root, double Object, KumoriTimelineMarkerKind Kind)> seen = [];
    private readonly HashSet<(double Root, double Object, HitResult Result)> scored = [];
    private readonly List<JudgementResult> deterministicJudgements = [];
    private readonly Dictionary<HitResult, int> resultCounts = [];
    private readonly List<double> timingOffsets = [];
    private int sliderTailMisses;
    private int maxCombo;
    private readonly Stopwatch stopwatch = Stopwatch.StartNew();
    private bool coverageClockCapped;
    private bool finished;

    public ReplaySimulationPlayer(
        Score score,
        OsuRuleset ruleset,
        WorkingBeatmap workingBeatmap,
        double firstHitTime,
        double lastHitTime,
        double? coverageEnd,
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
        this.firstHitTime = firstHitTime;
        this.lastHitTime = lastHitTime;
        this.coverageEnd = coverageEnd;
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

        if (DrawableRuleset is not DrawableOsuRuleset drawable)
        {
            finishWithError(new InvalidOperationException(
                "The osu! drawable ruleset was unavailable for accelerated replay analysis."));
            return;
        }
        if (coverageEnd is not null)
        {
            // Complete replays are already handled exactly by lazer's native
            // frame-stable playback. Retained partial captures need explicit
            // press transitions because their artificial endpoint otherwise
            // lets the 100x source clock overtake short taps near the cutoff.
            var replayHandler = new TransitionAccurateOsuReplayInputHandler(sourceScore.Replay)
            {
                FrameAccuratePlayback = true,
                GamefieldToScreenSpace = drawable.Playfield.GamefieldToScreenSpace,
            };
            ((IHasReplayHandler)drawable.KeyBindingInputManager).ReplayInputHandler = replayHandler;
            drawable.ChildrenOfType<FrameStabilityContainer>().Single().ReplayInputHandler = replayHandler;
        }

        ScoreProcessor.NewJudgement += collect;
        if (GameplayClockContainer is MasterGameplayClockContainer master)
        {
            master.UserPlaybackRate.MaxValue = simulation_playback_rate;
            master.UserPlaybackRate.Value = simulation_playback_rate;
        }
        GameplayClockContainer.Start();
    }

    protected override void Update()
    {
        if (!coverageClockCapped
            && coverageEnd is { } coverageCutoff
            && GameplayClockContainer.CurrentTime >= coverageCutoff)
        {
            // The 100x source clock can get far ahead of the frame-stable
            // ruleset. Pin its target to the retained replay boundary so the
            // catch-up pass cannot score the uncaptured tail.
            GameplayClockContainer.Seek(coverageCutoff);
            GameplayClockContainer.Stop();
            coverageClockCapped = true;
        }

        if (!finished && coverageEnd is { } cutoff && LoadedBeatmapSuccessfully)
        {
            double remaining = cutoff - DrawableRuleset.FrameStableClock.CurrentTime;
            if (remaining <= 0)
            {
                finishSuccessfully();
                return;
            }

            // Keep full-map recovery fast, but approach a partial capture's
            // endpoint slowly enough that a render tick cannot score far past
            // the final frame backed by real input.
            if (GameplayClockContainer is MasterGameplayClockContainer master)
            {
                double rate = remaining switch
                {
                    <= 250 => 1,
                    _ => simulation_playback_rate,
                };
                master.UserPlaybackRate.Value = Math.Clamp(
                    rate,
                    master.UserPlaybackRate.MinValue,
                    master.UserPlaybackRate.MaxValue);
            }
        }

        base.Update();
        if (finished)
            return;

        if (coverageEnd is { } end && DrawableRuleset.FrameStableClock.CurrentTime >= end)
            finishSuccessfully();
        else if (ScoreProcessor.HasCompleted.Value)
            finishSuccessfully();
        else if (stopwatch.Elapsed > TimeSpan.FromSeconds(30))
        {
            finishWithError(new TimeoutException("Replay analysis exceeded 30 seconds."));
        }
    }

    private void collect(JudgementResult result)
    {
        if (coverageEnd is { } end && result.TimeAbsolute > end)
            return;

        HitObject root = DrawableRuleset.Objects.FirstOrDefault(candidate =>
            ReferenceEquals(candidate, result.HitObject) || candidate.NestedHitObjects.Contains(result.HitObject))
            ?? result.HitObject;
        // Accelerated replay playback can publish the same final judgement on
        // adjacent update frames. Inspector entries were already deduplicated;
        // numeric totals must use the identical object-level guarantee.
        if (!scored.Add((root.StartTime, result.HitObject.StartTime, result.Type)))
            return;
        deterministicJudgements.Add(result);

        bool isSliderTailMiss = result.Type == HitResult.Miss && result.HitObject is SliderTailCircle;
        // Slider-tail failures are nested judgements. Keep them in their own
        // statistic; counting them as root misses corrupts partial-play core
        // result totals (for example 4 actual misses became 11).
        if (!isSliderTailMiss)
            resultCounts[result.Type] = resultCounts.GetValueOrDefault(result.Type) + 1;
        maxCombo = Math.Max(maxCombo, result.ComboAfterJudgement);
        if (isSliderTailMiss)
            sliderTailMisses++;
        if (result.Type is HitResult.Great or HitResult.Ok or HitResult.Meh
            && result.HitObject is HitCircle or SliderHeadCircle)
            timingOffsets.Add(result.TimeOffset);

        if (KumoriTimelineMarkers.KindFromJudgement(result) is not { } kind)
            return;

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
        // A frame-stable processor can briefly apply and revert the same
        // judgement while catching up at 100x. Rebuild the score once from
        // the object-level results we already deduplicated so identical
        // retained input always produces the same normalised score.
        ScoreProcessor deterministicProcessor = ruleset.CreateScoreProcessor();
        deterministicProcessor.Mods.Value = score.Mods;
        IBeatmap playableBeatmap = ScoreProcessor.Beatmap.Value
                                  ?? throw new InvalidOperationException("The playable beatmap was unavailable while rebuilding replay score.");
        deterministicProcessor.ApplyBeatmap(playableBeatmap);
        foreach (JudgementResult result in deterministicJudgements
                     .OrderBy(result => result.TimeAbsolute)
                     .ThenBy(result => result.HitObject.StartTime)
                     .ThenBy(result => result.Type))
        {
            deterministicProcessor.ApplyResult(result);
        }
        deterministicProcessor.PopulateScore(score);
        if (coverageEnd is not null)
        {
            // PopulateScore includes everything the accelerated player happened
            // to process in its final render tick. For an unfinished play only
            // judgements at or before the capture boundary are authoritative.
            score.Statistics = new Dictionary<HitResult, int>(resultCounts);
            score.MaxCombo = maxCombo;
            score.Accuracy = accuracyFrom(score.Statistics);
        }

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
        double replayStart = sourceScore.Replay.Frames.Count == 0
            ? 0
            : Math.Min(0, sourceScore.Replay.Frames.Min(frame => frame.Time));
        double replayEnd = coverageEnd
                           ?? (sourceScore.Replay.Frames.Count == 0
                               ? lastHitTime
                               : sourceScore.Replay.Frames.Max(frame => frame.Time));
        double durationSeconds = Math.Max(0, replayEnd - replayStart) / 1000;
        double progress = coverageEnd is null
            ? 1
            : Math.Clamp((replayEnd - firstHitTime) / Math.Max(1, lastHitTime - firstHitTime), 0, 1);

        return new PreparedReplaySimulationSummary(
            score.Statistics.GetValueOrDefault(HitResult.Great),
            score.Statistics.GetValueOrDefault(HitResult.Ok),
            score.Statistics.GetValueOrDefault(HitResult.Meh),
            score.Statistics.GetValueOrDefault(HitResult.Miss),
            score.Accuracy * 100,
            score.TotalScore,
            score.MaxCombo,
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
            adjustedOsuDifficulty.SpinnerCount,
            durationSeconds,
            progress);
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

    private void finishSuccessfully()
    {
        if (finished)
            return;
        finished = true;
        ScoreProcessor.NewJudgement -= collect;
        completed(badJudgements, createSummary());
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
