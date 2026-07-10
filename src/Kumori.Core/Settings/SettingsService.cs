using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kumori.Core.Settings;

/// <summary>
/// Loads/saves settings.v2.json. On first run, imports legacy
/// settings.json (flat keys like "osu_replay_master_volume") into the
/// strongly typed model. The legacy file is left untouched during migration.
/// </summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _settingsFile;
    private readonly string _legacyFile;
    private readonly object _lock = new();

    public KumoriSettings Current { get; private set; } = new();

    public event Action<KumoriSettings>? Changed;

    public SettingsService(string? settingsFile = null, string? legacyFile = null)
    {
        _settingsFile = settingsFile ?? AppPaths.SettingsFile;
        _legacyFile = legacyFile ?? AppPaths.LegacySettingsFile;
    }

    public KumoriSettings Load()
    {
        lock (_lock)
        {
            if (File.Exists(_settingsFile))
            {
                try
                {
                    Current = JsonSerializer.Deserialize<KumoriSettings>(
                        File.ReadAllText(_settingsFile), JsonOptions) ?? new KumoriSettings();
                    return Current;
                }
                catch
                {
                    // Corrupt v2 file: fall through to legacy import / defaults.
                }
            }

            Current = File.Exists(_legacyFile)
                ? ImportLegacy(File.ReadAllText(_legacyFile))
                : new KumoriSettings();
            SaveLocked();
            return Current;
        }
    }

    public void Update(Action<KumoriSettings> mutate)
    {
        lock (_lock)
        {
            mutate(Current);
            SaveLocked();
        }
        Changed?.Invoke(Current);
    }

    private void SaveLocked()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsFile)!);
        var tmp = _settingsFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(Current, JsonOptions));
        File.Move(tmp, _settingsFile, overwrite: true);
    }

    /// <summary>Maps legacy flat Python keys to the typed model. Unknown keys are ignored.</summary>
    public static KumoriSettings ImportLegacy(string legacyJson)
    {
        var s = new KumoriSettings();
        JsonObject? legacy;
        try
        {
            legacy = JsonNode.Parse(legacyJson) as JsonObject;
        }
        catch
        {
            return s;
        }
        if (legacy is null)
        {
            return s;
        }

        bool B(string key, bool fallback) =>
            legacy.TryGetPropertyValue(key, out var n) && n is JsonValue v &&
            v.TryGetValue<bool>(out var b) ? b : fallback;
        double D(string key, double fallback) =>
            legacy.TryGetPropertyValue(key, out var n) && n is JsonValue v &&
            v.TryGetValue<double>(out var d) ? d : fallback;
        int I(string key, int fallback) => (int)D(key, fallback);
        string S(string key, string fallback) =>
            legacy.TryGetPropertyValue(key, out var n) && n is JsonValue v &&
            v.TryGetValue<string>(out var str) && str is not null ? str : fallback;

        s.FirstRunCompleted = B("first_run_completed", s.FirstRunCompleted);
        s.OnboardingVersion = I("onboarding_version", s.OnboardingVersion);
        s.Tracking.Enabled = B("osu_advanced_tracking_enabled", s.Tracking.Enabled);
        s.Tracking.RetentionDays = I("osu_tracking_retention_days", s.Tracking.RetentionDays);
        s.Tracking.PacketRecordingEnabled = B("tosu_packet_recording_enabled", s.Tracking.PacketRecordingEnabled);
        s.ReplayViewer.Enabled = B("osu_native_replay_viewer_enabled", s.ReplayViewer.Enabled);
        s.ReplayViewer.MasterVolume = D("osu_replay_master_volume", s.ReplayViewer.MasterVolume);
        s.ReplayViewer.MusicVolume = D("osu_replay_music_volume", s.ReplayViewer.MusicVolume);
        s.ReplayViewer.HitsoundVolume = D("osu_replay_hitsound_volume", s.ReplayViewer.HitsoundVolume);
        s.ReplayViewer.SkinPath = S("osu_replay_skin_path", s.ReplayViewer.SkinPath);
        s.ReplayViewer.DisableHidden = B("osu_replay_disable_hidden", s.ReplayViewer.DisableHidden);
        s.Capture.LazerReplayFrameEnabled = B("osu_lazer_replay_frame_enabled", s.Capture.LazerReplayFrameEnabled);
        s.Media.PrimaryMirror = S("osu_media_mirror_base_url", s.Media.PrimaryMirror);
        s.OpenTabletDriver.InstallPath = S("otd_install_path", s.OpenTabletDriver.InstallPath);
        s.OpenTabletDriver.AutoLaunch = B("otd_auto_launch_enabled", s.OpenTabletDriver.AutoLaunch);
        s.Display.AutoSwitchDualMode = B("dual_mode_auto_switch_enabled", s.Display.AutoSwitchDualMode);
        s.Startup.RunAtLogin = B("run_at_windows_startup", s.Startup.RunAtLogin);
        return s;
    }
}
