using System.Diagnostics;
using osu.Framework.Allocation;
using osu.Framework.Audio;
using osu.Framework.Graphics;
using osu.Framework.Platform;
using osu.Framework.Screens;
using osu.Game;
using osu.Game.Beatmaps;
using osu.Game.Rulesets.Judgements;
using osu.Game.Rulesets.Objects;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets.Osu.Objects;
using osu.Game.Rulesets.Osu.Replays;
using osu.Game.Scoring;
using osu.Game.Screens;
using osu.Game.Screens.Play;
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
            var workingBeatmap = new KumoriWorkingBeatmap(contract.BeatmapPath, contract.MediaDirectory, audio, gameHost);
            // Headless hosts do not run the normal music-controller track
            // preparation path used by the desktop game.
            workingBeatmap.LoadTrack();
            Beatmap.Value = workingBeatmap;
            Ruleset.Value = ruleset.RulesetInfo;

            Score score = ReplayScoreFactory.Create(contract, ruleset, workingBeatmap, disableHidden: false);
            sourceScore = score;
            SelectedMods.Value = score.ScoreInfo.Mods;
            var player = new ReplaySimulationPlayer(score, complete, fail)
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

    private void complete(IReadOnlyList<PreparedReplayJudgement> judgements)
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
                prepareFrames()).Save(contractPath);
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
    private readonly Action<IReadOnlyList<PreparedReplayJudgement>> completed;
    private readonly Action<Exception> failed;
    private readonly List<PreparedReplayJudgement> badJudgements = [];
    private readonly HashSet<(double Root, double Object, KumoriTimelineMarkerKind Kind)> seen = [];
    private readonly Stopwatch stopwatch = Stopwatch.StartNew();
    private bool finished;

    public ReplaySimulationPlayer(
        Score score,
        Action<IReadOnlyList<PreparedReplayJudgement>> completed,
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
        this.completed = completed;
        this.failed = failed;
    }

    protected override IReadOnlyDependencyContainer CreateChildDependencies(IReadOnlyDependencyContainer parent)
    {
        var dependencies = base.CreateChildDependencies(parent);
        Mods.Value = sourceScore.ScoreInfo.Mods;
        Ruleset.Value = sourceScore.ScoreInfo.Ruleset;
        return dependencies;
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
            completed(badJudgements);
        }
        else if (stopwatch.Elapsed > TimeSpan.FromSeconds(30))
        {
            finishWithError(new TimeoutException("Replay analysis exceeded 30 seconds."));
        }
    }

    private void collect(JudgementResult result)
    {
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
