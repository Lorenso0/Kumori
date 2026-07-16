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
    private static readonly TimeSpan CompanionTransitionMonitorInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan IdleCompanionMonitorInterval = TimeSpan.FromSeconds(2);
    // Replay preparation includes final source draining, compression and a
    // durable SQLite transaction. Give both capture services one realistic,
    // shared window while keeping shutdown strictly bounded.
    private static readonly TimeSpan ReplayCaptureShutdownTimeout = TimeSpan.FromSeconds(15);
    private SingleInstance? _singleInstance;
    private TrayIconService? _tray;
    private MainWindow? _mainWindow;
    private TosuTrackingService? _tracking;
    private LazerReplayFrameCaptureService? _lazerReplayFrames;
    private LazerReplayFrameCaptureService? _stableReplayFrames;
    private bool _lazerReplayCaptureStarted;
    private bool _stableReplayCaptureStarted;
    private AppStateStore? _store;
    private GameplayWorkCoordinator? _gameplayWork;
    private AttemptSqliteSink? _attemptPersistence;
    private TrackingRuntimeController? _trackingRuntime;
    private CancellationTokenSource? _companionMonitorCts;
    private Task? _companionMonitorTask;
    private DispatcherTimer? _trayUpdateTimer;
    private string _pendingTrayStatus = "Tracker not running";
    private bool _pendingTrayEndSessionEnabled;
    private bool _trayStateDirty;
    private bool _otdLaunchAttemptedForKumori;
    private bool _otdLaunchInProgress;
    private bool _otdLifetimeManagementEnabled;
    private bool _observedOtdAutoLaunch;
    private string _observedOtdInstallPath = string.Empty;
    private bool _dualModeActivatedForOsu;
    private bool _managedOsuSessionActive;
    private bool _tosuStartedForOsu;
    private bool? _trayDualModeToggleEnabled;
    private bool _exitRequested;
    private bool _shutdownCleanupCompleted;
    private KumoriUpdateResult? _pendingUpdatePrompt;
    private string? _promptedUpdateVersion;
    private bool _updatePromptOpen;
    private bool _manualUpdateCheckRunning;
    private readonly CompanionTransitionPolicy _companionTransitionPolicy = new();
    private readonly object _osuCompanionGate = new();
    private readonly object _otdLifetimeGate = new();
    private readonly object _trayStateGate = new();
    private readonly object _recoveryRestartGate = new();
    private readonly HashSet<long> _queuedRecoveryRestartAttempts = [];
    private readonly HashSet<long> _completedRecoveryRestartAttempts = [];
    private bool _recoveryRestartWorkerRunning;
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
        _singleInstance = new SingleInstance();
        if (!_singleInstance.IsPrimaryInstance)
        {
            _singleInstance.SignalPrimaryInstance();
            _singleInstance.Dispose();
            Shutdown();
            return;
        }

        // Capture the real startup baseline before initialization can delay the
        // companion monitor. If osu! is launched while Kumori is still starting,
        // the monitor can now classify it as a new session and run display
        // automation instead of mistaking it for a pre-existing process.
        _companionTransitionPolicy.Observe(OsuProcessDetector.RunningProcessIds());

        try
        {
            // Install diagnostics before the fail-closed legacy-data migration.
            // Locked or incomplete database sets must produce a visible error,
            // not terminate the async startup path before the shell exists.
            ConfigureFileLogging(AppPaths.DefaultLogRetentionDays);
            Log.Information("Kumori starting");
            InstallCrashHandlers();
            AppDataOrganizer.Organize();
        }
        catch (Exception ex)
        {
            try { Log.Fatal(ex, "Application data initialization failed"); } catch { }
            MessageBox.Show(
                $"Kumori could not prepare its application data. No tracking data was deleted.\n\n{ex.Message}",
                "Kumori",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown();
            return;
        }

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
        try
        {
            settings.Load();
            _observedOtdAutoLaunch = settings.Current.OpenTabletDriver.AutoLaunch;
            _observedOtdInstallPath = settings.Current.OpenTabletDriver.InstallPath;
            settings.Changed += HandleSettingsChanged;
            var logRetentionDays = LogRetentionPolicy.NormalizeDays(settings.Current.Developer.LogRetentionDays);
            CacheActivityLog.ConfigureRotationDays(logRetentionDays);
            AppDataOrganizer.PruneLogs(retentionDays: logRetentionDays);
            Log.CloseAndFlush();
            ConfigureFileLogging(logRetentionDays);
            Themes = new ThemeManager(settings);
            Themes.ApplyCurrent();
            SyncStartupRegistration(settings.Current);
        }
        catch (Exception ex)
        {
            try { Log.Fatal(ex, "Settings and theme initialization failed"); } catch { }
            MessageBox.Show(
                $"Kumori could not load its settings. The settings file was not deleted.\n\n{ex.Message}",
                "Kumori",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown();
            return;
        }
        var showChangelogRequested = e.Args.Any(argument =>
            string.Equals(argument, "--show-changelog", StringComparison.Ordinal));
        var startMinimizedToTray = settings.Current.FirstRunCompleted
            && settings.Current.Startup.RunAtLogin
            && settings.Current.Startup.StartMinimized
            && !showChangelogRequested
            && e.Args.Any(argument => string.Equals(
                argument,
                StartupRegistration.StartMinimizedArgument,
                StringComparison.Ordinal));

        var store = new AppStateStore();
        _store = store;
        var gameplayWork = new GameplayWorkCoordinator(_backgroundCts.Token);
        _gameplayWork = gameplayWork;
        var factory = new SqliteConnectionFactory(AppPaths.TrackingDatabase, readOnly: false);
        var attempts = new AttemptRepository(factory);
        var details = new AttemptDetailsRepository(factory);
        var analytics = new AnalyticsRepository(factory);
        var movement = new MovementRepository(factory);
        var replayViewer = new ReplayViewerContractService(details, movement, () => settings.Current);
        var maintenance = new TrackingMaintenanceRepository(factory);
        var sessions = new SessionRepository(factory);
        var viewModel = new MainViewModel(
            store,
            attempts,
            details,
            analytics,
            settings,
            replayViewer,
            maintenance,
            sessions,
            CheckForKumoriUpdatesManuallyAsync);

        // Shell first — no data work before first paint.
        _mainWindow = new MainWindow(viewModel, settings);
        _mainWindow.StateChanged += (_, _) => ScheduleAvailableUpdatePrompt();
        _mainWindow.IsVisibleChanged += (_, _) => ScheduleAvailableUpdatePrompt();
        if (startMinimizedToTray)
        {
            // Initialize the WPF shell without activating it or flashing a
            // taskbar button, then leave the running app accessible by tray.
            _mainWindow.ShowActivated = false;
            _mainWindow.ShowInTaskbar = false;
            _mainWindow.WindowState = WindowState.Minimized;
            _mainWindow.Show();
            _mainWindow.Hide();
            _mainWindow.WindowState = WindowState.Normal;
            _mainWindow.ShowInTaskbar = true;
            _mainWindow.ShowActivated = true;
            Log.Information("Kumori started minimized to the system tray");
        }
        else
        {
            _mainWindow.Show();
        }
        await Dispatcher.Yield(DispatcherPriority.Loaded);
        TrackBackground(
            RunBelowNormalAsync(
                () =>
                {
                    SyncOpenTabletDriverLifetime(settings.Current);
                    return true;
                },
                _backgroundCts.Token),
            "OpenTabletDriver startup");
        TrackBackground(CleanupStaleUpdaterAfterStartupAsync(
                store,
                settings.Current.Tracking.Enabled,
                gameplayWork,
                _backgroundCts.Token),
            "stale updater cleanup");
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
        Task DeferAttemptPersistence(string key, Func<CancellationToken, Task> work) =>
            gameplayWork.EnqueuePriority(key, work);
        AttemptSqliteSink trackingSink;
        (int Attempts, int Sessions) recoveredTracking;
        int repairedMissingResults;
        int repairedPartialSimulationResults;
        try
        {
            trackingSink = await Task.Run(
                () => new AttemptSqliteSink(factory, DeferAttemptPersistence),
                _backgroundCts.Token);
            recoveredTracking = await Task.Run(
                () => new TrackingMaintenanceRepository(factory).RecoverInterruptedTracking(),
                _backgroundCts.Token);
            repairedMissingResults = await Task.Run(
                () => new TrackingMaintenanceRepository(factory).RepairMissingTosuResults(),
                _backgroundCts.Token);
            repairedPartialSimulationResults = await Task.Run(
                () => new TrackingMaintenanceRepository(factory).RepairPartialSimulationCoreResults(),
                _backgroundCts.Token);
        }
        catch (OperationCanceledException) when (_backgroundCts.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Tracking database initialization failed");
            // A tray-started instance must never disappear with only a log file
            // as evidence. Bring the shell forward and show an actionable error.
            ShowMainWindow();
            KumoriDialog.Show(
                _mainWindow,
                $"Kumori could not open or upgrade its tracking database. Your database was not deleted.\n\n{ex.Message}",
                "Tracking database could not be opened",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            _exitRequested = true;
            if (_mainWindow is not null)
                _mainWindow.ForceClose = true;
            Shutdown();
            return;
        }
        if (recoveredTracking is not (0, 0))
        {
            Log.Information(
                "Recovered {AttemptCount} interrupted attempt(s) and {SessionCount} interrupted session(s) from an earlier app run",
                recoveredTracking.Attempts,
                recoveredTracking.Sessions);
        }
        if (repairedMissingResults > 0)
        {
            Log.Warning(
                "Repaired or neutralized {AttemptCount} broken tosu result row(s) while replay recovery remains pending",
                repairedMissingResults);
        }
        if (repairedPartialSimulationResults > 0)
        {
            Log.Warning(
                "Restored tosu hit totals and judgement events for {AttemptCount} partial play(s) damaged by replay simulation",
                repairedPartialSimulationResults);
        }
        _attemptPersistence = trackingSink;
        // Visible read-only data is core startup work. It must not wait behind
        // backup, update, or replay-reconciliation maintenance.
        var dashboardHydration = viewModel.HydrateAsync(_backgroundCts.Token);
        TrackBackground(dashboardHydration, "dashboard hydration");
        TrackBackground(EnqueueAfterSafeStartupAsync(
            store,
            settings.Current.Tracking.Enabled,
            gameplayWork,
            "automatic-backup",
            token => Task.Run(
                () => new BackupService().CreateAutomaticIfDue(settings.Current.Backup, token),
                token),
            dashboardHydration,
            _backgroundCts.Token),
            "automatic backup");
        TrackBackground(EnqueueAfterSafeStartupAsync(
            store,
            settings.Current.Tracking.Enabled,
            gameplayWork,
            "historical-beatmap-recovery",
            token => Dispatcher.InvokeAsync(
                    () =>
                    {
                        token.ThrowIfCancellationRequested();
                        return RecoverHistoricalBeatmapsAsync(attempts, settings, token);
                    },
                    DispatcherPriority.ContextIdle)
                    .Task.Unwrap(),
            dashboardHydration,
            _backgroundCts.Token),
            "historical beatmap recovery");
        var trackingRetentionDays = settings.Current.Tracking.RetentionDays;
        if (trackingRetentionDays > 0)
        {
            TrackBackground(EnqueueAfterSafeStartupAsync(
                store,
                settings.Current.Tracking.Enabled,
                gameplayWork,
                "tracking-retention",
                token => Task.Run(() =>
                {
                    token.ThrowIfCancellationRequested();
                    var normalizedDays = Math.Clamp(trackingRetentionDays, 1, 36_500);
                    var cutoff = DateTimeOffset.UtcNow
                        .AddDays(-normalizedDays)
                        .ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
                    var deleted = maintenance.DeleteBefore(cutoff);
                    Log.Information(
                        "Tracking retention removed {DeletedSessionCount} session(s) older than {RetentionDays} days",
                        deleted,
                        normalizedDays);
                }, token),
                dashboardHydration,
                _backgroundCts.Token,
                coalesce: true),
                "tracking retention");
        }
        if (!settings.Current.FirstRunCompleted ||
            settings.Current.OnboardingVersion < WelcomeWindow.CurrentOnboardingVersion)
        {
            _ = Dispatcher.InvokeAsync(() =>
            {
                _mainWindow.OpenOnboarding(new WelcomeWindow(settings, store));
            }, DispatcherPriority.ContextIdle);
        }
        if (showChangelogRequested)
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
            if (_mainWindow is not null)
            {
                _mainWindow.ForceClose = true;
            }

            var statusWindow = new ShutdownStatusWindow();
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
            lock (_trayStateGate)
            {
                _pendingTrayStatus = status;
                _pendingTrayEndSessionEnabled = state.ActiveSession is not null;
                _trayStateDirty = true;
            }
        };
        _trayUpdateTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        _trayUpdateTimer.Tick += (_, _) => FlushTrayState();
        _trayUpdateTimer.Start();
        _companionMonitorCts = new CancellationTokenSource();
        _companionMonitorTask = Task.Run(() => CompanionMonitorLoopAsync(store, settings, _companionMonitorCts.Token));
        TrackBackground(
            EnqueueAfterSafeStartupAsync(
                store,
                settings.Current.Tracking.Enabled,
                gameplayWork,
                "tosu-startup-update-check",
                CheckForTosuUpdatesOnLaunchAsync,
                dashboardHydration,
                _backgroundCts.Token,
                coalesce: true),
            "tosu startup update check");
        TrackBackground(
            EnqueueAfterSafeStartupAsync(
                store,
                settings.Current.Tracking.Enabled,
                gameplayWork,
                "kumori-startup-update-check",
                token => CheckForKumoriUpdatesOnLaunchAsync(store, token),
                dashboardHydration,
                _backgroundCts.Token,
                coalesce: true),
            "Kumori update check");

        // Background services start only after the shell is visible
        // (no-flicker startup plan: shell first, services second).
        Task StartTrackingRuntimeAsync(KumoriSettings runtimeSettings)
        {
            if (!runtimeSettings.Tracking.Enabled || _tracking is not null)
                return Task.CompletedTask;
            var profileTelemetry = new ProfileTelemetryStore(
                factory,
                (key, work) => gameplayWork.Enqueue(key, work));
            IReplayPlaybackDetector replayPlaybackDetector;
            var dashboardRefreshRequested = 0;
            var dashboardRefreshRunning = 0;
            void QueueMovementUiRefresh(long attemptId)
            {
                TrackBackground(gameplayWork.Enqueue(
                    $"movement-ui-refresh-{attemptId}",
                    token => Dispatcher.InvokeAsync(
                            async () =>
                            {
                                token.ThrowIfCancellationRequested();
                                // Replay recovery may replace movement without
                                // changing any result fields. Refresh the row
                                // first so replay availability/HasMovement is
                                // visible immediately, then refresh an open
                                // inspector from the same committed data.
                                await viewModel.RefreshAttemptMovementAsync(attemptId, token);
                                await viewModel.Inspector.RefreshAfterMovementReplacementAsync(attemptId, token);
                            },
                            DispatcherPriority.Background)
                        .Task.Unwrap(),
                    coalesce: true),
                    $"movement UI refresh for attempt {attemptId}");
            }
            void QueueDashboardRefresh()
            {
                Interlocked.Exchange(ref dashboardRefreshRequested, 1);
                if (Interlocked.CompareExchange(ref dashboardRefreshRunning, 1, 0) == 0)
                    TrackBackground(RunDashboardRefreshLoopAsync(), "dashboard refresh");
            }
            async Task RunDashboardRefreshLoopAsync()
            {
                try
                {
                    while (Volatile.Read(ref dashboardRefreshRequested) != 0)
                    {
                        // Attempt and replay transactions usually commit within
                        // the same short window. Fold those notifications into
                        // one database/UI refresh instead of rebuilding up to a
                        // thousand rows twice in rapid succession.
                        await Task.Delay(TimeSpan.FromMilliseconds(150), _backgroundCts.Token);
                        if (Interlocked.Exchange(ref dashboardRefreshRequested, 0) == 0)
                            continue;
                        await Dispatcher.InvokeAsync(
                                () => viewModel.RefreshDashboardAsync(_backgroundCts.Token),
                                DispatcherPriority.Background)
                            .Task.Unwrap();
                    }
                }
                catch (OperationCanceledException) when (_backgroundCts.IsCancellationRequested)
                {
                }
                finally
                {
                    Interlocked.Exchange(ref dashboardRefreshRunning, 0);
                    if (!_backgroundCts.IsCancellationRequested &&
                        Volatile.Read(ref dashboardRefreshRequested) != 0)
                    {
                        QueueDashboardRefresh();
                    }
                }
            }
            void OnLiveReplayCaptureCommitted(long attemptId)
            {
                store.Update(s => s with
                {
                    Tracking = s.Tracking with
                    {
                        LatestReplayAttemptId = attemptId,
                    },
                });
                // Deferred capture can commit after the selected inspector has
                // cached the attempt without movement. Refresh both the row and
                // that cache from the same durable post-commit notification.
                QueueMovementUiRefresh(attemptId);
            }
            viewModel.SetDashboardRefreshHandler(QueueDashboardRefresh);
            profileTelemetry.ProfileUpdated += QueueDashboardRefresh;
            var statePublishingAttemptSink = new StatePublishingAttemptSink(trackingSink, store);
            IAttemptSink attemptSink = statePublishingAttemptSink;
            Task DeferReplayPersistence(string key, Func<CancellationToken, Task> work) =>
                gameplayWork.EnqueuePriority(key, async token =>
                {
                    await trackingSink.FlushPendingPersistenceAsync(token);
                    var failures = 0;
                    while (true)
                    {
                        token.ThrowIfCancellationRequested();
                        try
                        {
                            await work(token);
                            return;
                        }
                        catch (OperationCanceledException) when (token.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            failures++;
                            if (failures == 1 || (failures & (failures - 1)) == 0)
                            {
                                Log.Warning(
                                    ex,
                                    "Replay persistence {Operation} failed; the detached capture remains queued for retry",
                                    key);
                            }
                            var delayMs = Math.Min(2_000, 100 * (1 << Math.Min(failures - 1, 4)));
                            await Task.Delay(delayMs, token);
                        }
                    }
                });
            void OnReplayResultRecovered(ReplayResultRecoveryContext recovery)
            {
                // Exact header recovery proves that live tosu data was broken.
                // A normal partial-play simulation uses the retained capture
                // without implying companion failure.
                if (recovery.RequiresTosuRestart
                    && (recovery.TosuResultWasMissing || recovery.HeaderRecovery.RecoveredCoreResult))
                    RequestRecoveryTosuRestart(recovery.AttemptId);
                TrackBackground(
                    gameplayWork.Enqueue(
                        $"replay-result-completion-{recovery.AttemptId}",
                        token => CompleteReplayResultRecoveryAsync(recovery, token),
                        coalesce: true),
                    $"replay simulation recovery for attempt {recovery.AttemptId}");
            }
            async Task CompleteReplayResultRecoveryAsync(
                ReplayResultRecoveryContext recovery,
                CancellationToken operationToken)
            {
                if (recovery.RequiresSimulation)
                {
                    try
                    {
                        var simulation = await replayViewer.SimulateRecoveryAsync(
                            recovery.AttemptId,
                            recovery.ReplayPath,
                            recovery.BeatmapPath,
                            recovery.MediaDirectory,
                            recovery.MediaPaths,
                            recovery.Samples,
                            operationToken);
                        operationToken.ThrowIfCancellationRequested();
                        new ReplayResultRecoveryStore(factory).ApplySimulation(
                            recovery.AttemptId,
                            simulation,
                            simulationOwnsCoreResult: recovery.SimulationOwnsCoreResult,
                            tosuResultWasMissing: recovery.TosuResultWasMissing,
                            cancellationToken: operationToken);
                    }
                    catch (OperationCanceledException) when (operationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Replay result simulation failed for attempt {AttemptId}; header recovery was retained", recovery.AttemptId);
                    }
                }

                operationToken.ThrowIfCancellationRequested();
                QueueDashboardRefresh();
                QueueMovementUiRefresh(recovery.AttemptId);
                await Task.CompletedTask;
            }
            if (runtimeSettings.Capture.LazerReplayFrameEnabled)
            {
                var lazerFrameSource = new LazerMemoryReplayFrameSource();
                TrackBackground(
                    lazerFrameSource.EnsureReplayDetectionOffsetsAsync(_backgroundCts.Token),
                    "lazer memory offsets");
                replayPlaybackDetector = new OsuReplayPlaybackDetector(lazerFrameSource);
                _lazerReplayFrames = new LazerReplayFrameCaptureService(
                    store,
                    factory,
                    () => trackingSink.CurrentAttemptId,
                    lazerFrameSource,
                    sourceName: "lazer_memory",
                    deferPersistence: DeferReplayPersistence,
                    captureCommitted: OnLiveReplayCaptureCommitted);
                // Lazer's memory reader must be warm before the first client-kind
                // packet or StartAttempt transition. Its discovery is bounded and
                // below normal priority; starting here restores the original
                // always-on pre-read without restoring unbounded gameplay scans.
                _lazerReplayCaptureStarted = true;
                _lazerReplayFrames.Start();
                Log.Information("Started bounded lazer replay capture pre-read before client detection");
                var stableCaptureStatus = new StableCaptureStatusSink();
                _stableReplayFrames = new LazerReplayFrameCaptureService(
                    store,
                    factory,
                    () => trackingSink.CurrentAttemptId,
                    new StableLiveReplayFrameSource(status: stableCaptureStatus),
                    stableCaptureStatus,
                    sourceName: "stable_memory",
                    clientKind: OsuClientKind.Stable,
                    deferPersistence: DeferReplayPersistence,
                    captureCommitted: OnLiveReplayCaptureCommitted);
                attemptSink = new CompositeAttemptSink(
                    statePublishingAttemptSink,
                    new BestEffortAttemptSink(
                        new LazerReplayFrameRecoverySink(
                            factory,
                            () => trackingSink.CurrentAttemptId,
                            QueueMovementUiRefresh,
                            OnReplayResultRecovered,
                            RequestRecoveryTosuRestart,
                            recoverMovement: true,
                            cancellationToken: _backgroundCts.Token,
                            workCoordinator: gameplayWork),
                        "lazer Realm replay-frame recovery"),
                    new BestEffortAttemptSink(_lazerReplayFrames, "lazer replay-frame capture"),
                    new BestEffortAttemptSink(
                        new StableReplayFrameRecoverySink(
                            factory,
                            () => trackingSink.CurrentAttemptId,
                            QueueMovementUiRefresh,
                            OnReplayResultRecovered,
                            RequestRecoveryTosuRestart,
                            recoverMovement: true,
                            cancellationToken: _backgroundCts.Token,
                            workCoordinator: gameplayWork),
                        "stable replay-frame recovery"),
                    // Composite sinks finalize in reverse order. Store the live
                    // buffer first, then let an exact Data/r replay replace it.
                    new BestEffortAttemptSink(_stableReplayFrames, "stable live replay-frame capture"));
            }
            else
            {
                var replayDetectionSource = new LazerMemoryReplayFrameSource();
                TrackBackground(
                    replayDetectionSource.EnsureReplayDetectionOffsetsAsync(_backgroundCts.Token),
                    "lazer replay-detection offsets");
                TrackBackground(
                    replayDetectionSource.PrewarmGameBaseAsync(_backgroundCts.Token),
                    "lazer replay-detection prewarm");
                replayPlaybackDetector = new OsuReplayPlaybackDetector(replayDetectionSource);
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
                            resultTelemetryMissing: RequestRecoveryTosuRestart,
                            recoverMovement: false,
                            cancellationToken: _backgroundCts.Token,
                            workCoordinator: gameplayWork),
                        "lazer replay result recovery"),
                    new BestEffortAttemptSink(
                        new StableReplayFrameRecoverySink(
                            factory,
                            () => trackingSink.CurrentAttemptId,
                            resultRecovered: OnReplayResultRecovered,
                            resultTelemetryMissing: RequestRecoveryTosuRestart,
                            recoverMovement: false,
                            cancellationToken: _backgroundCts.Token,
                            workCoordinator: gameplayWork),
                        "stable replay result recovery"));
            }
            attemptSink = new ProfileAwareAttemptSink(
                attemptSink,
                profileTelemetry,
                () => trackingSink.CurrentAttemptId);
            attemptSink = new ReplayRecoveryTestAttemptSink(
                attemptSink,
                settings,
                persist => TrackBackground(
                    gameplayWork.Enqueue(
                        "consume-replay-recovery-test-switch",
                        token => Task.Run(() =>
                        {
                            token.ThrowIfCancellationRequested();
                            persist();
                        }, token),
                        coalesce: true),
                    "consume developer replay recovery switch"));
            attemptSink = new GameplayActivityAttemptSink(attemptSink, gameplayWork);
            _tracking = new TosuTrackingService(
                store,
                attemptTracker: new AttemptTracker(
                    attemptSink,
                    minimumAttemptSecondsProvider: () => settings.Current.Tracking.MinimumAttemptSeconds),
                sessionTracker: new SessionTracker(new StatePublishingSessionSink(trackingSink, store)),
                profileTelemetry: profileTelemetry,
                primaryMediaMirror: settings.Current.Media.PrimaryMirror,
                fallbackMediaMirrors: settings.Current.Media.FallbackMirrors,
                recordPackets: settings.Current.Tracking.PacketRecordingEnabled,
                replayPlaybackDetector: replayPlaybackDetector);
            _tracking.ClientKindObserved += StartReplayCaptureFor;
            _tracking.Start();
            var reconciliation = new PersistedReplayReconciliationService(
                factory,
                QueueMovementUiRefresh,
                OnReplayResultRecovered,
                recoverMovement: runtimeSettings.Capture.LazerReplayFrameEnabled);
            TrackBackground(gameplayWork.Enqueue(
                "persisted-replay-reconciliation",
                token => Task.Run(() => reconciliation.Run(token), token)),
                "persisted replay reconciliation");
            viewModel.SetEndLiveSessionHandler(() => Task.Run(() => _tracking?.EndSession() ?? false));
            EnsureTosuForOsu(store);
            return Task.CompletedTask;
        }

        async Task StopTrackingRuntimeAsync()
        {
            var tracking = _tracking;
            _tracking = null;
            if (tracking is not null)
            {
                tracking.ClientKindObserved -= StartReplayCaptureFor;
                await tracking.DisposeAsync();
            }

            var captureTasks = new List<Task>(2);
            if (_lazerReplayFrames is not null)
                captureTasks.Add(_lazerReplayFrames.DisposeAsync().AsTask());
            if (_stableReplayFrames is not null)
                captureTasks.Add(_stableReplayFrames.DisposeAsync().AsTask());
            if (captureTasks.Count > 0)
                await Task.WhenAll(captureTasks);
            _lazerReplayFrames = null;
            _stableReplayFrames = null;
            _lazerReplayCaptureStarted = false;
            _stableReplayCaptureStarted = false;
            if (_attemptPersistence is not null)
                await _attemptPersistence.FlushPendingPersistenceAsync();
            TosuManager.CloseOwned();
            lock (_osuCompanionGate)
                _tosuStartedForOsu = false;
            viewModel.SetEndLiveSessionHandler(null);
        }

        _trackingRuntime = new TrackingRuntimeController(
            store,
            StartTrackingRuntimeAsync,
            StopTrackingRuntimeAsync,
            (task, operation) => TrackBackground(task, operation),
            status => Dispatcher.InvokeAsync(() => viewModel.HistoryStatus = status));
        await _trackingRuntime.ApplyAsync(settings.Current);

        // The updater keeps the previous executable until this point. Reaching
        // here proves that the shell, database, and selected tracking runtime
        // initialized successfully.
        KumoriUpdateInstaller.SignalHealthy(e.Args);

    }
}
