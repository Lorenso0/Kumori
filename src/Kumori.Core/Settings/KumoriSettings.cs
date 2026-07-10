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

    public TrackingSettings Tracking { get; set; } = new();
    public ReplayViewerSettings ReplayViewer { get; set; } = new();
    public CaptureSettings Capture { get; set; } = new();
    public MediaSettings Media { get; set; } = new();
    public OpenTabletDriverSettings OpenTabletDriver { get; set; } = new();
    public DisplaySettings Display { get; set; } = new();
    public StartupSettings Startup { get; set; } = new();
    public WindowSettings Window { get; set; } = new();

    public sealed class TrackingSettings
    {
        public bool Enabled { get; set; } = true;
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
    }

    public sealed class StartupSettings
    {
        public bool RunAtLogin { get; set; } = false;
    }

    public sealed class WindowSettings
    {
        public double? Left { get; set; }
        public double? Top { get; set; }
        public double? Width { get; set; }
        public double? Height { get; set; }
        public bool Maximized { get; set; } = false;
    }
}
