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
        Assert.Equal("refined-kumori", s.Appearance.ThemeId);
    }

    [Theory]
    [InlineData("pulse", "pulse")]
    [InlineData("fluent", "windows-fluent")]
    [InlineData("anything-else", "refined-kumori")]
    public void ImportLegacy_MapsTheme(string legacyTheme, string expected)
    {
        var s = SettingsService.ImportLegacy($$"""{"theme":"{{legacyTheme}}"}""");
        Assert.Equal(expected, s.Appearance.ThemeId);
    }

    [Fact]
    public void ImportLegacy_UsesDefaultsForMissingKeys()
    {
        var s = SettingsService.ImportLegacy("{}");
        Assert.True(s.Tracking.Enabled);
        Assert.Equal(3, s.Tracking.MinimumAttemptSeconds);
        Assert.Equal(0.8, s.ReplayViewer.MasterVolume);
        Assert.True(s.Capture.LazerReplayFrameEnabled);
        Assert.Equal("https://api.rai.moe", s.Media.PrimaryMirror);
    }

    [Fact]
    public void ExistingV2SettingsWithoutMinimumDurationKeepThreeSecondDefault()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "settings.v2.json");
            File.WriteAllText(path, """{"tracking":{"enabled":true}}""");

            var loaded = new SettingsService(path, Path.Combine(dir.FullName, "missing.json")).Load();

            Assert.Equal(3, loaded.Tracking.MinimumAttemptSeconds);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
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

    [Fact]
    public void Appearance_RoundTripsThemeAndNavigationPreference()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "settings.v2.json");
            var service = new SettingsService(path, Path.Combine(dir.FullName, "missing.json"));
            service.Load();
            service.Update(settings =>
            {
                settings.Appearance.ThemeId = "custom";
                settings.Appearance.NavigationExpanded = false;
                settings.Appearance.CustomTheme.Name = "Night drive";
                settings.Appearance.CustomTheme.Colors["AccentPink"] = "#123456";
            });

            var loaded = new SettingsService(path, "unused").Load();
            Assert.Equal("custom", loaded.Appearance.ThemeId);
            Assert.False(loaded.Appearance.NavigationExpanded);
            Assert.Equal("Night drive", loaded.Appearance.CustomTheme.Name);
            Assert.Equal("#123456", loaded.Appearance.CustomTheme.Colors["AccentPink"]);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Update_PublishesNewSnapshotWithoutMutatingActiveReaders()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var service = new SettingsService(
                Path.Combine(dir.FullName, "settings.v2.json"),
                Path.Combine(dir.FullName, "missing.json"));
            var before = service.Load();

            service.Update(settings => settings.Appearance.ThemeId = "pulse");

            Assert.Equal("refined-kumori", before.Appearance.ThemeId);
            Assert.Equal("pulse", service.Current.Appearance.ThemeId);
            Assert.NotSame(before, service.Current);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Load_PreservesCorruptV2BeforeWritingDefaults()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "settings.v2.json");
            File.WriteAllText(path, "{ truncated");

            var loaded = new SettingsService(path, Path.Combine(dir.FullName, "missing.json")).Load();

            Assert.NotNull(loaded);
            Assert.Single(Directory.EnumerateFiles(dir.FullName, "settings.v2.json.corrupt-*"));
            Assert.Equal("{ truncated", File.ReadAllText(Directory.EnumerateFiles(dir.FullName, "settings.v2.json.corrupt-*").Single()));
            Assert.NotEqual("{ truncated", File.ReadAllText(path));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
