using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Kumori.Core.Settings;
using Kumori.Native;

namespace Kumori.App;

public partial class SettingsWindow : Window
{
    private readonly SettingsService _settings;

    public SettingsWindow(SettingsService settings)
    {
        _settings = settings;
        InitializeComponent();
        LoadValues();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        DarkTitleBar.Apply(new WindowInteropHelper(this).Handle);
    }

    private void LoadValues()
    {
        var s = _settings.Current;
        TrackingEnabled.IsChecked = s.Tracking.Enabled;
        PacketRecordingEnabled.IsChecked = s.Tracking.PacketRecordingEnabled;
        RetentionDays.Text = s.Tracking.RetentionDays.ToString(CultureInfo.InvariantCulture);
        LazerReplayFrameEnabled.IsChecked = s.Capture.LazerReplayFrameEnabled;
        OtdPath.Text = s.OpenTabletDriver.InstallPath;
        OtdAutoLaunch.IsChecked = s.OpenTabletDriver.AutoLaunch;
        ReplayEnabled.IsChecked = s.ReplayViewer.Enabled;
        DisableHidden.IsChecked = s.ReplayViewer.DisableHidden;
        SkinPath.Text = s.ReplayViewer.SkinPath;
        PrimaryMirror.Text = s.Media.PrimaryMirror;
        RunAtLogin.IsChecked = s.Startup.RunAtLogin;
        DualModeEnabled.IsChecked = s.Display.AutoSwitchDualMode;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(RetentionDays.Text, out var retention))
        {
            ErrorText.Text = "Check numeric values.";
            return;
        }

        _settings.Update(s =>
        {
            s.Tracking.Enabled = TrackingEnabled.IsChecked == true;
            s.Tracking.PacketRecordingEnabled = PacketRecordingEnabled.IsChecked == true;
            s.Tracking.RetentionDays = Math.Max(0, retention);
            s.Capture.LazerReplayFrameEnabled = LazerReplayFrameEnabled.IsChecked == true;
            s.OpenTabletDriver.InstallPath = OtdPath.Text.Trim();
            s.OpenTabletDriver.AutoLaunch = OtdAutoLaunch.IsChecked == true;
            s.ReplayViewer.Enabled = ReplayEnabled.IsChecked == true;
            s.ReplayViewer.DisableHidden = DisableHidden.IsChecked == true;
            s.ReplayViewer.SkinPath = SkinPath.Text.Trim();
            s.Media.PrimaryMirror = PrimaryMirror.Text.Trim();
            s.Startup.RunAtLogin = RunAtLogin.IsChecked == true;
            s.Display.AutoSwitchDualMode = DualModeEnabled.IsChecked == true;
        });
        try
        {
            StartupRegistration.SetEnabled(RunAtLogin.IsChecked == true);
        }
        catch (Exception ex)
        {
            ErrorText.Text = $"Could not update Windows startup: {ex.Message}";
            return;
        }
        DialogResult = true;
    }

    private void BrowseOtd_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select OpenTabletDriver",
            FileName = "OpenTabletDriver.UX.Wpf.exe",
            Filter = "OpenTabletDriver|OpenTabletDriver.UX.Wpf.exe|Programs (*.exe)|*.exe|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) == true)
        {
            OtdPath.Text = dialog.FileName;
        }
    }

    private void DetectOtd_Click(object sender, RoutedEventArgs e)
    {
        var detected = OpenTabletDriverService.Detect(OtdPath.Text.Trim());
        if (detected is null)
        {
            ErrorText.Text = "OpenTabletDriver was not found.";
            return;
        }
        OtdPath.Text = detected.ExecutablePath;
        ErrorText.Text = $"OpenTabletDriver {detected.VersionOrUnknown()} found.";
    }

    private void BrowseSkin_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select osu! skin",
            Filter = "osu! skin archives (*.osk)|*.osk|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) == true)
        {
            SkinPath.Text = SkinLibraryService.ImportFile(dialog.FileName);
        }
    }

    private void SkinLibrary_Click(object sender, RoutedEventArgs e)
    {
        new SkinLibraryWindow(_settings) { Owner = this }.ShowDialog();
        SkinPath.Text = _settings.Current.ReplayViewer.SkinPath;
    }

    private void ClearSkin_Click(object sender, RoutedEventArgs e) => SkinPath.Text = "";

    private void DeleteSkin_Click(object sender, RoutedEventArgs e)
    {
        var path = SkinPath.Text.Trim();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            ErrorText.Text = "No imported skin file is selected.";
            return;
        }
        SkinLibraryService.DeleteImported(path);
        SkinPath.Text = "";
        ErrorText.Text = "Imported skin deleted.";
    }

}

internal static class SettingsWindowExtensions
{
    public static string VersionOrUnknown(this OpenTabletDriverInstallation installation) =>
        string.IsNullOrWhiteSpace(installation.Version) ? "unknown" : installation.Version;
}
