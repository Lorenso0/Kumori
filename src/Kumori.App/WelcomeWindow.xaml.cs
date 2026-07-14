using System.IO;
using System.Windows;
using System.Windows.Controls;
using Kumori.Core;
using Kumori.Core.Settings;
using Kumori.Core.State;
using Kumori.Native;

namespace Kumori.App;

public partial class WelcomeWindow : Window
{
    public const int CurrentOnboardingVersion = 4;
    private const int StepCount = 6;
    private readonly SettingsService _settings;
    private readonly AppStateStore? _store;
    private readonly bool _initialTracking;
    private readonly bool _initialCapture;
    private int _step;
    private bool _loaded;
    public event EventHandler? DismissRequested;

    public WelcomeWindow(SettingsService settings, AppStateStore? store = null)
    {
        _settings = settings;
        _store = store;
        _initialTracking = settings.Current.Tracking.Enabled;
        _initialCapture = settings.Current.Capture.LazerReplayFrameEnabled;
        InitializeComponent();

        var saved = settings.Current.FirstRunCompleted ? 0 : settings.Current.OnboardingProgressStep;
        _step = Math.Clamp(saved, 0, StepCount - 1);
        TrackingEnabled.IsChecked = settings.Current.Tracking.Enabled;
        CaptureEnabled.IsChecked = settings.Current.Capture.LazerReplayFrameEnabled;
        RunAtLogin.IsChecked = settings.Current.Startup.RunAtLogin;
        AutoLaunchOtd.IsChecked = settings.Current.OpenTabletDriver.AutoLaunch;
        OtdPath.Text = settings.Current.OpenTabletDriver.InstallPath;
        UpdateOtdState();
        RefreshChecks();
        RenderStep();

        if (Content is FrameworkElement content)
        {
            content.Loaded += (_, _) =>
            {
                if (_loaded) return;
                _loaded = true;
                RefreshChecks();
            };
        }
        if (_store is not null)
        {
            _store.StateChanged += Store_StateChanged;
        }
    }

    private UIElement[] Steps => [WelcomeStep, SystemStep, TrackingStep, CaptureStep, OptionalStep, ReadyStep];

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        SaveDraft();
        _step = Math.Max(0, _step - 1);
        PersistProgress();
        RenderStep();
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_step == 2 && TrackingEnabled.IsChecked == true && !File.Exists(AppPaths.TosuExecutable))
        {
            FooterStatus.Text = "Install managed tosu before continuing, or disable play tracking.";
            return;
        }
        SaveDraft();
        _step = Math.Min(StepCount - 1, _step + 1);
        PersistProgress();
        RenderStep();
    }

    private void RenderStep()
    {
        var titles = new[] { "Welcome", "System check", "Play tracking", "Replay capture", "Optional integrations", "Ready" };
        foreach (var element in Steps) element.Visibility = Visibility.Collapsed;
        Steps[_step].Visibility = Visibility.Visible;
        StepCounter.Text = $"STEP {_step + 1} OF {StepCount}";
        StepProgress.Value = _step + 1;
        HeaderSubtitle.Text = titles[_step];
        BackButton.IsEnabled = _step > 0;
        NextButton.Visibility = _step < StepCount - 1 ? Visibility.Visible : Visibility.Collapsed;
        FinishButton.Visibility = _step == StepCount - 1 ? Visibility.Visible : Visibility.Collapsed;
        FooterStatus.Text = "Progress is saved automatically.";
        if (_step is 1 or 2 or 3) RefreshChecks();
        if (_step == StepCount - 1) UpdateSummary();
    }

    private void SaveDraft()
    {
        _settings.Update(s =>
        {
            s.OnboardingProgressStep = _step;
            s.Tracking.Enabled = TrackingEnabled.IsChecked == true;
            s.Capture.LazerReplayFrameEnabled = CaptureEnabled.IsChecked == true;
            s.Startup.RunAtLogin = RunAtLogin.IsChecked == true;
            s.OpenTabletDriver.AutoLaunch = AutoLaunchOtd.IsChecked == true;
            s.OpenTabletDriver.InstallPath = OtdPath.Text.Trim();
        });
        CaptureRestartNote.Visibility = CaptureEnabled.IsChecked != _initialCapture
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void PersistProgress()
        => _settings.Update(s => s.OnboardingProgressStep = _step);

    private void Done_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            StartupRegistration.SetEnabled(RunAtLogin.IsChecked == true);
        }
        catch (Exception ex)
        {
            FooterStatus.Text = $"Could not update Windows startup: {ex.Message}";
            return;
        }

        SaveDraft();
        _settings.Update(s =>
        {
            s.FirstRunCompleted = true;
            s.OnboardingVersion = CurrentOnboardingVersion;
            s.OnboardingProgressStep = StepCount - 1;
        });
        DismissRequested?.Invoke(this, EventArgs.Empty);
    }

    public void ReleaseFromHost()
    {
        if (_store is not null) _store.StateChanged -= Store_StateChanged;
    }

    private async void InstallTosu_Click(object sender, RoutedEventArgs e)
    {
        InstallTosuButton.IsEnabled = false;
        TosuProgress.Visibility = Visibility.Visible;
        TosuProgress.IsIndeterminate = true;
        TosuActionStatus.Text = "Checking the latest managed tosu release…";
        try
        {
            var result = await TosuManager.EnsureInstalledAsync(forceCheck: true);
            TosuActionStatus.Text = result.InstalledOrUpdated
                ? $"Installed tosu {result.Version}."
                : $"tosu {result.Version} is already ready.";
        }
        catch (Exception ex)
        {
            TosuActionStatus.Text = $"tosu setup failed: {ex.Message}";
        }
        finally
        {
            TosuProgress.IsIndeterminate = false;
            TosuProgress.Visibility = Visibility.Collapsed;
            InstallTosuButton.IsEnabled = true;
            RefreshChecks();
        }
    }

    private void RefreshChecks_Click(object sender, RoutedEventArgs e) => RefreshChecks();

    private void RefreshChecks()
    {
        var state = _store?.Current;
        SetStatus(OsuStatus, state?.Companions.OsuRunning == true ? "Running" : "Not running", state?.Companions.OsuRunning == true);
        var installed = File.Exists(AppPaths.TosuExecutable);
        var connected = state?.Tracking.TosuConnected == true;
        SetStatus(TosuStatus, connected ? "Connected" : installed ? "Installed · waiting" : "Not installed", installed);
        var storageReady = File.Exists(AppPaths.TrackingDatabase);
        SetStatus(StorageStatus, storageReady ? "Ready" : "Created after first play", storageReady);
        var captureEnabled = CaptureEnabled.IsChecked == true;
        var captureHealthy = state?.Capture.Health == HealthLevel.Ok;
        SetStatus(CaptureStatus, !captureEnabled ? "Disabled" : captureHealthy ? "Ready" : "Enabled · waiting", captureEnabled);

        TrackingTosuDetail.Text = connected
            ? "Connected to tosu and receiving live metadata."
            : installed
                ? "Installed. Connection starts automatically when osu! is running."
                : "Not installed yet.";
        CaptureDetail.Text = state?.Capture switch
        {
            { Health: HealthLevel.Ok, Source: { } source } => $"Capture is healthy using {source}.",
            { Error: { Length: > 0 } error } => $"Capture is waiting: {error}",
            _ => "Kumori supports detailed replay capture for both stable and lazer when compatible data is available.",
        };
    }

    private static void SetStatus(TextBlock target, string text, bool positive)
    {
        target.Text = text;
        target.SetResourceReference(TextBlock.ForegroundProperty, positive ? "Brush.Success" : "Brush.TextMuted");
    }

    private void Store_StateChanged(AppState state)
        => Dispatcher.InvokeAsync(RefreshChecks);

    private void UpdateSummary()
    {
        SummaryTracking.Text = TrackingEnabled.IsChecked == true ? "Enabled" : "Disabled";
        SummaryCapture.Text = CaptureEnabled.IsChecked == true ? "Enabled" : "Disabled";
        SummaryOtd.Text = AutoLaunchOtd.IsChecked == true ? "Enabled" : "Disabled";
        SummaryStartup.Text = RunAtLogin.IsChecked == true ? "Enabled" : "Disabled";
        if (TrackingEnabled.IsChecked != _initialTracking || CaptureEnabled.IsChecked != _initialCapture)
        {
            FooterStatus.Text = "Restart Kumori after setup to apply tracking or capture service changes.";
        }
    }

    private void Skin_Click(object sender, RoutedEventArgs e)
        => MainWindow.TryOpenOnboardingTool(new SkinLibraryWindow(_settings), "Skin library");

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
            AutoLaunchOtd.IsChecked = true;
            UpdateOtdState();
        }
    }

    private void DetectOtd_Click(object sender, RoutedEventArgs e)
    {
        var detected = OpenTabletDriverService.Detect(OtdPath.Text.Trim());
        if (detected is null)
        {
            OtdStatus.Text = "OpenTabletDriver was not found. This optional step can be skipped.";
            return;
        }
        OtdPath.Text = detected.ExecutablePath;
        AutoLaunchOtd.IsChecked = true;
        OtdStatus.Text = $"Detected {detected.VersionOrUnknown()} at {detected.ExecutablePath}";
        UpdateOtdState(preserveMessage: true);
    }

    private void AutoLaunchOtd_Changed(object sender, RoutedEventArgs e) => UpdateOtdState();

    private void UpdateOtdState(bool preserveMessage = false)
    {
        var enabled = AutoLaunchOtd.IsChecked == true;
        OtdPath.IsEnabled = enabled;
        if (preserveMessage) return;
        OtdStatus.Text = enabled
            ? string.IsNullOrWhiteSpace(OtdPath.Text)
                ? "Choose an executable or use Detect."
                : "OpenTabletDriver will run while Kumori is open."
            : "Optional · auto-launch is off.";
    }
}
