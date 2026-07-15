using Kumori.Core.Settings;
using Kumori.Core.State;

namespace Kumori.App;

/// <summary>
/// Serializes live tracking configuration changes and defers destructive
/// service rebuilds until the current play has reached a safe boundary.
/// </summary>
internal sealed class TrackingRuntimeController : IDisposable
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
            while (!disposed)
            {
                RuntimeOptions next;
                KumoriSettings snapshot;
                lock (stateGate)
                {
                    next = desired;
                    snapshot = desiredSettings ?? new KumoriSettings();
                }
                if (next == applied)
                {
                    pendingForPlayBoundary = false;
                    return;
                }

                var requiresStoppingCurrentRuntime = applied.Enabled
                    && (!next.Enabled || next.CaptureEnabled != applied.CaptureEnabled);
                if (requiresStoppingCurrentRuntime && store.Current.Tracking.LatestTelemetry?.IsPlaying == true)
                {
                    pendingForPlayBoundary = true;
                    publishStatus("Tracking settings saved; the current play will finish before they are applied.");
                    return;
                }

                pendingForPlayBoundary = false;
                if (applied.Enabled)
                    await stop();
                if (next.Enabled)
                    await start(snapshot);
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
        if (!pendingForPlayBoundary || state.Tracking.LatestTelemetry?.IsPlaying == true || disposed)
            return;
        pendingForPlayBoundary = false;
        schedule(ReconcileAsync(), "apply deferred tracking settings");
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        store.StateChanged -= OnStateChanged;
    }

    private readonly record struct RuntimeOptions(bool Enabled, bool CaptureEnabled)
    {
        public static RuntimeOptions From(KumoriSettings settings) => new(
            settings.Tracking.Enabled,
            settings.Capture.LazerReplayFrameEnabled);
    }
}
