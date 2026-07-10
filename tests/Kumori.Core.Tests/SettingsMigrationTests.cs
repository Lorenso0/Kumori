using Kumori.Core.Settings;
using Xunit;

namespace Kumori.Core.Tests;

public class SettingsMigrationTests
{
    [Fact]
    public void ImportLegacy_MapsKnownKeys()
    {
        const string legacy = """
        {
            "theme": "purple",
            "osu_advanced_tracking_enabled": false,
            "osu_tracking_retention_days": 30,
            "osu_replay_master_volume": 0.5,
            "osu_replay_skin_path": "C:\\skins\\my.osk",
            "osu_replay_disable_hidden": true,
            "osu_native_capture_enabled": true,
            "osu_lazer_replay_frame_enabled": true,
            "osu_lazer_replay_frame_bridge_port": 16030,
            "osu_media_mirror_base_url": "https://example.com",
            "input_method": "tablet",
            "otd_auto_launch_enabled": true,
            "run_at_windows_startup": true
        }
        """;

        var s = SettingsService.ImportLegacy(legacy);

        Assert.False(s.Tracking.Enabled);
        Assert.Equal(30, s.Tracking.RetentionDays);
        Assert.Equal(0.5, s.ReplayViewer.MasterVolume);
        Assert.Equal(@"C:\skins\my.osk", s.ReplayViewer.SkinPath);
        Assert.True(s.ReplayViewer.DisableHidden);
        Assert.True(s.Capture.LazerReplayFrameEnabled);
        Assert.Equal("https://example.com", s.Media.PrimaryMirror);
        Assert.True(s.OpenTabletDriver.AutoLaunch);
        Assert.True(s.Startup.RunAtLogin);
    }

    [Fact]
    public void ImportLegacy_UsesDefaultsForMissingKeys()
    {
        var s = SettingsService.ImportLegacy("{}");
        Assert.True(s.Tracking.Enabled);
        Assert.Equal(0.8, s.ReplayViewer.MasterVolume);
        Assert.True(s.Capture.LazerReplayFrameEnabled);
        Assert.Equal("https://api.rai.moe", s.Media.PrimaryMirror);
    }

    [Fact]
    public void ImportLegacy_ToleratesGarbage()
    {
        var s = SettingsService.ImportLegacy("not json at all");
        Assert.True(s.Tracking.Enabled);
    }

    [Fact]
    public void LoadThenSave_RoundTripsV2File()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var v2 = Path.Combine(dir.FullName, "settings.v2.json");
            var legacy = Path.Combine(dir.FullName, "settings.json");
            File.WriteAllText(legacy, """{"osu_tracking_retention_days": 7}""");

            var service = new SettingsService(v2, legacy);
            var loaded = service.Load();
            Assert.Equal(7, loaded.Tracking.RetentionDays);
            Assert.True(File.Exists(v2)); // imported once, then persisted as v2

            // Second load reads v2, not legacy.
            File.WriteAllText(legacy, """{"osu_tracking_retention_days": 99}""");
            var second = new SettingsService(v2, legacy).Load();
            Assert.Equal(7, second.Tracking.RetentionDays);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
