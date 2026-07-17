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
    private Task RefreshAsync() => ReloadFirstPageAsync();

    [RelayCommand]
    private void ClearFilters()
    {
        SearchText = "";
        SelectedFilterMode = "All";
        IsGroupRepeats = false;
        _mapFilterKey = null;
        ApplyVisibleAttempts();
    }

    [RelayCommand]
    private void OpenHealthDashboard()
    {
        OpenInWorkspace(new HealthDashboardWindow(_appState, _settings), "Health dashboard");
    }

    [RelayCommand]
    private void OpenBackups() => OpenInWorkspace(new BackupWindow(_settings), "Backup & restore");

    [RelayCommand]
    private void OpenSetupWizard()
    {
        MainWindow.TryOpenOnboarding(new WelcomeWindow(_settings, _appState));
    }

    [RelayCommand]
    private void OpenSkinLibrary()
    {
        OpenInWorkspace(new SkinLibraryWindow(_settings), "Skin library");
    }

    [RelayCommand]
    private void OpenTosuSetup()
    {
        OpenInWorkspace(new TosuSetupWindow(), "tosu setup");
    }

    [RelayCommand]
    private void OpenTosuDiagnostics()
    {
        OpenInWorkspace(new TosuDiagnosticsWindow(_appState), "tosu diagnostics");
    }

    [RelayCommand]
    private void OpenDeveloperSettings()
    {
        OpenInWorkspace(new DeveloperSettingsWindow(_settings), "Developer settings");
    }

    [RelayCommand]
    private void OpenSettings()
    {
        OpenInWorkspace(new SettingsWindow(_settings, () => RefreshDashboardAsync()), "Settings");
    }

    [RelayCommand]
    private void OpenLazerFrameDebug()
    {
        OpenInWorkspace(new LazerFrameDebugWindow(_settings), "Lazer frame debug");
    }

    [RelayCommand]
    private void OpenStableFrameDebug()
    {
        OpenInWorkspace(new StableFrameDebugWindow(), "Stable frame debug");
    }

    [RelayCommand]
    private Task CheckForUpdatesAsync() => OpenApplicationUpdaterAsync();

    [RelayCommand]
    private void OpenChangelog()
    {
        OpenInWorkspace(new ChangelogWindow(), "Changelog");
    }

    [RelayCommand]
    private Task OpenAvailableUpdateAsync() => OpenApplicationUpdaterAsync();

    private async Task OpenApplicationUpdaterAsync()
    {
        Func<Task>? updateFlow = _checkForUpdates;
        if (updateFlow is null && Application.Current is App app)
            updateFlow = app.CheckForKumoriUpdatesManuallyAsync;
        if (updateFlow is not null)
        {
            await updateFlow();
            return;
        }

        OpenInWorkspace(new UpdateCheckWindow(), "Updates");
    }

    private void OpenInWorkspace(Window window, string title)
        => WorkspaceWindowRequested?.Invoke(window, title);

    [RelayCommand]
    private void OpenLogs()
    {
        Directory.CreateDirectory(AppPaths.LogDir);
        Process.Start(new ProcessStartInfo { FileName = AppPaths.LogDir, UseShellExecute = true });
    }

    [RelayCommand]
    private void OpenAppData()
    {
        Directory.CreateDirectory(AppPaths.ReportsDir);
        Process.Start(new ProcessStartInfo { FileName = AppPaths.AppDataDir, UseShellExecute = true });
    }

    [RelayCommand(CanExecute = nameof(CanLaunchTosu))]
    private async Task LaunchTosuAsync()
    {
        IsLaunchingTosu = true;
        try
        {
            HistoryStatus = "Checking tosu release...";
            await Task.Run(() =>
            {
                TosuManager.EnsureInstalledAndLaunchAsync().GetAwaiter().GetResult();
            });

            HistoryStatus = "Launched tosu; waiting for tracker connection";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to launch tosu");
            HistoryStatus = "Could not launch tosu (see logs)";
            KumoriDialog.Show(ActiveOwner(), ex.Message, "Kumori", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsLaunchingTosu = false;
        }
    }

    [RelayCommand]
    private async Task ExportProblemReportAsync()
    {
        Directory.CreateDirectory(AppPaths.AppDataDir);
        var includeDatabase = KumoriDialog.Show(
            ActiveOwner(),
            "Include the tracking database in the diagnostics zip?",
            "Kumori",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question) == MessageBoxResult.Yes;
        var report = Path.Combine(AppPaths.ReportsDir, $"problem-report-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.zip");
        var tempDir = Path.Combine(Path.GetTempPath(), $"kumori-report-{Guid.NewGuid():N}");
        var state = _store.Current;
        try
        {
            HistoryStatus = "Creating diagnostics...";
            await Task.Run(() =>
            {
                Directory.CreateDirectory(tempDir);
                File.WriteAllText(Path.Combine(tempDir, "summary.txt"),
                    $"""
                    Kumori problem report
                    Generated: {DateTimeOffset.Now:O}

                    Tracking: {state.Tracking.Health} / {state.Tracking.Detail}
                    Capture: {state.Capture.Health} / {state.Capture.Error}
                    Media: {state.Media.LastError}

                    AppData: {AppPaths.AppDataDir}
                    Tracking DB: {AppPaths.TrackingDatabase}
                    Writer: .NET real DB
                    Logs: {AppPaths.LogDir}

                    Contact: {SupportLinks.DiscordInviteUrl}
                    """);
                CopyIfExists(AppPaths.SettingsFile, Path.Combine(tempDir, "settings.v2.json"));
                CopyIfExists(AppPaths.LegacySettingsFile, Path.Combine(tempDir, "settings.legacy.json"));
                CopyIfExists(LazerReplayFrameDiagnostics.StatusPath, Path.Combine(tempDir, "lazer_replay_frame_status.json"));
                CopyIfExists(StableReplayFrameDiagnostics.StatusPath, Path.Combine(tempDir, "stable_replay_frame_status.json"));
                CopyDirectoryIfExists(AppPaths.LogDir, Path.Combine(tempDir, "logs"));
                if (includeDatabase)
                {
                    CopyIfExists(AppPaths.TrackingDatabase, Path.Combine(tempDir, "osu_tracking.sqlite3"));
                    CopyIfExists(AppPaths.TrackingDatabase + "-wal", Path.Combine(tempDir, "osu_tracking.sqlite3-wal"));
                    CopyIfExists(AppPaths.TrackingDatabase + "-shm", Path.Combine(tempDir, "osu_tracking.sqlite3-shm"));
                }
                if (File.Exists(report))
                {
                    File.Delete(report);
                }
                ZipFile.CreateFromDirectory(tempDir, report, CompressionLevel.Optimal, includeBaseDirectory: false);
            });
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                ArgumentList = { "/select,", report },
                UseShellExecute = false,
            });
            HistoryStatus = $"Diagnostics written to {report}";
            if (KumoriDialog.Confirm(ActiveOwner(), "Copy the support Discord invite to clipboard?", "Kumori", MessageBoxImage.Question))
            {
                Clipboard.SetText(SupportLinks.DiscordInviteUrl);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Problem report export failed");
            KumoriDialog.Show(ActiveOwner(), ex.Message, "Kumori", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [RelayCommand]
    private async Task EndSessionAsync()
    {
        var changed = _endLiveSession is null
            ? await Task.Run(() => _maintenance.EndOpenSessions()) > 0
            : await _endLiveSession();
        _store.Update(s => s.ActiveSession is null ? s : s with { ActiveSession = null });
        HistoryStatus = changed ? "Ended current session" : "No open sessions to end";
        await ReloadFirstPageAsync();
    }

    [RelayCommand]
    private async Task ClearBeatmapCacheAsync()
    {
        if (!KumoriDialog.Confirm(ActiveOwner(), "Clear cached beatmap artwork and media?", "Kumori", MessageBoxImage.Question))
        {
            return;
        }

        await Task.Run(() => _maintenance.ClearBeatmapCache());
        HistoryStatus = "Beatmap cache cleared";
    }

    [RelayCommand(CanExecute = nameof(CanMaintainTrackingData))]
    private async Task DeleteEntriesBeforeAsync()
    {
        var value = KumoriDialog.Input(
            ActiveOwner(),
            "Delete sessions before this date (dd/MM/yyyy):",
            "Delete Entries Before",
            DateTimeOffset.Now.Date.ToString("dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture));
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (!DateTime.TryParseExact(value.Trim(), "dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var before))
        {
            KumoriDialog.Show(ActiveOwner(), "Enter the date as dd/MM/yyyy.", "Invalid date", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!KumoriDialog.Confirm(ActiveOwner(), $"Delete sessions before {before:dd/MM/yyyy}?", "Kumori", MessageBoxImage.Warning))
        {
            return;
        }

        var cutoff = before.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var deleted = await Task.Run(() => _maintenance.DeleteBefore(cutoff));
        HistoryStatus = $"Deleted {deleted} old session(s)";
        await ReloadFirstPageAsync();
    }

    [RelayCommand(CanExecute = nameof(CanMaintainTrackingData))]
    private async Task DeleteAllTrackingDataAsync()
    {
        if (!KumoriDialog.Confirm(ActiveOwner(), "Delete all tracking data? This cannot be undone.", "Kumori", MessageBoxImage.Warning))
        {
            return;
        }

        var deleted = await Task.Run(() => _maintenance.DeleteAll());
        HistoryStatus = $"Deleted {deleted} session(s)";
        await ReloadFirstPageAsync();
    }

    [RelayCommand(CanExecute = nameof(CanMaintainTrackingData))]
    private async Task CleanupInvalidAttemptsAsync()
    {
        if (!KumoriDialog.Confirm(ActiveOwner(), "Cleanup invalid/empty finalized attempts and rebuild personal bests?", "Kumori", MessageBoxImage.Warning))
        {
            return;
        }
        var minimumSeconds = Math.Clamp(_settings.Current.Tracking.MinimumAttemptSeconds, 1, 300);
        var result = await Task.Run(() => _maintenance.CleanupInvalidAttempts(minimumSeconds));
        HistoryStatus = $"Cleanup removed {result.InvalidAttempts} attempt(s), {result.EmptySessions} empty session(s), reclassified {result.ReclassifiedCompleted}";
        await ReloadFirstPageAsync();
    }

    [RelayCommand(CanExecute = nameof(CanMaintainTrackingData))]
    private async Task DeleteShortPlaysAsync()
    {
        var defaultSeconds = Math.Clamp(_settings.Current.Tracking.MinimumAttemptSeconds, 1, 300)
            .ToString(System.Globalization.CultureInfo.InvariantCulture);
        var value = KumoriDialog.Input(
            ActiveOwner(),
            "Delete finalized plays shorter than this many seconds (1–300):",
            "Delete Short Plays",
            defaultSeconds);
        if (string.IsNullOrWhiteSpace(value))
            return;
        if (!int.TryParse(value.Trim(), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var seconds)
            || seconds is < 1 or > 300)
        {
            KumoriDialog.Show(ActiveOwner(), "Enter a whole number from 1 to 300.", "Invalid duration",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var count = await Task.Run(() => _maintenance.PreviewAttemptsShorterThan(seconds));
        if (count == 0)
        {
            KumoriDialog.Show(ActiveOwner(), $"No finalized plays are shorter than {seconds} seconds.",
                "Delete Short Plays", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!KumoriDialog.Confirm(
                ActiveOwner(),
                $"Permanently delete {count} finalized play(s) shorter than {seconds} seconds? Empty ended sessions will also be removed. This cannot be undone.",
                "Delete Short Plays",
                MessageBoxImage.Warning))
            return;

        var result = await Task.Run(() => _maintenance.DeleteAttemptsShorterThan(seconds));
        HistoryStatus = $"Deleted {result.Attempts} short play(s) and {result.EmptySessions} empty session(s)";
        await ReloadFirstPageAsync();
    }
}
