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
    internal static bool ShouldLaunchTosuForRecovery(bool osuRunning) => osuRunning;

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
                                // Historical/startup reconciliation can discover
                                // a broken result while osu! is closed. That is
                                // not permission to launch tosu early: the next
                                // confirmed osu! startup performs a clean restart.
                                if (!ShouldLaunchTosuForRecovery(OsuProcessDetector.IsRunning()))
                                {
                                    TosuManager.CloseOwned();
                                    lock (_osuCompanionGate)
                                        _tosuStartedForOsu = false;
                                    Log.Information(
                                        "Skipped recovery tosu restart for attempt {AttemptId} because osu! is not running; confirmed osu! startup will launch a fresh process",
                                        attemptId);
                                    return true;
                                }
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

                if (!File.Exists(AppPaths.TosuExecutable))
                    await TosuManager.EnsureInstalledAsync(cancellationToken: _backgroundCts.Token);

                // Always replace an old managed process after the confirmed
                // osu! startup window. Reusing one left by an earlier Kumori or
                // osu! process preserves stale GameBase pointers and produces
                // all-zero gameplay telemetry on the first play.
                await TosuManager.RestartAsync(
                    _backgroundCts.Token,
                    reason: "confirmed osu! startup");
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
                RefreshTrayDualModeControls(settings.Current);
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
        UpdateTrayDualModeControls(
            _trayDualModeCompatible == true,
            settings.Display.AutoSwitchDualMode);
        PublishCompanionStatus(c => c with
        {
            DualModeEnabled = settings.Display.AutoSwitchDualMode,
        });

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

    internal static string FormatTrayTrackingStatus(AppState state)
    {
        if (!state.Companions.OsuRunning)
        {
            return "tosu: Waiting for osu!";
        }

        return state.Tracking.TosuConnected
            ? state.Tracking.CurrentBeatmap ?? "tosu: Connected"
            : state.Tracking.Detail ?? "tosu: Starting...";
    }

    private void RefreshTrayDualModeControls(
        KumoriSettings settings,
        bool forceCompatibilityCheck = false)
    {
        var now = DateTimeOffset.UtcNow;
        var compatible = _trayDualModeCompatible == true;
        if (forceCompatibilityCheck || now >= _nextDualModeCompatibilityCheckUtc)
        {
            compatible = DualModeService.HasCompatibleMonitor();
            _nextDualModeCompatibilityCheckUtc = now.AddSeconds(10);
        }

        UpdateTrayDualModeControls(compatible, settings.Display.AutoSwitchDualMode);
    }

    private void UpdateTrayDualModeControls(bool compatible, bool autoSwitchEnabled)
    {
        if (_trayDualModeCompatible == compatible
            && _trayDualModeAutoSwitchEnabled == autoSwitchEnabled)
        {
            return;
        }

        _trayDualModeCompatible = compatible;
        _trayDualModeAutoSwitchEnabled = autoSwitchEnabled;
        Dispatcher.InvokeAsync(() => _tray?.SetDualModeControls(compatible, autoSwitchEnabled));
    }

    private void ToggleDualModeAutoSwitchFromTray(SettingsService settings)
    {
        try
        {
            settings.Update(current =>
                current.Display.AutoSwitchDualMode = !current.Display.AutoSwitchDualMode);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not update LG dual-mode auto-switch from the tray");
            UpdateTrayDualModeControls(
                _trayDualModeCompatible == true,
                settings.Current.Display.AutoSwitchDualMode);
        }
    }

    private async Task ToggleDualModeFromTrayAsync()
    {
        var settings = new SettingsService();
        settings.Load();
        if (!DualModeService.HasCompatibleMonitor())
        {
            UpdateTrayDualModeControls(false, settings.Current.Display.AutoSwitchDualMode);
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
