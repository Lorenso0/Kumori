using System.IO;
using System.Diagnostics;
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

public partial class App : Application
{
    public ThemeManager? Themes { get; private set; }
    // osu!lazer exposes its managed object graph shortly after the process is
    // visible. Starting tosu immediately can race that initialization, leaving
    // it with a temporary GameBase resolution failure and zero telemetry.
    // Only attach to a confirmed osu! session. This prevents tosu from racing
    // osu!lazer's startup or attaching to a client that closes immediately.
    private static readonly TimeSpan TosuStartupGracePeriod = TimeSpan.FromSeconds(5);
    // Companion activation should feel immediate when osu! is launched, while
    // still avoiding a busy process-polling loop.
    private static readonly TimeSpan CompanionMonitorInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan IdleCompanionMonitorInterval = TimeSpan.FromSeconds(2);
    private SingleInstance? _singleInstance;
    private TrayIconService? _tray;
    private MainWindow? _mainWindow;
    private TosuTrackingService? _tracking;
    private LazerReplayFrameCaptureService? _lazerReplayFrames;
    private LazerReplayFrameCaptureService? _stableReplayFrames;
    private bool _lazerReplayCaptureStarted;
    private bool _stableReplayCaptureStarted;
    private AppStateStore? _store;
    private CancellationTokenSource? _companionMonitorCts;
    private Task? _companionMonitorTask;
    private bool _otdAutoLaunchAttemptedForOsu;
    private bool _dualModeActivatedForOsu;
    private bool _companionRestartInProgress;
    private bool _managedOsuSessionActive;
    private bool _tosuStartedForOsu;
    private bool _hasObservedOsuProcessState;
    private bool _osuWasRunning;
    private bool? _trayDualModeToggleEnabled;
    private bool _exitRequested;
    private bool _shutdownCleanupCompleted;
    private KumoriUpdateResult? _pendingUpdatePrompt;
    private string? _promptedUpdateVersion;
    private bool _updatePromptOpen;
    private readonly object _osuCompanionGate = new();
    private readonly CancellationTokenSource _backgroundCts = new();
    private readonly object _backgroundGate = new();
    private readonly HashSet<Task> _backgroundTasks = new();

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (KumoriUpdateInstaller.TryRunUpdater(e.Args))
        {
            Shutdown();
            return;
        }
        KumoriUpdateInstaller.CleanupStaleFiles();

        _singleInstance = new SingleInstance();
        if (!_singleInstance.IsPrimaryInstance)
        {
            _singleInstance.SignalPrimaryInstance();
            _singleInstance.Dispose();
            Shutdown();
            return;
        }

        AppDataOrganizer.Organize();

        ConfigureFileLogging(AppPaths.DefaultLogRetentionDays);
        Log.Information("Kumori starting");

        InstallCrashHandlers();

        try
        {
            BackupService.ApplyPendingRestore();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Staged backup restore failed");
            MessageBox.Show(
                $"Kumori could not restore the staged backup. Your existing data was preserved.\n\n{ex.Message}",
                "Kumori",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown();
            return;
        }

        var settings = new SettingsService();
        settings.Load();
        var logRetentionDays = LogRetentionPolicy.NormalizeDays(settings.Current.Developer.LogRetentionDays);
        CacheActivityLog.ConfigureRotationDays(logRetentionDays);
        AppDataOrganizer.PruneLogs(retentionDays: logRetentionDays);
        Log.CloseAndFlush();
        ConfigureFileLogging(logRetentionDays);
        SkinLibraryService.EnsureValidSelection(settings);
        Themes = new ThemeManager(settings);
        Themes.ApplyCurrent();
        SyncStartupRegistration(settings.Current);

        var store = new AppStateStore();
        _store = store;
        var factory = new SqliteConnectionFactory(AppPaths.TrackingDatabase, readOnly: false);
        var attempts = new AttemptRepository(factory);
        var details = new AttemptDetailsRepository(factory);
        var analytics = new AnalyticsRepository(factory);
        var movement = new MovementRepository(factory);
        var replayViewer = new ReplayViewerContractService(details, movement, () => settings.Current);
        var maintenance = new TrackingMaintenanceRepository(factory);
        var sessions = new SessionRepository(factory);
        var viewModel = new MainViewModel(store, attempts, details, analytics, settings, replayViewer, maintenance, sessions);

        // Shell first — no data work before first paint.
        _mainWindow = new MainWindow(viewModel, settings);
        _mainWindow.StateChanged += (_, _) => ScheduleAvailableUpdatePrompt();
        _mainWindow.IsVisibleChanged += (_, _) => ScheduleAvailableUpdatePrompt();
        _mainWindow.Show();
        await Dispatcher.Yield(DispatcherPriority.Loaded);
        TrackBackground(Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(3), _backgroundCts.Token);
            KumoriUpdateInstaller.CleanupStaleFiles();
        }, _backgroundCts.Token), "stale updater cleanup");
        if (KumoriUpdateInstaller.ConsumeFailure() is { } updateFailure)
        {
            _ = Dispatcher.InvokeAsync(() => KumoriDialog.Show(
                _mainWindow,
                updateFailure,
                "Update was not installed",
                MessageBoxButton.OK,
                MessageBoxImage.Error), DispatcherPriority.ContextIdle);
        }
        // Establish and migrate the application-owned schema after first paint,
        // even when live tracking is disabled, so application-owned schema
        // migrations are always applied consistently.
        var trackingSink = new AttemptSqliteSink(factory);
        TrackBackground(Task.Run(() => new BackupService().CreateAutomaticIfDue(settings.Current.Backup), _backgroundCts.Token), "automatic backup");
        TrackBackground(RecoverHistoricalBeatmapsAsync(attempts, settings, _backgroundCts.Token), "historical beatmap recovery");
        if (!settings.Current.FirstRunCompleted ||
            settings.Current.OnboardingVersion < WelcomeWindow.CurrentOnboardingVersion)
        {
            _ = Dispatcher.InvokeAsync(() =>
            {
                _mainWindow.OpenOnboarding(new WelcomeWindow(settings, store));
            }, DispatcherPriority.ContextIdle);
        }
        if (e.Args.Any(argument => string.Equals(argument, "--show-changelog", StringComparison.Ordinal)))
        {
            _ = Dispatcher.InvokeAsync(
                () => _mainWindow.OpenWorkspaceTab(new ChangelogWindow(), "Changelog"),
                DispatcherPriority.ContextIdle);
        }

        _tray = new TrayIconService(
            "Kumori — osu! Tracking",
            Path.Combine(AppContext.BaseDirectory, "assets", "logo.ico"));
        _tray.OpenRequested += ShowMainWindow;
        _tray.SettingsRequested += () => Dispatcher.InvokeAsync(() =>
        {
            ShowMainWindow();
            viewModel.OpenSettingsCommand.Execute(null);
        });
        _tray.LogsRequested += () => Dispatcher.InvokeAsync(() =>
            Process.Start(new ProcessStartInfo { FileName = AppPaths.LogDir, UseShellExecute = true }));
        _tray.EndSessionRequested += () => Dispatcher.InvokeAsync(() =>
            viewModel.EndSessionCommand.Execute(null));
        _tray.RestoreDualModeRequested += () => Dispatcher.InvokeAsync(RestoreDualModeAfterOsuClosed);
        _tray.KeepDualModeRequested += () => Dispatcher.InvokeAsync(() =>
            PublishCompanionStatus(c => c with { DualModeDetail = "Dual mode left active after osu! closed" }));
        _tray.DualModeToggleRequested += () =>
            TrackBackground(ToggleDualModeFromTrayAsync(), "tray dual-mode toggle");
        _tray.UpdateRequested += () => Dispatcher.InvokeAsync(() => OpenAvailableUpdate(store.Current.ApplicationUpdate.ReleaseUrl));
        UpdateTrayDualModeToggle(settings.Current.Display.AutoSwitchDualMode);
        _tray.ExitRequested += () =>
        {
            if (_exitRequested)
            {
                return;
            }
            _exitRequested = true;
            _tray.UpdateStatus("Kumori is exiting...");
            _tray.ShowNotification("Kumori is exiting", "Finishing capture and closing companion services.");
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

            // Let WPF render once, then keep the dispatcher alive while the
            // bounded cleanup runs so progress remains visible.
            Dispatcher.BeginInvoke(
                new Action(() => _ = ShutdownFromTrayAsync(statusWindow)),
                DispatcherPriority.ContextIdle);
        };

        _singleInstance.ListenForActivation(
            () => Dispatcher.InvokeAsync(ShowMainWindow));
        store.StateChanged += state =>
        {
            var status = state.Tracking.TosuConnected
                ? state.Tracking.CurrentBeatmap ?? "Tracker connected"
                : state.Tracking.Detail ?? "Tracker not running";
            Dispatcher.InvokeAsync(() =>
            {
                _tray?.UpdateStatus(status);
                _tray?.SetEndSessionEnabled(state.ActiveSession is not null);
            });
        };
        _companionMonitorCts = new CancellationTokenSource();
        _companionMonitorTask = Task.Run(() => CompanionMonitorLoopAsync(store, settings, _companionMonitorCts.Token));
        _ = Dispatcher.InvokeAsync(
            () => TrackBackground(
                CheckForTosuUpdatesOnLaunchAsync(_backgroundCts.Token),
                "tosu startup update check"),
            DispatcherPriority.ApplicationIdle);
        _ = Dispatcher.InvokeAsync(
            () => TrackBackground(
                CheckForKumoriUpdatesOnLaunchAsync(store, _backgroundCts.Token),
                "Kumori update check"),
            DispatcherPriority.ApplicationIdle);

        // Background services start only after the shell is visible
        // (no-flicker startup plan: shell first, services second).
        if (settings.Current.Tracking.Enabled)
        {
            var profileTelemetry = new ProfileTelemetryStore(factory);
            IReplayPlaybackDetector replayPlaybackDetector = new OsuReplayPlaybackDetector();
            profileTelemetry.ProfileUpdated += () => _ = Dispatcher.InvokeAsync(
                () => viewModel.RefreshDashboardAsync());
            IAttemptSink attemptSink = new StatePublishingAttemptSink(
                trackingSink,
                () => trackingSink.CurrentAttemptId,
                HasReplayData,
                store);
            void OnReplayResultRecovered(ReplayResultRecoveryContext recovery)
            {
                TrackBackground(
                    CompleteReplayResultRecoveryAsync(recovery),
                    $"replay simulation recovery for attempt {recovery.AttemptId}");
            }
            async Task CompleteReplayResultRecoveryAsync(ReplayResultRecoveryContext recovery)
            {
                if (!recovery.RequiresSimulation)
                {
                    await Dispatcher.InvokeAsync(async () =>
                    {
                        await viewModel.RefreshDashboardAsync();
                        await viewModel.Inspector.RefreshAfterMovementReplacementAsync(recovery.AttemptId);
                    }).Task.Unwrap();
                    return;
                }

                // Start the companion restart immediately while the headless
                // ruleset simulation runs independently at accelerated speed.
                Task restart = TosuManager.RestartAsync(_backgroundCts.Token);
                try
                {
                    var simulation = await replayViewer.SimulateRecoveryAsync(
                        recovery.AttemptId,
                        recovery.ReplayPath,
                        recovery.BeatmapPath,
                        recovery.MediaDirectory,
                        recovery.MediaPaths,
                        recovery.Samples,
                        _backgroundCts.Token);
                    new ReplayResultRecoveryStore(factory).ApplySimulation(recovery.AttemptId, simulation);
                }
                catch (OperationCanceledException) when (_backgroundCts.IsCancellationRequested)
                {
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Replay result simulation failed for attempt {AttemptId}; header recovery was retained", recovery.AttemptId);
                }
                finally
                {
                    await Dispatcher.InvokeAsync(async () =>
                    {
                        await viewModel.RefreshDashboardAsync();
                        await viewModel.Inspector.RefreshAfterMovementReplacementAsync(recovery.AttemptId);
                    }).Task.Unwrap();
                    await restart;
                }
            }
            if (settings.Current.Capture.LazerReplayFrameEnabled)
            {
                var lazerFrameSource = new LazerMemoryReplayFrameSource();
                replayPlaybackDetector = new OsuReplayPlaybackDetector(lazerFrameSource);
                _lazerReplayFrames = new LazerReplayFrameCaptureService(
                    store,
                    factory,
                    () => trackingSink.CurrentAttemptId,
                    lazerFrameSource,
                    sourceName: "lazer_memory");
                var stableCaptureStatus = new StableCaptureStatusSink();
                _stableReplayFrames = new LazerReplayFrameCaptureService(
                    store,
                    factory,
                    () => trackingSink.CurrentAttemptId,
                    new StableLiveReplayFrameSource(status: stableCaptureStatus),
                    stableCaptureStatus,
                    sourceName: "stable_memory",
                    clientKind: OsuClientKind.Stable);
                attemptSink = new CompositeAttemptSink(
                    new StatePublishingAttemptSink(
                        trackingSink,
                        () => trackingSink.CurrentAttemptId,
                        HasReplayData,
                        store),
                    new BestEffortAttemptSink(
                        new LazerReplayFrameRecoverySink(
                            factory,
                            () => trackingSink.CurrentAttemptId,
                            id => Dispatcher.InvokeAsync(() => viewModel.Inspector.RefreshAfterMovementReplacementAsync(id)),
                            OnReplayResultRecovered,
                            recoverMovement: true,
                            cancellationToken: _backgroundCts.Token),
                        "lazer Realm replay-frame recovery"),
                    new BestEffortAttemptSink(_lazerReplayFrames, "lazer replay-frame capture"),
                    new BestEffortAttemptSink(
                        new StableReplayFrameRecoverySink(
                            factory,
                            () => trackingSink.CurrentAttemptId,
                            id => Dispatcher.InvokeAsync(() => viewModel.Inspector.RefreshAfterMovementReplacementAsync(id)),
                            OnReplayResultRecovered,
                            recoverMovement: true,
                            cancellationToken: _backgroundCts.Token),
                        "stable replay-frame recovery"),
                    // Composite sinks finalize in reverse order. Store the live
                    // buffer first, then let an exact Data/r replay replace it.
                    new BestEffortAttemptSink(_stableReplayFrames, "stable live replay-frame capture"));
            }
            else
            {
                LazerReplayFrameDiagnostics.Update(s =>
                {
                    s.Enabled = false;
                    s.State = "disabled";
                    s.Detail = "Lazer replay-frame capture is disabled in Kumori settings.";
                    s.ActiveAttemptId = null;
                    s.FramesBufferedForAttempt = 0;
                    s.LastError = null;
                });
                store.Update(s => s with
                {
                    Capture = s.Capture with
                    {
                        Running = false,
                        Health = HealthLevel.Unknown,
                        Source = "lazer_memory",
                        Error = "Disabled in settings",
                    },
                });
                // Result recovery is independent of cursor capture. Even with
                // movement recording disabled, a persisted replay can repair
                // gameplay values omitted by tosu.
                attemptSink = new CompositeAttemptSink(
                    attemptSink,
                    new BestEffortAttemptSink(
                        new LazerReplayFrameRecoverySink(
                            factory,
                            () => trackingSink.CurrentAttemptId,
                            resultRecovered: OnReplayResultRecovered,
                            recoverMovement: false,
                            cancellationToken: _backgroundCts.Token),
                        "lazer replay result recovery"),
                    new BestEffortAttemptSink(
                        new StableReplayFrameRecoverySink(
                            factory,
                            () => trackingSink.CurrentAttemptId,
                            resultRecovered: OnReplayResultRecovered,
                            recoverMovement: false,
                            cancellationToken: _backgroundCts.Token),
                        "stable replay result recovery"));
            }
            attemptSink = new ProfileAwareAttemptSink(
                attemptSink,
                profileTelemetry,
                () => trackingSink.CurrentAttemptId);
            attemptSink = new ReplayRecoveryTestAttemptSink(attemptSink, settings);
            _tracking = new TosuTrackingService(
                store,
                attemptTracker: new AttemptTracker(attemptSink),
                sessionTracker: new SessionTracker(new StatePublishingSessionSink(trackingSink, store)),
                profileTelemetry: profileTelemetry,
                primaryMediaMirror: settings.Current.Media.PrimaryMirror,
                fallbackMediaMirrors: settings.Current.Media.FallbackMirrors,
                recordPackets: settings.Current.Tracking.PacketRecordingEnabled,
                replayPlaybackDetector: replayPlaybackDetector);
            _tracking.ClientKindObserved += StartReplayCaptureFor;
            _tracking.Start();
            if (settings.Current.Capture.LazerReplayFrameEnabled)
            {
                TrackBackground(Task.Run(() => new PersistedReplayReconciliationService(
                    factory,
                    id => Dispatcher.InvokeAsync(() => viewModel.Inspector.RefreshAfterMovementReplacementAsync(id)),
                    OnReplayResultRecovered).Run(_backgroundCts.Token),
                    _backgroundCts.Token), "persisted replay reconciliation");
            }
            viewModel.SetEndLiveSessionHandler(() => Task.Run(() => _tracking?.EndSession() ?? false));

            bool HasReplayData(long attemptId) =>
                movement.GetMetadata(attemptId) is { SampleCount: > 0 };
        }

        // Hydrate asynchronously after the shell is visible.
        _ = viewModel.HydrateAsync();
    }

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
                KumoriDialog.Show(
                    _mainWindow,
                    $"tosu was {action} successfully.\n\nVersion: {result.Version}",
                    "tosu ready",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
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
            if (result.IsUpdateAvailable)
            {
                _tray?.ShowUpdateNotification(result.LatestTag);
                Log.Information("Kumori update {Version} is available at {Url}", result.LatestTag, result.ReleaseUrl);
                QueueAvailableUpdatePrompt(result);
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
            DispatcherPriority.ContextIdle);
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
            .WriteTo.File(
                Path.Combine(AppPaths.AppLogDir, "kumori-.log"),
                rollingInterval: RollingInterval.Day,
                fileSizeLimitBytes: AppPaths.MaxLogFileBytes,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: LogRetentionPolicy.NormalizeDays(retentionDays))
            .CreateLogger();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (!_shutdownCleanupCompleted)
        {
            CleanupSynchronously();
        }
        _tray?.Dispose();
        _singleInstance?.Dispose();
        _backgroundCts.Dispose();
        Log.Information("Kumori exiting");
        Log.CloseAndFlush();
        base.OnExit(e);
    }

    private async Task ShutdownFromTrayAsync(ShutdownStatusWindow statusWindow)
    {
        try
        {
            statusWindow.UpdateStatus("Stopping live tracking...");
            if (_tracking is not null)
            {
                await AwaitBoundedAsync(_tracking.DisposeAsync().AsTask(), TimeSpan.FromSeconds(3));
            }

            statusWindow.UpdateStatus("Finishing replay capture...");
            if (_lazerReplayFrames is not null)
            {
                await AwaitBoundedAsync(_lazerReplayFrames.DisposeAsync().AsTask(), TimeSpan.FromSeconds(3));
            }
            if (_stableReplayFrames is not null)
            {
                await AwaitBoundedAsync(_stableReplayFrames.DisposeAsync().AsTask(), TimeSpan.FromSeconds(3));
            }

            statusWindow.UpdateStatus("Closing companion services...");
            _backgroundCts.Cancel();
            Task[] backgroundTasks;
            lock (_backgroundGate)
            {
                backgroundTasks = _backgroundTasks.ToArray();
            }
            if (backgroundTasks.Length > 0)
            {
                await AwaitBoundedAsync(Task.WhenAll(backgroundTasks), TimeSpan.FromSeconds(3));
            }
            _companionMonitorCts?.Cancel();
            if (_companionMonitorTask is not null)
            {
                await AwaitBoundedAsync(_companionMonitorTask, TimeSpan.FromSeconds(2));
            }
            _companionMonitorCts?.Dispose();

            statusWindow.UpdateStatus("Restoring display and closing helpers...");
            DeactivateDualModeIfRequested();
            OpenTabletDriverService.CloseOwned();
            TosuManager.CloseOwned();
            _shutdownCleanupCompleted = true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Visible shutdown cleanup failed");
            _shutdownCleanupCompleted = true;
        }
        finally
        {
            Shutdown();
        }
    }

    private static async Task AwaitBoundedAsync(Task task, TimeSpan timeout)
    {
        try
        {
            await task.WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            // Shutdown remains bounded if a socket/process does not respond.
        }
        catch
        {
            // Individual service cleanup must not prevent application exit.
        }
    }

    private void CleanupSynchronously()
    {
        if (_tracking is not null)
        {
            try { _tracking.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3)); } catch { }
        }
        if (_lazerReplayFrames is not null)
        {
            try { _lazerReplayFrames.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3)); } catch { }
        }
        if (_stableReplayFrames is not null)
        {
            try { _stableReplayFrames.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3)); } catch { }
        }
        _companionMonitorCts?.Cancel();
        _backgroundCts.Cancel();
        Task[] backgroundTasks;
        lock (_backgroundGate)
        {
            backgroundTasks = _backgroundTasks.ToArray();
        }
        try { Task.WhenAll(backgroundTasks).Wait(TimeSpan.FromSeconds(3)); } catch { }
        try { _companionMonitorTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _companionMonitorCts?.Dispose();
        DeactivateDualModeIfRequested();
        OpenTabletDriverService.CloseOwned();
        TosuManager.CloseOwned();
        _shutdownCleanupCompleted = true;
    }

    private void TrackBackground(Task task, string operation)
    {
        lock (_backgroundGate)
        {
            _backgroundTasks.Add(task);
        }
        _ = task.ContinueWith(completed =>
        {
            lock (_backgroundGate)
            {
                _backgroundTasks.Remove(completed);
            }
            if (completed.IsFaulted && completed.Exception is { } exception)
            {
                Log.Warning(exception.GetBaseException(), "Background operation {Operation} failed", operation);
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private static void SyncStartupRegistration(KumoriSettings settings)
    {
        try
        {
            if (StartupRegistration.IsEnabled() != settings.Startup.RunAtLogin)
            {
                StartupRegistration.SetEnabled(settings.Startup.RunAtLogin);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Startup registration sync failed");
        }
    }

    private void TryActivateOsuCompanions(KumoriSettings settings)
    {
        if (settings.Display.AutoSwitchDualMode)
        {
            if (settings.Display.SuspendOsuDuringDualModeSwitch)
            {
                SwitchDualModeWhileOsuSuspended(settings);
            }
            else
            {
                RestartOsuWithCompanions(settings);
            }
            return;
        }

        lock (_osuCompanionGate)
        {
            LaunchOpenTabletDriverIfRequested(settings);
            ActivateDualModeIfRequested(settings);
        }
    }

    private void StartReplayCaptureFor(OsuClientKind clientKind)
    {
        lock (_osuCompanionGate)
        {
            if (clientKind == OsuClientKind.Lazer && !_lazerReplayCaptureStarted && _lazerReplayFrames is not null)
            {
                _lazerReplayCaptureStarted = true;
                _lazerReplayFrames.Start();
                Log.Information("Started lazer replay capture after client detection");
            }
            else if (clientKind == OsuClientKind.Stable && !_stableReplayCaptureStarted && _stableReplayFrames is not null)
            {
                _stableReplayCaptureStarted = true;
                _stableReplayFrames.Start();
                Log.Information("Started stable replay capture after client detection");
            }
        }
    }

    private void SwitchDualModeWhileOsuSuspended(KumoriSettings settings)
    {
        TrackBackground(Task.Run(() =>
        {
            using var suspension = OsuProcessDetector.TrySuspendRunning();
            if (suspension is null)
            {
                Log.Warning("Could not suspend osu! for dual-mode switching; leaving the running client untouched");
                PublishCompanionStatus(c => c with { DualModeDetail = "Could not suspend osu! for dual-mode switching" });
                return;
            }

            try
            {
                Log.Information("osu! suspended while dual mode is switched");
                lock (_osuCompanionGate)
                {
                    ActivateDualModeIfRequested(settings);
                    LaunchOpenTabletDriverIfRequested(settings);
                    _managedOsuSessionActive = true;
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not switch dual mode while osu! was suspended");
            }
            // Disposing resumes osu! even if the DDC operation or OTD launch fails.
        }, _backgroundCts.Token), "suspend osu and switch dual mode");
    }

    private void RestartOsuWithCompanions(KumoriSettings settings)
    {
        lock (_osuCompanionGate)
        {
            if (_companionRestartInProgress || _managedOsuSessionActive)
            {
                return;
            }

            _companionRestartInProgress = true;
        }

        TrackBackground(Task.Run(() =>
        {
            try
            {
                Log.Information("Preparing osu! companion session: stopping osu! before switching display mode");
                // tosu keeps raw pointers into osu!lazer. It must not survive
                // the display-mode restart and attach those stale pointers to
                // the replacement process.
                Log.Information("Stopping managed tosu before restarting osu! for the display-mode switch");
                TosuManager.CloseOwned();
                lock (_osuCompanionGate)
                {
                    _tosuStartedForOsu = false;
                }

                var launchPaths = OsuProcessDetector.StopAndCaptureLaunchPaths();
                if (launchPaths.Count == 0)
                {
                    Log.Warning("Could not determine the running osu! executable path; companion restart was skipped");
                    return;
                }

                lock (_osuCompanionGate)
                {
                    // osu! is stopped here so the LG mode change happens before OTD
                    // and before the client is opened again.
                    ActivateDualModeIfRequested(settings);
                    LaunchOpenTabletDriverIfRequested(settings);
                }

                OsuProcessDetector.Launch(launchPaths);
                lock (_osuCompanionGate)
                {
                    _managedOsuSessionActive = true;
                }
                Log.Information("osu! relaunched after display and OpenTabletDriver preparation");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not prepare the osu! companion session");
            }
            finally
            {
                lock (_osuCompanionGate)
                {
                    _companionRestartInProgress = false;
                }

                // Do not wait for the three-second monitor tick: after a
                // successful relaunch this schedules a fresh tosu instance
                // (including its startup grace) against the new osu process.
                if (_store is not null && OsuProcessDetector.IsRunning())
                {
                    EnsureTosuForOsu(_store);
                }
            }
        }, _backgroundCts.Token), "restart osu with companion services");
    }

    private void EnsureTosuForOsu(AppStateStore store)
    {
        lock (_osuCompanionGate)
        {
            if (_companionRestartInProgress || _tosuStartedForOsu)
            {
                return;
            }

            _tosuStartedForOsu = true;
        }

        TrackBackground(Task.Run(async () =>
        {
            try
            {
                var osuProcessIdsAtDetection = OsuProcessDetector.RunningProcessIds();
                if (osuProcessIdsAtDetection.Count == 0)
                {
                    lock (_osuCompanionGate)
                    {
                        _tosuStartedForOsu = false;
                    }
                    return;
                }

                Log.Information("Waiting {DelaySeconds:0}s for osu!lazer memory to initialize before launching tosu", TosuStartupGracePeriod.TotalSeconds);
                await Task.Delay(TosuStartupGracePeriod, _backgroundCts.Token);
                var osuProcessIdsAfterGrace = OsuProcessDetector.RunningProcessIds();
                if (!osuProcessIdsAtDetection.Overlaps(osuProcessIdsAfterGrace))
                {
                    lock (_osuCompanionGate)
                    {
                        _tosuStartedForOsu = false;
                    }
                    Log.Information("osu! did not remain alive for the full tosu confirmation window; tosu launch was skipped");
                    // osu! may have restarted so quickly that the 250 ms
                    // monitor did not observe the brief stopped state. Retry
                    // against the replacement process, with a fresh five-
                    // second confirmation window.
                    if (osuProcessIdsAfterGrace.Count > 0)
                    {
                        EnsureTosuForOsu(store);
                    }
                    return;
                }

                await TosuManager.EnsureInstalledAndLaunchAsync(cancellationToken: _backgroundCts.Token);
                if (!OsuProcessDetector.IsRunning())
                {
                    TosuManager.CloseOwned();
                }
            }
            catch (OperationCanceledException) when (_backgroundCts.IsCancellationRequested)
            {
                lock (_osuCompanionGate)
                {
                    _tosuStartedForOsu = false;
                }
            }
            catch (Exception ex)
            {
                lock (_osuCompanionGate)
                {
                    _tosuStartedForOsu = false;
                }
                Log.Warning(ex, "Managed tosu install/launch failed");
                store.Update(s => s with
                {
                    Tracking = s.Tracking with
                    {
                        Health = HealthLevel.Degraded,
                        Detail = $"tosu setup failed: {ex.Message}",
                    },
                });
            }
        }, _backgroundCts.Token), "managed tosu startup");
    }

    private void EndOsuCompanionSession()
    {
        var promptToRestoreDualMode = false;
        lock (_osuCompanionGate)
        {
            if (_companionRestartInProgress ||
                (!_managedOsuSessionActive && !_tosuStartedForOsu && !_otdAutoLaunchAttemptedForOsu && !_dualModeActivatedForOsu))
            {
                return;
            }

            _managedOsuSessionActive = false;
            _tosuStartedForOsu = false;
            _otdAutoLaunchAttemptedForOsu = false;
            promptToRestoreDualMode = _dualModeActivatedForOsu;
            // The user's response now owns restoration. Clear the session flag
            // first so subsequent monitor ticks do not show duplicate prompts.
            _dualModeActivatedForOsu = false;
        }

        // Leave the LG monitor in its current mode until the user explicitly
        // chooses to restore it. OTD and tosu remain tied to the osu! session.
        OpenTabletDriverService.CloseOwned();
        TosuManager.CloseOwned();
        PublishCompanionStatus(c => c with
        {
            OpenTabletDriverLaunched = false,
            OpenTabletDriverDetail = "OpenTabletDriver closed with osu!",
            DualModeDetail = promptToRestoreDualMode
                ? "Waiting for confirmation to restore dual mode"
                : c.DualModeDetail,
        });

        if (promptToRestoreDualMode)
        {
            Dispatcher.InvokeAsync(PromptToRestoreDualMode);
        }
    }

    private void PromptToRestoreDualMode()
    {
        if (_tray is null)
        {
            PublishCompanionStatus(c => c with
            {
                DualModeDetail = "Dual mode left active after osu! closed",
            });
            return;
        }

        _tray.ShowDualModeRestoreNotification();
    }

    private void RestoreDualModeAfterOsuClosed()
    {
        try
        {
            var settings = new SettingsService();
            settings.Load();
            var restored = DualModeService.Deactivate(settings.Current);
            PublishCompanionStatus(c => c with
            {
                DualModeActive = !restored && DualModeService.IsDualModeActive(),
                DualModeDetail = restored ? "Dual mode restored after osu! closed" : "Dual mode restore failed",
            });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "LG dual-mode restore failed after osu! closed");
            PublishCompanionStatus(c => c with
            {
                DualModeDetail = $"Dual mode restore failed: {ex.Message}",
            });
        }
    }

    private async Task CompanionMonitorLoopAsync(AppStateStore store, SettingsService settings, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var osuRunning = OsuProcessDetector.IsRunning();
                UpdateTrayDualModeToggle(settings.Current.Display.AutoSwitchDualMode);
                // Kumori may be opened after osu! is already running. Treat
                // that first observation as a baseline, not as an osu! launch:
                // auto-switching the display or opening OTD at that point is
                // disruptive and can restart an active game.
                var transition = CompanionTransitionPolicy.Evaluate(
                    _hasObservedOsuProcessState,
                    _osuWasRunning,
                    osuRunning);
                _hasObservedOsuProcessState = true;
                _osuWasRunning = osuRunning;
                store.Update(s => s with
                {
                    Companions = s.Companions with
                    {
                        OsuRunning = osuRunning,
                        OpenTabletDriverEnabled = settings.Current.OpenTabletDriver.AutoLaunch,
                        DualModeEnabled = settings.Current.Display.AutoSwitchDualMode,
                    },
                });
                if (transition == CompanionTransition.Started)
                {
                    TryActivateOsuCompanions(settings.Current);
                    EnsureTosuForOsu(store);
                }
                else if (transition == CompanionTransition.EnsureTracking)
                {
                    // Starting Kumori after osu! must still bring up the non-disruptive
                    // tracking companion. Display switching and OTD automation remain
                    // transition-only so an active game is never restarted unexpectedly.
                    EnsureTosuForOsu(store);
                }
                else if (transition == CompanionTransition.Stopped)
                {
                    _tracking?.NotifyOsuStopped();
                    EndOsuCompanionSession();
                }
                await Task.Delay(
                    osuRunning || _companionRestartInProgress ? CompanionMonitorInterval : IdleCompanionMonitorInterval,
                    token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Companion monitor tick failed");
                await Task.Delay(TimeSpan.FromSeconds(5), token);
            }
        }
    }

    private void LaunchOpenTabletDriverIfRequested(KumoriSettings settings)
    {
        if (_otdAutoLaunchAttemptedForOsu || !settings.OpenTabletDriver.AutoLaunch)
        {
            return;
        }
        _otdAutoLaunchAttemptedForOsu = true;
        try
        {
            var installation = OpenTabletDriverService.Detect(settings.OpenTabletDriver.InstallPath);
            if (installation is null)
            {
                Log.Warning("OpenTabletDriver auto-launch requested, but no installation was found");
                PublishCompanionStatus(c => c with
                {
                    OpenTabletDriverEnabled = true,
                    OpenTabletDriverLaunched = false,
                    OpenTabletDriverDetail = "OpenTabletDriver not found",
                });
                return;
            }
            var launched = OpenTabletDriverService.Launch(installation.ExecutablePath);
            PublishCompanionStatus(c => c with
            {
                OpenTabletDriverEnabled = true,
                OpenTabletDriverLaunched = launched || OpenTabletDriverService.IsUiRunning(),
                OpenTabletDriverDetail = launched ? "OpenTabletDriver launched" : "OpenTabletDriver already running",
            });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "OpenTabletDriver auto-launch failed");
            PublishCompanionStatus(c => c with
            {
                OpenTabletDriverEnabled = true,
                OpenTabletDriverLaunched = false,
                OpenTabletDriverDetail = ex.Message,
            });
        }
    }

    private void ActivateDualModeIfRequested(KumoriSettings settings)
    {
        if (_dualModeActivatedForOsu || !settings.Display.AutoSwitchDualMode)
        {
            return;
        }
        try
        {
            var wasActive = DualModeService.IsDualModeActive();
            var sent = DualModeService.Activate(settings);
            var active = DualModeService.IsDualModeActive();
            PublishCompanionStatus(c => c with
            {
                DualModeEnabled = true,
                DualModeCommandSent = sent,
                DualModeActive = active,
                DualModeDetail = active
                    ? (wasActive ? "Dual mode already active" : "Dual mode active")
                    : sent ? "Dual mode command sent; waiting for display" : "Dual mode command failed",
            });
            if (sent && !wasActive && active)
            {
                _dualModeActivatedForOsu = true;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "LG dual-mode activation failed");
            PublishCompanionStatus(c => c with
            {
                DualModeEnabled = true,
                DualModeCommandSent = false,
                DualModeDetail = ex.Message,
            });
        }
    }

    private void PublishCompanionStatus(Func<CompanionStatus, CompanionStatus> update)
    {
        _store?.Update(s => s with { Companions = update(s.Companions) });
    }

    private void UpdateTrayDualModeToggle(bool enabled)
    {
        if (_trayDualModeToggleEnabled == enabled)
        {
            return;
        }

        _trayDualModeToggleEnabled = enabled;
        Dispatcher.InvokeAsync(() => _tray?.SetDualModeToggleEnabled(enabled));
    }

    private async Task ToggleDualModeFromTrayAsync()
    {
        var settings = new SettingsService();
        settings.Load();
        if (!settings.Current.Display.AutoSwitchDualMode)
        {
            UpdateTrayDualModeToggle(false);
            return;
        }

        try
        {
            var wasActive = DualModeService.IsDualModeActive();
            var sent = DualModeService.Toggle(settings.Current);
            await Task.Delay(750);
            var isActive = DualModeService.IsDualModeActive();
            PublishCompanionStatus(c => c with
            {
                DualModeEnabled = true,
                DualModeCommandSent = sent,
                DualModeActive = isActive,
                DualModeDetail = sent
                    ? (isActive == wasActive ? "Dual mode command sent; waiting for display" : isActive ? "Dual mode active" : "Dual mode restored")
                    : "Dual mode command failed",
            });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Manual LG dual-mode toggle failed");
            PublishCompanionStatus(c => c with
            {
                DualModeDetail = ex.Message,
            });
        }
    }

    private void DeactivateDualModeIfRequested()
    {
        if (!_dualModeActivatedForOsu)
        {
            return;
        }
        try
        {
            var settings = new SettingsService();
            settings.Load();
            DualModeService.Deactivate(settings.Current);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "LG dual-mode restore failed");
        }
    }
}
