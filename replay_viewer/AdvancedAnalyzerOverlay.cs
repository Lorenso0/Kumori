using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Cursor;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Input.Events;
using osu.Framework.Logging;
using osuTK;
using osuTK.Input;

namespace Kumori.ReplayViewer;

internal partial class AdvancedAnalyzerOverlay : CompositeDrawable
{
    private const float left_zone_width = KumoriAnalyzerSidebar.COMPACT_WIDTH;

    private readonly AdvancedAnalyzerViewModel viewModel;
    private readonly AdvancedAnalyzerRuntime runtime;
    private KumoriSettingsSidebar leftSidebar = null!;
    private KumoriSettingsSidebar rightSidebar = null!;
    private AdvancedAnalyzerSettingsGroup inspector = null!;
    private bool isOpen;
    private bool isPlaying;
    private bool loopSeekPending;
    private double entryTime;
    private double entryRate;
    private bool entryWasPaused;

    public AdvancedAnalyzerOverlay(AdvancedAnalyzerViewModel viewModel, AdvancedAnalyzerRuntime runtime)
    {
        this.viewModel = viewModel;
        this.runtime = runtime;
        RelativeSizeAxes = Axes.Both;
        Alpha = 0;
        Depth = -100;
    }

    public override bool HandlePositionalInput => isOpen;
    public override bool HandleNonPositionalInput => isOpen;

    [BackgroundDependencyLoader]
    private void load()
    {
        inspector = new AdvancedAnalyzerSettingsGroup(viewModel, this);
        leftSidebar = new KumoriAnalyzerSidebar();
        var browser = new AdvancedAnalyzerEventBrowser(viewModel, leftSidebar.ScrollIntoView);
        leftSidebar.SetGroups([browser]);
        rightSidebar = new KumoriSettingsSidebar(anchorLeft: false);
        rightSidebar.SetGroups([inspector]);
        InternalChildren =
        [
            new Box
            {
                RelativeSizeAxes = Axes.Y,
                Width = left_zone_width,
                Colour = new osuTK.Graphics.Color4(8, 9, 14, 252),
                Depth = 1,
            },
            new Box
            {
                RelativeSizeAxes = Axes.Y,
                Width = left_zone_width,
                Anchor = Anchor.TopRight,
                Origin = Anchor.TopRight,
                Colour = new osuTK.Graphics.Color4(8, 9, 14, 252),
                Depth = 1,
            },
            new PopoverContainer
            {
                RelativeSizeAxes = Axes.Both,
                Children =
                [
                    leftSidebar,
                    rightSidebar,
                ],
            },
        ];
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();
        viewModel.FiltersChanged += filtersChanged;
        viewModel.SelectionChanged += selectionChanged;
        viewModel.SelectionAppearanceChanged += selectionAppearanceChanged;
        viewModel.PlaybackSettingsChanged += playbackSettingsChanged;
        viewModel.ShowSelectedClickMarker.ValueChanged += selectedClickMarkerChanged;
    }

    public void Open()
    {
        if (isOpen)
            return;

        entryTime = runtime.Time;
        entryRate = runtime.Rate;
        entryWasPaused = runtime.IsPaused;
        if (!runtime.Enter())
        {
            Logger.Log("Kumori: advanced analyzer open was requested before the replay player was ready.", level: LogLevel.Important);
            return;
        }
        isPlaying = false;
        viewModel.SelectNextFrom(entryTime);
        isOpen = true;
        viewModel.SetAnalyzerOpen(true);
        runtime.SetRate(viewModel.PlaybackRate.Value);
        this.FadeIn(160, Easing.OutQuint);
        inspector.UpdateEntry(viewModel.SelectedEntry);
        updateSelectedClickMarker();
        Logger.Log($"Kumori: advanced analyzer opened with {viewModel.VisibleEntries.Count} visible events.");
    }

    public void Close()
    {
        if (!isOpen)
            return;
        leave(entryTime, restorePlayback: !entryWasPaused);
    }

    public void ReturnToReplayHere()
    {
        if (viewModel.SelectedEntry is not { } entry)
            return;
        leave(entry.EventTime, restorePlayback: false);
    }

    private void leave(double time, bool restorePlayback)
    {
        isOpen = false;
        viewModel.SetAnalyzerOpen(false);
        isPlaying = false;
        loopSeekPending = false;
        runtime.SetSelectedAnalysisMarkers(null, false, false, false, viewModel.SelectedNoteColour.Value);
        runtime.Pause();
        runtime.Exit();
        runtime.SetRate(entryRate);
        runtime.Seek(time);
        if (restorePlayback)
            runtime.Start();
        inspector.SetPlaying(false);
        this.FadeOut(140, Easing.OutQuint);
    }

    public void TogglePlayback()
    {
        if (viewModel.SelectedEntry is not { } entry)
            return;

        isPlaying = runtime.IsPaused;
        if (isPlaying)
        {
            runtime.SetRate(viewModel.PlaybackRate.Value);
            if (runtime.Time < loopStart(entry) || runtime.Time >= loopEnd(entry))
                runtime.Play(loopStart(entry));
            else
                runtime.Start();
        }
        else
            runtime.Pause();
        inspector.SetPlaying(isPlaying);
    }

    public void StepFrame(int direction)
    {
        isPlaying = false;
        runtime.Pause();
        runtime.StepFrame(direction);
        inspector.SetPlaying(false);
    }

    protected override void Update()
    {
        base.Update();
        if (!isOpen)
            return;

        if (!loopSeekPending)
        {
            bool clockIsPlaying = !runtime.IsPaused;
            if (clockIsPlaying != isPlaying)
            {
                isPlaying = clockIsPlaying;
                inspector.SetPlaying(isPlaying);
            }
        }

        if (viewModel.SelectedEntry is { } entry && isPlaying && !loopSeekPending && runtime.Time >= loopEnd(entry))
        {
            if (!viewModel.LoopEnabled.Value)
            {
                isPlaying = false;
                runtime.Pause();
                inspector.SetPlaying(false);
            }
            else
            {
                loopSeekPending = true;
                runtime.Pause();
                runtime.Seek(loopStart(entry));
                Scheduler.AddDelayed(() =>
                {
                    loopSeekPending = false;
                    if (isOpen && isPlaying)
                        runtime.Start();
                }, 60);
            }
        }
    }

    private void filtersChanged()
    {
        inspector.UpdateEntry(viewModel.SelectedEntry);
        updateSelectedClickMarker();
        if (isOpen)
            selectCurrent();
    }

    private void selectionChanged()
    {
        inspector.UpdateEntry(viewModel.SelectedEntry);
        updateSelectedClickMarker();
        if (isOpen)
            selectCurrent();
    }

    private void selectedClickMarkerChanged(osu.Framework.Bindables.ValueChangedEvent<bool> change)
        => updateSelectedClickMarker();

    private void selectionAppearanceChanged() => updateSelectedClickMarker();

    private void updateSelectedClickMarker()
        => runtime.SetSelectedAnalysisMarkers(
            viewModel.SelectedEntry,
            isOpen && viewModel.ShowSelectedClickMarker.Value,
            isOpen && viewModel.RecolourSelectedNote.Value,
            isOpen && viewModel.ShowSelectedNoteIndicator.Value,
            viewModel.SelectedNoteColour.Value);

    private void playbackSettingsChanged()
    {
        if (isOpen)
            runtime.SetRate(viewModel.PlaybackRate.Value);
    }

    private void selectCurrent()
    {
        isPlaying = false;
        loopSeekPending = false;
        inspector.SetPlaying(false);
        if (viewModel.SelectedEntry is not { } entry)
        {
            runtime.Pause();
            return;
        }
        double focusTime = loopStart(entry);
        runtime.Focus(focusTime);
    }

    private double loopStart(MissAnalysisEntry entry) => Math.Max(0, reviewTime(entry) - viewModel.LoopBefore.Value);
    private double loopEnd(MissAnalysisEntry entry) => reviewTime(entry) + viewModel.LoopAfter.Value;
    private static double reviewTime(MissAnalysisEntry entry) => entry.TargetStartTime;

    protected override bool OnKeyDown(KeyDownEvent e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            return true;
        }
        if (e.Key == Key.Space)
        {
            TogglePlayback();
            return true;
        }
        return base.OnKeyDown(e);
    }

    public override bool ReceivePositionalInputAt(Vector2 screenSpacePos)
        => isOpen
           && Alpha > 0.01f
           && (screenSpacePos.X < ToScreenSpace(new Vector2(left_zone_width, 0)).X
               || screenSpacePos.X > ToScreenSpace(new Vector2(DrawWidth - left_zone_width, 0)).X)
           && base.ReceivePositionalInputAt(screenSpacePos);

    protected override bool OnClick(ClickEvent e) => isOpen;

    protected override void Dispose(bool isDisposing)
    {
        viewModel.FiltersChanged -= filtersChanged;
        viewModel.SelectionChanged -= selectionChanged;
        viewModel.SelectionAppearanceChanged -= selectionAppearanceChanged;
        viewModel.PlaybackSettingsChanged -= playbackSettingsChanged;
        viewModel.ShowSelectedClickMarker.ValueChanged -= selectedClickMarkerChanged;
        base.Dispose(isDisposing);
    }
}
