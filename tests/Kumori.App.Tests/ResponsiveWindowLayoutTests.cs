using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Kumori.App.Controls;
using Kumori.App.ViewModels;
using Kumori.Core.Settings;
using Kumori.Core.State;
using Kumori.Core.Models;
using Kumori.Storage;
using Xunit;

namespace Kumori.App.Tests;

public sealed class ResponsiveWindowLayoutTests
{
    [Fact]
    public void MainWindowMeasuresAcrossSupportedSizeMatrix()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var app = new App();
                app.InitializeComponent();
                var directory = Directory.CreateTempSubdirectory();
                try
                {
                    var database = Path.Combine(directory.FullName, "layout.sqlite3");
                    var settings = new SettingsService(
                        Path.Combine(directory.FullName, "settings.v2.json"),
                        Path.Combine(directory.FullName, "legacy.json"));
                    settings.Load();
                    var factory = new SqliteConnectionFactory(database, readOnly: false);
                    var vm = new MainViewModel(
                        new AppStateStore(),
                        new AttemptRepository(factory),
                        new AttemptDetailsRepository(factory),
                        new AnalyticsRepository(factory),
                        settings,
                        maintenance: new TrackingMaintenanceRepository(factory),
                        sessions: new SessionRepository(factory));
                    vm.PerformanceDays.Add(new PerformanceDayViewModel(new DailyAttemptTrend
                    {
                        Day = "2026-07-12",
                        Attempts = 10,
                        Completed = 8,
                        AverageAccuracy = 96.25,
                        BestPp = 180.5,
                    }));
                    var window = new MainWindow(vm, settings);
                    window.ShowInTaskbar = false;
                    window.WindowStartupLocation = WindowStartupLocation.Manual;
                    window.Left = -20000;
                    window.Top = -20000;
                    window.Show();

                    (double Width, double Height)[] sizes =
                    [
                        (720, 480), (800, 600), (900, 600), (1280, 720),
                        (1920, 1080), (2560, 1440), (3840, 2160),
                    ];
                    foreach (var size in sizes)
                    {
                        window.Width = size.Width;
                        window.Height = size.Height;
                        window.UpdateLayout();

                        var root = Assert.IsType<Grid>(window.FindName("AppShell"));
                        // A shown WPF window cannot exceed Windows' maximum
                        // tracking dimensions. Keep requesting the full matrix,
                        // but assert against the largest size this machine can
                        // actually realize (which may extend behind the taskbar).
                        var attainableWidth = Math.Min(size.Width, SystemParameters.MaximumWindowTrackWidth);
                        var attainableHeight = Math.Min(size.Height, SystemParameters.MaximumWindowTrackHeight);
                        Assert.InRange(root.ActualWidth, attainableWidth - 32, attainableWidth);
                        Assert.InRange(root.ActualHeight, attainableHeight - 64, attainableHeight);
                        Assert.True(root.DesiredSize.Width <= root.ActualWidth + 0.5);
                        Assert.True(root.DesiredSize.Height <= root.ActualHeight + 0.5);

                        var navigation = Assert.IsType<AdaptiveNavigationRail>(window.FindName("NavigationView"));
                        var dashboardLabel = Descendants<TextBlock>(navigation).Single(text => text.Text == "Dashboard");
                        var navigationButtons = Descendants<Button>(navigation).ToArray();
                        Assert.NotEmpty(navigationButtons);
                        Assert.False(navigation.IsExpanded);
                        Assert.Equal(Visibility.Collapsed, dashboardLabel.Visibility);
                        Assert.InRange(navigation.ActualWidth, 56, 60);
                        Assert.All(navigationButtons.Where(button => button.Visibility == Visibility.Visible), button => Assert.True(button.ActualWidth >= 36));

                        foreach (var pageName in new[] { "Performance", "Maps" })
                        {
                            var navigationButton = Assert.IsType<Button>(navigation.FindName($"{pageName}Button"));
                            navigationButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            window.UpdateLayout();
                            var page = Assert.IsType<Grid>(window.FindName($"{pageName}Page"));
                            Assert.Equal(Visibility.Visible, page.Visibility);
                            Assert.True(page.DesiredSize.Width <= page.ActualWidth + 0.5);
                            Assert.True(page.DesiredSize.Height <= page.ActualHeight + 0.5);
                        }
                        var settingsButton = Assert.IsType<Button>(navigation.FindName("SettingsButton"));
                        settingsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        window.UpdateLayout();
                        var workspace = Assert.IsType<Grid>(window.FindName("WorkspacePage"));
                        Assert.Equal(Visibility.Visible, workspace.Visibility);
                        var workspaceTabs = Assert.IsType<TabControl>(window.FindName("WorkspaceTabs"));
                        Assert.NotEmpty(workspaceTabs.Items);
                        Assert.IsType<Button>(navigation.FindName("DashboardButton"))
                            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        window.UpdateLayout();

                        if (size.Width == 1920 && Environment.GetEnvironmentVariable("KUMORI_UI_SNAPSHOT") is { Length: > 0 } snapshot)
                        {
                            var bitmap = new RenderTargetBitmap(
                                (int)Math.Ceiling(root.ActualWidth),
                                (int)Math.Ceiling(root.ActualHeight),
                                96, 96, PixelFormats.Pbgra32);
                            bitmap.Render(root);
                            var encoder = new PngBitmapEncoder();
                            encoder.Frames.Add(BitmapFrame.Create(bitmap));
                            using var stream = File.Create(snapshot);
                            encoder.Save(stream);
                        }
                    }

                    settings.Update(s =>
                    {
                        s.FirstRunCompleted = false;
                        s.OnboardingProgressStep = 0;
                        s.Capture.LazerReplayFrameEnabled = false;
                    });
                    var onboarding = new WelcomeWindow(settings, new AppStateStore());
                    window.OpenOnboarding(onboarding);
                    window.UpdateLayout();
                    Assert.Equal(Visibility.Visible, Assert.IsType<Grid>(window.FindName("OnboardingOverlay")).Visibility);
                    window.OpenOnboardingTool(new SkinLibraryWindow(settings), "Skin library");
                    window.UpdateLayout();
                    Assert.Equal(Visibility.Visible, Assert.IsType<Border>(window.FindName("OnboardingToolLayer")).Visibility);
                    window.CloseOnboardingTool();
                    Assert.Equal("STEP 1 OF 6", Assert.IsType<TextBlock>(onboarding.FindName("StepCounter")).Text);
                    Assert.False(Assert.IsType<CheckBox>(onboarding.FindName("CaptureEnabled")).IsChecked);
                    Assert.IsType<Button>(onboarding.FindName("NextButton"))
                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Equal(1, settings.Current.OnboardingProgressStep);
                    window.CloseOnboarding();
                    Assert.Equal(Visibility.Collapsed, Assert.IsType<Grid>(window.FindName("OnboardingOverlay")).Visibility);
                    var resumedOnboarding = new WelcomeWindow(settings, new AppStateStore());
                    window.OpenOnboarding(resumedOnboarding);
                    Assert.Equal("STEP 2 OF 6", Assert.IsType<TextBlock>(resumedOnboarding.FindName("StepCounter")).Text);
                    Assert.False(Assert.IsType<CheckBox>(resumedOnboarding.FindName("CaptureEnabled")).IsChecked);
                    window.CloseOnboarding();

                    window.ForceClose = true;
                    window.Close();
                }
                finally
                {
                    directory.Delete(recursive: true);
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "WPF layout smoke test timed out.");
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
}
