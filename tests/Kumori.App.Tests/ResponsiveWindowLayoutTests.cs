using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Polygon = System.Windows.Shapes.Polygon;
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
                    var stressAttempt = new AttemptRowViewModel(new AttemptSummary
                    {
                        Id = 1,
                        Artist = "A deliberately long artist name",
                        Title = "A deliberately long beatmap title used to exercise compact history layout",
                        Difficulty = "Extraordinarily Long Difficulty Name",
                        Mapper = "A mapper with a long display name",
                        StartedAt = DateTimeOffset.Now.AddHours(-10).ToString("O"),
                        Outcome = "abandoned",
                        Progress = 0.42,
                        Accuracy = 100,
                        Combo = 9_999,
                        BeatmapMaxCombo = 9_999,
                        Pp = 9_876.5,
                        Grade = "S",
                        ModsKey = "HDDTHRFLDASDNFEZNCPFRXAPSOHT",
                        Mods =
                        [
                            new ModEntry("HD", "{}"), new ModEntry("DT", "{}"), new ModEntry("HR", "{}"),
                            new ModEntry("FL", "{}"),
                            new ModEntry("DA", "{\"approach_rate\":10,\"circle_size\":6,\"overall_difficulty\":10,\"drain_rate\":0}"),
                            new ModEntry("SD", "{}"), new ModEntry("NF", "{}"), new ModEntry("EZ", "{}"),
                            new ModEntry("NC", "{}"), new ModEntry("PF", "{}"), new ModEntry("RX", "{}"),
                            new ModEntry("AP", "{}"), new ModEntry("SO", "{}"), new ModEntry("HT", "{}"),
                        ],
                    });
                    vm.Rows.Add(stressAttempt);
                    var noModAttempt = new AttemptRowViewModel(new AttemptSummary
                    {
                        Id = 2,
                        Artist = "No-mod artist",
                        Title = "No-mod alignment fixture",
                        Difficulty = "Normal",
                        Mapper = "Layout tester",
                        StartedAt = DateTimeOffset.Now.AddHours(-9).ToString("O"),
                        Outcome = "completed",
                        Progress = 1,
                        Accuracy = 98.5,
                        Combo = 500,
                        BeatmapMaxCombo = 600,
                        Pp = 120,
                        Grade = "A",
                        ModsKey = "NM",
                    });
                    vm.Rows.Add(noModAttempt);
                    vm.Inspector.Details = new AttemptDetails
                    {
                        Summary = stressAttempt.Model,
                        Mods = stressAttempt.ModEntries,
                    };
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

                        var metrics = Assert.IsType<UniformGrid>(window.FindName("MetricsGrid"));
                        foreach (var text in Descendants<TextBlock>(metrics).Where(text => text.IsVisible))
                        {
                            var cell = Ancestors<Border>(text)
                                .First(border => ReferenceEquals(VisualTreeHelper.GetParent(border), metrics));
                            var bounds = text.TransformToAncestor(cell)
                                .TransformBounds(new Rect(new Point(), text.RenderSize));
                            Assert.True(bounds.Top >= -0.5 && bounds.Bottom <= cell.ActualHeight + 0.5,
                                $"Metric text '{text.Text}' is vertically clipped at {size.Width}x{size.Height}: bounds {bounds}, cell height {cell.ActualHeight}.");
                        }

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

                        var history = Assert.IsType<ListBox>(window.FindName("HistoryList"));
                        history.ScrollIntoView(noModAttempt);
                        window.UpdateLayout();
                        var noModTitle = Descendants<TextBlock>(history)
                            .Single(text => text.IsVisible
                                            && ReferenceEquals(text.DataContext, noModAttempt)
                                            && text.Text == noModAttempt.Title);
                        var noModRow = Ancestors<ListBoxItem>(noModTitle).First();
                        var noModTitleTop = noModTitle.TransformToAncestor(noModRow).Transform(new Point()).Y;

                        history.ScrollIntoView(stressAttempt);
                        window.UpdateLayout();
                        var importantCardText = Descendants<TextBlock>(history)
                            .Where(text => text.IsVisible && ReferenceEquals(text.DataContext, stressAttempt))
                            .Where(text => text.Text == stressAttempt.AccuracyText
                                           || text.Text == stressAttempt.Title
                                           || text.Text == stressAttempt.Artist
                                           || text.Text == stressAttempt.DifficultyLine
                                           || text.Text == stressAttempt.PerformanceLine
                                           || text.Text == stressAttempt.OutcomeWithProgress
                                           || text.Text == stressAttempt.WhenRelative)
                            .Distinct()
                            .ToArray();
                        Assert.Equal(vm.IsWideHistoryLayout ? 8 : 7, importantCardText.Length);
                        Assert.All(importantCardText, text =>
                        {
                            var desiredTextWidth = Math.Max(0, text.DesiredSize.Width - text.Margin.Left - text.Margin.Right);
                            var desiredTextHeight = Math.Max(0, text.DesiredSize.Height - text.Margin.Top - text.Margin.Bottom);
                            Assert.True(desiredTextWidth <= text.ActualWidth + 0.5,
                                $"'{text.Text}' is horizontally clipped at {size.Width}x{size.Height}: desired text {desiredTextWidth}, actual {text.ActualWidth}.");
                            Assert.True(desiredTextHeight <= text.ActualHeight + 0.5,
                                $"'{text.Text}' is vertically clipped at {size.Width}x{size.Height}: desired text {desiredTextHeight}, actual {text.ActualHeight}.");
                        });

                        var modIcons = Descendants<Polygon>(history)
                            .Where(icon => icon.IsVisible
                                           && icon.DataContext is ModEntry mod
                                           && stressAttempt.ModEntries.Contains(mod))
                            .ToArray();
                        Assert.Equal(stressAttempt.ModEntries.Count, modIcons.Length);
                        Assert.All(modIcons, icon => Assert.NotNull(icon.Fill));

                        var modIconHosts = Descendants<Grid>(history)
                            .Where(host => host.IsVisible
                                           && host.DataContext is ModEntry mod
                                           && stressAttempt.ModEntries.Contains(mod)
                                           && host.ToolTip is string)
                            .ToArray();
                        Assert.Equal(stressAttempt.ModEntries.Count, modIconHosts.Length);
                        Assert.All(modIconHosts, host =>
                        {
                            Assert.Equal(0, ToolTipService.GetInitialShowDelay(host));
                            Assert.Equal(0, ToolTipService.GetBetweenShowDelay(host));
                            Assert.Equal(PlacementMode.Top, ToolTipService.GetPlacement(host));
                            Assert.False(string.IsNullOrWhiteSpace(Assert.IsType<string>(host.ToolTip)));
                        });
                        var historyDaToolTip = Assert.IsType<string>(
                            modIconHosts.Single(host => ((ModEntry)host.DataContext).Acronym == "DA").ToolTip);
                        Assert.Contains("AR: 10", historyDaToolTip);
                        Assert.Contains("CS: 6", historyDaToolTip);

                        if (size.Width > ResponsiveLayoutResolver.CompactMaximumWidth)
                        {
                            var detailDaHost = Descendants<Grid>(root)
                                .Single(host => host.IsVisible
                                                && host.Width == 34
                                                && host.DataContext is ModEntry { Acronym: "DA" });
                            Assert.Equal(0, ToolTipService.GetInitialShowDelay(detailDaHost));
                            Assert.Equal(PlacementMode.Top, ToolTipService.GetPlacement(detailDaHost));
                            Assert.Contains("OD: 10", Assert.IsType<string>(detailDaHost.ToolTip));
                            Assert.Contains("HP: 0", Assert.IsType<string>(detailDaHost.ToolTip));
                        }

                        var moddedTitle = importantCardText.Single(text => text.Text == stressAttempt.Title);
                        var moddedRow = Ancestors<ListBoxItem>(moddedTitle).First();
                        var moddedTitleTop = moddedTitle.TransformToAncestor(moddedRow).Transform(new Point()).Y;
                        Assert.InRange(Math.Abs(noModTitleTop - moddedTitleTop), 0, 0.5);

                        if (size.Width == 720
                            && Environment.GetEnvironmentVariable("KUMORI_UI_SCALE_SNAPSHOT_DIR") is { Length: > 0 } scaleSnapshotDirectory)
                        {
                            Directory.CreateDirectory(scaleSnapshotDirectory);
                            foreach (var scale in new[] { 1d, 1.25d, 1.5d })
                            {
                                SaveSnapshot(
                                    root,
                                    Path.Combine(scaleSnapshotDirectory, $"history-720x480-{scale * 100:0}.png"),
                                    scale);
                            }
                        }

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
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "WPF layout smoke test timed out.");
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

    private static IEnumerable<T> Ancestors<T>(DependencyObject child) where T : DependencyObject
    {
        for (var current = VisualTreeHelper.GetParent(child); current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is T match)
                yield return match;
        }
    }

    private static void SaveSnapshot(FrameworkElement root, string path, double scale)
    {
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(root.ActualWidth * scale),
            (int)Math.Ceiling(root.ActualHeight * scale),
            96 * scale,
            96 * scale,
            PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
