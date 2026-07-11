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
    // osu!lazer exposes its managed object graph shortly after the process is
    // visible. Starting tosu immediately can race that initialization, leaving
    // it with a temporary GameBase resolution failure and zero telemetry.
    private static readonly TimeSpan TosuStartupGracePeriod = TimeSpan.FromSeconds(2);
    private SingleInstance? _singleInstance;
    private TrayIconService? _tray;
    private MainWindow? _mainWindow;
    private TosuTrackingService? _tracking;
    private LazerReplayFrameCaptureService? _lazerReplayFrames;
    private AppStateStore? _store;
    private CancellationTokenSource? _companionMonitorCts;
    private Task? _companionMonitorTask;
    private bool _otdAutoLaunchAttemptedForOsu;
    private bool _dualModeActivatedForOsu;
    private bool _companionRestartInProgress;
    private bool _managedOsuSessionActive;
    private bool _tosuStartedForOsu;
    private readonly object _osuCompanionGate = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppDataOrganizer.Organize();

        _singleInstance = new SingleInstance();
        if (!_singleInstance.IsPrimaryInstance)
        {
            _singleInstance.SignalPrimaryInstance();
            _singleInstance.Dispose();
            Shutdown();
            return;
        }

        Directory.CreateDirectory(AppPaths.AppLogDir);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                Path.Combine(AppPaths.AppLogDir, "kumori-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: AppPaths.LogRetentionDays)
            .CreateLogger();
        Log.Information("Kumori starting");

        InstallCrashHandlers();

        var settings = new SettingsService();
        settings.Load();
        SyncStartupRegistration(settings.Current);

        var store = new AppStateStore();
        _store = store;
        var factory = new SqliteConnectionFactory(AppPaths.TrackingDatabase, readOnly: false);
        var attempts = new AttemptRepository(factory);
        var details = new AttemptDetailsRepository(factory);
        var analytics = new AnalyticsRepository(factory);
        var movement = new MovementRepository(factory);
        var replayViewer = new ReplayViewerContractService(details, movement, settings.Current);
        var maintenance = new TrackingMaintenanceRepository(factory);
        var sessions = new SessionRepository(factory);
        var viewModel = new MainViewModel(store, attempts, details, analytics, settings, replayViewer, maintenance, sessions);

        // Shell first — no data work before first paint.
        _mainWindow = new MainWindow(viewModel, settings);
        _mainWindow.Show();
        if (!settings.Current.FirstRunCompleted ||
            settings.Current.OnboardingVersion < WelcomeWindow.CurrentOnboardingVersion)
        {
            Dispatcher.InvokeAsync(() =>
            {
                new WelcomeWindow(settings, store) { Owner = _mainWindow }.Show();
            }, DispatcherPriority.ContextIdle);
        }

        _tray = new TrayIconService(
            "Kumori — osu! Tracking",
            Path.Combine(AppContext.BaseDirectory, "assets", "logo.ico"));
        _tray.OpenRequested += ShowMainWindow;
        _tray.SettingsRequested += () => Dispatcher.InvokeAsync(() =>
            new SettingsWindow(settings) { Owner = _mainWindow }.ShowDialog());
        _tray.LogsRequested += () => Dispatcher.InvokeAsync(() =>
            Process.Start(new ProcessStartInfo { FileName = AppPaths.LogDir, UseShellExecute = true }));
        _tray.EndSessionRequested += () => Dispatcher.InvokeAsync(() =>
            viewModel.EndSessionCommand.Execute(null));
        _tray.ExitRequested += () =>
        {
            if (_mainWindow is not null)
            {
                _mainWindow.ForceClose = true;
            }
            Shutdown();
        };

        _singleInstance.ListenForActivation(
            () => Dispatcher.InvokeAsync(ShowMainWindow));
        store.StateChanged += state =>
        {
            var status = state.Tracking.TosuConnected
                ? state.Tracking.CurrentBeatmap ?? "Tracker connected"
                : state.Tracking.Detail ?? "Tracker not running";
            Dispatcher.InvokeAsync(() => _tray?.UpdateStatus(status));
        };
        _companionMonitorCts = new CancellationTokenSource();
        _companionMonitorTask = Task.Run(() => CompanionMonitorLoopAsync(store, settings, _companionMonitorCts.Token));
        _ = CheckForTosuUpdatesOnLaunchAsync();

        // Background services start only after the shell is visible
        // (no-flicker startup plan: shell first, services second).
        if (settings.Current.Tracking.Enabled)
        {
            var trackingSink = new AttemptSqliteSink(factory);
            IAttemptSink attemptSink = new StatePublishingAttemptSink(
                trackingSink,
                () => trackingSink.CurrentAttemptId,
                HasReplayData,
                store);
            if (settings.Current.Capture.LazerReplayFrameEnabled)
            {
                _lazerReplayFrames = new LazerReplayFrameCaptureService(
                    store,
                    factory,
                    () => trackingSink.CurrentAttemptId,
                    new LazerMemoryReplayFrameSource(),
                    sourceName: "lazer_memory");
                _lazerReplayFrames.Start();
                attemptSink = new CompositeAttemptSink(
                    new StatePublishingAttemptSink(
                        trackingSink,
                        () => trackingSink.CurrentAttemptId,
                        HasReplayData,
                        store),
                    new BestEffortAttemptSink(_lazerReplayFrames, "lazer replay-frame capture"));
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
            }
            _tracking = new TosuTrackingService(
                store,
                attemptTracker: new AttemptTracker(attemptSink),
                sessionTracker: new SessionTracker(new StatePublishingSessionSink(trackingSink, store)),
                primaryMediaMirror: settings.Current.Media.PrimaryMirror,
                fallbackMediaMirrors: settings.Current.Media.FallbackMirrors,
                recordPackets: settings.Current.Tracking.PacketRecordingEnabled);
            _tracking.Start();
            viewModel.SetEndLiveSessionHandler(() => Task.Run(() => _tracking?.EndSession() ?? false));

            bool HasReplayData(long attemptId) =>
                movement.GetMetadata(attemptId) is { SampleCount: > 0 };
        }

        // Hydrate asynchronously after the shell is visible.
        _ = viewModel.HydrateAsync();
    }

    private async Task CheckForTosuUpdatesOnLaunchAsync()
    {
        try
        {
            var wasInstalled = File.Exists(AppPaths.TosuExecutable);
            var result = await TosuManager.CheckForUpdatesAsync();
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
        catch (Exception ex)
        {
            Log.Warning(ex, "Managed tosu update check failed during Kumori startup");
        }
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
    }

    private void InstallCrashHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            LogCrash("DispatcherUnhandledException", args.Exception);
            args.Handled = true; // keep the app alive for UI-thread faults
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
            Directory.CreateDirectory(AppPaths.AppLogDir);
            File.AppendAllText(
                AppPaths.CrashLogFile,
                $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] {source}: {ex}\n\n");
        }
        catch
        {
            // never crash the crash handler
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_tracking is not null)
        {
            // Bounded wait: shutdown must not hang on a stuck socket.
            var dispose = _tracking.DisposeAsync().AsTask();
            dispose.Wait(TimeSpan.FromSeconds(3));
        }
        if (_lazerReplayFrames is not null)
        {
            var dispose = _lazerReplayFrames.DisposeAsync().AsTask();
            dispose.Wait(TimeSpan.FromSeconds(3));
        }
        _companionMonitorCts?.Cancel();
        try { _companionMonitorTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _companionMonitorCts?.Dispose();
        DeactivateDualModeIfRequested();
        OpenTabletDriverService.CloseOwned();
        TosuManager.CloseOwned();
        _tray?.Dispose();
        _singleInstance?.Dispose();
        Log.Information("Kumori exiting");
        Log.CloseAndFlush();
        base.OnExit(e);
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
            RestartOsuWithCompanions(settings);
            return;
        }

        lock (_osuCompanionGate)
        {
            LaunchOpenTabletDriverIfRequested(settings);
            ActivateDualModeIfRequested(settings);
        }
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

        _ = Task.Run(() =>
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
        });
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

        _ = Task.Run(async () =>
        {
            try
            {
                Log.Information("Waiting {DelaySeconds:0}s for osu!lazer memory to initialize before launching tosu", TosuStartupGracePeriod.TotalSeconds);
                await Task.Delay(TosuStartupGracePeriod);
                if (!OsuProcessDetector.IsRunning())
                {
                    lock (_osuCompanionGate)
                    {
                        _tosuStartedForOsu = false;
                    }
                    return;
                }

                await TosuManager.EnsureInstalledAndLaunchAsync();
                if (!OsuProcessDetector.IsRunning())
                {
                    TosuManager.CloseOwned();
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
        });
    }

    private void EndOsuCompanionSession()
    {
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
        }

        // Keep the shutdown order deterministic: restore LG first, then release
        // the tablet driver and finally stop the owned tosu instance.
        DeactivateDualModeIfRequested();
        OpenTabletDriverService.CloseOwned();
        TosuManager.CloseOwned();
        lock (_osuCompanionGate)
        {
            _dualModeActivatedForOsu = false;
        }
        PublishCompanionStatus(c => c with
        {
            OpenTabletDriverLaunched = false,
            OpenTabletDriverDetail = "OpenTabletDriver closed with osu!",
            DualModeActive = false,
            DualModeDetail = "Dual mode restored after osu! closed",
        });
    }

    private async Task CompanionMonitorLoopAsync(AppStateStore store, SettingsService settings, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var osuRunning = OsuProcessDetector.IsRunning();
                store.Update(s => s with
                {
                    Companions = s.Companions with
                    {
                        OsuRunning = osuRunning,
                        OpenTabletDriverEnabled = settings.Current.OpenTabletDriver.AutoLaunch,
                        DualModeEnabled = settings.Current.Display.AutoSwitchDualMode,
                    },
                });
                if (osuRunning)
                {
                    TryActivateOsuCompanions(settings.Current);
                    EnsureTosuForOsu(store);
                }
                else
                {
                    _tracking?.NotifyOsuStopped();
                    EndOsuCompanionSession();
                }
                await Task.Delay(TimeSpan.FromSeconds(3), token);
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
