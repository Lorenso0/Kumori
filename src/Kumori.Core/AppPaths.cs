namespace Kumori.Core;

/// <summary>
/// Well-known application paths shared by the app and helper services.
/// </summary>
public static class AppPaths
{
    public const int LogRetentionDays = 3;

    public static string AppDataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Kumori");

    public static string ConfigDir => Path.Combine(AppDataDir, "config");

    public static string DataDir => Path.Combine(AppDataDir, "data");

    public static string TrackingDataDir => Path.Combine(DataDir, "tracking");

    public static string CacheDir => Path.Combine(AppDataDir, "cache");

    public static string BeatmapCacheDir => Path.Combine(CacheDir, "beatmaps");

    public static string BeatmapMediaDir => Path.Combine(BeatmapCacheDir, "media");

    public static string OldBeatmapMediaDir => Path.Combine(BeatmapCacheDir, "media.old");

    public static string BeatmapCoversDir => Path.Combine(BeatmapCacheDir, "covers");

    public static string OldBeatmapCoversDir => Path.Combine(BeatmapCacheDir, "covers.old");

    public static string LegacyBeatmapFilesDir => Path.Combine(BeatmapCacheDir, "files");

    public static string OldLegacyBeatmapFilesDir => Path.Combine(BeatmapCacheDir, "files.old");

    public static string AssetsDir => Path.Combine(AppDataDir, "assets");

    public static string SkinsDir => Path.Combine(AssetsDir, "skins");

    public static string RuntimeDir => Path.Combine(AppDataDir, "runtime");

    public static string StatusDir => Path.Combine(RuntimeDir, "status");

    public static string ViewerContractsDir => Path.Combine(RuntimeDir, "viewer-contracts");

    public static string ViewerRuntimeDir => Path.Combine(RuntimeDir, "replay-viewer");

    public static string FixturesDir => Path.Combine(RuntimeDir, "fixtures");

    public static string ReportsDir => Path.Combine(AppDataDir, "reports");

    public static string ToolsDir => Path.Combine(AppDataDir, "tools");

    /// <summary>Legacy Python settings file (settings.json).</summary>
    public static string LegacySettingsFile => Path.Combine(ConfigDir, "settings.json");

    /// <summary>New strongly-typed settings file owned by the .NET app.</summary>
    public static string SettingsFile => Path.Combine(ConfigDir, "settings.v2.json");

    /// <summary>Tracking database written by the Python tracker (and later by .NET).</summary>
    public static string TrackingDatabase => Path.Combine(TrackingDataDir, "osu_tracking.sqlite3");

    /// <summary>Parallel Phase 3 database written by the .NET tracker before ownership flips.</summary>
    public static string TrackingShadowDatabase => Path.Combine(TrackingDataDir, "osu_tracking.net.sqlite3");

    public static string LogDir => Path.Combine(AppDataDir, "logs");

    public static string AppLogDir => Path.Combine(LogDir, "app");

    public static string ViewerLogDir => Path.Combine(LogDir, "viewer");

    public static string TosuLogDir => Path.Combine(LogDir, "tosu");

    public static string LegacyLogDir => Path.Combine(LogDir, "legacy");

    public static string CrashLogFile => Path.Combine(AppLogDir, "crash-net.log");

    public static string ViewerLogFile => Path.Combine(ViewerLogDir, $"native-viewer-{DateTimeOffset.Now:yyyyMMdd}.log");

    public static string TosuDir => Path.Combine(ToolsDir, "tosu");

    public static string TosuExecutable => Path.Combine(TosuDir, "tosu.exe");

    public static string TosuVersionFile => Path.Combine(TosuDir, "version.txt");

    public static string TosuEnvFile => Path.Combine(TosuDir, "tosu.env");

    public static string LazerReplayFrameStatusFile => Path.Combine(StatusDir, "lazer_replay_frame_status.json");
}
