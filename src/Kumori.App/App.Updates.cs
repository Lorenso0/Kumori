using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Kumori.App.ViewModels;
using Kumori.Core;
using Kumori.Core.Settings;
using Kumori.Core.State;
using Kumori.Native;
using Kumori.Storage;
using Kumori.Tracking;
using Serilog;

namespace Kumori.App;

public partial class App
{
    private async Task RecoverHistoricalBeatmapsAsync(
        AttemptRepository attempts,
        SettingsService settings,
        CancellationToken cancellationToken)
    {
        // Startup recovery must not compete with dashboard hydration over an
        // entire long-lived history. The newest plays are the only ones likely
        // to be opened immediately; older records remain recoverable on demand.
        var pending = await Task.Run(() => HistoricalBeatmapCacheRecovery.GetPending(
            attempts.GetRecentAttempts(limit: 5_000)));
        cancellationToken.ThrowIfCancellationRequested();
        if (pending.Count == 0 || _mainWindow is null)
        {
            return;
        }

        var dialog = new BeatmapCacheRecoveryWindow(pending.Count);
        _mainWindow.OpenWorkspaceTab(dialog, "Beatmap recovery");
        try
        {
            await Task.Run(() => HistoricalBeatmapCacheRecovery.Run(
                pending,
                settings.Current.Media.PrimaryMirror,
                settings.Current.Media.FallbackMirrors,
                progress => Dispatcher.Invoke(() => dialog.Report(progress)),
                cancellationToken));
        }
        finally
        {
            dialog.Close();
        }
    }

    private async Task CheckForTosuUpdatesOnLaunchAsync(CancellationToken cancellationToken)
    {
        try
        {
            var wasInstalled = File.Exists(AppPaths.TosuExecutable);
            // EnsureInstalledAsync uses the manager's 24-hour update throttle,
            // while still installing tosu on a clean machine.
            var result = await TosuManager.EnsureInstalledAsync(cancellationToken: cancellationToken);
            if (result is { InstalledOrUpdated: true })
            {
                var action = wasInstalled ? "updated" : "installed";
                Log.Information("{Action} managed tosu to {Version} during Kumori startup", action, result.Version);
                await Dispatcher.InvokeAsync(() => KumoriDialog.Show(
                    _mainWindow,
                    $"tosu was {action} successfully.\n\nVersion: {result.Version}",
                    "tosu ready",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Managed tosu update check failed during Kumori startup");
        }
    }

    private async Task CheckForKumoriUpdatesOnLaunchAsync(AppStateStore store, CancellationToken cancellationToken)
    {
        try
        {
            var result = await new KumoriUpdateService().CheckAsync(cancellationToken: cancellationToken);
            _availableUpdate = result.IsUpdateAvailable ? result : null;
            UpdateApplicationUpdateStatus(store, result);
            if (result.IsUpdateAvailable)
            {
                _tray?.ShowUpdateNotification(result.LatestTag);
                Log.Information("Kumori update {Version} is available at {Url}", result.LatestTag, result.ReleaseUrl);
                QueueAvailableUpdatePrompt(result);
            }
            else
            {
                Log.Debug(
                    "Kumori startup update check completed; {Version} is current",
                    result.CurrentVersion.ToString(3));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // Startup update failures stay quiet; the manual update window shows errors on demand.
            Log.Debug(ex, "Kumori startup update check failed");
        }
    }

    internal static async Task RunKumoriStartupUpdateCheckAsync(
        Task startupPrerequisite,
        Func<CancellationToken, Task> check,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(startupPrerequisite);
        ArgumentNullException.ThrowIfNull(check);

        // The release lookup is a lightweight network request and must not wait
        // for tosu to report a fresh idle state. In particular, an already
        // running osu! client with unavailable telemetry would otherwise block
        // the automatic check forever. Only wait for the visible dashboard to
        // finish its initial hydration.
        await startupPrerequisite.WaitAsync(cancellationToken).ConfigureAwait(false);
        await check(cancellationToken).ConfigureAwait(false);
    }

    internal async Task CheckForKumoriUpdatesManuallyAsync()
    {
        if (_manualUpdateCheckRunning || _updatePromptOpen || _exitRequested)
        {
            return;
        }

        _manualUpdateCheckRunning = true;
        try
        {
            var result = _availableUpdate;
            if (result is null)
            {
                result = await new KumoriUpdateService().CheckAsync(cancellationToken: _backgroundCts.Token);
                _availableUpdate = result.IsUpdateAvailable ? result : null;
            }

            if (_store is { } store)
            {
                UpdateApplicationUpdateStatus(store, result);
            }

            if (!result.IsUpdateAvailable)
            {
                KumoriDialog.Show(
                    _mainWindow,
                    $"You are running the latest Kumori release.\n\nVersion: {result.CurrentVersion.ToString(3)}",
                    "Kumori is up to date",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            Log.Information("Manual Kumori update check found {Version} at {Url}", result.LatestTag, result.ReleaseUrl);
            ShowAvailableUpdatePrompt(result);
        }
        catch (OperationCanceledException) when (_backgroundCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Manual Kumori update check failed");
            KumoriDialog.Show(
                _mainWindow,
                $"Kumori could not check for updates.\n\n{ex.Message}",
                "Update check failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            _manualUpdateCheckRunning = false;
        }
    }

    private static void UpdateApplicationUpdateStatus(AppStateStore store, KumoriUpdateResult result)
    {
        store.Update(state => state with
        {
            ApplicationUpdate = new ApplicationUpdateStatus
            {
                IsAvailable = result.IsUpdateAvailable,
                Version = result.LatestTag,
                ReleaseUrl = result.ReleaseUrl,
                PublishedAt = result.PublishedAt,
            },
        });
    }

    private static void OpenAvailableUpdate(string? releaseUrl)
    {
        var url = string.IsNullOrWhiteSpace(releaseUrl) ? KumoriUpdateService.ReleasesUrl : releaseUrl;
        try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); }
        catch (Exception ex) { Log.Warning(ex, "Could not open Kumori release page {Url}", url); }
    }

    private void ShowMainWindow()
    {
        if (_mainWindow is null)
        {
            return;
        }
        if (_mainWindow.WindowState == WindowState.Minimized)
        {
            _mainWindow.WindowState = WindowState.Normal;
        }
        _mainWindow.Show();
        _mainWindow.Activate();
        ScheduleAvailableUpdatePrompt();
    }

    private void QueueAvailableUpdatePrompt(KumoriUpdateResult update)
    {
        if (string.Equals(_promptedUpdateVersion, update.LatestTag, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        _pendingUpdatePrompt = update;
        ScheduleAvailableUpdatePrompt();
    }

    private void ScheduleAvailableUpdatePrompt()
    {
        if (_pendingUpdatePrompt is null || _updatePromptOpen || _exitRequested)
        {
            return;
        }

        if (_gameplayWork is { IsGameplayActive: true } coordinator)
        {
            TrackBackground(
                coordinator.Enqueue(
                    "kumori-update-prompt",
                    token => Dispatcher.InvokeAsync(
                        () =>
                        {
                            token.ThrowIfCancellationRequested();
                            TryShowAvailableUpdatePrompt();
                        },
                        DispatcherPriority.ContextIdle).Task,
                    coalesce: true),
                "Kumori update prompt");
            return;
        }

        _ = Dispatcher.InvokeAsync(TryShowAvailableUpdatePrompt, DispatcherPriority.ContextIdle);
    }

    private void TryShowAvailableUpdatePrompt()
    {
        if (_pendingUpdatePrompt is not { } update ||
            _updatePromptOpen ||
            _exitRequested ||
            _mainWindow is not { IsVisible: true } owner ||
            owner.WindowState == WindowState.Minimized)
        {
            return;
        }

        if (_gameplayWork?.IsGameplayActive == true)
        {
            ScheduleAvailableUpdatePrompt();
            return;
        }

        ShowAvailableUpdatePrompt(update, owner);
    }

    private void ShowAvailableUpdatePrompt(KumoriUpdateResult update, Window? owner = null)
    {
        owner ??= _mainWindow;
        if (owner is null || _updatePromptOpen || _exitRequested)
        {
            return;
        }

        _pendingUpdatePrompt = null;
        _promptedUpdateVersion = update.LatestTag;
        _updatePromptOpen = true;
        try
        {
            var prompt = new UpdateAvailableWindow(update) { Owner = owner };
            prompt.ShowDialog();
            if (prompt.SelectedAction == UpdateAvailableAction.ViewRelease)
            {
                OpenAvailableUpdate(update.ReleaseUrl);
            }
            else if (prompt.SelectedAction == UpdateAvailableAction.Install && prompt.StagedUpdate is { } staged)
            {
                BeginUpdateShutdown(staged);
            }
        }
        finally
        {
            _updatePromptOpen = false;
        }
    }

    private void BeginUpdateShutdown(StagedKumoriUpdate update)
    {
        try
        {
            KumoriUpdateInstaller.LaunchUpdater(update);
        }
        catch (Exception ex)
        {
            KumoriUpdateInstaller.Discard(update);
            KumoriUpdateInstaller.CleanupStaleFiles();
            Log.Warning(ex, "Could not launch the Kumori updater");
            KumoriDialog.Show(
                _mainWindow,
                $"The update was downloaded and verified, but the updater could not start.\n\n{ex.Message}",
                "Update could not start",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        _exitRequested = true;
        if (_mainWindow is not null)
        {
            _mainWindow.ForceClose = true;
        }
        var statusWindow = new ShutdownStatusWindow();
        if (_mainWindow?.IsVisible == true)
        {
            statusWindow.Owner = _mainWindow;
            statusWindow.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        statusWindow.Show();
        statusWindow.UpdateStatus($"Installing Kumori {update.Version}...");
        Dispatcher.BeginInvoke(
            new Action(() => _ = ShutdownFromTrayAsync(statusWindow)),
            ShutdownStartPriority);
    }

    private void InstallCrashHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            LogCrash("DispatcherUnhandledException", args.Exception);
            // Unknown UI-thread failures may leave tracking or persistence state
            // partially mutated. Log them, then let WPF terminate safely.
            args.Handled = false;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LogCrash("AppDomain.UnhandledException", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogCrash("UnobservedTaskException", args.Exception);
            args.SetObserved();
        };
    }

    private static void LogCrash(string source, Exception? ex)
    {
        Log.Error(ex, "Crash ({Source})", source);
        try
        {
            LogRetentionPolicy.AppendWithSizeRotation(
                AppPaths.CrashLogFile,
                $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] {source}: {ex}\n\n",
                maxAgeDays: LogRetentionPolicy.ReadConfiguredDays());
        }
        catch
        {
            // never crash the crash handler
        }
    }

    private static void ConfigureFileLogging(int retentionDays)
    {
        Directory.CreateDirectory(AppPaths.AppLogDir);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Async(sink => sink.File(
                    Path.Combine(AppPaths.AppLogDir, "kumori-.log"),
                    rollingInterval: RollingInterval.Day,
                    fileSizeLimitBytes: AppPaths.MaxLogFileBytes,
                    rollOnFileSizeLimit: true,
                    retainedFileCountLimit: LogRetentionPolicy.NormalizeDays(retentionDays)),
                bufferSize: 4_096,
                blockWhenFull: false)
            .CreateLogger();
    }
}
