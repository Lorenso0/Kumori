using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Input;
using System.Windows.Threading;
using Kumori.App.ViewModels;
using Kumori.App.Skins;
using Kumori.Core.Settings;
using Kumori.Native;
using Kumori.Storage;

namespace Kumori.App;

public partial class MainWindow : Window
{
    private const double DefaultWindowWidth = 1180;
    private const double DefaultWindowHeight = 820;
    private const double MinimumRestoredWindowWidth = 720;
    private const double MinimumRestoredWindowHeight = 480;
    private static readonly Geometry MaximizeIconGeometry = Geometry.Parse("M 1.5 1.5 L 10.5 1.5 L 10.5 10.5 L 1.5 10.5 Z");
    private static readonly Geometry RestoreIconGeometry = Geometry.Parse("M 3.5 1.5 L 10.5 1.5 L 10.5 8.5 M 1.5 3.5 L 8.5 3.5 L 8.5 10.5 L 1.5 10.5 Z");

    private readonly SettingsService _settings;
    private readonly MainViewModel _mainViewModel;
    private readonly ImportsViewModel? _importsViewModel;
    private readonly PlaySharePackageService? _playShare;
    private readonly ILazerSkinReloadService? _lazerSkinReload;
    private readonly SemaphoreSlim _importGate = new(1, 1);
    private ResponsiveLayoutState _layoutState;
    private bool _compactInspectorOpen;
    private string _selectedPage = "Dashboard";
    private WelcomeWindow? _onboardingWindow;
    private SkinLibraryWindow? _onboardingToolWindow;
    private SkinEditorPage? _skinEditorPage;
    private IInputElement? _focusBeforeOnboarding;
    private long? _expandedTechnicalDetailsAttemptId;

    /// <summary>Set by App before Shutdown so the tray Exit actually closes the window.</summary>
    public bool ForceClose { get; set; }

    public MainWindow(
        MainViewModel viewModel,
        SettingsService settings,
        ImportsViewModel? importsViewModel = null,
        PlaySharePackageService? playShare = null)
        : this(viewModel, settings, importsViewModel, playShare, gameplayWork: null)
    {
    }

    internal MainWindow(
        MainViewModel viewModel,
        SettingsService settings,
        ImportsViewModel? importsViewModel,
        PlaySharePackageService? playShare,
        GameplayWorkCoordinator? gameplayWork)
    {
        _settings = settings;
        _mainViewModel = viewModel;
        _importsViewModel = importsViewModel;
        _playShare = playShare;
        _lazerSkinReload = new LazerSkinReloadService(this);
        DataContext = viewModel;
        viewModel.WorkspaceWindowRequested += OpenWorkspaceTab;
        InitializeComponent();
        ApplyInitialBounds();
        SizeChanged += (_, _) => ApplyResponsiveLayout();
        StateChanged += (_, _) => UpdateWindowChromeState();
        Loaded += (_, _) =>
        {
            ApplyResponsiveLayout();
            UpdateWindowChromeState();
        };
        Closing += (_, e) =>
        {
            SaveBounds();
            // Tray app: closing the window hides it; Exit lives in the tray menu.
            if (!ForceClose)
            {
                e.Cancel = true;
                Hide();
            }
        };
        Closed += (_, _) => (_lazerSkinReload as IDisposable)?.Dispose();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowMaximizeWorkArea.Attach(this);
        // Dark title bar before first render — part of the no-flicker plan.
        DarkTitleBar.Apply(new WindowInteropHelper(this).Handle);
    }

    private void HistoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // History separators are not selectable — revert any non-attempt selection
        // back to the previously selected attempt (or clear it).
        if (sender is ListBox lb && lb.SelectedItem is not null and not AttemptRowViewModel)
        {
            lb.SelectedItem = e.RemovedItems.OfType<AttemptRowViewModel>().FirstOrDefault();
        }
        else if (sender is ListBox { SelectedItem: AttemptRowViewModel } && _layoutState.IsCompact)
        {
            _compactInspectorOpen = true;
            ApplyResponsiveLayout();
            Dispatcher.BeginInvoke(
                DispatcherPriority.Input,
                new Action(() => InspectorBackButton.Focus()));
        }

        ScheduleTechnicalDetailsSelectionCheck();
    }

    private void DayToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: DayRowViewModel row }
            && DataContext is MainViewModel vm)
        {
            vm.ToggleDay(row);
        }
    }

    private void TechnicalDetailsExpander_Expanded(object sender, RoutedEventArgs e)
        => _expandedTechnicalDetailsAttemptId = CurrentSelectedAttemptId();

    private void TechnicalDetailsExpander_Collapsed(object sender, RoutedEventArgs e)
        => _expandedTechnicalDetailsAttemptId = null;

    private void ScheduleTechnicalDetailsSelectionCheck()
    {
        if (TechnicalDetailsExpander is not { IsExpanded: true })
        {
            return;
        }

        Dispatcher.BeginInvoke(
            DispatcherPriority.ContextIdle,
            new Action(() =>
            {
                var selectedAttemptId = CurrentSelectedAttemptId();
                if (TechnicalDetailsExpander.IsExpanded
                    && selectedAttemptId != _expandedTechnicalDetailsAttemptId)
                {
                    TechnicalDetailsExpander.IsExpanded = false;
                }
            }));
    }

    private void SessionToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: SessionRowViewModel row }
            && DataContext is MainViewModel vm)
        {
            vm.ToggleSession(row);
        }
    }

    private void ScrollingTitle_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement viewport)
        {
            return;
        }
        var title = FindVisualChild<TextBlock>(viewport);
        if (title is null)
        {
            return;
        }

        var transform = EnsureTranslateTransform(title);
        transform.BeginAnimation(TranslateTransform.XProperty, null);
        transform.X = 0;
        title.Width = double.NaN;

        var fullWidth = MeasureTextWidth(title);
        var overflow = fullWidth - viewport.ActualWidth;
        if (overflow <= 1)
        {
            ResetTitleScroll(title, TimeSpan.Zero);
            return;
        }

        title.Width = fullWidth;
        var distance = overflow + 12;
        var seconds = Math.Clamp(distance / 48.0, 3.0, 9.0);
        var animation = new DoubleAnimation(0, -distance, TimeSpan.FromSeconds(seconds))
        {
            BeginTime = TimeSpan.FromMilliseconds(350),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
        };
        transform.BeginAnimation(TranslateTransform.XProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private void ScrollingTitle_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement viewport &&
            FindVisualChild<TextBlock>(viewport) is { } title)
        {
            ResetTitleScroll(title, TimeSpan.FromMilliseconds(120));
        }
    }

    private static void ResetTitleScroll(TextBlock title, TimeSpan duration)
    {
        var transform = EnsureTranslateTransform(title);
        var animation = new DoubleAnimation(0, duration);
        if (duration > TimeSpan.Zero)
        {
            animation.Completed += (_, _) => title.Width = double.NaN;
        }
        else
        {
            title.Width = double.NaN;
        }
        transform.BeginAnimation(TranslateTransform.XProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private static double MeasureTextWidth(TextBlock title)
    {
        var dpi = VisualTreeHelper.GetDpi(title);
        var formatted = new FormattedText(
            title.Text ?? "",
            CultureInfo.CurrentCulture,
            title.FlowDirection,
            new Typeface(title.FontFamily, title.FontStyle, title.FontWeight, title.FontStretch),
            title.FontSize,
            title.Foreground,
            dpi.PixelsPerDip)
        {
            MaxLineCount = 1,
        };
        return formatted.WidthIncludingTrailingWhitespace;
    }

    private static TranslateTransform EnsureTranslateTransform(TextBlock title)
    {
        if (title.RenderTransform is TranslateTransform { IsFrozen: false } transform)
        {
            return transform;
        }
        transform = new TranslateTransform();
        title.RenderTransform = transform;
        return transform;
    }

    private static T? FindVisualChild<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                return match;
            }
            if (FindVisualChild<T>(child) is { } nested)
            {
                return nested;
            }
        }
        return null;
    }

    private void CardOverflow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: AttemptRowViewModel row } button)
        {
            return;
        }
        var menu = new ContextMenu();
        var replay = new MenuItem { Header = "Open Replay Analyzer", IsEnabled = row.CanOpenReplayInspector };
        if (DashboardRoot.DataContext is ImportsViewModel imports)
        {
            replay.Click += async (_, _) => await TryUiActionAsync(() => imports.OpenReplayInspectorAsync(row));
            var deleteImport = new MenuItem { Header = "Delete imported play" };
            deleteImport.Click += async (_, _) => await TryUiActionAsync(() => imports.DeleteAsync(row, this));
            menu.Items.Add(replay);
            menu.Items.Add(new Separator());
            menu.Items.Add(deleteImport);
            menu.PlacementTarget = button;
            menu.IsOpen = true;
            return;
        }
        if (DashboardRoot.DataContext is not MainViewModel vm)
            return;
        replay.Click += async (_, _) => await TryUiActionAsync(() => vm.OpenReplayInspectorAsync(row));
        var export = new MenuItem { Header = "Export play as .kumori", IsEnabled = vm.CanExportPlay(row) };
        export.Click += async (_, _) => await TryUiActionAsync(() => vm.ExportPlayAsync(row));
        var showAll = new MenuItem { Header = "Show all plays for this map" };
        showAll.Click += async (_, _) => await vm.ShowAllPlaysForMapAsync(row);
        var delete = new MenuItem { Header = "Delete this attempt", IsEnabled = !vm.HasActiveSession };
        delete.Click += async (_, _) => await vm.DeleteAttemptAsync(row);
        menu.Items.Add(replay);
        menu.Items.Add(export);
        menu.Items.Add(new Separator());
        menu.Items.Add(showAll);
        menu.Items.Add(new Separator());
        menu.Items.Add(delete);
        menu.PlacementTarget = button;
        menu.IsOpen = true;
    }

    private void SessionOverflow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: SessionRowViewModel row } button
            || DataContext is not MainViewModel vm)
        {
            return;
        }
        var menu = new ContextMenu();
        var delete = new MenuItem { Header = "Delete this session", IsEnabled = !vm.HasActiveSession };
        delete.Click += async (_, _) => await vm.DeleteSessionAsync(row.Model.Id);
        menu.Items.Add(delete);
        menu.PlacementTarget = button;
        menu.IsOpen = true;
    }

    private void History_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (DashboardRoot.DataContext is not MainViewModel vm)
        {
            return;
        }
        // "At bottom" once the last pixels are in view, or when the whole list
        // already fits the viewport. Gates the Load older button.
        const double threshold = 24;
        vm.IsScrolledToBottom = e.ViewportHeight <= 0
            || e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - threshold;
    }

    private void ActionsButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button)
        {
            return;
        }

        menu.DataContext = button.DataContext;
        menu.PlacementTarget = button;
        menu.IsOpen = true;
    }

    private void FilterChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string filter })
            return;
        if (DashboardRoot.DataContext is MainViewModel viewModel)
        {
            viewModel.SelectedFilterMode = filter;
        }
        else if (DashboardRoot.DataContext is ImportsViewModel imports)
        {
            imports.SelectedFilterMode = filter;
        }
    }

    private void ModMatchMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string mode })
            return;
        if (DashboardRoot.DataContext is MainViewModel viewModel)
        {
            viewModel.SelectedModFilterMode = mode;
        }
        else if (DashboardRoot.DataContext is ImportsViewModel imports)
        {
            imports.SelectedModFilterMode = mode;
        }
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.OpenSettingsCommand.Execute(null);
        }
    }

    private void MinimizeWindow_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeWindow_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void UpdateWindowChromeState()
    {
        var isMaximized = WindowState == WindowState.Maximized;
        MaximizeGlyph.Data = isMaximized ? RestoreIconGeometry : MaximizeIconGeometry;
        MaximizeWindowButton.ToolTip = isMaximized ? "Restore" : "Maximize";
        System.Windows.Automation.AutomationProperties.SetName(
            MaximizeWindowButton,
            isMaximized ? "Restore window" : "Maximize window");
    }

    private void CloseWindow_Click(object sender, RoutedEventArgs e) => Close();

    private void DashboardNavigation_Click(object sender, RoutedEventArgs e)
    {
        _compactInspectorOpen = false;
        ShowPage("Dashboard");
        HistoryList.Focus();
    }

    private void PerformanceNavigation_Click(object sender, RoutedEventArgs e) => ShowPage("Performance");
    private void MapsNavigation_Click(object sender, RoutedEventArgs e) => ShowPage("Maps");
    private async void ImportsNavigation_Click(object sender, RoutedEventArgs e)
    {
        ShowPage("Imports");
        if (_importsViewModel is not null)
            await _importsViewModel.RefreshAsync(_importsViewModel.SelectedAttempt?.Id);
    }
    private async void SkinEditorNavigation_Click(object sender, RoutedEventArgs e)
    {
        ShowPage("SkinEditor");
        if (_skinEditorPage is null)
        {
            _skinEditorPage = new SkinEditorPage(
                _settings,
                realmService: null,
                reloadService: _lazerSkinReload);
            SkinEditorHost.Content = _skinEditorPage;
            // Let WPF paint the themed page shell before Realm discovery and
            // preview decoding begin.
            await Dispatcher.Yield(DispatcherPriority.Loaded);
        }
        await _skinEditorPage.EnsureLoadedAsync();
    }
    private void ChangelogNavigation_Click(object sender, RoutedEventArgs e) =>
        OpenWorkspaceTab(new ChangelogWindow(), "Changelog");
    private void DiscordNavigation_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(SupportLinks.DiscordInviteUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            KumoriDialog.Show(this, $"Could not open Discord.\n\n{ex.Message}", "Kumori", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SettingsNavigation_Click(object sender, RoutedEventArgs e)
    {
        ShowPage("Settings");
        if (WorkspaceTabs.Items.Count == 0 && DataContext is MainViewModel viewModel)
        {
            viewModel.OpenSettingsCommand.Execute(null);
        }
    }
    private void ShowPage(string page)
    {
        _selectedPage = page;
        NavigationView.SelectedPage = page;
        bool playBrowser = page is "Dashboard" or "Imports";
        DashboardRoot.Visibility = playBrowser ? Visibility.Visible : Visibility.Collapsed;
        DashboardRoot.DataContext = page == "Imports" && _importsViewModel is not null
            ? _importsViewModel
            : _mainViewModel;
        DashboardTitleText.Text = page == "Imports" ? "Imports" : "Dashboard";
        DashboardSubtitleText.Text = page == "Imports"
            ? "Shared plays imported from .kumori files"
            : "Overview of your recent session";
        HistoryHeaderTitle.Text = page == "Imports" ? "Imported Plays" : "Play History";
        InspectorHeaderTitle.Text = page == "Imports" ? "Shared Play" : "Selected Play";
        PerformancePage.Visibility = page == "Performance" ? Visibility.Visible : Visibility.Collapsed;
        MapsPage.Visibility = page == "Maps" ? Visibility.Visible : Visibility.Collapsed;
        SkinEditorPage.Visibility = page == "SkinEditor" ? Visibility.Visible : Visibility.Collapsed;
        WorkspacePage.Visibility = page == "Settings" ? Visibility.Visible : Visibility.Collapsed;
        ApplyResponsiveLayout();
    }

    private async void ImportSharedPlay_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import Kumori shared play",
            Filter = "Kumori shared play (*.kumori)|*.kumori",
            DefaultExt = PlaySharePackageService.FileExtension,
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) == true)
            await ImportPackageFromPathAsync(dialog.FileName);
    }

    public async Task ImportPackageFromPathAsync(string path)
    {
        if (_playShare is null || _importsViewModel is null)
        {
            KumoriDialog.Show(this, "Shared-play importing is not available.", "Import failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        await _importGate.WaitAsync();
        try
        {
            KumoriPackagePreview preview = await _playShare.PreviewAsync(path);
            SharedPlayV1 play = preview.Play;
            string omissions = preview.OptionalMediaOmissions.Count == 0
                ? ""
                : $"\n\nOptional media not included:\n{string.Join("\n", preview.OptionalMediaOmissions)}";
            KumoriDialog.ToggleConfirmation confirmation = KumoriDialog.ConfirmWithToggle(
                this,
                $"Shared by {preview.PlayerName}\n\n" +
                $"{play.Map.Artist} — {play.Map.Title} [{play.Map.Difficulty}]\n" +
                $"Score {play.Score:N0}  ·  {play.Accuracy:0.00}%  ·  {play.ModsKey}\n" +
                $"Replay {TimeSpan.FromSeconds(play.Results.DurationSeconds):m\\:ss}  ·  {FormatBytes(preview.PackageSize)}" +
                omissions +
                "\n\nPlayer attribution is supplied by the sender and is not verified.",
                "Delete the .kumori file after a successful import",
                _settings.Current.Startup.DeleteSharedPackageAfterImport,
                "Import shared play",
                MessageBoxImage.Question);
            if (confirmation.IsChecked != _settings.Current.Startup.DeleteSharedPackageAfterImport)
            {
                _settings.Update(settings =>
                    settings.Startup.DeleteSharedPackageAfterImport = confirmation.IsChecked);
            }
            if (!confirmation.Confirmed)
                return;
            KumoriImportResult result = await _playShare.ImportAsync(path);
            string? packageDeleteWarning = null;
            if (confirmation.IsChecked)
            {
                try
                {
                    File.Delete(Path.GetFullPath(path));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    packageDeleteWarning = ex.Message;
                }
            }
            await _importsViewModel.RefreshAsync(result.ImportId);
            _compactInspectorOpen = false;
            ShowPage("Imports");
            if (result.AlreadyImported)
                _importsViewModel.HistoryStatus = "This shared play was already imported";
            else if (result.ReusedLocalAssetCount > 0)
                _importsViewModel.HistoryStatus =
                    $"Imported play · reused {result.ReusedLocalAssetCount} local file(s), saving {FormatBytes(result.ReusedLocalAssetBytes)}";
            if (packageDeleteWarning is not null)
            {
                KumoriDialog.Show(
                    this,
                    $"The play was imported, but the .kumori file could not be deleted.\n\n{packageDeleteWarning}",
                    "Import complete",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            Activate();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            KumoriDialog.Show(
                this,
                $"Kumori could not import that file.\n\n{ex.Message}",
                "Import failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            _importGate.Release();
        }
    }

    public void OpenWorkspaceTab(Window window, string title)
    {
        ShowPage("Settings");
        foreach (TabItem existing in WorkspaceTabs.Items)
        {
            if (existing.Tag is Window existingWindow && existingWindow.GetType() == window.GetType())
            {
                WorkspaceTabs.SelectedItem = existing;
                // Commands construct the candidate window before requesting a
                // tab. Close an unused duplicate so its dispatcher timers and
                // event subscriptions cannot keep the full window alive.
                window.Close();
                return;
            }
        }

        if (window.Content is not UIElement content)
        {
            return;
        }

        window.Content = null;
        var host = new ContentControl
        {
            Content = content,
            DataContext = window.DataContext,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
        };
        foreach (var key in window.Resources.Keys)
        {
            host.Resources[key] = window.Resources[key];
        }
        foreach (var dictionary in window.Resources.MergedDictionaries)
        {
            host.Resources.MergedDictionaries.Add(dictionary);
        }

        var close = new Button
        {
            Content = "×",
            Width = 22,
            Height = 22,
            MinHeight = 22,
            Padding = new Thickness(0),
            Margin = new Thickness(8, 0, 0, 0),
            ToolTip = $"Close {title}",
        };
        close.Style = (Style)FindResource("HeaderChevronButton");
        close.Foreground = (System.Windows.Media.Brush)FindResource("Brush.TextMuted");
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(new TextBlock
        {
            Text = title,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
        });
        header.Children.Add(close);
        var tab = new TabItem
        {
            Header = header,
            Content = host,
            Tag = window,
            Style = (Style)FindResource("WorkspaceTabItem"),
        };
        close.Click += (_, _) => window.Close();
        window.Closed += (_, _) => Dispatcher.InvokeAsync(() =>
        {
            WorkspaceTabs.Items.Remove(tab);
            UpdateWorkspaceTabPresentation();
        });
        WorkspaceTabs.Items.Add(tab);
        WorkspaceTabs.SelectedItem = tab;
        UpdateWorkspaceTabPresentation();
    }

    private void UpdateWorkspaceTabPresentation()
    {
        var hasTabs = WorkspaceTabs.Items.Count > 0;
        WorkspaceTabs.Visibility = hasTabs ? Visibility.Visible : Visibility.Collapsed;
        WorkspaceEmptyText.Visibility = hasTabs ? Visibility.Collapsed : Visibility.Visible;
        WorkspaceTabs.Tag = WorkspaceTabs.Items.Count <= 1 ? "Single" : "Multiple";
    }

    public static bool TryOpenWorkspace(Window window, string title)
    {
        if (Application.Current.MainWindow is not MainWindow mainWindow)
        {
            return false;
        }
        mainWindow.OpenWorkspaceTab(window, title);
        return true;
    }

    public void OpenOnboarding(WelcomeWindow onboarding)
    {
        if (_onboardingWindow is not null)
        {
            OnboardingOverlay.Visibility = Visibility.Visible;
            SetUnderlyingContentEnabled(false);
            FocusFirstInteractive(OnboardingHost);
            onboarding.ReleaseFromHost();
            return;
        }
        if (onboarding.Content is not UIElement content)
        {
            return;
        }

        _onboardingWindow = onboarding;
        onboarding.Content = null;
        OnboardingHost.Resources.Clear();
        foreach (var key in onboarding.Resources.Keys)
        {
            OnboardingHost.Resources[key] = onboarding.Resources[key];
        }
        foreach (var dictionary in onboarding.Resources.MergedDictionaries)
        {
            OnboardingHost.Resources.MergedDictionaries.Add(dictionary);
        }
        OnboardingHost.DataContext = onboarding.DataContext;
        OnboardingHost.Content = content;
        _focusBeforeOnboarding ??= Keyboard.FocusedElement;
        SetUnderlyingContentEnabled(false);
        OnboardingOverlay.Visibility = Visibility.Visible;
        onboarding.DismissRequested += (_, _) => CloseOnboarding();
        FocusFirstInteractive(OnboardingHost);
    }

    public void CloseOnboarding()
    {
        CloseOnboardingTool();
        _onboardingWindow?.ReleaseFromHost();
        OnboardingHost.Content = null;
        OnboardingHost.Resources.Clear();
        OnboardingOverlay.Visibility = Visibility.Collapsed;
        _onboardingWindow = null;
        SetUnderlyingContentEnabled(true);
        var focusToRestore = _focusBeforeOnboarding;
        _focusBeforeOnboarding = null;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (focusToRestore is not null)
            {
                Keyboard.Focus(focusToRestore);
            }
            else
            {
                NavigationView.Focus();
            }
        }));
    }

    public void OpenOnboardingTool(SkinLibraryWindow tool, string title)
    {
        if (_onboardingWindow is null || tool.Content is not UIElement content)
        {
            return;
        }
        CloseOnboardingTool();
        _onboardingToolWindow = tool;
        tool.Content = null;
        OnboardingToolHost.Resources.Clear();
        foreach (var key in tool.Resources.Keys)
        {
            OnboardingToolHost.Resources[key] = tool.Resources[key];
        }
        foreach (var dictionary in tool.Resources.MergedDictionaries)
        {
            OnboardingToolHost.Resources.MergedDictionaries.Add(dictionary);
        }
        OnboardingToolHost.DataContext = tool.DataContext;
        OnboardingToolHost.Content = content;
        OnboardingToolTitle.Text = title;
        OnboardingHost.IsEnabled = false;
        OnboardingToolLayer.Visibility = Visibility.Visible;
        tool.DismissRequested += (_, _) => CloseOnboardingTool();
        FocusFirstInteractive(OnboardingToolHost);
    }

    public void CloseOnboardingTool()
    {
        OnboardingToolHost.Content = null;
        OnboardingToolHost.Resources.Clear();
        OnboardingToolLayer.Visibility = Visibility.Collapsed;
        _onboardingToolWindow = null;
        OnboardingHost.IsEnabled = true;
        if (OnboardingOverlay.Visibility == Visibility.Visible)
        {
            FocusFirstInteractive(OnboardingHost);
        }
    }

    private void SetUnderlyingContentEnabled(bool isEnabled)
    {
        NavigationView.IsEnabled = isEnabled;
        DashboardRoot.IsEnabled = isEnabled;
        PerformancePage.IsEnabled = isEnabled;
        MapsPage.IsEnabled = isEnabled;
        WorkspacePage.IsEnabled = isEnabled;
    }

    private void FocusFirstInteractive(FrameworkElement host) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (host.IsVisible && host.IsEnabled)
            {
                host.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
            }
        }));

    private void OnboardingToolBack_Click(object sender, RoutedEventArgs e) => CloseOnboardingTool();

    public static bool TryOpenOnboardingTool(SkinLibraryWindow tool, string title)
    {
        if (Application.Current.MainWindow is not MainWindow mainWindow)
        {
            return false;
        }
        mainWindow.OpenOnboardingTool(tool, title);
        return true;
    }

    public static bool TryOpenOnboarding(WelcomeWindow onboarding)
    {
        if (Application.Current.MainWindow is not MainWindow mainWindow)
        {
            return false;
        }
        mainWindow.OpenOnboarding(onboarding);
        return true;
    }

    private void InspectorBack_Click(object sender, RoutedEventArgs e)
    {
        _compactInspectorOpen = false;
        ApplyResponsiveLayout();
        HistoryList.Focus();
    }

    private void NavigationToggle_Click(object sender, RoutedEventArgs e)
    {
        if (!_layoutState.IsWide)
        {
            return;
        }

        _settings.Update(settings => settings.Appearance.NavigationExpanded = !settings.Appearance.NavigationExpanded);
        ApplyResponsiveLayout();
    }

    private async void MapCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: MapCardViewModel map } && DataContext is MainViewModel viewModel)
        {
            await viewModel.ShowAllPlaysForMapAsync(map);
            _compactInspectorOpen = false;
            ShowPage("Dashboard");
            HistoryList.Focus();
        }
    }

    private void ApplyResponsiveLayout()
    {
        if (!IsInitialized)
        {
            return;
        }

        _layoutState = ResponsiveLayoutResolver.Resolve(ActualWidth, ActualHeight);
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.IsWideHistoryLayout = ActualWidth >= 1600;
        }
        var expandedNavigation = _layoutState.IsWide && _settings.Current.Appearance.NavigationExpanded;
        NavigationColumn.Width = new GridLength(expandedNavigation ? 176 : 58);
        NavigationView.CanToggle = _layoutState.IsWide;
        NavigationView.IsExpanded = expandedNavigation;

        var pageMargin = _layoutState.IsCompact ? new Thickness(10, 10, 8, 10) : new Thickness(22, 18, 22, 18);
        PerformancePage.Margin = pageMargin;
        MapsPage.Margin = pageMargin;
        WorkspacePage.Margin = pageMargin;
        // At the 720 DIP minimum this still leaves the hosted settings view
        // more than its 420 DIP compact target, while keeping every rail label whole.
        WorkspaceNavigationColumn.Width = new GridLength(210);
        WorkspaceGapColumn.Width = new GridLength(_layoutState.IsCompact ? 8 : 10);

        var showPageBadges = ActualWidth >= 900;
        PerformanceScopeBadge.Visibility = showPageBadges ? Visibility.Visible : Visibility.Collapsed;
        MapsSortBadge.Visibility = showPageBadges ? Visibility.Visible : Visibility.Collapsed;

        MetricsGrid.Columns = _layoutState.IsCompact ? 3 : 6;
        MetricsRow.Height = _selectedPage == "Imports"
            ? new GridLength(0)
            : new GridLength(_layoutState.IsCompact ? 136 : 88);
        var compactInspector = _layoutState.IsCompact && _compactInspectorOpen;
        TopBarRow.Height = compactInspector
            ? new GridLength(0)
            : new GridLength(_layoutState.IsCompact ? 46 : _layoutState.IsShort ? 54 : 72);
        DashboardTitleText.Visibility = compactInspector ? Visibility.Collapsed : Visibility.Visible;
        DashboardSubtitleText.Visibility = _layoutState.IsCompact ? Visibility.Collapsed : Visibility.Visible;
        RefreshButton.Visibility = compactInspector ? Visibility.Collapsed : Visibility.Visible;
        TopStatusStrip.Visibility = Visibility.Collapsed;
        InspectorHeaderTitle.Visibility = _layoutState.IsCompact && _compactInspectorOpen
            ? Visibility.Collapsed
            : Visibility.Visible;

        GroupRepeatsCheckBox.Visibility = Visibility.Collapsed;
        ArtworkModeComboBox.Visibility = Visibility.Collapsed;
        ResultsTextBlock.Visibility = Visibility.Collapsed;
        ClearFiltersButton.Visibility = Visibility.Collapsed;
        SyncStatusText.Visibility = _selectedPage != "Imports" && _layoutState.IsWide
            ? Visibility.Visible
            : Visibility.Collapsed;
        TopSettingsButton.Visibility = _selectedPage == "Imports" ? Visibility.Visible : Visibility.Collapsed;

        if (ActualHeight >= 1000)
        {
            ScrollHitTimingRow.Height = new GridLength(146);
            ScrollMapPressureRow.Height = new GridLength(116);
        }
        else if (ActualHeight >= 700)
        {
            ScrollHitTimingRow.Height = new GridLength(126);
            ScrollMapPressureRow.Height = new GridLength(86);
        }
        else
        {
            ScrollHitTimingRow.Height = new GridLength(112);
            ScrollMapPressureRow.Height = new GridLength(74);
        }

        ScrollableChartsPanel.Visibility = Visibility.Visible;
        PinnedChartsPanel.Visibility = Visibility.Collapsed;

        if (_layoutState.IsCompact)
        {
            var showInspector = _compactInspectorOpen;
            HistoryPane.Visibility = showInspector ? Visibility.Collapsed : Visibility.Visible;
            InspectorPane.Visibility = showInspector ? Visibility.Visible : Visibility.Collapsed;
            MainGridSplitter.Visibility = Visibility.Collapsed;
            HistoryColumn.Width = showInspector ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            SplitterColumn.Width = new GridLength(0);
            InspectorColumn.Width = showInspector ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            InspectorBackButton.Visibility = showInspector ? Visibility.Visible : Visibility.Collapsed;
            MainSplitGrid.Margin = new Thickness(6);
        }
        else
        {
            HistoryPane.Visibility = Visibility.Visible;
            InspectorPane.Visibility = Visibility.Visible;
            MainGridSplitter.Visibility = Visibility.Visible;
            HistoryColumn.Width = new GridLength(48, GridUnitType.Star);
            SplitterColumn.Width = new GridLength(8);
            InspectorColumn.Width = new GridLength(52, GridUnitType.Star);
            InspectorBackButton.Visibility = Visibility.Collapsed;
            MainSplitGrid.Margin = new Thickness(10);
        }
    }

    /// <summary>
    /// Monitor-relative default size, centered on the work area. Restores the
    /// saved size/position only if it is still (mostly) on screen; otherwise
    /// recenters — handles monitor layout changes and offscreen windows.
    /// </summary>
    private void ApplyInitialBounds()
    {
        var work = SystemParameters.WorkArea;
        var saved = _settings.Current.Window;

        var useSavedSize = saved.Width is >= MinimumRestoredWindowWidth
            && saved.Height is >= MinimumRestoredWindowHeight
            && double.IsFinite(saved.Width.Value)
            && double.IsFinite(saved.Height.Value);
        double width = useSavedSize
            ? saved.Width!.Value
            : Math.Min(Math.Max(DefaultWindowWidth, MinWidth), work.Width);
        double height = useSavedSize && saved.Height is { } savedHeight
            ? savedHeight
            : Math.Min(Math.Max(DefaultWindowHeight, MinHeight), work.Height);
        // Do not squeeze a valid large-monitor window back to the primary
        // monitor's work area. The virtual desktop bounds preserve 4K and
        // secondary-monitor restores while still rejecting absurd sizes.
        width = Math.Min(width, useSavedSize ? SystemParameters.VirtualScreenWidth : work.Width);
        height = Math.Min(height, useSavedSize ? SystemParameters.VirtualScreenHeight : work.Height);

        double left, top;
        if (useSavedSize && saved.Left is { } l && saved.Top is { } t && IsMostlyOnScreen(l, t, width, height))
        {
            left = l;
            top = t;
        }
        else
        {
            left = work.Left + (work.Width - width) / 2;
            top = work.Top + (work.Height - height) / 2;
        }

        Width = width;
        Height = height;
        Left = left;
        Top = top;
        // Always start the main GUI as a normal window. Preserve the restored
        // bounds below, but do not let a previous maximized session turn every
        // subsequent launch into a full-screen dashboard.
        WindowState = WindowState.Normal;
    }

    private static bool IsMostlyOnScreen(double left, double top, double width, double height)
    {
        // VirtualScreen covers all monitors; require the title bar region visible.
        var vsLeft = SystemParameters.VirtualScreenLeft;
        var vsTop = SystemParameters.VirtualScreenTop;
        var vsRight = vsLeft + SystemParameters.VirtualScreenWidth;
        var vsBottom = vsTop + SystemParameters.VirtualScreenHeight;
        return left + width - 100 > vsLeft
            && left + 100 < vsRight
            && top + 40 > vsTop
            && top + 40 < vsBottom;
    }

    private long? CurrentSelectedAttemptId() => DashboardRoot.DataContext switch
    {
        MainViewModel main => main.SelectedAttempt?.Id,
        ImportsViewModel imports => imports.SelectedAttempt?.Id,
        _ => null,
    };

    private async Task TryUiActionAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            KumoriDialog.Show(this, ex.Message, "Kumori", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double value = Math.Max(0, bytes);
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.#} {units[unit]}";
    }

    private void SaveBounds()
    {
        var maximized = WindowState == WindowState.Maximized;
        var bounds = maximized ? RestoreBounds : new Rect(Left, Top, Width, Height);
        _settings.Update(s =>
        {
            s.Window.Left = bounds.Left;
            s.Window.Top = bounds.Top;
            s.Window.Width = bounds.Width;
            s.Window.Height = bounds.Height;
            // Maximising is an action for the current session, not a startup
            // preference. Keeping this false also clears older settings files
            // which persisted a maximised launch state.
            s.Window.Maximized = false;
        });
    }
}
