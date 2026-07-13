using Kumori.ReplayViewer;
using osu.Framework.Platform;
using osu.Framework.Graphics;
using osuTK;
using Xunit;

namespace Kumori.Core.Tests;

public sealed class KumoriViewerConfigTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"kumori-viewer-tests-{Guid.NewGuid():N}");

    [Fact]
    public void AnalyzerDefaultsPrioritiseMissesAndSliderBreaks()
    {
        var storage = new NativeStorage(directory);
        using var config = new KumoriViewerConfig(storage);

        Assert.True(config.Get<bool>(KumoriViewerSetting.ShowMissMarkers));
        Assert.True(config.Get<bool>(KumoriViewerSetting.ShowSliderBreakMarkers));
        Assert.False(config.Get<bool>(KumoriViewerSetting.ShowMehMarkers));
        Assert.False(config.Get<bool>(KumoriViewerSetting.ShowOkMarkers));
        Assert.True(config.Get<bool>(KumoriViewerSetting.MissAnalyzerLoopEnabled));
        Assert.Equal(0.5, config.Get<double>(KumoriViewerSetting.MissAnalyzerPlaybackRate));
        Assert.Equal(800, config.Get<double>(KumoriViewerSetting.MissAnalyzerLoopBefore));
        Assert.True(config.Get<bool>(KumoriViewerSetting.MissAnalyzerShowMovementSamples));
        Assert.True(config.Get<bool>(KumoriViewerSetting.MissAnalyzerShowHeldSamples));
        Assert.Equal(1, config.Get<double>(KumoriViewerSetting.MasterVolume));
        Assert.Equal(1, config.Get<double>(KumoriViewerSetting.MusicVolume));
        Assert.Equal(1, config.Get<double>(KumoriViewerSetting.HitsoundVolume));
        Assert.Equal(
            config.Get<Colour4>(KumoriViewerSetting.ComparisonReplayCursorColour),
            config.Get<Colour4>(KumoriViewerSetting.ComparisonReplayCursorTrailColour));
    }

    [Fact]
    public void AudioPreferencesPersistInViewerConfig()
    {
        var storage = new NativeStorage(directory);
        using (var config = new KumoriViewerConfig(storage))
        {
            config.SetValue(KumoriViewerSetting.MasterVolume, 0.8);
            config.SetValue(KumoriViewerSetting.MusicVolume, 0.6);
            config.SetValue(KumoriViewerSetting.HitsoundVolume, 0.4);
            config.Save();
        }

        using var reopened = new KumoriViewerConfig(new NativeStorage(directory));
        Assert.Equal(0.8, reopened.Get<double>(KumoriViewerSetting.MasterVolume));
        Assert.Equal(0.6, reopened.Get<double>(KumoriViewerSetting.MusicVolume));
        Assert.Equal(0.4, reopened.Get<double>(KumoriViewerSetting.HitsoundVolume));
    }

    [Fact]
    public void AnalyzerPreferencesPersist()
    {
        var storage = new NativeStorage(directory);
        using (var config = new KumoriViewerConfig(storage))
        {
            config.SetValue(KumoriViewerSetting.MissAnalyzerShowMovementSamples, false);
            config.SetValue(KumoriViewerSetting.MissAnalyzerShowHeldSamples, false);
            config.Save();
        }

        var reopenedStorage = new NativeStorage(directory);
        using var reopened = new KumoriViewerConfig(reopenedStorage);
        Assert.False(reopened.Get<bool>(KumoriViewerSetting.MissAnalyzerShowMovementSamples));
        Assert.False(reopened.Get<bool>(KumoriViewerSetting.MissAnalyzerShowHeldSamples));
    }

    [Fact]
    public void ReplayCursorColoursPersist()
    {
        var colour = Colour4.FromHex("#42a5f5");
        var trailColour = Colour4.FromHex("#ab47bc");
        using (var config = new KumoriViewerConfig(new NativeStorage(directory)))
        {
            config.SetValue(KumoriViewerSetting.ComparisonReplayCursorColour, colour);
            config.SetValue(KumoriViewerSetting.ComparisonReplayCursorTrailColour, trailColour);
            config.Save();
        }

        using var reopened = new KumoriViewerConfig(new NativeStorage(directory));
        Assert.Equal(colour, reopened.Get<Colour4>(KumoriViewerSetting.ComparisonReplayCursorColour));
        Assert.Equal(trailColour, reopened.Get<Colour4>(KumoriViewerSetting.ComparisonReplayCursorTrailColour));
    }

    [Fact]
    public void ComparisonPanelHasNativeOverlayWidth()
    {
        using var config = new KumoriViewerConfig(new NativeStorage(directory));
        var panel = new KumoriComparisonPanel(
            config, [], null, _ => { }, () => { }, new osu.Framework.Bindables.Bindable<string>(), () => { }, () => { });

        Assert.Equal(KumoriComparisonPanel.NativePanelWidth, panel.Width);
        Assert.Equal(Axes.None, panel.RelativeSizeAxes & Axes.X);
    }

    [Fact]
    public void ComparisonMovementUsesTheSameInterpolatedPositionForCursorAndJudgements()
    {
        MovementSample[] samples = KumoriComparisonMovement.Prepare([
            new MovementSample { MapTimeMs = 100, X = 10, Y = 20 },
            new MovementSample { MapTimeMs = 200, X = 30, Y = 60 },
        ]);

        Assert.True(KumoriComparisonMovement.TryPositionAt(samples, 150, out Vector2 position));
        Assert.Equal(new Vector2(20, 40), position);
    }

    [Fact]
    public void ComparisonMovementDoesNotBridgePausedOrMissingCaptureData()
    {
        MovementSample[] samples = KumoriComparisonMovement.Prepare([
            new MovementSample { MapTimeMs = 0, X = 10, Y = 20 },
            new MovementSample { MapTimeMs = 100, X = 99, Y = 99, Flags = KumoriComparisonMovement.PausedFlag },
            new MovementSample { MapTimeMs = 400, X = 30, Y = 60 },
        ]);

        Assert.Equal(2, samples.Length);
        Assert.False(KumoriComparisonMovement.TryPositionAt(samples, 200, out _));
    }

    [Fact]
    public void ComparisonMovementShowsTheFirstExactFrameAfterACaptureGap()
    {
        MovementSample[] samples = KumoriComparisonMovement.Prepare([
            new MovementSample { MapTimeMs = 0, X = 10, Y = 20 },
            new MovementSample { MapTimeMs = 400, X = 30, Y = 60 },
        ]);

        Assert.True(KumoriComparisonMovement.TryPositionAt(samples, 400, out Vector2 position));
        Assert.Equal(new Vector2(30, 60), position);
    }

    [Fact]
    public void ComparisonMovementDeduplicatesCaptureTimesAndMatchesNativeLinearInterpolation()
    {
        MovementSample[] samples = KumoriComparisonMovement.Prepare([
            new MovementSample { MapTimeMs = 0, X = 0, Y = 0 },
            new MovementSample { MapTimeMs = 100, X = 9, Y = 9 },
            new MovementSample { MapTimeMs = 100, X = 10, Y = 10 },
            new MovementSample { MapTimeMs = 200, X = 30, Y = 30 },
            new MovementSample { MapTimeMs = 300, X = 60, Y = 60 },
        ]);

        Assert.Equal(4, samples.Length);
        Assert.Equal(10, samples[1].X);
        Assert.True(KumoriComparisonMovement.TryPositionAt(samples, 150, out Vector2 position));
        Assert.Equal(new Vector2(20, 20), position);
    }

    [Fact]
    public void AnalyzerFiltersStayInSyncWithMainViewerSettings()
    {
        var storage = new NativeStorage(directory);
        using var config = new KumoriViewerConfig(storage);
        var viewModel = new AdvancedAnalyzerViewModel(new MissAnalysisModel([]), config);

        // ConfigManager.GetBindable() returns a weakly bound copy. Verify the
        // view model remains connected after temporary bindables are collected.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        config.SetValue(KumoriViewerSetting.ShowOkMarkers, true);
        Assert.True(viewModel.ShowOks.Value);

        viewModel.ShowMehs.Value = true;
        Assert.True(config.Get<bool>(KumoriViewerSetting.ShowMehMarkers));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, true);
    }
}
