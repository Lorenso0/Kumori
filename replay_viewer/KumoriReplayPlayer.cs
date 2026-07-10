using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Logging;
using osu.Framework.Testing;
using osu.Game.Rulesets.Objects;
using osu.Game.Rulesets.Osu.UI;
using osu.Game.Rulesets.Osu.Objects;
using osu.Game.Rulesets.Scoring;
using osu.Game.Rulesets.Judgements;
using osu.Game.Scoring;
using osu.Game.Screens.Play;
using osu.Game.Screens.Play.HUD;
using osu.Game.Screens.Play.PlayerSettings;
using osu.Framework.Screens;

namespace Kumori.ReplayViewer;

internal partial class KumoriReplayPlayer : ReplayPlayer
{
    private readonly Score sourceScore;
    private KumoriSelectedClickMarker? selectedClickMarker;

    /// <summary>
    /// The seek bar to attach and feed judgement markers into. Assigned by
    /// ReplayViewerGame before this screen is pushed. It is attached at
    /// positive depth so it renders UNDER the gameplay/HUD layers and never
    /// obstructs them.
    /// </summary>
    public KumoriSeekBar? SeekBar { get; set; }

    /// <summary>Persisted viewer settings (marker toggles). Assigned by ReplayViewerGame.</summary>
    public KumoriViewerConfig? ViewerConfig { get; set; }

    /// <summary>Called when a setting needs the replay player to be rebuilt.</summary>
    public Action? RequestReload { get; set; }

    /// <summary>Called when the advanced analyzer should open.</summary>
    public Action? OpenMissAnalyzer { get; set; }

    /// <summary>Called after lazer's native hold-to-exit completes.</summary>
    public Action? RequestWindowClose { get; set; }

    /// <summary>Frame-stable gameplay time for the game-level seek bar.
    /// Null-safe: the clock container is absent when beatmap load fails.</summary>
    public double GameplayTime => GameplayClockContainer?.CurrentTime ?? 0;

    public bool IsGameplayPaused => GameplayClockContainer?.IsPaused.Value ?? true;

    private float seekBarAlphaBeforeAnalysis;
    private IReadOnlyList<PlayerSettingsGroup>? settingsBeforeAnalysis;
    private readonly List<ReplayJudgementSnapshot> analysisJudgements = [];

    public Action<IReadOnlyList<ReplayJudgementSnapshot>>? AnalysisJudgementsReady { get; set; }

    public KumoriReplayPlayer(Score score)
        : base(score, new PlayerConfiguration
        {
            ShowResults = false,
            AllowRestart = false,
            AllowUserInteraction = true,
            AllowSkipping = true,
            ShowLeaderboard = false,
        })
    {
        sourceScore = score;
    }

    protected override IReadOnlyDependencyContainer CreateChildDependencies(IReadOnlyDependencyContainer parent)
    {
        var dependencies = base.CreateChildDependencies(parent);

        // Player's BackgroundDependencyLoader reads Mods.Value to build the
        // playable beatmap and apply IApplicableToTrack mods. OnEntering() is
        // too late here; the raw screen is already loaded by then. This mirrors
        // the important part of ReplayPlayerLoader without using its full
        // loading UI/state machine.
        Mods.Value = sourceScore.ScoreInfo.Mods;
        Ruleset.Value = sourceScore.ScoreInfo.Ruleset;

        return dependencies;
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();

        if (!LoadedBeatmapSuccessfully)
        {
            Logger.Log("Kumori: beatmap not loaded successfully; skipping HUD customisation.", level: LogLevel.Important);
            return;
        }

        try
        {
            // Kumori is a viewer, not the full game; lazer's full per-player
            // visual and audio tweaking groups are noise here. Playback
            // controls (and ruleset-provided groups such as replay analysis)
            // are kept, then a minimal background group is added back below.
            ConfigureReplaySidebar();

            // The skin layout re-creates its own SongProgress on every skin
            // change; keep it suppressed so only Kumori's bar is visible.
            Scheduler.AddDelayed(hideSkinProgress, 500, true);
            hideSkinProgress();

            if (AttachKumoriHud)
            {
                attachSeekBar();
            }

            beginAnalysisCollection();
            if (DrawableRuleset is DrawableOsuRuleset osuDrawable)
            {
                // The analyzer supplies one selected-object marker. Disable
                // lazer's replay-wide marker layer so stale config cannot
                // display every click underneath it.
                osuDrawable.ReplayClickMarkersEnabled.Value = false;
                selectedClickMarker = new KumoriSelectedClickMarker();
                osuDrawable.PlayfieldAdjustmentContainer.Add(selectedClickMarker);
                Drawable markerProxy = selectedClickMarker.CreateProxy();
                markerProxy.Depth = float.NegativeInfinity;
                osuDrawable.Overlays.Add(markerProxy);
            }

        }
        catch (Exception e)
        {
            // Never let HUD customisation take the whole viewer down, but
            // leave an unambiguous trace in runtime.log.
            Logger.Error(e, "Kumori: failed to customise the player HUD.");
        }
    }

    protected virtual bool AttachKumoriHud => true;

    public override bool OnExiting(ScreenExitEvent e)
    {
        if (base.OnExiting(e))
            return true;

        if (RequestWindowClose == null)
            return false;

        Scheduler.Add(RequestWindowClose);
        return true;
    }

    public void SetSelectedClickMarker(MissAnalysisEntry? entry, bool visible)
        => selectedClickMarker?.Set(entry, visible);

    protected virtual void ConfigureReplaySidebar()
    {
        ReplayOverlay.Settings.RemoveAll(d => d is VisualSettings || d is AudioSettings, true);
        ReplayOverlay.Settings.Add(new KumoriSeekBarSettings(ViewerConfig!, RequestReload, SeekBar, OpenMissAnalyzer));
        ReplayOverlay.Settings.Add(new KumoriAudioSettings(ViewerConfig!));
        ReplayOverlay.Settings.Add(new KumoriBackgroundSettings(ViewerConfig!));
        Logger.Log("Kumori: seek bar, audio, and background settings groups added to replay side menu.");
    }

    public void PauseGameplay()
    {
        if (LoadedBeatmapSuccessfully && GameplayClockContainer.IsPaused.Value == false)
            GameplayClockContainer.Stop();
    }

    public void StartReplayPlayback()
    {
        if (LoadedBeatmapSuccessfully && GameplayClockContainer.IsPaused.Value)
            GameplayClockContainer.Start();
    }

    public void SetPlaybackRate(double rate)
    {
        if (GameplayClockContainer is MasterGameplayClockContainer master)
            master.UserPlaybackRate.Value = Math.Clamp(rate, master.UserPlaybackRate.MinValue, master.UserPlaybackRate.MaxValue);
    }

    public double PlaybackRate => GameplayClockContainer is MasterGameplayClockContainer master
        ? master.UserPlaybackRate.Value
        : 1;

    public void EnterAnalysisMode(PlayerSettingsGroup analyzerSettings)
    {
        if (!LoadedBeatmapSuccessfully)
            return;
        PauseGameplay();
        settingsBeforeAnalysis ??= ReplayOverlay.Settings.Groups;
        ReplayOverlay.Settings.SetGroups([analyzerSettings]);
        ReplayOverlay.SetSettingsForced(true);
        ReplayOverlay.Show();
        if (SeekBar != null)
        {
            seekBarAlphaBeforeAnalysis = SeekBar.Alpha;
            SeekBar.Alpha = 0;
        }
    }

    public void ExitAnalysisMode()
    {
        ReplayOverlay.SetSettingsForced(false);
        if (settingsBeforeAnalysis != null)
        {
            ReplayOverlay.Settings.SetGroups(settingsBeforeAnalysis);
            settingsBeforeAnalysis = null;
        }
        if (SeekBar != null)
            SeekBar.Alpha = seekBarAlphaBeforeAnalysis;
    }

    private void attachSeekBar()
    {
        if (SeekBar is not KumoriSeekBar bar || bar.Parent != null)
            return;

        // Positive depth sorts behind GameplayClockContainer (depth 0):
        // the pill draws above the dimmed background but underneath the
        // playfield, cursor and HUD, so it can never obstruct gameplay.
        bar.Depth = 1;
        AddInternal(bar);
        Logger.Log("Kumori: seek bar attached beneath the gameplay layer.");
    }

    private void hideSkinProgress()
    {
        foreach (SongProgress progress in HUDOverlay.ChildrenOfType<SongProgress>())
            progress.Alpha = 0;
    }

    private void beginAnalysisCollection()
    {
        if (!LoadedBeatmapSuccessfully || AnalysisJudgementsReady == null)
            return;

        analysisJudgements.Clear();
        Alpha = 0;
        PauseGameplay();

        void collect(JudgementResult result)
        {
            KumoriTimelineMarkerKind? mapped = KumoriTimelineMarkers.KindFromJudgement(result);
            if (mapped is not { } kind)
                return;
            analysisJudgements.Add(new ReplayJudgementSnapshot(
                result.HitObject,
                kind,
                result.TimeAbsolute,
                result.TimeOffset,
                result.ComboAtJudgement,
                result.ComboAfterJudgement));
        }

        Scheduler.AddDelayed(() =>
        {
            double returnTime = GameplayTime;
            double endTime = DrawableRuleset.Objects.LastOrDefault()?.GetEndTime() + 1000 ?? returnTime;
            ScoreProcessor.NewJudgement += collect;
            Seek(endTime);
            Scheduler.AddDelayed(() =>
            {
                ScoreProcessor.NewJudgement -= collect;
                ReplayJudgementSnapshot[] snapshots = analysisJudgements
                    .DistinctBy(j => (j.HitObject, j.Kind, (int)Math.Round(j.EventTime)))
                    .ToArray();
                Seek(returnTime);
                Alpha = 1;
                AnalysisJudgementsReady?.Invoke(snapshots);
                Logger.Log($"Kumori: lazer analysis pass collected {snapshots.Length} bad judgements.");
            }, 500);
        }, 400);
    }

}
