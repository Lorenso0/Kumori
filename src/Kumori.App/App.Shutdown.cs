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
    internal static AttemptSqliteSink CreateAttemptPersistence(SqliteConnectionFactory factory) =>
        new(factory);

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
            if (_trackingRuntime is not null)
                await AwaitBoundedAsync(_trackingRuntime.DisposeAsync().AsTask(), TimeSpan.FromSeconds(3));
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
        if (_trackingRuntime is not null)
        {
            _trackingRuntime.Dispose();
            try { _trackingRuntime.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3)); } catch { }
        }
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
}
