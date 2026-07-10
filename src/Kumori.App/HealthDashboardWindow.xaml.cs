using System.Diagnostics;
using System.IO;
using System.Windows;
using Kumori.Core;
using Kumori.Core.Settings;
using Kumori.Core.State;
using Kumori.Native;
using Kumori.Storage;

namespace Kumori.App;

public partial class HealthDashboardWindow : Window
{
    private readonly AppStateStore _store;
    private readonly SettingsService _settings;

    public HealthDashboardWindow(AppStateStore store, SettingsService settings)
    {
        _store = store;
        _settings = settings;
        InitializeComponent();
        Refresh();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        DarkTitleBar.Apply(new System.Windows.Interop.WindowInteropHelper(this).Handle);
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Logs_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.LogDir);
        Process.Start(new ProcessStartInfo { FileName = AppPaths.LogDir, UseShellExecute = true });
    }

    private void Refresh()
    {
        var s = _store.Current;
        var otd = OpenTabletDriverService.Detect(_settings.Current.OpenTabletDriver.InstallPath);
        HealthList.ItemsSource = new[]
        {
            new HealthRow("Tracking", s.Tracking.Health.ToString(),
                s.Tracking.TosuConnected
                    ? $"{s.Tracking.CurrentBeatmap ?? "connected"}; packet {s.Tracking.LastPacketAgeSeconds?.ToString("0.0") ?? "n/a"}s"
                    : s.Tracking.Detail ?? "not connected"),
            new HealthRow("tosu", File.Exists(AppPaths.TosuExecutable) ? "Installed" : "Missing",
                $"{ReadFile(AppPaths.TosuVersionFile, "unknown")} - {AppPaths.TosuExecutable}"),
            new HealthRow("Lazer replay frames", s.Capture.Health.ToString(),
                $"{s.Capture.Source}; received {s.Capture.FramesReceived}; buffered {s.Capture.FramesBuffered}; stored {s.Capture.FramesStored}; {s.Capture.Error ?? "no errors"}"),
            new HealthRow("Companions", s.Companions.OsuRunning ? "osu! detected" : "Waiting",
                $"{s.Companions.OpenTabletDriverDetail ?? "OTD pending/off"}; {s.Companions.DualModeDetail ?? "dual mode pending/off"}"),
            new HealthRow("OpenTabletDriver", otd is null ? "Not found" : "Detected",
                otd is null ? "Detection did not find an installation" : $"{otd.Version.NullIfEmpty() ?? "unknown"} - {otd.ExecutablePath}"),
            new HealthRow("Replay Viewer", _settings.Current.ReplayViewer.Enabled ? "Enabled" : "Disabled",
                $"{ReplayViewerContractService.ResolveViewerExecutable()} - skin {(_settings.Current.ReplayViewer.SkinPath.NullIfEmpty() ?? "default")}"),
            new HealthRow("Storage", File.Exists(AppPaths.TrackingDatabase) ? "Ready" : "Missing",
                $"{SafeSize(AppPaths.TrackingDatabase) / 1_048_576.0:0.0} MB - {AppPaths.TrackingDatabase}"),
            new HealthRow("Media", s.Media.LastError is null ? "Ready" : "Degraded",
                $"{s.Media.BeatmapFile}; audio {s.Media.Audio}; bg {s.Media.Background}; {s.Media.LastError ?? s.Media.Mirror}"),
        };
    }

    private static string ReadFile(string path, string fallback)
    {
        try { return File.Exists(path) ? File.ReadAllText(path).Trim() : fallback; } catch { return fallback; }
    }

    private static long SafeSize(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0; } catch { return 0; }
    }
}

public sealed record HealthRow(string Component, string State, string Detail);

internal static class HealthDashboardExtensions
{
    public static string? NullIfEmpty(this string value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
