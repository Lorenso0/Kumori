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
    private readonly ThemeManager? _themes;
    private readonly string _originalThemeId;
    private bool _loading = true;
    private bool _accepted;

    public SettingsWindow(SettingsService settings)
    {
        _settings = settings;
        _themes = (Application.Current as App)?.Themes;
        _originalThemeId = ThemeManager.Resolve(settings.Current.Appearance.ThemeId).Id;
        InitializeComponent();
        LoadValues();
        _loading = false;
        Closed += (_, _) =>
        {
            if (!_accepted)
            {
                _themes?.Apply(_originalThemeId, persist: false);
            }
        };
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
        SuspendOsuDuringDualModeSwitch.IsChecked = s.Display.SuspendOsuDuringDualModeSwitch;
        switch (ThemeManager.Resolve(s.Appearance.ThemeId).Id)
        {
            case "pulse": PulseTheme.IsChecked = true; break;
            case "windows-fluent": FluentTheme.IsChecked = true; break;
            default: RefinedTheme.IsChecked = true; break;
        }
    }

    private string SelectedThemeId =>
        PulseTheme.IsChecked == true ? "pulse" :
        FluentTheme.IsChecked == true ? "windows-fluent" :
        ThemeManager.DefaultThemeId;

    private void Theme_Checked(object sender, RoutedEventArgs e)
    {
        if (!_loading && sender is RadioButton { Tag: string themeId })
        {
            _themes?.Apply(themeId, persist: false);
        }
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
            s.Display.SuspendOsuDuringDualModeSwitch = SuspendOsuDuringDualModeSwitch.IsChecked == true;
            s.Appearance.ThemeId = SelectedThemeId;
        });
        _themes?.Apply(SelectedThemeId, persist: false);
        try
        {
            StartupRegistration.SetEnabled(RunAtLogin.IsChecked == true);
        }
        catch (Exception ex)
        {
            ErrorText.Text = $"Could not update Windows startup: {ex.Message}";
            return;
        }
        _accepted = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void BrowseOtd_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select OpenTabletDriver",
            FileName = "OpenTabletDriver.UX.Wpf.exe",
            Filter = "OpenTabletDriver|OpenTabletDriver.UX.Wpf.exe|Programs (*.exe)|*.exe|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog() == true)
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
        if (dialog.ShowDialog() == true)
        {
            SkinPath.Text = SkinLibraryService.ImportFile(dialog.FileName);
        }
    }

    private void SkinLibrary_Click(object sender, RoutedEventArgs e)
    {
        MainWindow.TryOpenWorkspace(new SkinLibraryWindow(_settings), "Skin library");
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
