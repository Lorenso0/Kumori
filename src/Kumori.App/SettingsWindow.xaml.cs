using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using MediaBrush = System.Windows.Media.Brush;
using MediaColor = System.Windows.Media.Color;
using MediaColorConverter = System.Windows.Media.ColorConverter;
using Kumori.App.FarmFinder;
using Kumori.Core;
using Kumori.Core.Settings;
using Kumori.FarmFinder;
using Kumori.Native;
using Kumori.Storage;
using Serilog;

namespace Kumori.App;

public partial class SettingsWindow : Window
{
    private readonly SettingsService _settings;
    private readonly ThemeManager? _themes;
    private readonly string _originalThemeId;
    private readonly TrackingMaintenanceRepository _maintenance;
    private readonly Func<Task>? _trackingDataChanged;
    private readonly ObservableCollection<CustomColorRow> _customColors = [];
    private bool _loading = true;
    private bool _accepted;
    private bool _observedAutoSwitchDualMode;
    private CustomColorRow? _selectedColor;

    public SettingsWindow(
        SettingsService settings,
        Func<Task>? trackingDataChanged = null,
        TrackingMaintenanceRepository? maintenance = null)
    {
        _settings = settings;
        _trackingDataChanged = trackingDataChanged;
        _maintenance = maintenance ?? new TrackingMaintenanceRepository(
            new SqliteConnectionFactory(AppPaths.TrackingDatabase, readOnly: false));
        _themes = (Application.Current as App)?.Themes;
        _originalThemeId = ThemeManager.Resolve(settings.Current.Appearance.ThemeId).Id;
        _observedAutoSwitchDualMode = settings.Current.Display.AutoSwitchDualMode;
        InitializeComponent();
        var colorView = new ListCollectionView(_customColors);
        colorView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(CustomColorRow.Group)));
        CustomColorList.ItemsSource = colorView;
        IntegratedColorPicker.ColourChanged += Picker_ColourChanged;
        IntegratedColorPicker.CloseRequested += () => CustomColorPickerPopup.IsOpen = false;
        LoadValues();
        _loading = false;
        _settings.Changed += Settings_Changed;
        Closed += (_, _) =>
        {
            _settings.Changed -= Settings_Changed;
            if (!_accepted)
            {
                _themes?.Apply(_originalThemeId, persist: false);
            }
        };
    }

    private void Settings_Changed(KumoriSettings settings)
    {
        var autoSwitchDualMode = settings.Display.AutoSwitchDualMode;
        if (_observedAutoSwitchDualMode == autoSwitchDualMode)
            return;

        _observedAutoSwitchDualMode = autoSwitchDualMode;
        if (Dispatcher.CheckAccess())
            DualModeEnabled.IsChecked = autoSwitchDualMode;
        else
            Dispatcher.InvokeAsync(() => DualModeEnabled.IsChecked = autoSwitchDualMode);
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
        MinimumAttemptSeconds.Text = s.Tracking.MinimumAttemptSeconds.ToString(CultureInfo.InvariantCulture);
        CleanupAgeDays.Text = "30";
        LazerReplayFrameEnabled.IsChecked = s.Capture.LazerReplayFrameEnabled;
        OtdPath.Text = s.OpenTabletDriver.InstallPath;
        OtdAutoLaunch.IsChecked = s.OpenTabletDriver.AutoLaunch;
        ReplayEnabled.IsChecked = s.ReplayViewer.Enabled;
        DisableHidden.IsChecked = s.ReplayViewer.DisableHidden;
        SkinPath.Text = s.ReplayViewer.SkinPath;
        PrimaryMirror.Text = s.Media.PrimaryMirror;
        RunAtLogin.IsChecked = s.Startup.RunAtLogin;
        StartMinimized.IsChecked = s.Startup.StartMinimized;
        OnlyShowLazerExtras.IsChecked = s.SkinEditor.OnlyShowLazerExtras;
        ShowCatchExtras.IsChecked = s.SkinEditor.ShowCatchExtras;
        ShowTaikoExtras.IsChecked = s.SkinEditor.ShowTaikoExtras;
        ShowManiaExtras.IsChecked = s.SkinEditor.ShowManiaExtras;
        DualModeEnabled.IsChecked = s.Display.AutoSwitchDualMode;
        SuspendOsuDuringDualModeSwitch.IsChecked = s.Display.SuspendOsuDuringDualModeSwitch;
        AutomaticBackups.IsChecked = s.Backup.AutomaticEnabled;
        BackupInterval.Text = s.Backup.IntervalHours.ToString(CultureInfo.InvariantCulture);
        BackupRetention.Text = s.Backup.RetentionCount.ToString(CultureInfo.InvariantCulture);
        BackupDirectory.Text = s.Backup.Directory;
        DailyWebhookEnabled.IsChecked = s.DailyWebhook.Enabled;
        ScoreWebhookEnabled.IsChecked = s.DailyWebhook.ScoreAlertsEnabled;
        DailyWebhookUrl.Text = s.DailyWebhook.WebhookUrl;
        ScoreWebhookUrl.Text = s.DailyWebhook.ScoreAlertsWebhookUrl;
        RefreshKumoriAssociationStatus();
        LoadCustomTheme(CustomThemePalette.Normalize(s.Appearance.CustomTheme));
        switch (ThemeManager.Resolve(s.Appearance.ThemeId).Id)
        {
            case "pulse": PulseTheme.IsChecked = true; break;
            case "windows-fluent": FluentTheme.IsChecked = true; break;
            case ThemeManager.CustomThemeId: CustomTheme.IsChecked = true; break;
            default: RefinedTheme.IsChecked = true; break;
        }
        CustomThemeEditor.Visibility = CustomTheme.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private string SelectedThemeId =>
        PulseTheme.IsChecked == true ? "pulse" :
        FluentTheme.IsChecked == true ? "windows-fluent" :
        CustomTheme.IsChecked == true ? ThemeManager.CustomThemeId :
        ThemeManager.DefaultThemeId;

    private void Theme_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string themeId })
            return;

        CustomThemeEditor.Visibility = themeId == ThemeManager.CustomThemeId ? Visibility.Visible : Visibility.Collapsed;
        if (!_loading)
        {
            if (themeId == ThemeManager.CustomThemeId && TryReadCustomTheme(out var customTheme, showError: true))
                _themes?.PreviewCustom(customTheme);
            else if (themeId != ThemeManager.CustomThemeId)
                _themes?.Apply(themeId, persist: false);
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(MinimumAttemptSeconds.Text, out var minimumAttemptSeconds)
            || minimumAttemptSeconds is < 1 or > 300
            || !int.TryParse(BackupInterval.Text, out var backupInterval)
            || !int.TryParse(BackupRetention.Text, out var backupRetention))
        {
            SetStatus("Check the numeric values. Minimum play duration must be a whole number from 1 to 300 seconds.", isError: true);
            return;
        }

        var hasValidCustomTheme = TryReadCustomTheme(out var customTheme, showError: SelectedThemeId == ThemeManager.CustomThemeId);
        if (SelectedThemeId == ThemeManager.CustomThemeId && !hasValidCustomTheme)
            return;
        var dailyWebhookUrl = DailyWebhookUrl.Text.Trim();
        var scoreWebhookUrl = ScoreWebhookUrl.Text.Trim();
        if (DailyWebhookEnabled.IsChecked == true
            && !DailyProgressWebhookService.TryValidateWebhookUrl(dailyWebhookUrl, out _))
        {
            SetStatus("Enter a valid HTTPS Discord webhook URL for daily updates.", isError: true);
            return;
        }
        if (ScoreWebhookEnabled.IsChecked == true
            && !DailyProgressWebhookService.TryValidateWebhookUrl(scoreWebhookUrl, out _))
        {
            SetStatus("Enter a valid HTTPS Discord webhook URL for PB alerts.", isError: true);
            return;
        }
        if (ScoreWebhookEnabled.IsChecked == true)
        {
            try
            {
                var credentialStore = new FarmFinder.WindowsCredentialsStore(
                    AppPaths.FarmFinderCredentialsFile);
                if ((await credentialStore.LoadAsync())?.IsConfigured != true)
                {
                    SetStatus("Configure osu! API credentials in Farm Finder before enabling PB alerts.", isError: true);
                    return;
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or System.ComponentModel.Win32Exception)
            {
                SetStatus("Kumori could not read the protected osu! API credentials. Re-enter them in Farm Finder.", isError: true);
                return;
            }
        }
        _settings.Update(s =>
        {
            s.Tracking.Enabled = TrackingEnabled.IsChecked == true;
            s.Tracking.MinimumAttemptSeconds = minimumAttemptSeconds;
            s.Capture.LazerReplayFrameEnabled = LazerReplayFrameEnabled.IsChecked == true;
            s.OpenTabletDriver.InstallPath = OtdPath.Text.Trim();
            s.OpenTabletDriver.AutoLaunch = OtdAutoLaunch.IsChecked == true;
            s.ReplayViewer.Enabled = ReplayEnabled.IsChecked == true;
            s.ReplayViewer.DisableHidden = DisableHidden.IsChecked == true;
            s.ReplayViewer.SkinPath = SkinPath.Text.Trim();
            s.Media.PrimaryMirror = PrimaryMirror.Text.Trim();
            s.Startup.RunAtLogin = RunAtLogin.IsChecked == true;
            s.Startup.StartMinimized = StartMinimized.IsChecked == true;
            s.SkinEditor.OnlyShowLazerExtras = OnlyShowLazerExtras.IsChecked == true;
            s.SkinEditor.ShowCatchExtras = ShowCatchExtras.IsChecked == true;
            s.SkinEditor.ShowTaikoExtras = ShowTaikoExtras.IsChecked == true;
            s.SkinEditor.ShowManiaExtras = ShowManiaExtras.IsChecked == true;
            s.Display.AutoSwitchDualMode = DualModeEnabled.IsChecked == true;
            s.Display.SuspendOsuDuringDualModeSwitch = SuspendOsuDuringDualModeSwitch.IsChecked == true;
            s.Appearance.ThemeId = SelectedThemeId;
            if (hasValidCustomTheme)
                s.Appearance.CustomTheme = customTheme;
            s.Backup.AutomaticEnabled = AutomaticBackups.IsChecked == true;
            s.Backup.IntervalHours = Math.Clamp(backupInterval, 1, 720);
            s.Backup.RetentionCount = Math.Clamp(backupRetention, 1, 365);
            s.Backup.Directory = BackupDirectory.Text.Trim();
            s.DailyWebhook.Enabled = DailyWebhookEnabled.IsChecked == true;
            s.DailyWebhook.ScoreAlertsEnabled = ScoreWebhookEnabled.IsChecked == true;
            s.DailyWebhook.WebhookUrl = dailyWebhookUrl;
            s.DailyWebhook.ScoreAlertsWebhookUrl = scoreWebhookUrl;
        });
        _themes?.Apply(SelectedThemeId, persist: false);
        try
        {
            StartupRegistration.SetEnabled(
                RunAtLogin.IsChecked == true,
                StartMinimized.IsChecked == true,
                _settings.Current.Startup.ExecutablePath);
        }
        catch (Exception ex)
        {
            SetStatus($"Could not update Windows startup: {ex.Message}", isError: true);
            return;
        }
        _accepted = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private async void SendDailyWebhookTest_Click(object sender, RoutedEventArgs e)
    {
        var webhookUrl = DailyWebhookUrl.Text.Trim();
        if (!DailyProgressWebhookService.TryValidateWebhookUrl(webhookUrl, out _))
        {
            SetStatus("Enter a valid HTTPS Discord webhook URL first.", isError: true);
            return;
        }

        SendDailyWebhookTest.IsEnabled = false;
        SetStatus("Sending test daily update…");
        try
        {
            var analytics = new AnalyticsRepository(
                new SqliteConnectionFactory(AppPaths.TrackingDatabase, readOnly: true));
            await new DailyProgressWebhookService(_settings, analytics)
                .SendTestAsync(webhookUrl);
            SetStatus("Test daily update sent.");
        }
        catch (InvalidOperationException ex)
        {
            SetStatus(ex.Message, isError: true);
        }
        finally
        {
            SendDailyWebhookTest.IsEnabled = true;
        }
    }

    private async void SendScoreWebhookTest_Click(object sender, RoutedEventArgs e)
    {
        var webhookUrl = ScoreWebhookUrl.Text.Trim();
        if (!DailyProgressWebhookService.TryValidateWebhookUrl(webhookUrl, out _))
        {
            SetStatus("Enter a valid HTTPS Discord webhook URL first.", isError: true);
            return;
        }

        SendScoreWebhookTest.IsEnabled = false;
        SetStatus("Sending test PB alert…");
        try
        {
            var factory = new SqliteConnectionFactory(AppPaths.TrackingDatabase, readOnly: false);
            var credentials = new FarmFinder.WindowsCredentialsStore(AppPaths.FarmFinderCredentialsFile);
            using var api = new OsuApiClient(credentials, new OsuRankedModCatalog(), new ClockRateCalculator());
            var profileTelemetry = new ProfileTelemetryStore(factory);
            var service = new ScoreWebhookService(
                _settings,
                new ScoreWebhookRepository(factory),
                new AttemptDetailsRepository(factory),
                new MovementRepository(factory),
                new PlaySharePackageService(
                    new AttemptDetailsRepository(factory),
                    new MovementRepository(factory),
                    new SessionRepository(factory)),
                api,
                profileTelemetry.GetCurrentIdentity);
            await service.SendTestAsync(webhookUrl);
            SetStatus("Test PB alert sent.");
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, isError: true);
            Log.Warning(ex, "Could not send test PB alert");
        }
        finally
        {
            SendScoreWebhookTest.IsEnabled = true;
        }
    }

    private async void DeleteOldTrackingData_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(CleanupAgeDays.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var days)
            || days is < 1 or > 36_500)
        {
            SetStatus("Enter a whole number of days from 1 to 36500.", isError: true);
            return;
        }

        var cutoffDate = DateTime.Today.AddDays(-days);
        var owner = Application.Current?.MainWindow;
        if (!KumoriDialog.Confirm(
                owner,
                $"Permanently delete tracked plays and account history older than {days} day(s) (before {cutoffDate:dd/MM/yyyy})? This cannot be undone.",
                "Clear old performance data",
                MessageBoxImage.Warning))
        {
            return;
        }

        DeleteOldTrackingData.IsEnabled = false;
        CleanupAgeDays.IsEnabled = false;
        SetStatus("Deleting old tracking data...");
        try
        {
            var cutoff = cutoffDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var deleted = await Task.Run(() => _maintenance.DeleteTrackingBefore(cutoff));
            if (_trackingDataChanged is not null)
            {
                await _trackingDataChanged();
            }
            SetStatus($"Deleted {deleted.Attempts:N0} old play(s) and {deleted.Sessions:N0} empty session(s). Newer history was kept.");
        }
        catch (InvalidOperationException ex)
        {
            SetStatus(ex.Message, isError: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            SetStatus($"Could not clear old tracking data: {ex.Message}", isError: true);
        }
        finally
        {
            DeleteOldTrackingData.IsEnabled = true;
            CleanupAgeDays.IsEnabled = true;
        }
    }

    private void LoadCustomTheme(CustomThemeSettings theme)
    {
        CustomColorPickerPopup.IsOpen = false;
        _selectedColor = null;
        CustomThemeName.Text = theme.Name;
        foreach (var row in _customColors)
            row.PropertyChanged -= CustomColorRow_PropertyChanged;
        _customColors.Clear();
        foreach (var key in CustomThemePalette.ColorKeys)
        {
            var row = new CustomColorRow(key, theme.Colors[key]);
            row.PropertyChanged += CustomColorRow_PropertyChanged;
            _customColors.Add(row);
        }
        CustomThemeError.Text = string.Empty;
        UpdateContrastWarning();
    }

    private bool TryReadCustomTheme(out CustomThemeSettings theme, bool showError)
    {
        var colors = _customColors.ToDictionary(row => row.Key, row => row.Value, StringComparer.OrdinalIgnoreCase);
        var valid = CustomThemePalette.TryValidate(CustomThemeName.Text, colors, out theme, out var error);
        if (showError)
            CustomThemeError.Text = valid ? string.Empty : error;
        return valid;
    }

    private void CustomColor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || CustomTheme.IsChecked != true)
            return;
        if (TryReadCustomTheme(out var theme, showError: true))
            _themes?.PreviewCustom(theme);
    }

    private void CustomColorRow_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CustomColorRow.Value))
            UpdateContrastWarning();
    }

    private void CustomSwatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: CustomColorRow row } button)
            return;
        ShowColorPicker(row, button);
        e.Handled = true;
    }

    private void CustomColorRow_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { DataContext: CustomColorRow row } border)
            return;
        if (e.OriginalSource is DependencyObject source && FindAncestor<Control>(source) is Button or TextBox)
            return;
        ShowColorPicker(row, border);
        e.Handled = true;
    }

    private void CustomColorRow_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not Border { DataContext: CustomColorRow row } border
            || !ReferenceEquals(Keyboard.FocusedElement, border)
            || e.Key is not (Key.Enter or Key.Space))
        {
            return;
        }

        ShowColorPicker(row, border);
        e.Handled = true;
    }

    private void CustomColorRow_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is Border { DataContext: CustomColorRow row } border
            && ReferenceEquals(e.NewFocus, border))
        {
            SelectColor(row);
        }
    }

    private void ThemeColorScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer scroll)
            return;
        scroll.ScrollToVerticalOffset(scroll.VerticalOffset - (e.Delta / 2d));
        e.Handled = true;
    }

    private void CustomHex_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is TextBox { DataContext: CustomColorRow row })
            SelectColor(row);
    }

    private void ShowColorPicker(CustomColorRow row, FrameworkElement placementTarget)
    {
        if (!CustomThemePalette.TryNormalizeHex(row.Value, out var normalized))
        {
            CustomThemeError.Text = $"{row.Label} needs a valid #RRGGBB or #AARRGGBB value before the picker can open.";
            return;
        }

        SelectColor(row);
        IntegratedColorPicker.Open(normalized, row.Label, row.Description);
        CustomColorPickerPopup.PlacementTarget = placementTarget;
        CustomColorPickerPopup.IsOpen = true;
    }

    private void Picker_ColourChanged(string value)
    {
        if (_selectedColor is null)
            return;
        _selectedColor.Value = value;
        if (CustomTheme.IsChecked == true && TryReadCustomTheme(out var theme, showError: true))
            _themes?.PreviewCustom(theme);
    }

    private void SelectColor(CustomColorRow row)
    {
        _selectedColor = row;
        PreviewSelectionTitle.Text = row.Label;
        PreviewSelectionDescription.Text = row.Description;
        Dispatcher.BeginInvoke(() => HighlightPreview(row.Key), DispatcherPriority.Loaded);
    }

    private void UpdateContrastWarning()
    {
        if (!TryGetThemeColor("AppBackground", out var appBackground)
            || !TryGetThemeColor("CardBackground", out var cardBackground)
            || !TryGetThemeColor("CardSelectedBackground", out var selectedBackground)
            || !TryGetThemeColor("ControlBackground", out var controlBackground)
            || !TryGetThemeColor("SubtleBorder", out var subtleBorder)
            || !TryGetThemeColor("TextMuted", out var mutedText))
        {
            CustomThemeContrastWarningPanel.Visibility = Visibility.Collapsed;
            CustomThemeContrastWarning.Text = string.Empty;
            return;
        }

        var opaqueCanvas = Composite(appBackground, MediaColor.FromRgb(0, 0, 0));
        var card = Composite(cardBackground, opaqueCanvas);
        var selectedCard = Composite(selectedBackground, opaqueCanvas);
        var control = Composite(controlBackground, opaqueCanvas);
        var mutedOnCard = ContrastRatio(Composite(mutedText, card), card);
        var mutedOnSelectedCard = ContrastRatio(Composite(mutedText, selectedCard), selectedCard);
        var borderOnControl = ContrastRatio(Composite(subtleBorder, control), control);
        var warnings = new List<string>();

        if (mutedOnCard < 4.5 || mutedOnSelectedCard < 4.5)
        {
            warnings.Add(
                $"Muted text is {mutedOnCard:0.0}:1 on standard cards and {mutedOnSelectedCard:0.0}:1 on selected cards; 4.5:1 is recommended for small text.");
        }

        if (borderOnControl < 3)
        {
            warnings.Add(
                $"Subtle borders are {borderOnControl:0.0}:1 against controls; 3:1 makes control boundaries easier to distinguish.");
        }

        CustomThemeContrastWarning.Text = warnings.Count == 0
            ? string.Empty
            : $"Contrast advisory: {string.Join(" ", warnings)} This does not prevent saving or exporting the theme.";
        CustomThemeContrastWarningPanel.Visibility = warnings.Count == 0
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private bool TryGetThemeColor(string key, out MediaColor color)
    {
        var value = _customColors.FirstOrDefault(row => row.Key.Equals(key, StringComparison.OrdinalIgnoreCase))?.Value;
        if (value is not null && CustomThemePalette.TryNormalizeHex(value, out var normalized))
        {
            color = (MediaColor)MediaColorConverter.ConvertFromString(normalized);
            return true;
        }

        color = default;
        return false;
    }

    private static MediaColor Composite(MediaColor foreground, MediaColor background)
    {
        var alpha = foreground.A / 255d;
        return MediaColor.FromRgb(
            (byte)Math.Round((foreground.R * alpha) + (background.R * (1 - alpha))),
            (byte)Math.Round((foreground.G * alpha) + (background.G * (1 - alpha))),
            (byte)Math.Round((foreground.B * alpha) + (background.B * (1 - alpha))));
    }

    private static double ContrastRatio(MediaColor first, MediaColor second)
    {
        var lighter = Math.Max(RelativeLuminance(first), RelativeLuminance(second));
        var darker = Math.Min(RelativeLuminance(first), RelativeLuminance(second));
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double RelativeLuminance(MediaColor color)
    {
        static double Linearize(byte component)
        {
            var value = component / 255d;
            return value <= 0.04045
                ? value / 12.92
                : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Linearize(color.R))
               + (0.7152 * Linearize(color.G))
               + (0.0722 * Linearize(color.B));
    }

    private void HighlightPreview(string key)
    {
        FrameworkElement target = key switch
        {
            "AppBackground" => ThemePreviewRoot,
            "PanelBackground" => PreviewPanel,
            "CardBackground" => PreviewSelectedCard,
            "CardHoverBackground" => PreviewHoverCard,
            "CardSelectedBackground" => PreviewSelectedCard,
            "ControlBackground" => PreviewControl,
            "ControlHoverBackground" => PreviewHoverControl,
            "SubtleBorder" => PreviewPanel,
            "StrongBorder" => PreviewControl,
            "AccentPink" => PreviewPrimaryAccent,
            "AccentPurple" => PreviewSecondaryAccent,
            "TextPrimary" => PreviewPrimaryText,
            "TextSecondary" => PreviewSecondaryText,
            "TextMuted" => PreviewMutedText,
            "Success" => PreviewSuccess,
            "Warning" => PreviewWarning,
            "Danger" => PreviewDanger,
            "Cyan" => PreviewCyan,
            "NavigationBackground" => PreviewNavigation,
            "TopBarBackground" => PreviewTopBar,
            "OverlayBackground" => PreviewOverlay,
            "MetricBackground" => PreviewMetric,
            _ => ThemePreviewRoot,
        };

        var origin = target.TranslatePoint(new System.Windows.Point(0, 0), ThemePreviewHighlightLayer);
        Canvas.SetLeft(ThemePreviewHighlight, origin.X - 2);
        Canvas.SetTop(ThemePreviewHighlight, origin.Y - 2);
        ThemePreviewHighlight.Width = Math.Max(4, target.ActualWidth + 4);
        ThemePreviewHighlight.Height = Math.Max(4, target.ActualHeight + 4);
        ThemePreviewHighlight.Visibility = Visibility.Visible;
    }

    private static T? FindAncestor<T>(DependencyObject source) where T : DependencyObject
    {
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is T match)
                return match;
        return null;
    }

    private void ImportTheme_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import Kumori theme",
            Filter = "Kumori themes (*.kumori-theme.json)|*.kumori-theme.json|JSON files (*.json)|*.json",
        };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            var theme = CustomThemePalette.Import(File.ReadAllText(dialog.FileName));
            _loading = true;
            LoadCustomTheme(theme);
            _loading = false;
            CustomTheme.IsChecked = true;
            _themes?.PreviewCustom(theme);
            SetStatus($"Imported theme ‘{theme.Name}’. Choose Save to keep it.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _loading = false;
            CustomThemeError.Text = ex.Message;
        }
    }

    private void ExportTheme_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadCustomTheme(out var theme, showError: true))
            return;

        var safeName = string.Concat(theme.Name.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export Kumori theme",
            Filter = "Kumori themes (*.kumori-theme.json)|*.kumori-theme.json",
            FileName = $"{safeName}.kumori-theme.json",
            DefaultExt = ".kumori-theme.json",
            AddExtension = true,
        };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            File.WriteAllText(dialog.FileName, CustomThemePalette.Export(theme));
            SetStatus($"Exported theme ‘{theme.Name}’.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            CustomThemeError.Text = ex.Message;
        }
    }

    private void ResetTheme_Click(object sender, RoutedEventArgs e)
    {
        _loading = true;
        var theme = new CustomThemeSettings();
        LoadCustomTheme(theme);
        _loading = false;
        CustomTheme.IsChecked = true;
        _themes?.PreviewCustom(theme);
        SetStatus("Custom colors reset to Refined Kumori defaults. Choose Save to keep them.");
    }

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
            SetStatus("OpenTabletDriver was not found.", isError: true);
            return;
        }
        OtdPath.Text = detected.ExecutablePath;
        SetStatus($"OpenTabletDriver {detected.VersionOrUnknown()} found.");
    }

    private async void BrowseSkin_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select osu! skin",
            Filter = "osu! skin archives (*.osk)|*.osk|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog() == true)
        {
            IsEnabled = false;
            SetStatus("Importing skin…");
            try
            {
                SkinPath.Text = await Task.Run(() => SkinLibraryService.ImportFile(dialog.FileName));
                SetStatus("Skin imported.");
            }
            catch (Exception ex)
            {
                SetStatus(ex.Message, isError: true);
            }
            finally
            {
                IsEnabled = true;
            }
        }
    }

    private void SkinLibrary_Click(object sender, RoutedEventArgs e)
    {
        MainWindow.TryOpenWorkspace(new SkinLibraryWindow(_settings), "Replay viewer skin library");
        SkinPath.Text = _settings.Current.ReplayViewer.SkinPath;
    }

    private void ClearSkin_Click(object sender, RoutedEventArgs e)
    {
        SkinPath.Text = "";
        SetStatus("The built-in Argon Pro skin will be used.");
    }

    private void DeleteSkin_Click(object sender, RoutedEventArgs e)
    {
        var path = SkinPath.Text.Trim();
        if (SkinLibraryService.IsBuiltInPath(path))
        {
            SetStatus("Argon Pro is built in and cannot be deleted.");
            return;
        }
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            SetStatus("The selected imported skin no longer exists.", isError: true);
            return;
        }
        SkinLibraryService.DeleteImported(path);
        SkinPath.Text = "";
        SetStatus("Imported skin deleted. The built-in Argon Pro skin will be used.");
    }

    private void RepairKumoriAssociation_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _settings.Update(settings => settings.Startup.RegisterKumoriFiles = true);
            KumoriFileAssociation.Register();
            RefreshKumoriAssociationStatus();
            SetStatus("The .kumori file handler was registered.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            SetStatus($"Could not register .kumori files: {ex.Message}", isError: true);
        }
    }

    private void RemoveKumoriAssociation_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            KumoriFileAssociation.Remove();
            _settings.Update(settings => settings.Startup.RegisterKumoriFiles = false);
            RefreshKumoriAssociationStatus();
            SetStatus("Kumori's .kumori file-handler registration was removed and will stay disabled.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            SetStatus($"Could not remove the .kumori registration: {ex.Message}", isError: true);
        }
    }

    private void OpenDefaultApps_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            KumoriFileAssociation.OpenWindowsDefaultApps();
        }
        catch (Exception ex)
        {
            SetStatus($"Could not open Windows default-app settings: {ex.Message}", isError: true);
        }
    }

    private void RefreshKumoriAssociationStatus()
    {
        try
        {
            KumoriAssociationStatus.Text = !KumoriFileAssociation.IsRegistered()
                ? "The Kumori handler is not currently registered."
                : KumoriFileAssociation.IsCurrentDefault()
                    ? "Double-clicking .kumori files opens them in Kumori."
                    : "Kumori is available in Open with, but Windows currently uses another default.";
        }
        catch
        {
            KumoriAssociationStatus.Text = "The current Windows file association could not be read.";
        }
    }

    private void SetStatus(string message, bool isError = false)
    {
        ErrorText.Text = message;
        ErrorText.SetResourceReference(
            TextBlock.ForegroundProperty,
            isError ? "Brush.Negative" : "Brush.TextSecondary");
        StatusPanel.Visibility = string.IsNullOrWhiteSpace(message)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private sealed class CustomColorRow : INotifyPropertyChanged
    {
        private string value;

        public CustomColorRow(string key, string value)
        {
            var role = CustomThemePalette.Role(key);
            Key = key;
            Group = role.Group;
            Label = role.Label;
            Description = role.Description;
            this.value = value;
            Swatch = BrushFor(value);
        }

        public string Key { get; }
        public string Group { get; }
        public string Label { get; }
        public string Description { get; }

        public string Value
        {
            get => value;
            set
            {
                if (this.value == value)
                    return;
                this.value = value;
                if (CustomThemePalette.TryNormalizeHex(value, out var normalized))
                    Swatch = BrushFor(normalized);
                OnPropertyChanged();
            }
        }

        public MediaBrush Swatch { get; private set; }

        public event PropertyChangedEventHandler? PropertyChanged;

        private static SolidColorBrush BrushFor(string value) =>
            new((MediaColor)MediaColorConverter.ConvertFromString(value));

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            if (propertyName == nameof(Value))
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Swatch)));
        }
    }

}

internal static class SettingsWindowExtensions
{
    public static string VersionOrUnknown(this OpenTabletDriverInstallation installation) =>
        string.IsNullOrWhiteSpace(installation.Version) ? "unknown" : installation.Version;
}
