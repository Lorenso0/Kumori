using System.Runtime.ExceptionServices;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Polygon = System.Windows.Shapes.Polygon;
using Kumori.App.Controls;
using Kumori.App.Skins;
using Kumori.App.ViewModels;
using Kumori.Core.Settings;
using Kumori.Core.State;
using Kumori.Core.Models;
using Kumori.Storage;
using Xunit;

namespace Kumori.App.Tests;

public sealed class ResponsiveWindowLayoutTests
{
    // WPF rounds window and text geometry to physical pixels. Depending on the
    // active monitor DPI, the corresponding DIP values can differ by slightly
    // more than one unit without any visual displacement or clipping.
    private const double LayoutTolerance = 2;

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
                    var studioLauncher = new SkinStudioLauncherPage(settings, () => Task.CompletedTask);
                    Assert.IsType<SkinStudioEmbeddedHost>(studioLauncher.FindName("StudioHost"));
                    Assert.IsType<Button>(studioLauncher.FindName("RetryButton"));
                    Assert.IsType<Border>(studioLauncher.FindName("QuickColourSwatch"));
                    Assert.True(Assert.IsType<WrapPanel>(
                        studioLauncher.FindName("QuickColourSwatchPanel")).Children.Count >= 8);
                    Assert.IsType<Popup>(studioLauncher.FindName("SkinStudioColorPickerPopup"));
                    Assert.Equal(Visibility.Visible,
                        Assert.IsType<Border>(studioLauncher.FindName("WelcomePanel")).Visibility);
                    Assert.Equal(Visibility.Collapsed,
                        Assert.IsType<Border>(studioLauncher.FindName("ExtrasWorkspace")).Visibility);
                    Assert.IsType<Border>(studioLauncher.FindName("MainRendererMount"));
                    var studioWindow = new Window
                    {
                        Content = studioLauncher,
                        Width = 800,
                        Height = 650,
                        ShowInTaskbar = false,
                        WindowStartupLocation = WindowStartupLocation.Manual,
                        Left = -20000,
                        Top = -20000,
                    };
                    studioWindow.Show();
                    studioWindow.UpdateLayout();
                    var overflowMenuButton = Assert.IsType<Button>(
                        studioLauncher.FindName("OverflowMenuButton"));
                    overflowMenuButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.True(overflowMenuButton.ContextMenu?.IsOpen);
                    overflowMenuButton.ContextMenu!.IsOpen = false;
                    Assert.Equal(Visibility.Visible,
                        Assert.IsType<Border>(studioLauncher.FindName("CompactNavigation")).Visibility);
                    Assert.Equal(Visibility.Visible,
                        Assert.IsType<Border>(studioLauncher.FindName("CanvasPane")).Visibility);
                    Assert.Equal(Visibility.Collapsed,
                        Assert.IsType<Border>(studioLauncher.FindName("NavigatorPane")).Visibility);
                    var browseButton = Assert.IsType<Button>(studioLauncher.FindName("CompactBrowseButton"));
                    browseButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    studioWindow.UpdateLayout();
                    Assert.Equal(Visibility.Visible,
                        Assert.IsType<Border>(studioLauncher.FindName("NavigatorPane")).Visibility);
                    Assert.Equal(Visibility.Collapsed,
                        Assert.IsType<Border>(studioLauncher.FindName("CanvasPane")).Visibility);
                    studioWindow.Width = 1200;
                    studioWindow.UpdateLayout();
                    Assert.Equal(Visibility.Collapsed,
                        Assert.IsType<Border>(studioLauncher.FindName("CompactNavigation")).Visibility);
                    Assert.Equal(Visibility.Visible,
                        Assert.IsType<Border>(studioLauncher.FindName("InspectorPane")).Visibility);
                    studioWindow.Content = null;
                    studioWindow.Close();
                    studioLauncher.Dispose();
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
                    var stressMap = new MapCardViewModel("layout-map", new[]
                    {
                        new AttemptSummary
                        {
                            Id = 2,
                            Artist = "A deliberately long beatmap artist name",
                            Title = "A deliberately long beatmap title used to exercise the compact maps layout",
                            Difficulty = "Extraordinarily Long Difficulty Name",
                            Mapper = "Layout tester",
                            StartedAt = DateTimeOffset.Now.AddHours(-4).ToString("O"),
                            Outcome = "completed",
                            Accuracy = 98.75,
                            Combo = 1_234,
                            BeatmapMaxCombo = 2_000,
                            Pp = 321.5,
                        },
                        new AttemptSummary
                        {
                            Id = 1,
                            Artist = "A deliberately long beatmap artist name",
                            Title = "A deliberately long beatmap title used to exercise the compact maps layout",
                            Difficulty = "Extraordinarily Long Difficulty Name",
                            Mapper = "Layout tester",
                            StartedAt = DateTimeOffset.Now.AddDays(-1).ToString("O"),
                            Outcome = "failed",
                            Accuracy = 84.25,
                            Combo = 456,
                            BeatmapMaxCombo = 2_000,
                            Pp = 120,
                        },
                    });
                    vm.MapCards.Add(stressMap);
                    foreach (var index in Enumerable.Range(1, 200))
                    {
                        vm.MapCards.Add(new MapCardViewModel($"layout-map-{index}", new[]
                        {
                            new AttemptSummary
                            {
                                Id = index + 2,
                                Artist = $"Layout artist {index}",
                                Title = $"Compact map row {index}",
                                Difficulty = index % 2 == 0 ? "Insane" : "Expert",
                                Mapper = "Layout tester",
                                StartedAt = DateTimeOffset.Now.AddDays(-index).ToString("O"),
                                Outcome = index % 3 == 0 ? "failed" : "completed",
                                Accuracy = 99 - index,
                                Combo = 1_000 - index * 50,
                                BeatmapMaxCombo = 1_200,
                                Pp = 300 - index * 12,
                            },
                        }));
                    }
                    Assert.Equal(101, vm.MapRows.Count);
                    Assert.Equal(2, vm.MapRows[0].Cards.Count);
                    Assert.Single(vm.MapRows[^1].Cards);
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
                    var stressDetails = new AttemptDetails
                    {
                        Summary = stressAttempt.Model,
                        Mods = stressAttempt.ModEntries,
                        UnstableRate = 206.1,
                        Timing = new TimingSummary
                        {
                            HitCount = 24,
                            EarlyCount = 18,
                            LateCount = 6,
                            Mean = -13.5,
                            Deviation = 20.6,
                            Offsets =
                            [
                                -14, -17, -20, 4, -26, -15, -23, -18,
                                8, -21, -24, -13, -29, 6, -19, -22,
                                -16, 12, -25, -17, 5, -27, -22, 9,
                            ],
                        },
                    };
                    var noModDetails = new AttemptDetails
                    {
                        Summary = noModAttempt.Model,
                    };
                    vm.Inspector.Details = stressDetails;
                    var window = new MainWindow(vm, settings);
                    window.ShowInTaskbar = false;
                    window.WindowStartupLocation = WindowStartupLocation.Manual;
                    window.Left = -20000;
                    window.Top = -20000;
                    window.Show();

                    (double Width, double Height)[] sizes =
                    [
                        (720, 480), (800, 600), (900, 600), (1023, 650), (1024, 680),
                        (1100, 720), (1280, 720), (1386, 947),
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
                        Assert.InRange(root.ActualWidth, attainableWidth - 32, attainableWidth + LayoutTolerance);
                        Assert.InRange(root.ActualHeight, attainableHeight - 64, attainableHeight + LayoutTolerance);
                        Assert.True(root.DesiredSize.Width <= root.ActualWidth + LayoutTolerance);
                        Assert.True(root.DesiredSize.Height <= root.ActualHeight + LayoutTolerance);

                        var metrics = Assert.IsType<UniformGrid>(window.FindName("MetricsGrid"));
                        foreach (var text in Descendants<TextBlock>(metrics).Where(text => text.IsVisible))
                        {
                            var cell = Ancestors<Border>(text)
                                .First(border => ReferenceEquals(VisualTreeHelper.GetParent(border), metrics));
                            var bounds = text.TransformToAncestor(cell)
                                .TransformBounds(new Rect(new Point(), text.RenderSize));
                            Assert.True(bounds.Top >= -LayoutTolerance && bounds.Bottom <= cell.ActualHeight + LayoutTolerance,
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
                        var changelogButton = Assert.IsType<Button>(navigation.FindName("ChangelogButton"));
                        var discordButton = Assert.IsType<Button>(navigation.FindName("DiscordButton"));
                        Assert.Equal(Visibility.Visible, changelogButton.Visibility);
                        Assert.Equal(Visibility.Visible, discordButton.Visibility);
                        Assert.True(changelogButton.ActualHeight >= 44);
                        Assert.True(discordButton.ActualHeight >= 44);
                        Assert.Equal("Changelog", AutomationProperties.GetName(changelogButton));
                        Assert.Equal("Join the Kumori Discord", AutomationProperties.GetName(discordButton));
                        Assert.Equal("Opens an external website", AutomationProperties.GetHelpText(discordButton));
                        Assert.All(navigationButtons.Where(button => button.IsVisible), button =>
                        {
                            var bounds = button.TransformToAncestor(navigation)
                                .TransformBounds(new Rect(new Point(), button.RenderSize));
                            Assert.True(bounds.Top >= -LayoutTolerance && bounds.Bottom <= navigation.ActualHeight + LayoutTolerance,
                                $"Navigation action '{AutomationProperties.GetName(button)}' is clipped at {size.Width}x{size.Height}: bounds {bounds}, rail height {navigation.ActualHeight}.");
                        });

                        foreach (var pageName in new[] { "Performance", "Maps" })
                        {
                            var navigationButton = Assert.IsType<Button>(navigation.FindName($"{pageName}Button"));
                            navigationButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            window.UpdateLayout();
                            var page = Assert.IsType<Grid>(window.FindName($"{pageName}Page"));
                            Assert.Equal(Visibility.Visible, page.Visibility);
                            var allowedWidth = page.ActualWidth + LayoutTolerance;
                            if (pageName == "Maps")
                            {
                                // A populated virtualized list contributes the page's own horizontal
                                // margin to DesiredSize even though its rendered viewport stays bounded.
                                allowedWidth += page.Margin.Left + page.Margin.Right;
                            }
                            Assert.True(page.DesiredSize.Width <= allowedWidth,
                                $"{pageName} page overflows at {size.Width}x{size.Height}: desired {page.DesiredSize}, actual {page.RenderSize}.");
                            if (pageName == "Maps")
                            {
                                var mapsList = Descendants<ListBox>(page).Single();
                                Assert.True(mapsList.ActualHeight <= page.ActualHeight + LayoutTolerance);
                                Assert.True(VirtualizingPanel.GetIsVirtualizing(mapsList));
                                Assert.Equal(VirtualizationMode.Recycling, VirtualizingPanel.GetVirtualizationMode(mapsList));
                                Assert.True(ScrollViewer.GetCanContentScroll(mapsList));
                                var realizedMapCards = Descendants<Button>(mapsList)
                                    .Count(button => button.DataContext is MapCardViewModel);
                                Assert.InRange(realizedMapCards, 1, vm.MapCards.Count - 1);
                            }
                            else
                            {
                                Assert.True(page.DesiredSize.Height <= page.ActualHeight + LayoutTolerance,
                                    $"{pageName} page is vertically clipped at {size.Width}x{size.Height}: desired {page.DesiredSize}, actual {page.RenderSize}.");
                            }
                            if (pageName == "Maps")
                            {
                                var mapRow = Descendants<Button>(page)
                                    .Single(button => ReferenceEquals(button.DataContext, stressMap));
                                Assert.InRange(mapRow.ActualHeight, 158, 162);
                                Assert.True(mapRow.DesiredSize.Width <= page.ActualWidth + LayoutTolerance);
                                var mapsPanels = Descendants<UniformGrid>(page)
                                    .Where(grid => grid.Columns == 2)
                                    .ToArray();
                                Assert.NotEmpty(mapsPanels);
                                Assert.All(mapsPanels, panel => Assert.Equal(2, panel.Columns));
                            }
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
                        Assert.Equal(7, importantCardText.Length);
                        var visibleOutcome = Assert.Single(importantCardText
                            .Where(text => text.Text == stressAttempt.OutcomeWithProgress));
                        if (vm.IsWideHistoryLayout)
                        {
                            var stressRow = Ancestors<ListBoxItem>(visibleOutcome).First();
                            var expandedStats = Descendants<UniformGrid>(stressRow)
                                .Single(grid => grid.IsVisible && grid.Columns == 3);
                            Assert.DoesNotContain(
                                Descendants<TextBlock>(expandedStats),
                                text => text.Text == stressAttempt.OutcomeWithProgress);
                        }
                        Assert.All(importantCardText, text =>
                        {
                            var desiredTextWidth = Math.Max(0, text.DesiredSize.Width - text.Margin.Left - text.Margin.Right);
                            var desiredTextHeight = Math.Max(0, text.DesiredSize.Height - text.Margin.Top - text.Margin.Bottom);
                            Assert.True(desiredTextWidth <= text.ActualWidth + LayoutTolerance,
                                $"'{text.Text}' is horizontally clipped at {size.Width}x{size.Height}: desired text {desiredTextWidth}, actual {text.ActualWidth}.");
                            Assert.True(desiredTextHeight <= text.ActualHeight + LayoutTolerance,
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
                        Assert.True(moddedRow.ActualWidth <= history.ActualWidth + LayoutTolerance,
                            $"A history row overflows its viewport at {size.Width}x{size.Height}: row {moddedRow.ActualWidth}, list {history.ActualWidth}.");
                        var moddedTitleTop = moddedTitle.TransformToAncestor(moddedRow).Transform(new Point()).Y;
                        var titleOffset = Math.Abs(noModTitleTop - moddedTitleTop);
                        Assert.True(titleOffset <= LayoutTolerance,
                            $"No-mod and modded titles differ vertically by {titleOffset:0.###} DIP at {size.Width}x{size.Height}.");

                        var selectedPlayMods = Assert.IsType<ItemsControl>(window.FindName("SelectedPlayMods"));
                        var noModsText = Assert.IsType<TextBlock>(window.FindName("NoModsText"));
                        Assert.Equal(Visibility.Visible, selectedPlayMods.Visibility);
                        Assert.Equal(Visibility.Collapsed, noModsText.Visibility);

                        vm.Inspector.Details = noModDetails;
                        window.UpdateLayout();
                        Assert.Equal(Visibility.Collapsed, selectedPlayMods.Visibility);
                        Assert.Equal(Visibility.Visible, noModsText.Visibility);
                        Assert.Equal("No active modifications", AutomationProperties.GetName(noModsText));
                        if (size.Width > ResponsiveLayoutResolver.CompactMaximumWidth)
                        {
                            Assert.True(noModsText.IsVisible);
                            var desiredNoModsWidth = Math.Max(
                                0,
                                noModsText.DesiredSize.Width - noModsText.Margin.Left - noModsText.Margin.Right);
                            var desiredNoModsHeight = Math.Max(
                                0,
                                noModsText.DesiredSize.Height - noModsText.Margin.Top - noModsText.Margin.Bottom);
                            Assert.True(desiredNoModsWidth <= noModsText.ActualWidth + LayoutTolerance);
                            Assert.True(desiredNoModsHeight <= noModsText.ActualHeight + LayoutTolerance);
                        }

                        if (Environment.GetEnvironmentVariable("KUMORI_UI_AUDIT_SNAPSHOT_DIR") is { Length: > 0 } auditSnapshotDirectory
                            && size.Width is 720 or 1023 or 1024 or 1386)
                        {
                            Directory.CreateDirectory(auditSnapshotDirectory);
                            Descendants<ScrollViewer>(history).First().ScrollToTop();
                            window.UpdateLayout();
                            var state = size.Width <= ResponsiveLayoutResolver.CompactMaximumWidth
                                ? "sidebar"
                                : "no-mod";
                            SaveSnapshot(
                                root,
                                Path.Combine(auditSnapshotDirectory, $"{state}-{size.Width:0}x{size.Height:0}.png"),
                                1);
                        }

                        vm.Inspector.Details = stressDetails;
                        window.UpdateLayout();

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

                    var skinEditor = new SkinEditorPage(settings);
                    var layoutIni = SkinIniDocument.Parse(Encoding.UTF8.GetBytes(
                        """
                        [General]
                        Name: Layout skin with a deliberately long display name
                        Author: Layout tester
                        Version: 2.7
                        CursorCentre: 1
                        CursorExpand: 0
                        CursorRotate: 1

                        [Colours]
                        Combo1: 80,220,255
                        Combo2: 243,72,63
                        SliderBorder: 255,255,255
                        SliderTrackOverride: 34,48,64

                        [Fonts]
                        HitCirclePrefix: default
                        ScorePrefix: score
                        """));
                    typeof(SkinEditorPage)
                        .GetField("iniDocument", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .SetValue(skinEditor, layoutIni);
                    typeof(SkinEditorPage)
                        .GetMethod("BuildIniForm", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(skinEditor, null);
                    var skinWindow = new Window
                    {
                        Content = skinEditor,
                        ShowInTaskbar = false,
                        WindowStartupLocation = WindowStartupLocation.Manual,
                        WindowStyle = WindowStyle.None,
                        ResizeMode = ResizeMode.NoResize,
                        Left = -20000,
                        Top = -20000,
                    };
                    skinWindow.Show();
                    var pickerSkin = new Kumori.Tracking.LazerSkinInfo(
                        Guid.NewGuid(),
                        "A selected skin whose name must remain visible",
                        "Layout tester",
                        []);
                    typeof(SkinEditorPage)
                        .GetField("allSkins", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .SetValue(skinEditor, new[] { pickerSkin });
                    Assert.IsType<ComboBox>(skinEditor.FindName("CompactSkinPicker"))
                        .ItemsSource = new[] { pickerSkin };
                    typeof(SkinEditorPage)
                        .GetMethod("SetSkinPickerSelection", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(skinEditor, [pickerSkin]);
                    skinWindow.UpdateLayout();
                    var activeSkinPicker = Assert.IsType<ComboBox>(
                        skinEditor.FindName("CompactSkinPicker"));
                    Assert.Same(pickerSkin, activeSkinPicker.SelectedItem);
                    Assert.Equal(pickerSkin.DisplayName, activeSkinPicker.Text);
                    var editableSkinText = Assert.IsType<TextBox>(
                        activeSkinPicker.Template.FindName("PART_EditableTextBox", activeSkinPicker));
                    Assert.Equal(Visibility.Visible, editableSkinText.Visibility);
                    Assert.Equal(pickerSkin.DisplayName, editableSkinText.Text);

                    (double Width, double Height)[] skinSizes =
                    [
                        (720, 480), (1024, 600), (1180, 820), (1472, 1035), (1920, 1080),
                    ];
                    foreach (var size in skinSizes)
                    {
                        skinWindow.Width = size.Width;
                        skinWindow.Height = size.Height;
                        skinWindow.UpdateLayout();

                        // WPF can clamp an off-screen test window to the runner's
                        // virtual desktop. Assert against the editor's measured
                        // size, which is also what production layout resolves.
                        var measuredState = ResponsiveLayoutResolver.Resolve(
                            skinEditor.ActualWidth,
                            skinEditor.ActualHeight);
                        var compact = measuredState.IsCompact;
                        var shortLayout = measuredState.IsShort;
                        var compactBar = Assert.IsType<Border>(skinEditor.FindName("CompactSurfaceBar"));
                        var navigator = Assert.IsType<Border>(skinEditor.FindName("NavigatorPanel"));
                        var center = Assert.IsType<Border>(skinEditor.FindName("CenterPanel"));
                        var inspector = Assert.IsType<Border>(skinEditor.FindName("InspectorPanel"));
                        Assert.Equal(compact ? Visibility.Visible : Visibility.Collapsed, compactBar.Visibility);
                        Assert.Equal(
                            shortLayout ? Visibility.Collapsed : Visibility.Visible,
                            Assert.IsType<TextBlock>(skinEditor.FindName("ActiveSkinLabel")).Visibility);
                        if (compact)
                        {
                            Assert.Equal(Visibility.Collapsed, navigator.Visibility);
                            Assert.Equal(Visibility.Visible, center.Visibility);
                            Assert.Equal(Visibility.Collapsed, inspector.Visibility);
                            Assert.IsType<ToggleButton>(skinEditor.FindName("CompactBrowseButton"))
                                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            skinWindow.UpdateLayout();
                            Assert.Equal(Visibility.Visible, navigator.Visibility);
                            Assert.Equal(Visibility.Collapsed, center.Visibility);
                            Assert.IsType<ToggleButton>(skinEditor.FindName("CompactCanvasButton"))
                                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        }
                        else
                        {
                            Assert.Equal(Visibility.Visible, navigator.Visibility);
                            Assert.Equal(Visibility.Visible, center.Visibility);
                            Assert.Equal(Visibility.Visible, inspector.Visibility);
                            var navigatorColumn = Assert.IsType<ColumnDefinition>(skinEditor.FindName("NavigatorColumn"));
                            var inspectorColumn = Assert.IsType<ColumnDefinition>(skinEditor.FindName("InspectorColumn"));
                            Assert.Equal(measuredState.IsStandard ? 220 : 256, navigatorColumn.Width.Value);
                            Assert.Equal(measuredState.IsStandard ? 288 : 320, inspectorColumn.Width.Value);
                        }

                        var root = Assert.IsType<Grid>(skinEditor.FindName("RootGrid"));
                        Assert.True(root.DesiredSize.Width <= root.ActualWidth + LayoutTolerance,
                            $"Skin editor overflows at {size.Width}x{size.Height}: desired {root.DesiredSize}, actual {root.RenderSize}.");
                        Assert.True(root.DesiredSize.Height <= root.ActualHeight + LayoutTolerance,
                            $"Skin editor clips vertically at {size.Width}x{size.Height}: desired {root.DesiredSize}, actual {root.RenderSize}.");

                        if (Environment.GetEnvironmentVariable("KUMORI_SKIN_EDITOR_SNAPSHOT_DIR") is { Length: > 0 } skinSnapshotDirectory)
                        {
                            Directory.CreateDirectory(skinSnapshotDirectory);
                            SaveSnapshot(
                                root,
                                Path.Combine(skinSnapshotDirectory, $"skin-editor-{size.Width:0}x{size.Height:0}.png"),
                                1);
                        }
                    }

                    Assert.IsType<ToggleButton>(skinEditor.FindName("GameplayCanvasButton"))
                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    skinWindow.UpdateLayout();
                    Assert.Equal(Visibility.Visible, Assert.IsType<Grid>(skinEditor.FindName("GameplayPreviewPanel")).Visibility);
                    var gameplayScroller = Assert.IsType<ScrollViewer>(skinEditor.FindName("GameplayPreviewScrollViewer"));
                    Assert.True(gameplayScroller.ScrollableHeight > 0);
                    var gameplayCards = new[]
                    {
                        Assert.IsType<Border>(skinEditor.FindName("GameplayHudCard")),
                        Assert.IsType<Border>(skinEditor.FindName("GameplayHitObjectCard")),
                        Assert.IsType<Border>(skinEditor.FindName("GameplaySliderCard")),
                        Assert.IsType<Border>(skinEditor.FindName("GameplaySpinnerCard")),
                        Assert.IsType<Border>(skinEditor.FindName("GameplayCursorCard")),
                    };
                    Assert.All(gameplayCards, card =>
                        Assert.InRange(card.ActualWidth, gameplayScroller.ViewportWidth - 32, gameplayScroller.ViewportWidth));
                    var spinnerScene = Assert.IsType<Canvas>(skinEditor.FindName("GameplaySpinnerScene"));
                    Assert.Equal(640, spinnerScene.Width);
                    Assert.Equal(480, spinnerScene.Height);
                    var sliderEndToggle = Assert.IsType<ToggleButton>(skinEditor.FindName("SliderEndCircleToggle"));
                    sliderEndToggle.IsChecked = false;
                    sliderEndToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Equal(Visibility.Collapsed, Assert.IsType<Image>(skinEditor.FindName("GameplayTailCircle")).Visibility);
                    Assert.Equal(Visibility.Collapsed, Assert.IsType<Image>(skinEditor.FindName("GameplayTailOverlay")).Visibility);
                    Assert.Equal(Visibility.Visible, Assert.IsType<Image>(skinEditor.FindName("GameplayReverseArrow")).Visibility);
                    sliderEndToggle.IsChecked = true;
                    sliderEndToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Equal(Visibility.Visible, Assert.IsType<Image>(skinEditor.FindName("GameplayTailCircle")).Visibility);
                    Assert.IsType<ToggleButton>(skinEditor.FindName("IniRawModeButton"))
                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    var relatedIniButton = Assert.IsType<Button>(
                        skinEditor.FindName("OpenElementIniLinkButton"));
                    relatedIniButton.Tag = ("General", "CursorCentre");
                    relatedIniButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    skinWindow.UpdateLayout();
                    Assert.Equal(1, Assert.IsType<TabControl>(
                        skinEditor.FindName("WorkspaceTabs")).SelectedIndex);
                    Assert.Equal(0, Assert.IsType<TabControl>(
                        skinEditor.FindName("IniModeTabs")).SelectedIndex);
                    var firstOpenForm = Assert.IsType<StackPanel>(
                        skinEditor.FindName("IniFormPanel"));
                    var firstOpenSection = Assert.Single(
                        firstOpenForm.Children.OfType<StackPanel>());
                    Assert.NotEmpty(
                        Assert.IsType<StackPanel>(firstOpenSection.Children[0]).Children);
                    Assert.Equal(
                        "Cursor origin at centre",
                        Assert.IsType<TextBlock>(skinEditor.FindName("IniContextTitle")).Text);
                    if (Environment.GetEnvironmentVariable("KUMORI_SKIN_EDITOR_SNAPSHOT_DIR") is { Length: > 0 } gameplaySnapshotDirectory)
                    {
                        SaveSnapshot(
                            Assert.IsType<Grid>(skinEditor.FindName("RootGrid")),
                            Path.Combine(gameplaySnapshotDirectory, "skin-editor-gameplay.png"),
                            1);
                        gameplayScroller.ScrollToVerticalOffset(850);
                        skinWindow.UpdateLayout();
                        SaveSnapshot(
                            Assert.IsType<Grid>(skinEditor.FindName("RootGrid")),
                            Path.Combine(gameplaySnapshotDirectory, "skin-editor-gameplay-spinner.png"),
                            1);
                    }
                    gameplayScroller.ScrollToVerticalOffset(850);
                    skinWindow.UpdateLayout();
                    var gameplayOffset = gameplayScroller.VerticalOffset;
                    Assert.IsType<ToggleButton>(skinEditor.FindName("AssetCanvasButton"))
                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.IsType<ToggleButton>(skinEditor.FindName("GameplayCanvasButton"))
                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    skinWindow.UpdateLayout();
                    Assert.InRange(gameplayScroller.VerticalOffset, gameplayOffset - 1, gameplayOffset + 1);

                    Assert.IsType<ToggleButton>(skinEditor.FindName("IniWorkspaceModeButton"))
                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    skinWindow.UpdateLayout();
                    Assert.Equal(Visibility.Visible, Assert.IsType<Grid>(skinEditor.FindName("IniNavigatorContent")).Visibility);
                    Assert.Equal(Visibility.Collapsed, Assert.IsType<Grid>(skinEditor.FindName("ElementNavigatorContent")).Visibility);
                    Assert.Equal(1, Assert.IsType<TabControl>(skinEditor.FindName("WorkspaceTabs")).SelectedIndex);
                    var iniFormPanel = Assert.IsType<StackPanel>(skinEditor.FindName("IniFormPanel"));
                    var visibleSection = Assert.Single(iniFormPanel.Children.OfType<StackPanel>());
                    Assert.Equal(Visibility.Visible, visibleSection.Visibility);
                    Assert.NotEmpty(Assert.IsType<StackPanel>(visibleSection.Children[0]).Children);
                    var iniSections = Assert.IsType<ListBox>(skinEditor.FindName("IniSectionList"));
                    iniSections.SelectedIndex = 0;
                    skinWindow.UpdateLayout();
                    visibleSection = Assert.Single(iniFormPanel.Children.OfType<StackPanel>());
                    Assert.NotEmpty(Assert.IsType<StackPanel>(visibleSection.Children[0]).Children);
                    iniSections.SelectedIndex = 1;
                    skinWindow.UpdateLayout();
                    visibleSection = Assert.Single(iniFormPanel.Children.OfType<StackPanel>());
                    Assert.NotEmpty(Assert.IsType<StackPanel>(visibleSection.Children[0]).Children);
                    Assert.IsType<ToggleButton>(skinEditor.FindName("IniRawModeButton"))
                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    var rawIni = Assert.IsType<TextBox>(skinEditor.FindName("RawIniText"));
                    rawIni.AppendText(Environment.NewLine + "; layout round-trip");
                    Assert.IsType<ToggleButton>(skinEditor.FindName("IniFormModeButton"))
                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    skinWindow.UpdateLayout();
                    visibleSection = Assert.Single(iniFormPanel.Children.OfType<StackPanel>());
                    Assert.Equal(Visibility.Visible, visibleSection.Visibility);
                    Assert.NotEmpty(Assert.IsType<StackPanel>(visibleSection.Children[0]).Children);
                    foreach (var iniSize in new[]
                             {
                                 (Width: 720d, Height: 480d),
                                 (Width: 1024d, Height: 600d),
                                 (Width: 1180d, Height: 820d),
                             })
                    {
                        skinWindow.Width = iniSize.Width;
                        skinWindow.Height = iniSize.Height;
                        skinWindow.UpdateLayout();
                        if (iniSize.Width <= ResponsiveLayoutResolver.CompactMaximumWidth)
                        {
                            Assert.IsType<ToggleButton>(skinEditor.FindName("CompactCanvasButton"))
                                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            skinWindow.UpdateLayout();
                        }

                        visibleSection = Assert.Single(iniFormPanel.Children.OfType<StackPanel>());
                        var settingRows = Assert.IsType<StackPanel>(visibleSection.Children[0]);
                        Assert.NotEmpty(settingRows.Children);
                        Assert.All(
                            Descendants<TextBlock>(settingRows)
                                .Where(text => text.ToolTip is string),
                            label =>
                            {
                                Assert.Equal(TextWrapping.Wrap, label.TextWrapping);
                                Assert.Equal(TextTrimming.None, label.TextTrimming);
                                Assert.True(label.ActualHeight + LayoutTolerance >= label.FontSize);
                            });
                        Assert.All(
                            Descendants<TextBox>(settingRows),
                            input => Assert.True(input.ActualHeight >= 32 - LayoutTolerance));
                        var iniScroller = Assert.IsType<ScrollViewer>(
                            skinEditor.FindName("IniFormScroll"));
                        Assert.True(
                            visibleSection.DesiredSize.Width <= iniScroller.ViewportWidth + LayoutTolerance,
                            $"skin.ini form overflows at {iniSize.Width}x{iniSize.Height}: "
                            + $"desired {visibleSection.DesiredSize.Width}, viewport {iniScroller.ViewportWidth}.");

                        if (Environment.GetEnvironmentVariable("KUMORI_SKIN_EDITOR_SNAPSHOT_DIR")
                            is { Length: > 0 } compactIniSnapshotDirectory)
                        {
                            SaveSnapshot(
                                Assert.IsType<Grid>(skinEditor.FindName("RootGrid")),
                                Path.Combine(
                                    compactIniSnapshotDirectory,
                                    $"skin-editor-ini-{iniSize.Width:0}x{iniSize.Height:0}.png"),
                                1);
                        }
                    }
                    skinWindow.Width = 1920;
                    skinWindow.Height = 1080;
                    skinWindow.UpdateLayout();
                    if (Environment.GetEnvironmentVariable("KUMORI_SKIN_EDITOR_SNAPSHOT_DIR") is { Length: > 0 } modeSnapshotDirectory)
                    {
                        SaveSnapshot(
                            Assert.IsType<Grid>(skinEditor.FindName("RootGrid")),
                            Path.Combine(modeSnapshotDirectory, "skin-editor-ini.png"),
                            1);
                    }
                    Assert.IsType<Button>(skinEditor.FindName("DraftReviewButton"))
                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    skinWindow.UpdateLayout();
                    Assert.Equal(Visibility.Visible, Assert.IsType<Grid>(skinEditor.FindName("ReviewInspectorContent")).Visibility);
                    Assert.Equal(Visibility.Collapsed, Assert.IsType<Grid>(skinEditor.FindName("ContextInspectorContent")).Visibility);
                    if (Environment.GetEnvironmentVariable("KUMORI_SKIN_EDITOR_SNAPSHOT_DIR") is { Length: > 0 } reviewSnapshotDirectory)
                    {
                        SaveSnapshot(
                            Assert.IsType<Grid>(skinEditor.FindName("RootGrid")),
                            Path.Combine(reviewSnapshotDirectory, "skin-editor-review.png"),
                            1);
                    }

                    var themeOverride = new SolidColorBrush(Color.FromRgb(0x10, 0x20, 0x30));
                    themeOverride.Freeze();
                    app.Resources["Brush.PanelBackground"] = themeOverride;
                    skinWindow.UpdateLayout();
                    var themedPanel = Assert.IsType<SolidColorBrush>(
                        Assert.IsType<Border>(skinEditor.FindName("CenterPanel")).Background);
                    Assert.Equal(Color.FromRgb(0x10, 0x20, 0x30), themedPanel.Color);
                    app.Resources.Remove("Brush.PanelBackground");
                    skinWindow.Close();

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
