using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
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
    private readonly bool _isReturning;
    private int _step;
    private bool _loaded;
    public event EventHandler? DismissRequested;

    public WelcomeWindow(SettingsService settings, AppStateStore? store = null)
    {
        _settings = settings;
        _store = store;
        _initialTracking = settings.Current.Tracking.Enabled;
        _initialCapture = settings.Current.Capture.LazerReplayFrameEnabled;
        _isReturning = settings.Current.FirstRunCompleted;
        InitializeComponent();

        var saved = _isReturning ? 0 : settings.Current.OnboardingProgressStep;
        _step = Math.Clamp(saved, 0, StepCount - 1);
        TrackingEnabled.IsChecked = settings.Current.Tracking.Enabled;
        MinimumAttemptSeconds.Text = settings.Current.Tracking.MinimumAttemptSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        CaptureEnabled.IsChecked = settings.Current.Capture.LazerReplayFrameEnabled;
        RunAtLogin.IsChecked = settings.Current.Startup.RunAtLogin;
        StartMinimized.IsChecked = settings.Current.Startup.StartMinimized;
        AutoLaunchOtd.IsChecked = settings.Current.OpenTabletDriver.AutoLaunch;
        OtdPath.Text = settings.Current.OpenTabletDriver.InstallPath;
        ConfigureModeCopy();
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
                MoveFocusToCurrentStep();
            };
        }
        if (_store is not null)
        {
            _store.StateChanged += Store_StateChanged;
        }
    }

    private UIElement[] Steps => [WelcomeStep, SystemStep, TrackingStep, CaptureStep, OptionalStep, ReadyStep];
    private FrameworkElement[] StepHeadings => [WelcomeHeading, SystemHeading, TrackingHeading, CaptureHeading, OptionalHeading, ReadyHeading];

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (!_isReturning) SaveDraft();
        _step = Math.Max(0, _step - 1);
        if (!_isReturning) PersistProgress();
        RenderStep();
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_step == 2 && TrackingEnabled.IsChecked == true && !File.Exists(AppPaths.TosuExecutable))
        {
            ShowValidation("Install managed tosu before continuing, or disable play tracking.", InstallTosuButton);
            return;
        }
        if (_step == 2 && !TryReadMinimumAttemptSeconds(out _))
        {
            ShowValidation("Minimum play duration must be a whole number from 1 to 300 seconds.", MinimumAttemptSeconds);
            return;
        }
        if (!_isReturning) SaveDraft();
        _step = Math.Min(StepCount - 1, _step + 1);
        if (!_isReturning) PersistProgress();
        RenderStep();
    }

    private void RenderStep()
    {
        var titles = new[] { "Welcome", "System check", "Play tracking", "Replay capture", "Optional integrations", "Ready" };
        foreach (var element in Steps) element.Visibility = Visibility.Collapsed;
        Steps[_step].Visibility = Visibility.Visible;
        StepCounter.Text = $"STEP {_step + 1} OF {StepCount}";
        StepProgress.Value = _step + 1;
        HeaderSubtitle.Text = _isReturning
            ? $"{titles[_step]} · Changes apply only when you save."
            : $"{titles[_step]} · Progress is saved automatically.";
        BackButton.IsEnabled = _step > 0;
        NextButton.Visibility = _step < StepCount - 1 ? Visibility.Visible : Visibility.Collapsed;
        FinishButton.Visibility = _step == StepCount - 1 ? Visibility.Visible : Visibility.Collapsed;
        SetFooterStatus(
            _isReturning ? "Nothing has been applied yet." : "Progress is saved automatically.",
            isError: false,
            announce: false);
        CaptureRestartNote.Visibility = CaptureEnabled.IsChecked != _initialCapture
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (_step is 1 or 2 or 3) RefreshChecks();
        if (_step == StepCount - 1) UpdateSummary();
        if (_loaded) MoveFocusToCurrentStep();
    }

    private void ConfigureModeCopy()
    {
        if (!_isReturning)
        {
            CancelButton.Visibility = Visibility.Collapsed;
            return;
        }

        Title = "Review Kumori Setup";
        HeaderTitle.Text = "Review Kumori setup";
        CancelButton.Visibility = Visibility.Visible;
        FinishButton.Content = "Save changes";
        AutomationProperties.SetName(FinishButton, "Save setup changes");
        ReadyHeading.Text = "Review changes";
        ReadyIntro.Text = "Confirm your choices below. Nothing changes until you choose Save changes.";
        ReadyPrimaryMessage.Text = "Ready to update Kumori.";
        ReadySecondaryMessage.Text = "Choose Save changes to apply these choices, or Cancel to keep your current setup.";
        CaptureRestartNote.Text = "This change is not applied until you save. If a play is active then, it applies after the play finishes.";
    }

    private void MoveFocusToCurrentStep()
    {
        Dispatcher.InvokeAsync(() =>
        {
            StepScroller.ScrollToTop();
            var heading = StepHeadings[_step];
            heading.Focus();
            Keyboard.Focus(heading);
            RaiseLiveRegionChanged(StepCounter);
        }, DispatcherPriority.Input);
    }

    private void ShowTrackingValidation(string message, FrameworkElement target)
    {
        _step = 2;
        if (!_isReturning) PersistProgress();
        RenderStep();
        ShowValidation(message, target);
    }

    private void ShowValidation(string message, FrameworkElement target)
    {
        SetFooterStatus(message, isError: true, announce: true);
        Dispatcher.InvokeAsync(() =>
        {
            target.Focus();
            Keyboard.Focus(target);
            if (target is TextBox textBox) textBox.SelectAll();
        }, DispatcherPriority.Input);
    }

    private void SetFooterStatus(string message, bool isError, bool announce)
    {
        FooterStatus.Text = message;
        FooterStatus.SetResourceReference(
            TextBlock.ForegroundProperty,
            isError ? "Brush.Warning" : "Brush.TextSecondary");
        if (announce && _loaded)
        {
            Dispatcher.InvokeAsync(
                () => RaiseLiveRegionChanged(FooterStatus),
                DispatcherPriority.ContextIdle);
        }
    }

    private static void RaiseLiveRegionChanged(UIElement element)
    {
        var peer = UIElementAutomationPeer.FromElement(element)
            ?? UIElementAutomationPeer.CreatePeerForElement(element);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private void SetLiveText(TextBlock target, string text)
    {
        if (string.Equals(target.Text, text, StringComparison.Ordinal)) return;
        target.Text = text;
        if (_loaded && target.IsVisible)
        {
            Dispatcher.InvokeAsync(
                () => RaiseLiveRegionChanged(target),
                DispatcherPriority.ContextIdle);
        }
    }

    private void SaveDraft()
    {
        _settings.Update(s =>
        {
            s.OnboardingProgressStep = _step;
            s.Tracking.Enabled = TrackingEnabled.IsChecked == true;
            if (TryReadMinimumAttemptSeconds(out var minimumAttemptSeconds))
                s.Tracking.MinimumAttemptSeconds = minimumAttemptSeconds;
            s.Capture.LazerReplayFrameEnabled = CaptureEnabled.IsChecked == true;
            s.Startup.RunAtLogin = RunAtLogin.IsChecked == true;
            s.Startup.StartMinimized = StartMinimized.IsChecked == true;
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
        if (TrackingEnabled.IsChecked == true && !File.Exists(AppPaths.TosuExecutable))
        {
            ShowTrackingValidation(
                "Install managed tosu before saving, or disable play tracking.",
                InstallTosuButton);
            return;
        }
        if (!TryReadMinimumAttemptSeconds(out _))
        {
            ShowTrackingValidation(
                "Minimum play duration must be a whole number from 1 to 300 seconds.",
                MinimumAttemptSeconds);
            return;
        }
        try
        {
            StartupRegistration.SetEnabled(
                RunAtLogin.IsChecked == true,
                StartMinimized.IsChecked == true,
                _settings.Current.Startup.ExecutablePath);
        }
        catch (Exception ex)
        {
            ShowValidation($"Could not update Windows startup: {ex.Message}", FinishButton);
            return;
        }

        SaveDraft();
        _settings.Update(s =>
        {
            s.FirstRunCompleted = true;
            s.OnboardingVersion = CurrentOnboardingVersion;
            s.OnboardingProgressStep = StepCount - 1;
        });
        RequestDismiss();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (!_isReturning) return;
        RequestDismiss();
    }

    private void SetupRoot_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_isReturning || e.Key != Key.Escape) return;
        e.Handled = true;
        RequestDismiss();
    }

    private void RequestDismiss()
    {
        if (DismissRequested is { } dismissRequested)
        {
            dismissRequested.Invoke(this, EventArgs.Empty);
            return;
        }

        Close();
    }

    private bool TryReadMinimumAttemptSeconds(out int value)
        => int.TryParse(
               MinimumAttemptSeconds.Text,
               System.Globalization.NumberStyles.Integer,
               System.Globalization.CultureInfo.InvariantCulture,
               out value)
           && value is >= 1 and <= 300;

    public void ReleaseFromHost()
    {
        if (_store is not null) _store.StateChanged -= Store_StateChanged;
    }

    private async void InstallTosu_Click(object sender, RoutedEventArgs e)
    {
        InstallTosuButton.IsEnabled = false;
        TosuProgress.Visibility = Visibility.Visible;
        TosuProgress.IsIndeterminate = true;
        SetLiveText(TosuActionStatus, "Checking the latest managed tosu release…");
        try
        {
            var result = await TosuManager.EnsureInstalledAsync(forceCheck: true);
            SetLiveText(TosuActionStatus, result.InstalledOrUpdated
                ? $"Installed tosu {result.Version}."
                : $"tosu {result.Version} is already ready.");
        }
        catch (Exception ex)
        {
            SetLiveText(TosuActionStatus, $"tosu setup failed: {ex.Message}");
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

    private void SetStatus(TextBlock target, string text, bool positive)
    {
        var changed = !string.Equals(target.Text, text, StringComparison.Ordinal);
        target.Text = text;
        target.SetResourceReference(TextBlock.ForegroundProperty, positive ? "Brush.Success" : "Brush.TextMuted");
        if (changed && _loaded && SystemStep.IsVisible)
        {
            Dispatcher.InvokeAsync(
                () => RaiseLiveRegionChanged(target),
                DispatcherPriority.ContextIdle);
        }
    }

    private void Store_StateChanged(AppState state)
        => Dispatcher.InvokeAsync(RefreshChecks);

    private void UpdateSummary()
    {
        SummaryTracking.Text = TrackingEnabled.IsChecked == true ? "Enabled" : "Disabled";
        SummaryMinimumDuration.Text = TryReadMinimumAttemptSeconds(out var minimumAttemptSeconds)
            ? $"{minimumAttemptSeconds} seconds"
            : "Invalid";
        SummaryCapture.Text = CaptureEnabled.IsChecked == true ? "Enabled" : "Disabled";
        SummaryOtd.Text = AutoLaunchOtd.IsChecked == true ? "Enabled" : "Disabled";
        SummaryStartup.Text = RunAtLogin.IsChecked == true
            ? StartMinimized.IsChecked == true ? "Enabled (minimized)" : "Enabled"
            : "Disabled";
        if (TrackingEnabled.IsChecked != _initialTracking || CaptureEnabled.IsChecked != _initialCapture)
        {
            SetFooterStatus(
                _isReturning
                    ? "Changes are ready to save; nothing has been applied yet."
                    : "Tracking changes are applied automatically; an active play is allowed to finish first.",
                isError: false,
                announce: false);
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
            SetLiveText(OtdStatus, "OpenTabletDriver was not found. This optional step can be skipped.");
            return;
        }
        OtdPath.Text = detected.ExecutablePath;
        AutoLaunchOtd.IsChecked = true;
        SetLiveText(OtdStatus, $"Detected {detected.VersionOrUnknown()} at {detected.ExecutablePath}");
        UpdateOtdState(preserveMessage: true);
    }

    private void AutoLaunchOtd_Changed(object sender, RoutedEventArgs e) => UpdateOtdState();

    private void CaptureEnabled_Changed(object sender, RoutedEventArgs e)
    {
        CaptureRestartNote.Visibility = CaptureEnabled.IsChecked != _initialCapture
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (_loaded && CaptureRestartNote.IsVisible)
            RaiseLiveRegionChanged(CaptureRestartNote);
    }

    private void UpdateOtdState(bool preserveMessage = false)
    {
        var enabled = AutoLaunchOtd.IsChecked == true;
        OtdPath.IsEnabled = enabled;
        if (preserveMessage) return;
        SetLiveText(OtdStatus, enabled
            ? string.IsNullOrWhiteSpace(OtdPath.Text)
                ? "Choose an executable or use Detect."
                : "OpenTabletDriver will run while Kumori is open."
            : "Optional · auto-launch is off.");
    }
}
