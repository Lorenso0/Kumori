using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Sprites;
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

    /// <summary>Hard playback cutoff for an unfinished capture.</summary>
    public double? PlaybackEndTime { get; init; }

    /// <summary>Map time to return to when Play is pressed at the cutoff.</summary>
    public double PlaybackRestartTime { get; init; }

    /// <summary>
    /// Stable cursor/replay frames are exact movement evidence, but lazer's
    /// Classic hit simulation can differ from stable at hit-area boundaries,
    /// including when playing the original .osr.
    /// When supplied, show the stable-recorded accuracy instead of presenting
    /// lazer's reconstructed value as authoritative.
    /// </summary>
    public double? RecordedAccuracyOverride { get; init; }

    private readonly List<ReplayJudgementSnapshot> analysisJudgements = [];
    private bool analysisMode;
    private bool playbackEndReached;

    public Action<IReadOnlyList<ReplayJudgementSnapshot>>? AnalysisJudgementsReady { get; set; }

    public KumoriReplayPlayer(Score score)
        : base(score, new PlayerConfiguration
        {
            ShowResults = false,
            AllowRestart = false,
            AllowUserInteraction = true,
            AllowSkipping = true,
            ShowLeaderboard = false,
            // Kumori is an analysis viewer. The full-screen red failing tint
            // obscures replay details when health drops, so it is always off.
            ShowFailingOverlay = false,
        })
    {
        sourceScore = score;
    }

    protected override IReadOnlyDependencyContainer CreateChildDependencies(IReadOnlyDependencyContainer parent)
    {
        // Player's BackgroundDependencyLoader reads Mods.Value to build the
        // playable beatmap and apply IApplicableToTrack mods. OnEntering() is
        // too late here, and base.CreateChildDependencies() must not cache the
        // previous empty values. This mirrors the important part of
        // ReplayPlayerLoader without using its full loading UI/state machine.
        Mods.Value = sourceScore.ScoreInfo.Mods;
        Ruleset.Value = sourceScore.ScoreInfo.Ruleset;

        return base.CreateChildDependencies(parent);
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
            ReplayOverlay.Settings.Padding = new MarginPadding
            {
                Bottom = KumoriSeekBar.ReservedBottomHeight,
            };
            ReplayOverlay.Settings.Expanded.BindValueChanged(replaySettingsExpandedChanged, true);

            // The skin layout re-creates its own SongProgress on every skin
            // change; keep it suppressed so only Kumori's bar is visible.
            Scheduler.AddDelayed(hideSkinProgress, 500, true);
            hideSkinProgress();
            if (RecordedAccuracyOverride is not null)
            {
                addRecordedAccuracy();
            }

            if (AttachKumoriHud)
            {
                attachSeekBar();
            }

            beginAnalysisCollection();
            if (DrawableRuleset is DrawableOsuRuleset osuDrawable)
            {
                selectedClickMarker = new KumoriSelectedClickMarker(osuDrawable.Playfield);
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

    protected override void Update()
    {
        base.Update();

        if (analysisMode)
        {
            // Upstream replay hotkeys and hover logic may attempt to show or
            // expand this overlay after the analyzer has opened. The analyzer
            // owns both side zones until it closes.
            if (ReplayOverlay.Settings.Expanded.Value)
                ReplayOverlay.Settings.Expanded.Value = false;
            ReplayOverlay.ClearTransforms();
            ReplayOverlay.Alpha = 0;
        }

        if (PlaybackEndTime is not { } endTime || !LoadedBeatmapSuccessfully)
            return;

        if (GameplayTime < endTime - 1)
        {
            playbackEndReached = false;
            return;
        }

        if (!playbackEndReached)
        {
            playbackEndReached = true;
            PauseGameplay();
            return;
        }

        // Treat Play at an incomplete replay's endpoint like replaying a
        // finished video instead of immediately pausing at the cutoff again.
        if (!IsGameplayPaused)
        {
            playbackEndReached = false;
            Seek(PlaybackRestartTime);
            StartReplayPlayback();
        }
    }

    public override bool OnExiting(ScreenExitEvent e)
    {
        if (base.OnExiting(e))
            return true;

        if (RequestWindowClose == null)
            return false;

        Scheduler.Add(RequestWindowClose);
        return true;
    }

    public void SetSelectedAnalysisMarkers(
        MissAnalysisEntry? entry,
        bool showClickMarker,
        bool recolourNote,
        bool showNoteIndicator,
        osu.Framework.Graphics.Colour4 colour)
        => selectedClickMarker?.Set(entry, showClickMarker, recolourNote, showNoteIndicator, colour);

    protected virtual void ConfigureReplaySidebar()
    {
        ReplayOverlay.Settings.RemoveAll(d => d is VisualSettings || d is AudioSettings || d is ReplayAnalysisSettings, true);
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

    public void EnterAnalysisMode()
    {
        if (!LoadedBeatmapSuccessfully)
            return;
        PauseGameplay();
        // The analyzer owns its sidebars. Collapse and lock lazer's normal
        // replay menu so edge-hover cannot reopen it over the analyzer.
        analysisMode = true;
        ReplayOverlay.Settings.Expanded.Value = false;
        ReplayOverlay.ClearTransforms();
        ReplayOverlay.Hide();
    }

    public void ExitAnalysisMode()
    {
        analysisMode = false;
        ReplayOverlay.ClearTransforms();
        ReplayOverlay.Show();
    }

    private void replaySettingsExpandedChanged(osu.Framework.Bindables.ValueChangedEvent<bool> expanded)
    {
        if (analysisMode && expanded.NewValue)
        {
            ReplayOverlay.Settings.Expanded.Value = false;
            return;
        }

        SeekBar?.SetInputBlocked(expanded.NewValue);
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

    private void addRecordedAccuracy()
    {
        if (RecordedAccuracyOverride is not { } accuracy)
            return;
        double truncated = Math.Floor(accuracy * 100) / 100;
        AddInternal(new SpriteText
        {
            Text = $"lazer may not reproduce osu!stable accuracy exactly  ·  stable {truncated:0.00}%",
            Anchor = Anchor.TopRight,
            Origin = Anchor.TopRight,
            Position = new osuTK.Vector2(-20, 48),
            Font = FontUsage.Default.With(size: 12),
            Colour = Colour4.White.Opacity(0.72f),
            Depth = -1000,
        });
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
