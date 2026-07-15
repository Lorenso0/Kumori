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
        var viewModel = new MainViewModel(store, attempts, details, analytics, settings, replayViewer, maintenance, sessions);

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
        var trackingSink = await Task.Run(
            () => new AttemptSqliteSink(factory, DeferAttemptPersistence),
            _backgroundCts.Token);
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
                // Recovered result data proves the live tosu stream was broken.
                // Restarting tosu is mandatory even when replay simulation is
                // unnecessary; coalescing prevents a burst of recoveries from
                // serially restarting the same companion several times.
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
                            operationToken);
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
                            recoverMovement: false,
                            cancellationToken: _backgroundCts.Token,
                            workCoordinator: gameplayWork),
                        "lazer replay result recovery"),
                    new BestEffortAttemptSink(
                        new StableReplayFrameRecoverySink(
                            factory,
                            () => trackingSink.CurrentAttemptId,
                            resultRecovered: OnReplayResultRecovered,
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
            if (runtimeSettings.Capture.LazerReplayFrameEnabled)
            {
                var reconciliation = new PersistedReplayReconciliationService(
                    factory,
                    QueueMovementUiRefresh,
                    OnReplayResultRecovered);
                TrackBackground(gameplayWork.Enqueue(
                    "persisted-replay-reconciliation",
                    token => Task.Run(() => reconciliation.Run(token), token)),
                    "persisted replay reconciliation");
            }
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

    protected override void OnExit(ExitEventArgs e)
    {
        if (!_shutdownCleanupCompleted)
        {
            CleanupSynchronously();
        }
        _tray?.Dispose();
        _trayUpdateTimer?.Stop();
        _gameplayWork?.Dispose();
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
            _trackingRuntime?.Dispose();
            statusWindow.UpdateStatus("Stopping live tracking...");
            if (_tracking is not null)
            {
                await AwaitBoundedAsync(_tracking.DisposeAsync().AsTask(), TimeSpan.FromSeconds(3));
            }
            if (_attemptPersistence is not null)
            {
                await AwaitBoundedAsync(
                    _attemptPersistence.FlushPendingPersistenceAsync(),
                    TimeSpan.FromSeconds(3));
            }

            statusWindow.UpdateStatus("Finishing replay capture...");
            _gameplayWork?.BeginShutdownDrain();
            Task[] replayCaptureTasks = ReplayCaptureDisposeTasks();
            if (replayCaptureTasks.Length > 0)
                await AwaitBoundedAsync(Task.WhenAll(replayCaptureTasks), ReplayCaptureShutdownTimeout);

            statusWindow.UpdateStatus("Closing companion services...");
            _backgroundCts.Cancel();
            _gameplayWork?.Dispose();
            _trayUpdateTimer?.Stop();
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

            statusWindow.UpdateStatus("Closing tracking helper...");
            TosuManager.CloseOwned();
            OpenTabletDriverService.StopDisplayMappingRefresh();
            OpenTabletDriverService.CloseOwned();
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

    private static Task<T> RunBelowNormalAsync<T>(
        Func<T> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() =>
        {
            var nativeThread = GetCurrentThread();
            try
            {
                // A dedicated thread is safe to reprioritize permanently. On
                // Windows background mode also lowers disk and memory I/O
                // priority while schema migration/ID initialization runs.
                _ = SetThreadPriority(nativeThread, ThreadModeBackgroundBegin);
                cancellationToken.ThrowIfCancellationRequested();
                completion.TrySetResult(operation());
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                completion.TrySetCanceled(cancellationToken);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
            finally
            {
                _ = SetThreadPriority(nativeThread, ThreadModeBackgroundEnd);
            }
        })
        {
            IsBackground = true,
            Name = "Kumori low-priority background I/O",
            Priority = ThreadPriority.BelowNormal,
        };
        worker.Start();
        return completion.Task;
    }

    private const int ThreadModeBackgroundBegin = 0x00010000;
    private const int ThreadModeBackgroundEnd = 0x00020000;

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadPriority(nint thread, int priority);

    private void CleanupSynchronously()
    {
        _trackingRuntime?.Dispose();
        if (_tracking is not null)
        {
            try { _tracking.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3)); } catch { }
        }
        if (_attemptPersistence is not null)
        {
            try { _attemptPersistence.FlushPendingPersistenceAsync().Wait(TimeSpan.FromSeconds(3)); } catch { }
        }
        _gameplayWork?.BeginShutdownDrain();
        Task[] replayCaptureTasks = ReplayCaptureDisposeTasks();
        if (replayCaptureTasks.Length > 0)
        {
            try { Task.WhenAll(replayCaptureTasks).Wait(ReplayCaptureShutdownTimeout); } catch { }
        }
        _companionMonitorCts?.Cancel();
        _backgroundCts.Cancel();
        _gameplayWork?.Dispose();
        _trayUpdateTimer?.Stop();
        Task[] backgroundTasks;
        lock (_backgroundGate)
        {
            backgroundTasks = _backgroundTasks.ToArray();
        }
        try { Task.WhenAll(backgroundTasks).Wait(TimeSpan.FromSeconds(3)); } catch { }
        try { _companionMonitorTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _companionMonitorCts?.Dispose();
        TosuManager.CloseOwned();
        OpenTabletDriverService.StopDisplayMappingRefresh();
        OpenTabletDriverService.CloseOwned();
        _shutdownCleanupCompleted = true;
    }

    private Task[] ReplayCaptureDisposeTasks()
    {
        var tasks = new List<Task>(2);
        if (_lazerReplayFrames is not null)
            tasks.Add(_lazerReplayFrames.DisposeAsync().AsTask());
        if (_stableReplayFrames is not null)
            tasks.Add(_stableReplayFrames.DisposeAsync().AsTask());
        return tasks.ToArray();
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

    private static async Task WaitForSafeStartupMaintenanceAsync(
        AppStateStore store,
        bool trackingEnabled,
        CancellationToken cancellationToken)
    {
        while (OsuProcessDetector.IsRunning())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tracking = store.Current.Tracking;
            var telemetry = tracking.LatestTelemetry;
            if (trackingEnabled
                && tracking.TosuConnected
                && telemetry is not null
                && !telemetry.IsPlaying
                && DateTimeOffset.UtcNow - telemetry.ReceivedAt < TimeSpan.FromSeconds(2))
            {
                return;
            }

            // If tracking is disabled or tosu has not produced a fresh state,
            // prefer postponing optional backup/update/recovery work until osu!
            // exits over guessing that an active map is safe.
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
    }

    private static async Task CleanupStaleUpdaterAfterStartupAsync(
        AppStateStore store,
        bool trackingEnabled,
        GameplayWorkCoordinator coordinator,
        CancellationToken cancellationToken)
    {
        await WaitForSafeStartupMaintenanceAsync(store, trackingEnabled, cancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
        await coordinator.Enqueue(
            "stale-updater-cleanup",
            token => Task.Run(KumoriUpdateInstaller.CleanupStaleFiles, token),
            coalesce: true);
    }

    private static async Task EnqueueAfterSafeStartupAsync(
        AppStateStore store,
        bool trackingEnabled,
        GameplayWorkCoordinator coordinator,
        string key,
        Func<CancellationToken, Task> work,
        Task startupPrerequisite,
        CancellationToken cancellationToken,
        bool coalesce = false)
    {
        await startupPrerequisite.WaitAsync(cancellationToken);
        await WaitForSafeStartupMaintenanceAsync(store, trackingEnabled, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await coordinator.Enqueue(key, work, coalesce);
    }

    private void RequestRecoveryTosuRestart(long attemptId)
    {
        var startWorker = false;
        lock (_recoveryRestartGate)
        {
            if (_completedRecoveryRestartAttempts.Contains(attemptId) ||
                !_queuedRecoveryRestartAttempts.Add(attemptId))
                return;
            if (!_recoveryRestartWorkerRunning)
            {
                _recoveryRestartWorkerRunning = true;
                startWorker = true;
            }
        }
        if (!startWorker)
            return;
        TrackBackground(
            RestartTosuAfterRecoveryAsync(),
            "mandatory tosu restart after replay recovery");
    }

    private async Task RestartTosuAfterRecoveryAsync()
    {
        var restarted = false;
        long attemptId = 0;
        HashSet<long> generationAttempts = [];
        try
        {
            // Let a burst of reconciliation callbacks join one mandatory
            // restart generation. Only recoveries queued before the actual
            // restart sequence begins join it; later recoveries remain queued
            // for the next mandatory generation.
            await Task.Delay(100, _backgroundCts.Token);
            lock (_recoveryRestartGate)
            {
                generationAttempts = [.. _queuedRecoveryRestartAttempts];
                attemptId = generationAttempts.FirstOrDefault();
            }

            Exception? lastFailure = null;
            var failedBatches = 0;
            while (!restarted)
            {
                _backgroundCts.Token.ThrowIfCancellationRequested();
                try
                {
                    var coordinator = _gameplayWork
                        ?? throw new InvalidOperationException("Gameplay work coordinator is unavailable.");
                    restarted = await coordinator.RunFairRetryLoopAsync(
                        "mandatory-tosu-restart-recovery-generation",
                        maxAttempts: 3,
                        retryDelay: TimeSpan.FromSeconds(2),
                        async (attemptIndex, gameplayToken) =>
                        {
                            // Do not begin a close while gameplay is active. Once the
                            // close starts, use the application token so cancellation
                            // can never strand tosu between close and relaunch.
                            gameplayToken.ThrowIfCancellationRequested();
                            try
                            {
                                if (!File.Exists(AppPaths.TosuExecutable))
                                {
                                    await TosuManager.EnsureInstalledAsync(
                                        cancellationToken: gameplayToken);
                                    gameplayToken.ThrowIfCancellationRequested();
                                }
                                await TosuManager.RestartAsync(
                                    _backgroundCts.Token,
                                    transition => coordinator.ExecuteGameplayExcludingTransition(
                                        gameplayToken,
                                        transition));
                                lock (_osuCompanionGate)
                                    _tosuStartedForOsu = true;
                                return true;
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            {
                                lastFailure = ex;
                                Log.Warning(ex,
                                    "Mandatory tosu recovery restart for attempt {AttemptId} failed on outer attempt {OuterAttempt}",
                                    attemptId,
                                    attemptIndex + 1);
                                return false;
                            }
                        },
                        _backgroundCts.Token,
                        priority: true);
                }
                catch (OperationCanceledException) when (_backgroundCts.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastFailure = ex;
                    Log.Warning(ex,
                        "Mandatory tosu recovery restart sequence for attempt {AttemptId} failed; it remains pending",
                        attemptId);
                }

                if (!restarted)
                {
                    failedBatches++;
                    Log.Error(lastFailure,
                        "Mandatory tosu recovery restart for attempt {AttemptId} remains pending after {AttemptCount} attempts",
                        attemptId,
                        failedBatches * 3);
                    try
                    {
                        _store?.Update(state => state with
                        {
                            Tracking = state.Tracking with
                            {
                                Health = HealthLevel.Degraded,
                                Detail = $"mandatory tosu restart pending after recovered attempt {attemptId}",
                            },
                        });
                    }
                    catch (Exception ex)
                    {
                        // Status publication is optional; it must never abandon
                        // the required restart loop.
                        Log.Debug(ex, "Could not publish pending mandatory tosu restart status");
                    }

                    // A recovered attempt is proof that tosu's tracker is broken.
                    // Never abandon the required restart after a short transient
                    // failure; retain it until success or application shutdown.
                    var retrySeconds = Math.Min(30, 5 * Math.Pow(2, Math.Min(failedBatches - 1, 3)));
                    await Task.Delay(TimeSpan.FromSeconds(retrySeconds), _backgroundCts.Token);
                }
            }
        }
        catch (OperationCanceledException) when (_backgroundCts.IsCancellationRequested)
        {
        }
        finally
        {
            var startNextGeneration = false;
            lock (_recoveryRestartGate)
            {
                if (restarted)
                {
                    foreach (var queuedAttemptId in generationAttempts)
                    {
                        _completedRecoveryRestartAttempts.Add(queuedAttemptId);
                        _queuedRecoveryRestartAttempts.Remove(queuedAttemptId);
                    }
                }
                else if (_backgroundCts.IsCancellationRequested)
                {
                    _queuedRecoveryRestartAttempts.Clear();
                }
                _recoveryRestartWorkerRunning = false;
                if (!_backgroundCts.IsCancellationRequested
                    && _queuedRecoveryRestartAttempts.Count > 0)
                {
                    _recoveryRestartWorkerRunning = true;
                    startNextGeneration = true;
                }
            }
            if (startNextGeneration)
            {
                TrackBackground(
                    RestartTosuAfterRecoveryAsync(),
                    "next mandatory tosu restart recovery generation");
            }
        }
    }

    private static void SyncStartupRegistration(KumoriSettings settings)
    {
        try
        {
            if (!StartupRegistration.IsConfigured(
                    settings.Startup.RunAtLogin,
                    settings.Startup.StartMinimized,
                    settings.Startup.ExecutablePath))
            {
                StartupRegistration.SetEnabled(
                    settings.Startup.RunAtLogin,
                    settings.Startup.StartMinimized,
                    settings.Startup.ExecutablePath);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Startup registration sync failed");
        }
    }

    private void TryActivateOsuCompanions(KumoriSettings settings)
    {
        lock (_osuCompanionGate)
        {
            if (_managedOsuSessionActive)
                return;
            _managedOsuSessionActive = true;
        }

        // Display and tablet preparation is allowed only after the process PID
        // has been confirmed by the debounce policy. The user's opt-in suspend
        // setting is honored inside the gameplay-excluding transition; osu! is
        // never terminated or relaunched.
        TrackBackground(
            RunBelowNormalAsync(
                () =>
                {
                    ActivateDualModeIfRequested(settings);
                    return true;
                },
                _backgroundCts.Token),
            "prepare display and tablet companions");
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

    private void EnsureTosuForOsu(AppStateStore store)
    {
        lock (_osuCompanionGate)
        {
            if (_tosuStartedForOsu)
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

                if (File.Exists(AppPaths.TosuExecutable))
                    TosuManager.LaunchInstalled();
                else
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
            if (!_managedOsuSessionActive && !_tosuStartedForOsu && !_dualModeActivatedForOsu)
            {
                return;
            }

            _managedOsuSessionActive = false;
            _tosuStartedForOsu = false;
            promptToRestoreDualMode = _dualModeActivatedForOsu;
            // The user's response now owns restoration. Clear the session flag
            // first so subsequent monitor ticks do not show duplicate prompts.
            _dualModeActivatedForOsu = false;
        }

        // OTD follows Kumori's lifetime, while the display remains stable across
        // osu! process transitions. Only tosu owns process-local pointers and
        // must be closed when the confirmed osu! session ends.
        TosuManager.CloseOwned();
        var otdRunning = OpenTabletDriverService.IsRunning();
        PublishCompanionStatus(c => c with
        {
            OpenTabletDriverLaunched = otdRunning,
            OpenTabletDriverDetail = otdRunning
                ? "OpenTabletDriver running with Kumori"
                : c.OpenTabletDriverDetail,
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
            if (restored && settings.Current.OpenTabletDriver.AutoLaunch)
                OpenTabletDriverService.RefreshAfterDisplayTransition();
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
                var observedProcessIds = OsuProcessDetector.RunningProcessIds();
                var observation = _companionTransitionPolicy.Observe(observedProcessIds);
                var osuRunning = observation.IsRunning;
                var transition = observation.Transition;
                if (transition != CompanionTransition.None)
                {
                    Log.Information(
                        "Confirmed osu! companion transition {Transition} for process IDs {ProcessIds}",
                        transition,
                        observation.ProcessIds.Order().ToArray());
                }
                SyncOpenTabletDriverLifetime(settings.Current);
                UpdateTrayDualModeToggle(settings.Current.Display.AutoSwitchDualMode);
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
                    // tracking companion. Display switching remains transition-only.
                    EnsureTosuForOsu(store);
                }
                else if (transition == CompanionTransition.Replaced)
                {
                    // A confirmed replacement PID invalidates tosu's process
                    // pointers, but does not end the display/tablet session.
                    _tracking?.NotifyOsuStopped();
                    TosuManager.CloseOwned();
                    lock (_osuCompanionGate)
                    {
                        _tosuStartedForOsu = false;
                    }
                    EnsureTosuForOsu(store);
                }
                else if (transition == CompanionTransition.Stopped)
                {
                    _tracking?.NotifyOsuStopped();
                    EndOsuCompanionSession();
                }
                var transitionPending = observedProcessIds.Count > 0
                    && (!osuRunning || !observedProcessIds.SetEquals(observation.ProcessIds));
                await Task.Delay(
                    transitionPending
                        ? CompanionTransitionMonitorInterval
                        : osuRunning ? CompanionMonitorInterval : IdleCompanionMonitorInterval,
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

    private void SyncOpenTabletDriverLifetime(KumoriSettings settings)
    {
        if (!settings.OpenTabletDriver.AutoLaunch)
        {
            var stopOwned = false;
            lock (_otdLifetimeGate)
            {
                stopOwned = _otdLifetimeManagementEnabled;
                _otdLifetimeManagementEnabled = false;
                _otdLaunchAttemptedForKumori = false;
            }
            OpenTabletDriverService.StopDisplayMappingRefresh();
            OpenTabletDriverService.CloseOwned();
            if (stopOwned)
            {
                PublishCompanionStatus(c => c with
                {
                    OpenTabletDriverEnabled = false,
                    OpenTabletDriverLaunched = OpenTabletDriverService.IsRunning(),
                    OpenTabletDriverDetail = "OpenTabletDriver auto-launch is off",
                });
            }
            return;
        }

        EnsureOpenTabletDriverForKumori(settings);
        if (OpenTabletDriverService.RefreshDisplayMappingsIfChanged())
        {
            PublishCompanionStatus(c => c with
            {
                OpenTabletDriverDetail = "OpenTabletDriver display cache refreshed",
            });
        }
    }

    private void HandleSettingsChanged(KumoriSettings settings)
    {
        if (_trackingRuntime is not null && !_backgroundCts.IsCancellationRequested)
            TrackBackground(_trackingRuntime.ApplyAsync(settings), "apply saved tracking settings");

        var synchronizeOtd = false;
        lock (_otdLifetimeGate)
        {
            if (_observedOtdAutoLaunch != settings.OpenTabletDriver.AutoLaunch
                || !string.Equals(
                    _observedOtdInstallPath,
                    settings.OpenTabletDriver.InstallPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                _observedOtdAutoLaunch = settings.OpenTabletDriver.AutoLaunch;
                _observedOtdInstallPath = settings.OpenTabletDriver.InstallPath;
                synchronizeOtd = true;
            }
        }
        if (!synchronizeOtd || _backgroundCts.IsCancellationRequested)
            return;

        Log.Information(
            "Applying saved OpenTabletDriver settings immediately: AutoLaunch={AutoLaunch}, InstallPath={InstallPath}",
            settings.OpenTabletDriver.AutoLaunch,
            settings.OpenTabletDriver.InstallPath);
        TrackBackground(
            RunBelowNormalAsync(
                () =>
                {
                    SyncOpenTabletDriverLifetime(settings);
                    return true;
                },
                _backgroundCts.Token),
            "apply saved OpenTabletDriver settings");
    }

    private void EnsureOpenTabletDriverForKumori(KumoriSettings settings)
    {
        lock (_otdLifetimeGate)
        {
            _otdLifetimeManagementEnabled = true;
            if (_otdLaunchInProgress
                || (_otdLaunchAttemptedForKumori && OpenTabletDriverService.IsUiRunning()))
                return;
            _otdLaunchAttemptedForKumori = true;
            _otdLaunchInProgress = true;
        }

        try
        {
            _backgroundCts.Token.ThrowIfCancellationRequested();
            var uiWasRunning = OpenTabletDriverService.IsUiRunning();
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

            var launched = !uiWasRunning && OpenTabletDriverService.Launch(installation.ExecutablePath);
            var uiRunning = launched || OpenTabletDriverService.IsUiRunning();
            var running = OpenTabletDriverService.IsRunning();
            if (running)
                OpenTabletDriverService.ConfigureDisplayMappingRefresh(installation.ExecutablePath);
            PublishCompanionStatus(c => c with
            {
                OpenTabletDriverEnabled = true,
                OpenTabletDriverLaunched = running,
                OpenTabletDriverDetail = launched
                    ? "OpenTabletDriver launched in the tray"
                    : uiRunning
                        ? "OpenTabletDriver already running"
                        : running
                            ? "OpenTabletDriver daemon is running, but its tray failed to open"
                            : "OpenTabletDriver launch failed",
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
        finally
        {
            lock (_otdLifetimeGate)
            {
                _otdLaunchInProgress = false;
            }
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
            var coordinator = _gameplayWork;
            var transitionExecuted = false;
            var suspensionFailed = false;
            bool ExecuteTransition(Func<bool> transition)
            {
                bool RunTransition()
                {
                    transitionExecuted = true;
                    bool ExecuteAndRefreshOpenTabletDriver()
                    {
                        var transitioned = transition();
                        if (transitioned && settings.OpenTabletDriver.AutoLaunch)
                            OpenTabletDriverService.RefreshAfterDisplayTransition();
                        return transitioned;
                    }

                    if (!settings.Display.SuspendOsuDuringDualModeSwitch)
                        return ExecuteAndRefreshOpenTabletDriver();

                    using var suspension = OsuProcessDetector.TrySuspendRunning();
                    if (suspension is null)
                    {
                        suspensionFailed = true;
                        Log.Warning("Could not suspend every osu! process for LG dual-mode switching");
                        return false;
                    }

                    Log.Information("osu! suspended while dual mode is switched");
                    return ExecuteAndRefreshOpenTabletDriver();
                }

                if (coordinator is null)
                    return RunTransition();
                if (!coordinator.TryExecuteGameplayExcludingTransition(
                        _backgroundCts.Token,
                        RunTransition,
                        out var result))
                {
                    return false;
                }
                return result;
            }

            var activated = DualModeService.Activate(
                settings,
                _backgroundCts.Token,
                ExecuteTransition);
            var active = DualModeService.IsDualModeActive();
            if (!wasActive && !transitionExecuted && !active)
            {
                Log.Information("Skipped automatic dual-mode switch because gameplay began during companion preparation");
                PublishCompanionStatus(c => c with
                {
                    DualModeEnabled = true,
                    DualModeCommandSent = false,
                    DualModeActive = false,
                    DualModeDetail = "Display switch skipped because gameplay started",
                });
                return;
            }
            if (suspensionFailed && !active)
            {
                PublishCompanionStatus(c => c with
                {
                    DualModeEnabled = true,
                    DualModeCommandSent = false,
                    DualModeActive = false,
                    DualModeDetail = "Display switch failed because osu! could not be suspended",
                });
                return;
            }
            Log.Information(
                "LG dual-mode activation completed: WasActive={WasActive}, Activated={Activated}, Active={Active}, SuspendOsu={SuspendOsu}",
                wasActive,
                activated,
                active,
                settings.Display.SuspendOsuDuringDualModeSwitch);
            PublishCompanionStatus(c => c with
            {
                DualModeEnabled = true,
                DualModeCommandSent = activated,
                DualModeActive = active,
                DualModeDetail = active
                    ? (wasActive ? "Dual mode already active" : "Dual mode active")
                    : activated ? "Dual mode command sent; waiting for display" : "Dual mode command failed",
            });
            if (activated && !wasActive && active)
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

    private void FlushTrayState()
    {
        string status;
        bool endSessionEnabled;
        lock (_trayStateGate)
        {
            if (!_trayStateDirty)
                return;
            _trayStateDirty = false;
            status = _pendingTrayStatus;
            endSessionEnabled = _pendingTrayEndSessionEnabled;
        }

        _tray?.UpdateStatus(status);
        _tray?.SetEndSessionEnabled(endSessionEnabled);
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
            var sent = false;
            if (_gameplayWork is { } coordinator
                && !coordinator.TryExecuteGameplayExcludingTransition(
                    _backgroundCts.Token,
                    () => DualModeService.Toggle(settings.Current),
                    out sent))
            {
                PublishCompanionStatus(c => c with
                {
                    DualModeDetail = "Manual display switch blocked during gameplay",
                });
                return;
            }
            else if (_gameplayWork is null)
            {
                sent = DualModeService.Toggle(settings.Current);
            }
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

}
