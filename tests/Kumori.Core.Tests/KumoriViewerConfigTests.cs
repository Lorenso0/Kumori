using Kumori.ReplayViewer;
using osu.Framework.Platform;
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
    public void AnalyzerFiltersStayInSyncWithMainViewerSettings()
    {
        var storage = new NativeStorage(directory);
        using var config = new KumoriViewerConfig(storage);
        var viewModel = new AdvancedAnalyzerViewModel(new MissAnalysisModel([]), config);

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
