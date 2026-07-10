using System.Windows;
using Kumori.Core.Settings;
using Kumori.Core.State;
using Kumori.Native;

namespace Kumori.App;

public partial class WelcomeWindow : Window
{
    public const int CurrentOnboardingVersion = 3;
    private readonly SettingsService _settings;
    private readonly AppStateStore? _store;

    public WelcomeWindow(SettingsService settings, AppStateStore? store = null)
    {
        _settings = settings;
        _store = store;
        InitializeComponent();
        SourceInitialized += (_, _) =>
            DarkTitleBar.Apply(new System.Windows.Interop.WindowInteropHelper(this).Handle);
        RunAtLogin.IsChecked = _settings.Current.Startup.RunAtLogin;
        AutoLaunchOtd.IsChecked = _settings.Current.OpenTabletDriver.AutoLaunch;
        OtdPath.Text = _settings.Current.OpenTabletDriver.InstallPath;
        UpdateOtdState();
        Loaded += (_, _) => AttachOwnerCentering();
        Closed += (_, _) => DetachOwnerCentering();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        new SettingsWindow(_settings) { Owner = this }.ShowDialog();
    }

    private void Tosu_Click(object sender, RoutedEventArgs e) =>
        new TosuSetupWindow { Owner = this }.ShowDialog();

    private void Health_Click(object sender, RoutedEventArgs e)
    {
        if (_store is not null)
        {
            new HealthDashboardWindow(_store, _settings) { Owner = this }.ShowDialog();
        }
    }

    private void Capture_Click(object sender, RoutedEventArgs e)
    {
        new LazerFrameDebugWindow(_settings) { Owner = this }.ShowDialog();
    }

    private void Skin_Click(object sender, RoutedEventArgs e) =>
        new SkinLibraryWindow(_settings) { Owner = this }.ShowDialog();

    private void Skip_Click(object sender, RoutedEventArgs e) => MarkComplete();

    private void Done_Click(object sender, RoutedEventArgs e) => MarkComplete();

    private void MarkComplete()
    {
        _settings.Update(s =>
        {
            s.FirstRunCompleted = true;
            s.OnboardingVersion = CurrentOnboardingVersion;
            s.Startup.RunAtLogin = RunAtLogin.IsChecked == true;
            s.OpenTabletDriver.AutoLaunch = AutoLaunchOtd.IsChecked == true;
            s.OpenTabletDriver.InstallPath = OtdPath.Text.Trim();
            s.Capture.LazerReplayFrameEnabled = true;
        });
        try
        {
            StartupRegistration.SetEnabled(RunAtLogin.IsChecked == true);
        }
        catch
        {
        }
        Close();
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
            AutoLaunchOtd.IsChecked = true;
            OtdStatus.Text = "OpenTabletDriver path selected.";
        }
    }

    private void DetectOtd_Click(object sender, RoutedEventArgs e)
    {
        var detected = OpenTabletDriverService.Detect(OtdPath.Text.Trim());
        if (detected is not null)
        {
            OtdPath.Text = detected.ExecutablePath;
            AutoLaunchOtd.IsChecked = true;
            UpdateOtdState();
            OtdStatus.Text = $"Detected {detected.VersionOrUnknown()} at {detected.ExecutablePath}";
            return;
        }

        OtdStatus.Text = "OpenTabletDriver was not found.";
    }

    private void AutoLaunchOtd_Changed(object sender, RoutedEventArgs e) => UpdateOtdState();

    private void UpdateOtdState()
    {
        var enabled = AutoLaunchOtd.IsChecked == true;
        OtdPath.IsEnabled = enabled;
        OtdStatus.Text = enabled
            ? string.IsNullOrWhiteSpace(OtdPath.Text)
                ? "Choose an executable path or use Detect."
                : "OpenTabletDriver will launch when osu! is detected."
            : "OpenTabletDriver auto-launch is off.";
    }

    private void AttachOwnerCentering()
    {
        if (Owner is null)
        {
            return;
        }

        Owner.LocationChanged += Owner_MovedOrSized;
        Owner.SizeChanged += Owner_MovedOrSized;
        Owner.StateChanged += Owner_MovedOrSized;
        CenterOnOwner();
    }

    private void DetachOwnerCentering()
    {
        if (Owner is null)
        {
            return;
        }

        Owner.LocationChanged -= Owner_MovedOrSized;
        Owner.SizeChanged -= Owner_MovedOrSized;
        Owner.StateChanged -= Owner_MovedOrSized;
    }

    private void Owner_MovedOrSized(object? sender, EventArgs e) => CenterOnOwner();

    private void CenterOnOwner()
    {
        if (Owner is null || Owner.WindowState == WindowState.Minimized)
        {
            return;
        }

        Left = Owner.Left + Math.Max(0, (Owner.ActualWidth - ActualWidth) / 2);
        Top = Owner.Top + Math.Max(0, (Owner.ActualHeight - ActualHeight) / 2);
    }

}
