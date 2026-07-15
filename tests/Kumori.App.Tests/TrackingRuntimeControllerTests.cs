using Kumori.Core.Settings;
using Kumori.Core.State;
using Xunit;

namespace Kumori.App.Tests;

public sealed class TrackingRuntimeControllerTests
{
    [Fact]
    public async Task AppliesEnableAndDisableImmediatelyWhileIdle()
    {
        var store = new AppStateStore();
        var starts = new List<KumoriSettings>();
        var stops = 0;
        using var controller = new TrackingRuntimeController(
            store,
            settings => { starts.Add(settings); return Task.CompletedTask; },
            () => { stops++; return Task.CompletedTask; },
            (task, _) => task.GetAwaiter().GetResult(),
            _ => { });

        await controller.ApplyAsync(Settings(enabled: true, capture: true));
        await controller.ApplyAsync(Settings(enabled: false, capture: false));

        Assert.Single(starts);
        Assert.Equal(1, stops);
    }

    [Fact]
    public async Task DefersRebuildDuringPlayAndAppliesOnlyLatestConfigurationAtBoundary()
    {
        var store = new AppStateStore();
        var startedCaptureValues = new List<bool>();
        var stops = 0;
        Task? scheduled = null;
        string? status = null;
        using var controller = new TrackingRuntimeController(
            store,
            settings =>
            {
                startedCaptureValues.Add(settings.Capture.LazerReplayFrameEnabled);
                return Task.CompletedTask;
            },
            () => { stops++; return Task.CompletedTask; },
            (task, _) => scheduled = task,
            value => status = value);

        await controller.ApplyAsync(Settings(enabled: true, capture: true));
        store.Update(state => state with
        {
            Tracking = state.Tracking with { LatestTelemetry = new TosuTelemetry { IsPlaying = true } },
        });

        await controller.ApplyAsync(Settings(enabled: false, capture: false));
        await controller.ApplyAsync(Settings(enabled: true, capture: false));

        Assert.Equal([true], startedCaptureValues);
        Assert.Equal(0, stops);
        Assert.Contains("current play", status, StringComparison.OrdinalIgnoreCase);

        store.Update(state => state with
        {
            Tracking = state.Tracking with { LatestTelemetry = new TosuTelemetry { IsPlaying = false } },
        });
        Assert.NotNull(scheduled);
        await scheduled!;

        Assert.Equal([true, false], startedCaptureValues);
        Assert.Equal(1, stops);
    }

    [Fact]
    public async Task DisposedControllerDoesNotApplyPendingChangeAfterPlay()
    {
        var store = new AppStateStore();
        var stops = 0;
        var controller = new TrackingRuntimeController(
            store,
            _ => Task.CompletedTask,
            () => { stops++; return Task.CompletedTask; },
            (task, _) => task.GetAwaiter().GetResult(),
            _ => { });

        await controller.ApplyAsync(Settings(enabled: true, capture: true));
        store.Update(state => state with
        {
            Tracking = state.Tracking with { LatestTelemetry = new TosuTelemetry { IsPlaying = true } },
        });
        await controller.ApplyAsync(Settings(enabled: false, capture: false));
        controller.Dispose();
        store.Update(state => state with
        {
            Tracking = state.Tracking with { LatestTelemetry = new TosuTelemetry { IsPlaying = false } },
        });

        Assert.Equal(0, stops);
    }

    private static KumoriSettings Settings(bool enabled, bool capture)
    {
        var settings = new KumoriSettings();
        settings.Tracking.Enabled = enabled;
        settings.Capture.LazerReplayFrameEnabled = capture;
        return settings;
    }
}
