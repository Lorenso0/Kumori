using System.Diagnostics;
using System.IO;
using System.Windows;
using Kumori.Core;
using Kumori.Core.Settings;
using Kumori.Core.State;
using Kumori.Native;
using Kumori.Storage;
using Microsoft.Data.Sqlite;

namespace Kumori.App;

public partial class HealthDashboardWindow : Window
{
    private readonly AppStateStore store;
    private readonly SettingsService settings;

    public HealthDashboardWindow(AppStateStore store, SettingsService settings)
    {
        this.store = store;
        this.settings = settings;
        InitializeComponent();
        if (Content is FrameworkElement content)
            content.Loaded += async (_, _) => await RefreshAsync();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        DarkTitleBar.Apply(new System.Windows.Interop.WindowInteropHelper(this).Handle);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Backups_Click(object sender, RoutedEventArgs e) => MainWindow.TryOpenWorkspace(new BackupWindow(settings), "Backup & restore");

    private void Logs_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.LogDir);
        Process.Start(new ProcessStartInfo { FileName = AppPaths.LogDir, UseShellExecute = true });
    }

    private async Task RefreshAsync()
    {
        var state = store.Current;
        var snapshot = settings.Current;
        HealthList.ItemsSource = new[] { new HealthRow("Data health", "Checking", "Validating local storage and caches...") };
        HealthData data;
        try
        {
            data = await Task.Run(() => ReadHealthData(snapshot));
        }
        catch (Exception ex)
        {
            HealthList.ItemsSource = new[] { new HealthRow("Data health", "Error", ex.Message) };
            return;
        }
        HealthList.ItemsSource = new[]
        {
            new HealthRow("Tracking", state.Tracking.Health.ToString(),
                state.Tracking.TosuConnected
                    ? $"{state.Tracking.CurrentBeatmap ?? "connected"}; packet {state.Tracking.LastPacketAgeSeconds?.ToString("0.0") ?? "n/a"}s"
                    : state.Tracking.Detail ?? "not connected"),
            new HealthRow("tosu", File.Exists(AppPaths.TosuExecutable) ? "Installed" : "Missing",
                $"{ReadFile(AppPaths.TosuVersionFile, "unknown")} - {AppPaths.TosuExecutable}"),
            new HealthRow("Lazer replay frames", state.Capture.Health.ToString(),
                $"{state.Capture.Source}; received {state.Capture.FramesReceived}; buffered {state.Capture.FramesBuffered}; stored {state.Capture.FramesStored}; {state.Capture.Error ?? "no errors"}"),
            new HealthRow("Companions", state.Companions.OsuRunning ? "osu! detected" : "Waiting",
                $"{state.Companions.OpenTabletDriverDetail ?? "OTD pending/off"}; {state.Companions.DualModeDetail ?? "dual mode pending/off"}"),
            new HealthRow("OpenTabletDriver", data.Otd is null ? "Not found" : "Detected",
                data.Otd is null ? "Detection did not find an installation" : $"{data.Otd.Version.NullIfEmpty() ?? "unknown"} - {data.Otd.ExecutablePath}"),
            new HealthRow("Replay Viewer", snapshot.ReplayViewer.Enabled ? "Enabled" : "Disabled",
                $"{ReplayViewerContractService.ResolveViewerExecutable()} - skin {snapshot.ReplayViewer.SkinPath.NullIfEmpty() ?? "default"}"),
            new HealthRow("Storage", data.Integrity == "ok" ? "Healthy" : data.Integrity,
                $"{SafeSize(AppPaths.TrackingDatabase) / 1_048_576.0:0.0} MB; {data.InvalidAttempts} invalid attempt(s); {data.EmptySessions} empty session(s)"),
            new HealthRow("Backups", snapshot.Backup.AutomaticEnabled ? "Automatic" : "Manual",
                $"{data.BackupCount} backup(s); every {snapshot.Backup.IntervalHours}h; keep {snapshot.Backup.RetentionCount}"),
            new HealthRow("Network", "Local-first",
                $"Optional network: GitHub releases, tosu assets/offsets, mirror {snapshot.Media.PrimaryMirror} (+{snapshot.Media.FallbackMirrors.Count} fallback). History is never uploaded."),
            new HealthRow("Data inventory", "Local",
                $"Database {SafeSize(AppPaths.TrackingDatabase) / 1_048_576d:0.0} MB; caches {data.CacheBytes / 1_048_576d:0.0} MB; reports {AppPaths.ReportsDir}"),
            new HealthRow("Media", state.Media.LastError is null ? "Ready" : "Degraded",
                $"{state.Media.BeatmapFile}; audio {state.Media.Audio}; bg {state.Media.Background}; {state.Media.LastError ?? state.Media.Mirror}"),
        };
    }

    private static HealthData ReadHealthData(KumoriSettings snapshot)
    {
        var factory = new SqliteConnectionFactory(AppPaths.TrackingDatabase, readOnly: true);
        var integrity = File.Exists(AppPaths.TrackingDatabase) ? "Unknown" : "Missing";
        if (factory.DatabaseExists)
        {
            using var connection = factory.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA quick_check";
            integrity = command.ExecuteScalar() as string ?? "Unknown";
        }
        var cleanup = new TrackingMaintenanceRepository(factory).PreviewCleanup();
        return new HealthData(
            OpenTabletDriverService.Detect(snapshot.OpenTabletDriver.InstallPath),
            new BackupService().List(snapshot.Backup).Count,
            CacheStorageUsage.GetAdditionalBytes(AppPaths.CacheDir),
            integrity,
            cleanup.InvalidAttempts,
            cleanup.EmptySessions);
    }

    private static string ReadFile(string path, string fallback)
    {
        try { return File.Exists(path) ? File.ReadAllText(path).Trim() : fallback; }
        catch { return fallback; }
    }

    private static long SafeSize(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch { return 0; }
    }
}

public sealed record HealthRow(string Component, string State, string Detail);

internal sealed record HealthData(
    OpenTabletDriverInstallation? Otd,
    int BackupCount,
    long CacheBytes,
    string Integrity,
    int InvalidAttempts,
    int EmptySessions);

internal static class HealthDashboardExtensions
{
    public static string? NullIfEmpty(this string value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
