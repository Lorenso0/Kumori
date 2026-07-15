using Kumori.Core.Settings;
using Kumori.Core.State;

namespace Kumori.App;

/// <summary>
/// Serializes live tracking configuration changes and defers destructive
/// service rebuilds until the current play has reached a safe boundary.
/// </summary>
internal sealed class TrackingRuntimeController : IDisposable, IAsyncDisposable
{
    private readonly AppStateStore store;
    private readonly Func<KumoriSettings, Task> start;
    private readonly Func<Task> stop;
    private readonly Action<Task, string> schedule;
    private readonly Action<string> publishStatus;
    private readonly SemaphoreSlim reconcileGate = new(1, 1);
    private readonly object stateGate = new();
    private RuntimeOptions applied;
    private RuntimeOptions desired;
    private KumoriSettings? desiredSettings;
    private bool pendingForPlayBoundary;
    private bool disposed;

    public TrackingRuntimeController(
        AppStateStore store,
        Func<KumoriSettings, Task> start,
        Func<Task> stop,
        Action<Task, string> schedule,
        Action<string> publishStatus)
    {
        this.store = store;
        this.start = start;
        this.stop = stop;
        this.schedule = schedule;
        this.publishStatus = publishStatus;
        store.StateChanged += OnStateChanged;
    }

    public Task ApplyAsync(KumoriSettings settings)
    {
        lock (stateGate)
        {
            if (disposed)
                return Task.CompletedTask;
            desiredSettings = settings;
            desired = RuntimeOptions.From(settings);
        }
        return ReconcileAsync();
    }

    private async Task ReconcileAsync()
    {
        await reconcileGate.WaitAsync();
        try
        {
            while (true)
            {
                RuntimeOptions next;
                KumoriSettings snapshot;
                lock (stateGate)
                {
                    if (disposed)
                        return;
                    next = desired;
                    snapshot = desiredSettings ?? new KumoriSettings();
                }
                if (next == applied)
                {
                    ClearPendingForPlayBoundary();
                    return;
                }

                var requiresStoppingCurrentRuntime = applied.Enabled
                    && (!next.Enabled || next.CaptureEnabled != applied.CaptureEnabled);
                while (requiresStoppingCurrentRuntime && store.Current.Tracking.LatestTelemetry?.IsPlaying == true)
                {
                    if (!MarkPendingForPlayBoundary())
                        return;

                    // The results transition can race the first telemetry read.
                    // Recheck after publishing the pending flag. If a very fast
                    // results-to-next-play transition cleared the flag, arm it
                    // again for that new play before returning.
                    if (store.Current.Tracking.LatestTelemetry?.IsPlaying != true)
                    {
                        ClearPendingForPlayBoundary();
                        break;
                    }
                    if (!IsPendingForPlayBoundary())
                        continue;
                    publishStatus("Tracking settings saved; the current play will finish before they are applied.");
                    return;
                }

                ClearPendingForPlayBoundary();
                if (applied.Enabled)
                {
                    await stop();
                    if (IsDisposed())
                        return;
                }
                if (next.Enabled)
                {
                    await start(snapshot);
                    if (IsDisposed())
                    {
                        // Dispose may race the synchronous service construction.
                        // Tear that just-created runtime back down before an async
                        // disposer is allowed to complete.
                        await stop();
                        return;
                    }
                }
                applied = next;
                publishStatus(next.Enabled
                    ? next.CaptureEnabled ? "Tracking and replay capture settings applied." : "Tracking settings applied. Replay capture is off."
                    : "Play tracking is disabled.");
            }
        }
        finally
        {
            reconcileGate.Release();
        }
    }

    private void OnStateChanged(AppState state)
    {
        lock (stateGate)
        {
            if (!pendingForPlayBoundary || state.Tracking.LatestTelemetry?.IsPlaying == true || disposed)
                return;
            pendingForPlayBoundary = false;
        }
        schedule(ReconcileAsync(), "apply deferred tracking settings");
    }

    public void Dispose()
    {
        MarkDisposed();
    }

    public async ValueTask DisposeAsync()
    {
        MarkDisposed();
        await reconcileGate.WaitAsync().ConfigureAwait(false);
        reconcileGate.Release();
    }

    private bool IsDisposed()
    {
        lock (stateGate)
            return disposed;
    }

    private bool MarkPendingForPlayBoundary()
    {
        lock (stateGate)
        {
            if (disposed)
                return false;
            pendingForPlayBoundary = true;
            return true;
        }
    }

    private void ClearPendingForPlayBoundary()
    {
        lock (stateGate)
            pendingForPlayBoundary = false;
    }

    private bool IsPendingForPlayBoundary()
    {
        lock (stateGate)
            return pendingForPlayBoundary;
    }

    private void MarkDisposed()
    {
        var unsubscribe = false;
        lock (stateGate)
        {
            if (!disposed)
            {
                disposed = true;
                pendingForPlayBoundary = false;
                unsubscribe = true;
            }
        }
        if (unsubscribe)
            store.StateChanged -= OnStateChanged;
    }

    private readonly record struct RuntimeOptions(bool Enabled, bool CaptureEnabled)
    {
        public static RuntimeOptions From(KumoriSettings settings) => new(
            settings.Tracking.Enabled,
            settings.Capture.LazerReplayFrameEnabled);
    }
}
