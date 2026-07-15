using System.Globalization;
using System.IO;
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
using Kumori.Core;
using Kumori.Core.Settings;
using Kumori.Native;

namespace Kumori.App;

public partial class SettingsWindow : Window
{
    private readonly SettingsService _settings;
    private readonly ThemeManager? _themes;
    private readonly string _originalThemeId;
    private readonly ObservableCollection<CustomColorRow> _customColors = [];
    private bool _loading = true;
    private bool _accepted;
    private CustomColorRow? _selectedColor;

    public SettingsWindow(SettingsService settings)
    {
        _settings = settings;
        _themes = (Application.Current as App)?.Themes;
        _originalThemeId = ThemeManager.Resolve(settings.Current.Appearance.ThemeId).Id;
        InitializeComponent();
        var colorView = new ListCollectionView(_customColors);
        colorView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(CustomColorRow.Group)));
        CustomColorList.ItemsSource = colorView;
        IntegratedColorPicker.ColourChanged += Picker_ColourChanged;
        IntegratedColorPicker.CloseRequested += () => CustomColorPickerPopup.IsOpen = false;
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
        MinimumAttemptSeconds.Text = s.Tracking.MinimumAttemptSeconds.ToString(CultureInfo.InvariantCulture);
        RetentionDays.Text = s.Tracking.RetentionDays.ToString(CultureInfo.InvariantCulture);
        LazerReplayFrameEnabled.IsChecked = s.Capture.LazerReplayFrameEnabled;
        OtdPath.Text = s.OpenTabletDriver.InstallPath;
        OtdAutoLaunch.IsChecked = s.OpenTabletDriver.AutoLaunch;
        ReplayEnabled.IsChecked = s.ReplayViewer.Enabled;
        DisableHidden.IsChecked = s.ReplayViewer.DisableHidden;
        SkinPath.Text = s.ReplayViewer.SkinPath;
        PrimaryMirror.Text = s.Media.PrimaryMirror;
        RunAtLogin.IsChecked = s.Startup.RunAtLogin;
        StartMinimized.IsChecked = s.Startup.StartMinimized;
        DualModeEnabled.IsChecked = s.Display.AutoSwitchDualMode;
        SuspendOsuDuringDualModeSwitch.IsChecked = s.Display.SuspendOsuDuringDualModeSwitch;
        AutomaticBackups.IsChecked = s.Backup.AutomaticEnabled;
        BackupInterval.Text = s.Backup.IntervalHours.ToString(CultureInfo.InvariantCulture);
        BackupRetention.Text = s.Backup.RetentionCount.ToString(CultureInfo.InvariantCulture);
        BackupDirectory.Text = s.Backup.Directory;
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

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(MinimumAttemptSeconds.Text, out var minimumAttemptSeconds)
            || minimumAttemptSeconds is < 1 or > 300
            || !int.TryParse(RetentionDays.Text, out var retention)
            || !int.TryParse(BackupInterval.Text, out var backupInterval)
            || !int.TryParse(BackupRetention.Text, out var backupRetention))
        {
            ErrorText.Text = "Check numeric values. Minimum play duration must be a whole number from 1 to 300 seconds.";
            return;
        }

        var hasValidCustomTheme = TryReadCustomTheme(out var customTheme, showError: SelectedThemeId == ThemeManager.CustomThemeId);
        if (SelectedThemeId == ThemeManager.CustomThemeId && !hasValidCustomTheme)
            return;

        _settings.Update(s =>
        {
            s.Tracking.Enabled = TrackingEnabled.IsChecked == true;
            s.Tracking.MinimumAttemptSeconds = minimumAttemptSeconds;
            s.Tracking.RetentionDays = Math.Max(0, retention);
            s.Capture.LazerReplayFrameEnabled = LazerReplayFrameEnabled.IsChecked == true;
            s.OpenTabletDriver.InstallPath = OtdPath.Text.Trim();
            s.OpenTabletDriver.AutoLaunch = OtdAutoLaunch.IsChecked == true;
            s.ReplayViewer.Enabled = ReplayEnabled.IsChecked == true;
            s.ReplayViewer.DisableHidden = DisableHidden.IsChecked == true;
            s.ReplayViewer.SkinPath = SkinPath.Text.Trim();
            s.Media.PrimaryMirror = PrimaryMirror.Text.Trim();
            s.Startup.RunAtLogin = RunAtLogin.IsChecked == true;
            s.Startup.StartMinimized = StartMinimized.IsChecked == true;
            s.Display.AutoSwitchDualMode = DualModeEnabled.IsChecked == true;
            s.Display.SuspendOsuDuringDualModeSwitch = SuspendOsuDuringDualModeSwitch.IsChecked == true;
            s.Appearance.ThemeId = SelectedThemeId;
            if (hasValidCustomTheme)
                s.Appearance.CustomTheme = customTheme;
            s.Backup.AutomaticEnabled = AutomaticBackups.IsChecked == true;
            s.Backup.IntervalHours = Math.Clamp(backupInterval, 1, 720);
            s.Backup.RetentionCount = Math.Clamp(backupRetention, 1, 365);
            s.Backup.Directory = BackupDirectory.Text.Trim();
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
            ErrorText.Text = $"Could not update Windows startup: {ex.Message}";
            return;
        }
        _accepted = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void LoadCustomTheme(CustomThemeSettings theme)
    {
        CustomColorPickerPopup.IsOpen = false;
        _selectedColor = null;
        CustomThemeName.Text = theme.Name;
        _customColors.Clear();
        foreach (var key in CustomThemePalette.ColorKeys)
            _customColors.Add(new CustomColorRow(key, theme.Colors[key]));
        CustomThemeError.Text = string.Empty;
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
            ErrorText.Text = $"Imported theme ‘{theme.Name}’. Save to keep it.";
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
            ErrorText.Text = $"Exported theme ‘{theme.Name}’.";
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
        ErrorText.Text = "Custom colors reset to Refined Kumori defaults. Save to keep them.";
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
            ErrorText.Text = "OpenTabletDriver was not found.";
            return;
        }
        OtdPath.Text = detected.ExecutablePath;
        ErrorText.Text = $"OpenTabletDriver {detected.VersionOrUnknown()} found.";
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
            ErrorText.Text = "Importing skin...";
            try
            {
                SkinPath.Text = await Task.Run(() => SkinLibraryService.ImportFile(dialog.FileName));
                ErrorText.Text = "Skin imported.";
            }
            catch (Exception ex)
            {
                ErrorText.Text = ex.Message;
            }
            finally
            {
                IsEnabled = true;
            }
        }
    }

    private void SkinLibrary_Click(object sender, RoutedEventArgs e)
    {
        MainWindow.TryOpenWorkspace(new SkinLibraryWindow(_settings), "Skin library");
        SkinPath.Text = _settings.Current.ReplayViewer.SkinPath;
    }

    private void ClearSkin_Click(object sender, RoutedEventArgs e)
    {
        SkinPath.Text = "";
        ErrorText.Text = "Argon Pro will be used.";
    }

    private void DeleteSkin_Click(object sender, RoutedEventArgs e)
    {
        var path = SkinPath.Text.Trim();
        if (SkinLibraryService.IsBuiltInPath(path))
        {
            ErrorText.Text = "Argon Pro is built in and cannot be deleted.";
            return;
        }
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            ErrorText.Text = "The selected imported skin no longer exists.";
            return;
        }
        SkinLibraryService.DeleteImported(path);
        SkinPath.Text = "";
        ErrorText.Text = "Imported skin deleted.";
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
