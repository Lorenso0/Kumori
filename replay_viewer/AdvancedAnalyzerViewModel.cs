using osu.Framework.Bindables;
using osu.Framework.Graphics;

namespace Kumori.ReplayViewer;

internal sealed class AdvancedAnalyzerViewModel : IDisposable
{
    private MissAnalysisModel model;
    private readonly KumoriViewerConfig config;
    private int selectedIndex;
    private readonly List<Action> unbindActions = [];

    public BindableBool ShowMisses { get; } = new();
    public BindableBool ShowMehs { get; } = new();
    public BindableBool ShowOks { get; } = new();
    public BindableBool ShowSliderBreaks { get; } = new();
    public BindableBool LoopEnabled { get; } = new();
    public BindableBool ShowInputMarkers { get; } = new();
    public BindableBool ShowMovementSamples { get; } = new();
    public BindableBool ShowHeldSamples { get; } = new();
    public BindableBool ShowSelectedClickMarker { get; } = new();
    public BindableBool RecolourSelectedNote { get; } = new();
    public BindableBool ShowSelectedNoteIndicator { get; } = new();
    public Bindable<Colour4> SelectedNoteColour { get; } = new();
    public Bindable<Colour4> ComparisonCursorColour { get; } = new();
    public BindableDouble LoopBefore { get; } = new();
    public BindableDouble LoopAfter { get; } = new();
    public BindableDouble PlaybackRate { get; } = new();

    public IReadOnlyList<MissAnalysisEntry> VisibleEntries { get; private set; } = [];
    public MissAnalysisEntry? SelectedEntry => VisibleEntries.Count == 0 ? null : VisibleEntries[selectedIndex];
    public MissAnalysisEntry? HoveredEntry { get; private set; }
    public int SelectedIndex => selectedIndex;
    public int TotalCount => model.Entries.Count;
    public IReadOnlyList<MissAnalysisEntry> AllEntries => model.Entries;
    public int CountFor(KumoriTimelineMarkerKind kind) => model.Entries.Count(entry => entry.Kind == kind);
    public bool UsesFallbackData => model.Entries.Any(e => e.Source == AnalysisDataSource.Inferred);
    public bool AnalyzerOpen { get; private set; }
    public string RecentTrendSummary { get; }
    public ComparisonContract? Comparison { get; }

    public event Action? FiltersChanged;
    public event Action? SelectionChanged;
    public event Action? HoverChanged;
    public event Action? AnalyzerVisibilityChanged;
    public event Action? SelectionAppearanceChanged;
    public event Action? PlaybackSettingsChanged;

    public AdvancedAnalyzerViewModel(MissAnalysisModel model, KumoriViewerConfig config, ViewerContract? contract = null)
    {
        this.model = model;
        this.config = config;
        RecentTrendSummary = AdvancedAnalyzerMetrics.RecentTrendSummary(contract);
        Comparison = contract?.Comparison;
        initialise(ShowMisses, KumoriViewerSetting.ShowMissMarkers);
        initialise(ShowMehs, KumoriViewerSetting.ShowMehMarkers);
        initialise(ShowOks, KumoriViewerSetting.ShowOkMarkers);
        initialise(ShowSliderBreaks, KumoriViewerSetting.ShowSliderBreakMarkers);
        initialise(LoopEnabled, KumoriViewerSetting.MissAnalyzerLoopEnabled);
        initialise(ShowInputMarkers, KumoriViewerSetting.MissAnalyzerShowInputMarkers);
        initialise(ShowMovementSamples, KumoriViewerSetting.MissAnalyzerShowMovementSamples);
        initialise(ShowHeldSamples, KumoriViewerSetting.MissAnalyzerShowHeldSamples);
        initialise(ShowSelectedClickMarker, KumoriViewerSetting.MissAnalyzerShowSelectedClickMarker);
        initialise(RecolourSelectedNote, KumoriViewerSetting.MissAnalyzerRecolourSelectedNote);
        initialise(ShowSelectedNoteIndicator, KumoriViewerSetting.MissAnalyzerShowSelectedNoteIndicator);
        initialise(SelectedNoteColour, KumoriViewerSetting.MissAnalyzerSelectedNoteColour);
        initialise(ComparisonCursorColour, KumoriViewerSetting.ComparisonReplayCursorColour);
        initialise(LoopBefore, KumoriViewerSetting.MissAnalyzerLoopBefore, 150, 2000, 50);
        initialise(LoopAfter, KumoriViewerSetting.MissAnalyzerLoopAfter, 150, 2000, 50);
        initialise(PlaybackRate, KumoriViewerSetting.MissAnalyzerPlaybackRate, 0.05, 2, 0.01);

        ShowMisses.ValueChanged += _ => refreshFilters();
        ShowMehs.ValueChanged += _ => refreshFilters();
        ShowOks.ValueChanged += _ => refreshFilters();
        ShowSliderBreaks.ValueChanged += _ => refreshFilters();
        LoopEnabled.ValueChanged += _ => PlaybackSettingsChanged?.Invoke();
        LoopBefore.ValueChanged += _ => PlaybackSettingsChanged?.Invoke();
        LoopAfter.ValueChanged += _ => PlaybackSettingsChanged?.Invoke();
        PlaybackRate.ValueChanged += _ => PlaybackSettingsChanged?.Invoke();
        RecolourSelectedNote.ValueChanged += _ => SelectionAppearanceChanged?.Invoke();
        ShowSelectedNoteIndicator.ValueChanged += _ => SelectionAppearanceChanged?.Invoke();
        SelectedNoteColour.ValueChanged += _ => SelectionAppearanceChanged?.Invoke();
        refreshFilters(false);
    }

    public void ReplaceModel(MissAnalysisModel replacement)
    {
        double? selectedTime = SelectedEntry?.EventTime;
        model = replacement;
        refreshFilters(false);
        if (selectedTime != null && VisibleEntries.Count > 0)
            selectedIndex = nearestIndex(selectedTime.Value);
        FiltersChanged?.Invoke();
        SelectionChanged?.Invoke();
    }

    public void Select(int index)
    {
        if (VisibleEntries.Count == 0)
            return;
        int next = Math.Clamp(index, 0, VisibleEntries.Count - 1);
        selectedIndex = next;
        SelectionChanged?.Invoke();
    }

    public void SelectNextFrom(double time)
    {
        if (VisibleEntries.Count == 0)
            return;
        int index = VisibleEntries.ToList().FindIndex(e => e.EventTime >= time);
        selectedIndex = index < 0 ? 0 : index;
        SelectionChanged?.Invoke();
    }

    public void SelectPrevious() => Select(selectedIndex - 1);
    public void SelectNext() => Select(selectedIndex + 1);

    public void Select(MissAnalysisEntry entry)
    {
        int index = VisibleEntries.ToList().IndexOf(entry);
        if (index >= 0)
            Select(index);
    }

    public void SetHovered(MissAnalysisEntry? entry)
    {
        if (ReferenceEquals(HoveredEntry, entry))
            return;
        HoveredEntry = entry;
        HoverChanged?.Invoke();
    }

    public void ClearHovered(MissAnalysisEntry entry)
    {
        if (ReferenceEquals(HoveredEntry, entry))
            SetHovered(null);
    }

    public void SetAnalyzerOpen(bool open)
    {
        if (AnalyzerOpen == open)
            return;
        AnalyzerOpen = open;
        AnalyzerVisibilityChanged?.Invoke();
    }

    private int nearestIndex(double time)
        => Enumerable.Range(0, VisibleEntries.Count).MinBy(i => Math.Abs(VisibleEntries[i].EventTime - time));

    private void refreshFilters(bool notify = true)
    {
        double? selectedTime = SelectedEntry?.EventTime;
        VisibleEntries = model.Entries.Where(isVisible).ToArray();
        if (HoveredEntry != null && !VisibleEntries.Contains(HoveredEntry))
        {
            HoveredEntry = null;
            HoverChanged?.Invoke();
        }
        selectedIndex = VisibleEntries.Count == 0 ? 0 : selectedTime == null ? 0 : nearestIndex(selectedTime.Value);
        if (notify)
            FiltersChanged?.Invoke();
    }

    private bool isVisible(MissAnalysisEntry entry) => entry.Kind switch
    {
        KumoriTimelineMarkerKind.Miss => ShowMisses.Value,
        KumoriTimelineMarkerKind.SliderBreak => ShowSliderBreaks.Value,
        KumoriTimelineMarkerKind.Meh => ShowMehs.Value,
        KumoriTimelineMarkerKind.Ok => ShowOks.Value,
        _ => false,
    };

    private void initialise(BindableBool bindable, KumoriViewerSetting setting)
    {
        var persisted = config.GetBindable<bool>(setting);
        bindable.Value = persisted.Value;
        bool syncing = false;
        Action<ValueChangedEvent<bool>> persistedChanged = change =>
        {
            if (syncing) return;
            syncing = true;
            bindable.Value = change.NewValue;
            syncing = false;
        };
        Action<ValueChangedEvent<bool>> localChanged = change =>
        {
            if (syncing) return;
            syncing = true;
            persisted.Value = change.NewValue;
            syncing = false;
            config.Save();
        };
        persisted.ValueChanged += persistedChanged;
        bindable.ValueChanged += localChanged;
        unbindActions.Add(() =>
        {
            persisted.ValueChanged -= persistedChanged;
            bindable.ValueChanged -= localChanged;
        });
    }

    private void initialise(Bindable<Colour4> bindable, KumoriViewerSetting setting)
    {
        var persisted = config.GetBindable<Colour4>(setting);
        bindable.Value = persisted.Value;
        bool syncing = false;
        Action<ValueChangedEvent<Colour4>> persistedChanged = change =>
        {
            if (syncing) return;
            syncing = true;
            bindable.Value = change.NewValue;
            syncing = false;
        };
        Action<ValueChangedEvent<Colour4>> localChanged = change =>
        {
            if (syncing) return;
            syncing = true;
            persisted.Value = change.NewValue;
            syncing = false;
            config.Save();
        };
        persisted.ValueChanged += persistedChanged;
        bindable.ValueChanged += localChanged;
        unbindActions.Add(() =>
        {
            persisted.ValueChanged -= persistedChanged;
            bindable.ValueChanged -= localChanged;
        });
    }

    private void initialise(BindableDouble bindable, KumoriViewerSetting setting, double min, double max, double precision)
    {
        bindable.MinValue = min;
        bindable.MaxValue = max;
        bindable.Precision = precision;
        var persisted = config.GetBindable<double>(setting);
        bindable.Value = persisted.Value;
        bool syncing = false;
        Action<ValueChangedEvent<double>> persistedChanged = change =>
        {
            if (syncing) return;
            syncing = true;
            bindable.Value = change.NewValue;
            syncing = false;
        };
        Action<ValueChangedEvent<double>> localChanged = change =>
        {
            if (syncing) return;
            syncing = true;
            persisted.Value = change.NewValue;
            syncing = false;
            config.Save();
        };
        persisted.ValueChanged += persistedChanged;
        bindable.ValueChanged += localChanged;
        unbindActions.Add(() =>
        {
            persisted.ValueChanged -= persistedChanged;
            bindable.ValueChanged -= localChanged;
        });
    }

    public void Dispose()
    {
        foreach (var unbind in unbindActions)
            unbind();
        unbindActions.Clear();
    }
}
