using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kumori.Core;
using Kumori.Core.Models;
using Kumori.Core.Settings;
using Kumori.Core.State;
using Kumori.Native;
using Kumori.Storage;
using Kumori.Tracking;
using Serilog;
using static System.FormattableString;

namespace Kumori.App.ViewModels;

public partial class MainViewModel
{
    [RelayCommand]
    private async Task LoadOlderAsync()
    {
        if (IsLoadingMore || _oldestLoadedId is null)
        {
            return;
        }
        IsLoadingMore = true;
        try
        {
            var before = _oldestLoadedId;
            var search = _activeSearch;
            var page = await Task.Run(() => _attempts.GetRecentAttempts(before, PageSize, search, mapKey: _mapFilterKey));
            if (search != _activeSearch)
            {
                return;
            }
            (_mapFilterKey is null ? _loadedAttempts : _mapAttempts).AddRange(page);
            _reachedEnd = page.Count < PageSize;
            OnPropertyChanged(nameof(LoadOlderVisible));
            ApplyVisibleAttempts(selectFirst: false);
            if (page.Count > 0)
            {
                _oldestLoadedId = page[^1].Id;
            }
            HistoryStatus = page.Count == 0
                ? "No additional older plays were found"
                : $"{Attempts.Count:N0} plays loaded";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Load older attempts failed");
            HistoryStatus = "Could not load older plays - try again";
        }
        finally
        {
            IsLoadingMore = false;
        }
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)] + "...";

    private static void ReplaceAttemptModel(List<AttemptSummary> attempts, AttemptSummary replacement)
    {
        var index = attempts.FindIndex(attempt => attempt.Id == replacement.Id);
        if (index >= 0)
        {
            attempts[index] = replacement;
        }
    }

    private void ApplyVisibleAttempts(bool selectFirst = true)
    {
        var previousSelectedId = SelectedAttempt?.Id;
        var source = _mapFilterKey is null ? _loadedAttempts : _mapAttempts;
        var filtered = FilterAttempts(source).ToArray();

        Attempts.Clear();
        Rows.Clear();
        var ordered = filtered.OrderByDescending(attempt => attempt.Id).ToArray();
        var attemptRows = new Dictionary<long, AttemptRowViewModel>();
        foreach (var model in ordered)
        {
            var row = new AttemptRowViewModel(model);
            Attempts.Add(row);
            attemptRows[model.Id] = row;
        }

        if (IsGroupSessions)
        {
            foreach (var group in ordered.GroupBy(attempt => attempt.SessionId))
            {
                var collapsed = _collapsedSessions.Contains(group.Key);
                if (_sessions.TryGetValue(group.Key, out var session))
                {
                    Rows.Add(new SessionRowViewModel(session, collapsed, _activeSessionId));
                }
                if (!collapsed)
                {
                    foreach (var model in group)
                    {
                        Rows.Add(attemptRows[model.Id]);
                    }
                }
            }
        }
        else
        {
            foreach (var model in ordered)
            {
                Rows.Add(attemptRows[model.Id]);
            }
        }

        SelectedAttempt = Attempts.FirstOrDefault(a => a.Id == previousSelectedId)
            ?? (selectFirst ? Attempts.FirstOrDefault() : SelectedAttempt);
        OnPropertyChanged(nameof(ResultsText));
        OnPropertyChanged(nameof(ResultsShortText));
        ApplyDashboard(_currentAnalytics, filtered, _dbBytes, _cacheBytes);
        HistoryStatus = filtered.Length == 0
            ? "No results match the current filters"
            : $"{filtered.Length} visible attempt(s)";
    }

    public void ToggleSession(SessionRowViewModel row)
    {
        if (!_collapsedSessions.Add(row.Model.Id))
        {
            _collapsedSessions.Remove(row.Model.Id);
        }
        ApplyVisibleAttempts(selectFirst: false);
    }

    private static string MapKey(AttemptSummary a) => a.OsuBeatmapId is > 0
        ? $"id:{a.OsuBeatmapId}"
        : !string.IsNullOrWhiteSpace(a.Checksum)
            ? $"hash:{a.Checksum.ToLowerInvariant()}"
            : $"meta:{a.Artist.ToLowerInvariant()}\u001f{a.Title.ToLowerInvariant()}\u001f{a.Difficulty.ToLowerInvariant()}\u001f{a.Mapper.ToLowerInvariant()}";

    /// <summary>Confirm + delete a single attempt, then reload.</summary>
    public async Task DeleteAttemptAsync(AttemptRowViewModel row)
    {
        if (!EnsureMaintenanceAvailable())
            return;
        if (!KumoriDialog.Confirm(ActiveOwner(), "Permanently delete this attempt?", "Delete attempt", MessageBoxImage.Warning))
        {
            return;
        }
        if (!await TryRunHistoryDeletionAsync(
                () => _maintenance.DeleteAttempt(row.Id),
                "attempt"))
        {
            return;
        }
        Inspector.ForgetAttempt(row.Id);
        if (SelectedAttempt?.Id == row.Id)
        {
            SelectedAttempt = null;
        }
        await ReloadFirstPageAsync();
    }

    /// <summary>Confirm + delete a whole session and its attempts, then reload.</summary>
    public async Task DeleteSessionAsync(long sessionId)
    {
        if (!EnsureMaintenanceAvailable())
            return;
        if (!KumoriDialog.Confirm(ActiveOwner(), "Permanently delete this session and all its attempts?", "Delete session", MessageBoxImage.Warning))
        {
            return;
        }
        if (!await TryRunHistoryDeletionAsync(
                () => _maintenance.DeleteSession(sessionId),
                "session"))
        {
            return;
        }
        if (SelectedAttempt?.Model.SessionId == sessionId)
        {
            Inspector.ForgetAttempt(SelectedAttempt.Id);
            SelectedAttempt = null;
        }
        await ReloadFirstPageAsync();
    }

    private async Task<bool> TryRunHistoryDeletionAsync(Func<int> delete, string itemName)
    {
        try
        {
            await Task.Run(delete);
            return true;
        }
        catch (InvalidOperationException ex)
        {
            // The in-memory session state can change after the menu is opened or
            // confirmation is shown. The repository is the final authority and
            // deliberately rejects the delete; keep that race non-fatal in WPF's
            // async menu-click handler.
            Log.Warning(ex, "Could not delete tracking history {ItemName}", itemName);
            HistoryStatus = $"Could not delete the {itemName} while tracking is active";
            KumoriDialog.Show(
                ActiveOwner(),
                $"The {itemName} was not deleted because a tracking session is active. End the current session and try again.",
                "Tracking is active",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not delete tracking history {ItemName}", itemName);
            HistoryStatus = $"Could not delete the {itemName} - see logs";
            KumoriDialog.Show(
                ActiveOwner(),
                $"Kumori could not delete the {itemName}. No tracking history was changed.",
                "Delete failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return false;
        }
    }

    private bool EnsureMaintenanceAvailable()
    {
        if (!HasActiveSession)
            return true;
        KumoriDialog.Show(
            ActiveOwner(),
            "Finish or end the active session before changing tracking history.",
            "Tracking is active",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
        return false;
    }

    /// <summary>Filter the list to every loaded attempt on the same beatmap.</summary>
    public async Task ShowAllPlaysForMapAsync(AttemptRowViewModel row)
    {
        _mapFilterKey = MapKey(row.Model);
        SelectedFilterMode = "All";
        _activeSearch = null;
        SearchText = "";
        await ReloadFirstPageAsync();
    }

    public async Task ShowAllPlaysForMapAsync(MapCardViewModel map)
    {
        _mapFilterKey = map.MapKey;
        SelectedFilterMode = "All";
        _activeSearch = null;
        SearchText = "";
        await ReloadFirstPageAsync();
    }

    public void ShowProblemPlaysForMap(AttemptRowViewModel row)
    {
        _mapFilterKey = MapKey(row.Model);
        SelectedFilterMode = "All";
        IsGroupRepeats = false;
        ApplyVisibleAttempts();
        foreach (var attempt in Attempts.Where(a => a.Model.Misses == 0 && a.Model.Progress >= 0.98 && !string.Equals(a.Model.Outcome, "failed", StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            Attempts.Remove(attempt);
            Rows.Remove(attempt);
        }
        HistoryStatus = $"{Attempts.Count} problem play(s) for this map";
    }

    public async Task OpenReplayInspectorAsync(AttemptRowViewModel row)
    {
        SelectedAttempt = row;
        var owner = ActiveOwner();
        if (!_settings.Current.ReplayViewer.Enabled)
        {
            KumoriDialog.Show(
                owner,
                "Replay Analyzer is disabled in Settings.",
                "Kumori",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }
        IsReplayAnalyzerLoading = true;
        ReplayAnalyzerLoadingText = "Loading replay details...";
        try
        {
            await Inspector.LoadAsync(row.Id);
            if (Inspector.OpenReplayInspectorCommand.CanExecute(null))
            {
                ReplayAnalyzerLoadingText = "Simulating replay judgements...";
                await Inspector.OpenReplayInspectorCommand.ExecuteAsync(null);
                if (!string.IsNullOrWhiteSpace(Inspector.LoadError))
                {
                    HistoryStatus = "Could not open Replay Analyzer";
                    return;
                }

                if (Inspector.TakeLastReplayInspectorProcess() is { } process && owner is not null)
                {
                    ReplayAnalyzerLoadingText = "Opening replay analyzer...";
                    _ = await ReplayAnalyzerWindowPlacement.CenterNearOwnerAsync(process, owner);
                }

                HistoryStatus = "Replay Analyzer opened";
            }
            else
            {
                KumoriDialog.Show(owner, "Replay Analyzer needs movement capture and cached beatmap media for this play.",
                    "Kumori", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Replay Analyzer launch failed for attempt {AttemptId}", row.Id);
            HistoryStatus = "Could not open Replay Analyzer";
            KumoriDialog.Show(owner, ex.Message, "Kumori", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsReplayAnalyzerLoading = false;
            ReplayAnalyzerLoadingText = "";
        }
    }

    [RelayCommand]
    private Task OpenSelectedReplayInspector()
    {
        if (SelectedAttempt is { } row)
        {
            return OpenReplayInspectorAsync(row);
        }
        return Task.CompletedTask;
    }

    private IEnumerable<AttemptSummary> FilterAttempts(IEnumerable<AttemptSummary> attempts)
    {
        if (_mapFilterKey is { } mapKey)
        {
            attempts = attempts.Where(a => MapKey(a) == mapKey);
        }
        var mode = SelectedFilterMode;
        var filtered = attempts.Where(a => mode switch
        {
            "All" => true,
            _ => string.Equals(a.Outcome, mode, StringComparison.OrdinalIgnoreCase),
        });

        var selectedMods = AvailableMods
            .Where(mod => mod.IsSelected)
            .Select(mod => mod.Acronym)
            .ToArray();
        if (selectedMods.Length > 0)
        {
            var exact = SelectedModFilterMode == "Exact";
            filtered = filtered.Where(attempt => ModFilterMatcher.Matches(attempt, selectedMods, exact));
        }

        if (IsGroupRepeats)
        {
            filtered = filtered
                .GroupBy(a => $"{a.Checksum ?? a.OsuBeatmapId?.ToString() ?? a.Title}|{a.ModsKey}")
                .Select(g => g.OrderByDescending(a => a.Id).First())
                .OrderByDescending(a => a.Id);
        }

        return filtered;
    }

    private static Window? ActiveOwner() =>
        Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
        ?? Application.Current?.MainWindow;

    private static void CopyIfExists(string source, string destination)
    {
        try
        {
            if (File.Exists(source))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not copy diagnostics file {Path}", source);
        }
    }

    private static void CopyDirectoryIfExists(string source, string destination)
    {
        if (!Directory.Exists(source))
        {
            return;
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            CopyIfExists(file, Path.Combine(destination, relative));
        }
    }

}
