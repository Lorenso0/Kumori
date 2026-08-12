using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kumori.Core.Models;
using Kumori.Storage;
using Serilog;

namespace Kumori.App.ViewModels;

public partial class ImportsViewModel : ObservableObject
{
    private readonly PlaySharePackageService packages;
    private readonly List<AttemptSummary> loaded = [];

    public ObservableCollection<AttemptRowViewModel> Attempts { get; } = [];
    public ObservableCollection<object> Rows { get; } = [];
    public ObservableCollection<ModFilterOptionViewModel> AvailableMods { get; } = [];
    public AttemptDetailsViewModel Inspector { get; }
    public IReadOnlyList<string> FilterModeOptions { get; } = ["All", "Completed", "Failed", "Retried", "Quit"];
    public IReadOnlyList<string> ArtworkModeOptions { get; } = ["Thumbnail cards", "No artwork"];

    [ObservableProperty] private AttemptRowViewModel? selectedAttempt;
    [ObservableProperty] private string searchText = "";
    [ObservableProperty] private string selectedFilterMode = "All";
    [ObservableProperty] private string selectedModFilterMode = "Contains";
    [ObservableProperty] private string selectedArtworkMode = "Thumbnail cards";
    [ObservableProperty] private bool isGroupRepeats;
    [ObservableProperty] private bool isGroupSessions;
    [ObservableProperty] private bool isReplayAnalyzerLoading;
    [ObservableProperty] private string replayAnalyzerLoadingText = "";
    [ObservableProperty] private string historyStatus = "No imported plays";
    [ObservableProperty] private bool isLoadingMore;
    [ObservableProperty] private bool isScrolledToBottom;

    public ImportsViewModel(
        PlaySharePackageService packages,
        ReplayViewerContractService replayViewer)
    {
        this.packages = packages;
        Inspector = new AttemptDetailsViewModel(
            packages.GetImportedDetails,
            packages.GetImportedMovement,
            replayViewer);
    }

    public string ResultsText => $"{Attempts.Count:N0} results";
    public string ResultsShortText => $"{Attempts.Count:N0}";
    public bool LoadOlderVisible => false;
    public bool HasAvailableMods => AvailableMods.Count > 0;
    public bool IsModFilterActive => AvailableMods.Any(option => option.IsSelected);
    public string ModsFilterLabel => IsModFilterActive ? "Mods · filtered" : "Mods";
    public string ModFilterModeDescription => SelectedModFilterMode == "Exact"
        ? "Only imports with exactly the selected combination."
        : "Imports may include other mods in addition to your selection.";
    public bool IsThumbnailArtwork => SelectedArtworkMode == "Thumbnail cards";
    public bool HasActiveSession => false;
    public bool ShowSessionGrouping => false;

    partial void OnSelectedAttemptChanged(AttemptRowViewModel? value) =>
        _ = Inspector.LoadAsync(value?.Id);

    partial void OnSearchTextChanged(string value) => ApplyFilter();
    partial void OnSelectedFilterModeChanged(string value) => ApplyFilter();
    partial void OnSelectedModFilterModeChanged(string value)
    {
        OnPropertyChanged(nameof(ModFilterModeDescription));
        ApplyFilter();
    }
    partial void OnSelectedArtworkModeChanged(string value) => OnPropertyChanged(nameof(IsThumbnailArtwork));
    partial void OnIsGroupRepeatsChanged(bool value) => ApplyFilter();

    public async Task RefreshAsync(long? selectId = null)
    {
        try
        {
            AttemptSummary[] rows = (await Task.Run(() => packages.GetImportedAttempts())).ToArray();
            loaded.Clear();
            loaded.AddRange(rows);
            RebuildModFilters();
            ApplyFilter(selectId);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not load imported plays");
            HistoryStatus = "Could not load imports";
        }
    }

    [RelayCommand]
    private Task Refresh() => RefreshAsync(SelectedAttempt?.Id);

    [RelayCommand]
    private void ClearFilters()
    {
        SearchText = "";
        SelectedFilterMode = "All";
        IsGroupRepeats = false;
        foreach (ModFilterOptionViewModel option in AvailableMods)
            option.IsSelected = false;
        ApplyFilter();
    }

    public async Task OpenReplayInspectorAsync(AttemptRowViewModel row, Window? owner = null)
    {
        if (IsReplayAnalyzerLoading)
            return;

        SelectedAttempt = row;
        IsReplayAnalyzerLoading = true;
        ReplayAnalyzerLoadingText = "Loading shared replay...";
        try
        {
            await Inspector.LoadAsync(row.Id);
            if (!Inspector.OpenReplayInspectorCommand.CanExecute(null))
                throw new InvalidOperationException("This imported play cannot be opened in Replay Analyzer.");
            ReplayAnalyzerLoadingText = "Preparing Replay Analyzer...";
            await Inspector.OpenReplayInspectorCommand.ExecuteAsync(null);
            if (!string.IsNullOrWhiteSpace(Inspector.LoadError))
                throw new InvalidOperationException(Inspector.LoadError);

            if (Inspector.TakeLastReplayInspectorProcess() is { } process && owner is not null)
            {
                ReplayAnalyzerLoadingText = "Opening Replay Analyzer...";
                _ = await ReplayAnalyzerWindowPlacement.CenterNearOwnerAsync(
                    process,
                    owner,
                    activate: true);
            }
            HistoryStatus = "Replay Analyzer opened";
        }
        finally
        {
            IsReplayAnalyzerLoading = false;
            ReplayAnalyzerLoadingText = "";
        }
    }

    public async Task<bool> DeleteAsync(AttemptRowViewModel row, Window? owner)
    {
        if (!KumoriDialog.Confirm(
                owner,
                $"Delete the play shared by {row.Model.SharedByPlayerName}?\n\nThis does not affect local play history.",
                "Delete imported play",
                MessageBoxImage.Warning))
            return false;
        bool deleted = await Task.Run(() => packages.DeleteImport(row.Id));
        if (deleted)
        {
            Inspector.ForgetAttempt(row.Id);
            await RefreshAsync();
        }
        return deleted;
    }

    private void ApplyFilter(long? preferredId = null)
    {
        long? selectedId = preferredId ?? SelectedAttempt?.Id;
        IEnumerable<AttemptSummary> filtered = loaded;
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            string search = SearchText.Trim();
            filtered = filtered.Where(play =>
                play.Artist.Contains(search, StringComparison.OrdinalIgnoreCase)
                || play.Title.Contains(search, StringComparison.OrdinalIgnoreCase)
                || play.Difficulty.Contains(search, StringComparison.OrdinalIgnoreCase)
                || play.ModsKey.Contains(search, StringComparison.OrdinalIgnoreCase)
                || (play.SharedByPlayerName?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false));
        }
        if (SelectedFilterMode != "All")
            filtered = filtered.Where(play => play.Outcome.Equals(SelectedFilterMode, StringComparison.OrdinalIgnoreCase));
        string[] selectedMods = AvailableMods.Where(option => option.IsSelected).Select(option => option.Acronym).ToArray();
        if (selectedMods.Length > 0)
        {
            bool exact = SelectedModFilterMode == "Exact";
            filtered = filtered.Where(play => ModFilterMatcher.Matches(play, selectedMods, exact));
        }
        if (IsGroupRepeats)
        {
            filtered = filtered
                .GroupBy(play => $"{play.Checksum ?? play.OsuBeatmapId?.ToString() ?? play.Title}|{play.ModsKey}")
                .Select(group => group.OrderByDescending(play => play.Id).First());
        }

        Attempts.Clear();
        Rows.Clear();
        foreach (AttemptSummary play in filtered.OrderByDescending(play => play.Id))
        {
            var row = new AttemptRowViewModel(play);
            Attempts.Add(row);
            Rows.Add(row);
        }
        SelectedAttempt = Attempts.FirstOrDefault(row => row.Id == selectedId) ?? Attempts.FirstOrDefault();
        HistoryStatus = Attempts.Count == 0 ? "No imported plays match the current filters" : $"{Attempts.Count} imported play(s)";
        OnPropertyChanged(nameof(ResultsText));
        OnPropertyChanged(nameof(ResultsShortText));
    }

    private void RebuildModFilters()
    {
        string[] acronyms = loaded
            .SelectMany(play => play.Mods.Select(mod => mod.Acronym))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        AvailableMods.Clear();
        foreach (string acronym in acronyms)
        {
            var option = new ModFilterOptionViewModel(acronym);
            option.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(ModFilterOptionViewModel.IsSelected))
                {
                    OnPropertyChanged(nameof(HasAvailableMods));
                    OnPropertyChanged(nameof(IsModFilterActive));
                    OnPropertyChanged(nameof(ModsFilterLabel));
                    ApplyFilter();
                }
            };
            AvailableMods.Add(option);
        }
        OnPropertyChanged(nameof(HasAvailableMods));
        OnPropertyChanged(nameof(IsModFilterActive));
        OnPropertyChanged(nameof(ModsFilterLabel));
    }
}
