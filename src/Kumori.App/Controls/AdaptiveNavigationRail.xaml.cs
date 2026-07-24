using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;

namespace Kumori.App.Controls;

public partial class AdaptiveNavigationRail : UserControl
{
    public static readonly DependencyProperty IsExpandedProperty = DependencyProperty.Register(
        nameof(IsExpanded), typeof(bool), typeof(AdaptiveNavigationRail), new PropertyMetadata(false));

    public static readonly DependencyProperty CanToggleProperty = DependencyProperty.Register(
        nameof(CanToggle), typeof(bool), typeof(AdaptiveNavigationRail), new PropertyMetadata(false));

    public static readonly DependencyProperty SelectedPageProperty = DependencyProperty.Register(
        nameof(SelectedPage), typeof(string), typeof(AdaptiveNavigationRail),
        new PropertyMetadata("Dashboard", (dependencyObject, _) => ((AdaptiveNavigationRail)dependencyObject).UpdateSelection()));

    public AdaptiveNavigationRail()
    {
        InitializeComponent();
        UpdateSelection();
    }

    public bool IsExpanded
    {
        get => (bool)GetValue(IsExpandedProperty);
        set => SetValue(IsExpandedProperty, value);
    }

    public bool CanToggle
    {
        get => (bool)GetValue(CanToggleProperty);
        set => SetValue(CanToggleProperty, value);
    }

    public string SelectedPage
    {
        get => (string)GetValue(SelectedPageProperty);
        set => SetValue(SelectedPageProperty, value);
    }

    public event RoutedEventHandler? DashboardRequested;
    public event RoutedEventHandler? PerformanceRequested;
    public event RoutedEventHandler? MapsRequested;
    public event RoutedEventHandler? ImportsRequested;
    public event RoutedEventHandler? SkinEditorRequested;
    public event RoutedEventHandler? ChangelogRequested;
    public event RoutedEventHandler? DiscordRequested;
    public event RoutedEventHandler? SettingsRequested;
    public event RoutedEventHandler? ToggleRequested;

    private void Dashboard_Click(object sender, RoutedEventArgs e) => DashboardRequested?.Invoke(this, e);
    private void Performance_Click(object sender, RoutedEventArgs e) => PerformanceRequested?.Invoke(this, e);
    private void Maps_Click(object sender, RoutedEventArgs e) => MapsRequested?.Invoke(this, e);
    private void Imports_Click(object sender, RoutedEventArgs e) => ImportsRequested?.Invoke(this, e);
    private void SkinEditor_Click(object sender, RoutedEventArgs e) => SkinEditorRequested?.Invoke(this, e);
    private void Changelog_Click(object sender, RoutedEventArgs e) => ChangelogRequested?.Invoke(this, e);
    private void Discord_Click(object sender, RoutedEventArgs e) => DiscordRequested?.Invoke(this, e);
    private void Settings_Click(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke(this, e);
    private void Toggle_Click(object sender, RoutedEventArgs e) => ToggleRequested?.Invoke(this, e);

    private void UpdateSelection()
    {
        if (!IsInitialized && DashboardButton is null) return;
        var normal = (Style)Resources["NavigationButton"];
        var active = (Style)Resources["ActiveNavigationButton"];
        DashboardButton.Style = SelectedPage == "Dashboard" ? active : normal;
        PerformanceButton.Style = SelectedPage == "Performance" ? active : normal;
        MapsButton.Style = SelectedPage == "Maps" ? active : normal;
        ImportsButton.Style = SelectedPage == "Imports" ? active : normal;
        SkinEditorButton.Style = SelectedPage == "SkinEditor" ? active : normal;
        SettingsButton.Style = SelectedPage == "Settings" ? active : normal;
        SetCurrentPageStatus(DashboardButton, "Dashboard");
        SetCurrentPageStatus(PerformanceButton, "Performance");
        SetCurrentPageStatus(MapsButton, "Maps");
        SetCurrentPageStatus(ImportsButton, "Imports");
        SetCurrentPageStatus(SkinEditorButton, "SkinEditor");
        SetCurrentPageStatus(SettingsButton, "Settings");
    }

    private void SetCurrentPageStatus(Button button, string page) =>
        AutomationProperties.SetItemStatus(
            button,
            SelectedPage == page ? "Current page" : string.Empty);
}
