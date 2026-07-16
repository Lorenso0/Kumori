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

    private KumoriSettings current = new();

    /// <summary>Atomically published snapshot; service updates use copy-on-write.</summary>
    public KumoriSettings Current => Volatile.Read(ref current);

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
                    current = Normalize(JsonSerializer.Deserialize<KumoriSettings>(
                        File.ReadAllText(_settingsFile), JsonOptions) ?? new KumoriSettings());
                    return current;
                }
                catch (Exception ex) when (ex is JsonException or NotSupportedException)
                {
                    // Preserve the unreadable file before defaults are written so a
                    // user or support engineer can recover it.
                    if (!TryPreserveCorruptSettings())
                    {
                        current = new KumoriSettings();
                        return current;
                    }
                }
            }

            current = File.Exists(_legacyFile)
                ? ImportLegacy(File.ReadAllText(_legacyFile))
                : new KumoriSettings();
            SaveLocked(current);
            return current;
        }
    }

    public void Update(Action<KumoriSettings> mutate)
    {
        KumoriSettings next;
        lock (_lock)
        {
            next = Clone(current);
            mutate(next);
            Normalize(next);
            SaveLocked(next);
            Volatile.Write(ref current, next);
        }
        Changed?.Invoke(next);
    }

    private void SaveLocked(KumoriSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsFile)!);
        var tmp = _settingsFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(tmp, _settingsFile, overwrite: true);
    }

    private static KumoriSettings Clone(KumoriSettings settings) =>
        Normalize(JsonSerializer.Deserialize<KumoriSettings>(JsonSerializer.Serialize(settings, JsonOptions), JsonOptions)
        ?? throw new InvalidOperationException("Settings snapshot could not be cloned."));

    /// <summary>
    /// Validates a settings document from a backup and removes values that can
    /// cause external side effects before the user has reviewed them again.
    /// Ordinary visual and tracking preferences are retained.
    /// </summary>
    public static string PrepareRestoredSettings(string json)
    {
        KumoriSettings restored;
        try
        {
            restored = Normalize(JsonSerializer.Deserialize<KumoriSettings>(json, JsonOptions)
                ?? throw new InvalidDataException("Backup settings are empty."));
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw new InvalidDataException("Backup settings are not a valid Kumori settings document.", ex);
        }

        restored.Version = 2;

        // Backups are portable data, not authorization to launch executables,
        // create persistence, reconfigure displays, contact a custom server,
        // or write future backups to an arbitrary local/UNC path.
        restored.OpenTabletDriver = new KumoriSettings.OpenTabletDriverSettings();
        restored.Startup = new KumoriSettings.StartupSettings();
        restored.Display = new KumoriSettings.DisplaySettings();
        restored.Media = new KumoriSettings.MediaSettings();
        restored.ReplayViewer.SkinPath = string.Empty;
        restored.Tracking.MinimumAttemptSeconds = new KumoriSettings.TrackingSettings().MinimumAttemptSeconds;
        restored.Tracking.RetentionDays = 0;
        restored.Tracking.PacketRecordingEnabled = false;
        restored.Backup = new KumoriSettings.BackupSettings();
        restored.Developer = new KumoriSettings.DeveloperSettings();

        return JsonSerializer.Serialize(restored, JsonOptions);
    }

    /// <summary>
    /// Restores non-null defaults for sections that System.Text.Json permits a
    /// hand-edited or older file to explicitly set to null.
    /// </summary>
    internal static KumoriSettings Normalize(KumoriSettings settings)
    {
        settings.Tracking ??= new KumoriSettings.TrackingSettings();
        settings.ReplayViewer ??= new KumoriSettings.ReplayViewerSettings();
        settings.Capture ??= new KumoriSettings.CaptureSettings();
        settings.Media ??= new KumoriSettings.MediaSettings();
        settings.OpenTabletDriver ??= new KumoriSettings.OpenTabletDriverSettings();
        settings.Display ??= new KumoriSettings.DisplaySettings();
        settings.Startup ??= new KumoriSettings.StartupSettings();
        settings.Window ??= new KumoriSettings.WindowSettings();
        settings.Appearance ??= new KumoriSettings.AppearanceSettings();
        settings.Backup ??= new KumoriSettings.BackupSettings();
        settings.Developer ??= new KumoriSettings.DeveloperSettings();
        settings.Appearance.CustomTheme ??= new CustomThemeSettings();
        settings.Appearance.CustomTheme.Colors ??= CustomThemePalette.CreateDefaultColors();
        settings.Media.FallbackMirrors ??= [];
        // Tracking history is retained indefinitely. RetentionDays remains in the
        // serialized contract so older settings/backups still deserialize safely,
        // but cleanup is now always an explicit user action.
        settings.Tracking.RetentionDays = 0;
        return settings;
    }

    private bool TryPreserveCorruptSettings()
    {
        try
        {
            var backup = $"{_settingsFile}.corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}";
            File.Move(_settingsFile, backup);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
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
        s.OnboardingProgressStep = I("onboarding_progress_step", s.OnboardingProgressStep);
        s.Appearance.ThemeId = S("theme", "purple") switch
        {
            "pulse" => "pulse",
            "windows-fluent" or "fluent" => "windows-fluent",
            _ => "refined-kumori",
        };
        s.Tracking.Enabled = B("osu_advanced_tracking_enabled", s.Tracking.Enabled);
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
