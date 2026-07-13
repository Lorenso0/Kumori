using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using Kumori.Core;
using Kumori.Core.Settings;
using Kumori.Native;

namespace Kumori.App;

public partial class DeveloperSettingsWindow : Window
{
    private readonly SettingsService settings;

    public DeveloperSettingsWindow(SettingsService settings)
    {
        this.settings = settings;
        InitializeComponent();
        PacketRecordingEnabled.IsChecked = settings.Current.Tracking.PacketRecordingEnabled;
        ForceReplayRecoveryNextPlay.IsChecked = settings.Current.Developer.ForceReplayRecoveryNextPlay;
        LogRetentionDays.Text = settings.Current.Developer.LogRetentionDays.ToString(CultureInfo.InvariantCulture);
        RefreshCacheActivity();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        DarkTitleBar.Apply(new WindowInteropHelper(this).Handle);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(LogRetentionDays.Text, out int retentionDays))
        {
            ErrorText.Text = "Log retention must be a whole number of days.";
            return;
        }

        settings.Update(value =>
        {
            value.Tracking.PacketRecordingEnabled = PacketRecordingEnabled.IsChecked == true;
            value.Developer.ForceReplayRecoveryNextPlay = ForceReplayRecoveryNextPlay.IsChecked == true;
            value.Developer.LogRetentionDays = LogRetentionPolicy.NormalizeDays(retentionDays);
        });
        CacheActivityLog.ConfigureRotationDays(settings.Current.Developer.LogRetentionDays);
        AppDataOrganizer.PruneLogs(retentionDays: settings.Current.Developer.LogRetentionDays);
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void RefreshCacheLog_Click(object sender, RoutedEventArgs e) => RefreshCacheActivity();

    private void OpenCacheLog_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.CacheActivityLog)!);
        if (!File.Exists(AppPaths.CacheActivityLog))
            File.WriteAllText(AppPaths.CacheActivityLog, string.Empty);

        var start = new ProcessStartInfo("notepad.exe") { UseShellExecute = true };
        start.ArgumentList.Add(AppPaths.CacheActivityLog);
        Process.Start(start);
    }

    private void OpenCacheFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.CacheDir);
        Process.Start(new ProcessStartInfo(AppPaths.CacheDir) { UseShellExecute = true });
    }

    private void RefreshCacheActivity()
    {
        CacheActivityPath.Text = AppPaths.CacheActivityLog;
        var rows = CacheActivityLog.ReadRecent(30).Select(entry => new CacheActivityRow(entry)).ToArray();
        CacheActivityList.ItemsSource = rows;
        CacheActivityEmpty.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private sealed class CacheActivityRow(CacheActivityEntry entry)
    {
        public string Title => entry.BeatmapId is { } beatmapId
            ? $"Beatmap {beatmapId} · {entry.FileName}"
            : entry.FileName;

        public string Reason => !string.IsNullOrWhiteSpace(entry.Reason)
            ? entry.Reason
            : FriendlyReason(entry.Source);

        public string Details => $"{entry.TimestampUtc.ToLocalTime():dd MMM yyyy HH:mm:ss} · {entry.Source} · {FormatBytes(entry.Bytes)}";

        private static string FriendlyReason(string source) => source switch
        {
            "local-beatmap" => "Copied the beatmap definition from the local osu! installation.",
            "local-beatmap-media" => "Copied media required by the cached beatmap and replay viewer.",
            "beatmap-manifest" => "Created the media manifest that lets Kumori reopen this cached map.",
            "osu-lazer-hardlink" or "osu-lazer-symlink" => "Linked an existing osu!lazer file without duplicating its contents.",
            "embedded-replay-viewer" => "Installed a bundled replay-viewer runtime file.",
            "tosu-memory-offsets" => "Cached the current osu!lazer memory layout used for replay-frame capture.",
            _ when source.StartsWith("mirror:", StringComparison.OrdinalIgnoreCase) => "Downloaded media that was unavailable from the local osu! installation.",
            _ => "Added by a Kumori cache or runtime component.",
        };

        private static string FormatBytes(long? bytes)
        {
            if (bytes is null) return "size unavailable";
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024d:0.0} KB";
            return $"{bytes / 1_048_576d:0.0} MB";
        }
    }
}
