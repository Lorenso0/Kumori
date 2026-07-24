namespace Kumori.Core.Settings;

/// <summary>
/// Strongly typed, versioned application settings.
/// Persisted as settings.v2.json; imported once from the legacy
/// Python settings.json (see SettingsService.ImportLegacy).
/// </summary>
public sealed class KumoriSettings
{
    public int Version { get; set; } = 2;
    public bool FirstRunCompleted { get; set; }
    public int OnboardingVersion { get; set; }
    public int OnboardingProgressStep { get; set; }

    public TrackingSettings Tracking { get; set; } = new();
    public ReplayViewerSettings ReplayViewer { get; set; } = new();
    public SkinEditorSettings SkinEditor { get; set; } = new();
    public CaptureSettings Capture { get; set; } = new();
    public MediaSettings Media { get; set; } = new();
    public OpenTabletDriverSettings OpenTabletDriver { get; set; } = new();
    public DisplaySettings Display { get; set; } = new();
    public StartupSettings Startup { get; set; } = new();
    public WindowSettings Window { get; set; } = new();
    public AppearanceSettings Appearance { get; set; } = new();
    public BackupSettings Backup { get; set; } = new();
    public DeveloperSettings Developer { get; set; } = new();

    public sealed class TrackingSettings
    {
        public bool Enabled { get; set; } = true;
        public int MinimumAttemptSeconds { get; set; } = 3;
        public int RetentionDays { get; set; } = 0;
        public bool PacketRecordingEnabled { get; set; } = false;
    }

    public sealed class ReplayViewerSettings
    {
        public bool Enabled { get; set; } = true;
        public double MasterVolume { get; set; } = 0.8;
        public double MusicVolume { get; set; } = 0.8;
        public double HitsoundVolume { get; set; } = 0.75;
        public string SkinPath { get; set; } = "";
        public bool DisableHidden { get; set; } = false;
    }

    public sealed class CaptureSettings
    {
        public bool LazerReplayFrameEnabled { get; set; } = true;
    }

    public sealed class MediaSettings
    {
        public string PrimaryMirror { get; set; } = "https://api.rai.moe";
        public List<string> FallbackMirrors { get; set; } = new();
    }

    public sealed class OpenTabletDriverSettings
    {
        public string InstallPath { get; set; } = "";
        public bool AutoLaunch { get; set; } = false;
    }

    public sealed class DisplaySettings
    {
        public bool AutoSwitchDualMode { get; set; } = false;
        /// <summary>
        /// Keeps osu! alive while the display mode changes. This is opt-in because
        /// some graphics drivers require a full client restart after a topology change.
        /// </summary>
        public bool SuspendOsuDuringDualModeSwitch { get; set; } = false;
    }

    public sealed class StartupSettings
    {
        public bool RunAtLogin { get; set; } = false;
        public bool StartMinimized { get; set; } = false;
        public bool RegisterKumoriFiles { get; set; } = true;
        public bool DeleteSharedPackageAfterImport { get; set; } = false;
        public string ExecutablePath { get; set; } = "";
    }

    public sealed class SkinEditorSettings
    {
        public string LazerRootOverride { get; set; } = "";
        public List<string> CustomSwatches { get; set; } = new();
        public bool HideEmptyElements { get; set; }
    }


    public sealed class WindowSettings
    {
        public double? Left { get; set; }
        public double? Top { get; set; }
        public double? Width { get; set; }
        public double? Height { get; set; }
        public bool Maximized { get; set; } = false;
    }

    public sealed class AppearanceSettings
    {
        public string ThemeId { get; set; } = "refined-kumori";
        public CustomThemeSettings CustomTheme { get; set; } = new();
        public bool NavigationExpanded { get; set; } = false;
        public bool GroupSessions { get; set; } = true;
    }

    public sealed class BackupSettings
    {
        public bool AutomaticEnabled { get; set; } = true;
        public int IntervalHours { get; set; } = 24;
        public int RetentionCount { get; set; } = 14;
        public string Directory { get; set; } = "";
    }

    public sealed class DeveloperSettings
    {
        /// <summary>Maximum age of every file below the Kumori logs directory.</summary>
        public int LogRetentionDays { get; set; } = AppPaths.DefaultLogRetentionDays;

        /// <summary>
        /// One-shot diagnostic which makes the next completed play enter the
        /// same replay recovery path as a tosu result-data failure.
        /// </summary>
        public bool ForceReplayRecoveryNextPlay { get; set; }
    }

}
