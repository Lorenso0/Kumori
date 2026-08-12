using System.Xml.Linq;
using Xunit;

namespace Kumori.App.Tests;

public sealed class FarmFinderPagePresentationTests
{
    [Fact]
    public void ReadOnlyProgressPropertiesUseOneWayBindings()
    {
        var document = LoadPage();
        XNamespace presentation =
            "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        var progressBars =
            document.Descendants(presentation + "ProgressBar")
                .Where(element => element.Attribute("Maximum") is not null)
                .ToArray();

        Assert.NotEmpty(progressBars);
        Assert.All(progressBars, progressBar =>
        {
            Assert.Equal(
                "{Binding ProgressMaximum, Mode=OneWay}",
                (string?)progressBar.Attribute("Maximum"));
            Assert.Equal(
                "{Binding ProgressValue, Mode=OneWay}",
                (string?)progressBar.Attribute("Value"));
        });
    }

    [Fact]
    public void OptionalTextInputsShowAnEmptyPlaceholder()
    {
        var document = LoadPage();
        XNamespace presentation =
            "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        var inputs = document
            .Descendants(presentation + "TextBox")
            .Where(element => element.Attribute("Tag") is not null)
            .ToArray();

        Assert.NotEmpty(inputs);
        var resultsSearch = Assert.Single(
            inputs.Where(input =>
                (string?)input.Attribute(xaml + "Name") == "ResultsSearchBox"));
        Assert.Equal("Search results…", (string?)resultsSearch.Attribute("Tag"));
        Assert.Equal(
            "ResultsSearchBox_TextChanged",
            (string?)resultsSearch.Attribute("TextChanged"));

        var filterInputs = inputs.Except([resultsSearch]).ToArray();
        var compact = filterInputs
            .Where(input =>
                (string?)input.Attribute("Style") ==
                "{StaticResource FarmCompactInput}")
            .ToArray();
        Assert.Equal(6, compact.Length);
        Assert.All(compact, input => Assert.Equal("Any", (string?)input.Attribute("Tag")));
        Assert.All(
            filterInputs.Except(compact),
            input => Assert.Equal("Optional", (string?)input.Attribute("Tag")));
    }

    [Fact]
    public void ReadOnlyResultColumnsUseOneWayBindings()
    {
        var document = LoadPage();
        XNamespace presentation =
            "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        var columns = document
            .Descendants(presentation + "DataGridTextColumn")
            .ToArray();

        Assert.All(
            columns,
            column => Assert.Contains(
                "Mode=OneWay",
                (string?)column.Attribute("Binding")));

        var resultsGrid = document.Descendants(presentation + "DataGrid").Single();
        var boundRuns = resultsGrid
            .Descendants(presentation + "Run")
            .Where(element =>
                ((string?)element.Attribute("Text"))?.StartsWith(
                    "{Binding",
                    StringComparison.Ordinal) == true)
            .ToArray();

        Assert.NotEmpty(boundRuns);
        Assert.All(
            boundRuns,
            run => Assert.Contains(
                "Mode=OneWay",
                (string?)run.Attribute("Text")));
    }

    [Fact]
    public void NativeWhiteControlSurfacesAreReplacedByThemeStyles()
    {
        var document = LoadPage();
        XNamespace presentation =
            "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        var progressBars = document.Descendants(presentation + "ProgressBar").ToArray();
        Assert.Equal(2, progressBars.Length);
        Assert.Contains(
            progressBars,
            progressBar =>
                (string?)progressBar.Attribute("Style") ==
                "{StaticResource FarmProgressBar}");
        Assert.Contains(
            progressBars,
            progressBar =>
                (string?)progressBar.Attribute("Style") ==
                "{StaticResource FarmSearchProgressBar}");

        var resultsGrid = Assert.Single(
            document.Descendants(presentation + "DataGrid"));
        Assert.Equal(
            "None",
            (string?)resultsGrid.Attribute("HeadersVisibility"));
        Assert.Null(resultsGrid.Attribute("ColumnHeaderStyle"));
        Assert.Equal(
            "{StaticResource FarmGridRow}",
            (string?)resultsGrid.Attribute("RowStyle"));
        Assert.Equal(
            "{StaticResource FarmGridCell}",
            (string?)resultsGrid.Attribute("CellStyle"));

        Assert.Empty(document.Descendants(presentation + "Expander"));
        var moreButton = document.Descendants(presentation + "Button")
            .Single(element =>
                (string?)element.Attribute("Content") == "…");
        Assert.Equal(
            "MoreButton",
            (string?)moreButton.Attribute(
                XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml") + "Name"));
        Assert.Equal(
            "{StaticResource FarmMoreButton}",
            (string?)moreButton.Attribute("Style"));
        var moreStyle = document.Descendants(presentation + "Style")
            .Single(element =>
                (string?)element.Attribute(
                    XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml") + "Key") ==
                "FarmMoreButton");
        Assert.Equal(
            "{StaticResource Button.Icon}",
            (string?)moreStyle.Attribute("BasedOn"));
        Assert.Contains(
            moreStyle.Elements(presentation + "Setter"),
            setter =>
                (string?)setter.Attribute("Property") == "Width" &&
                (string?)setter.Attribute("Value") == "30");
        Assert.Contains(
            document.Descendants(presentation + "MenuItem"),
            element => (string?)element.Attribute("Header") == "osu! API setup");
        Assert.Single(document.Descendants(presentation + "Popup"));
    }

    [Fact]
    public void RankedModFiltersUseIconTilesInsteadOfDropdowns()
    {
        var document = LoadPage();
        XNamespace presentation =
            "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        var mods = document.Descendants(presentation + "ItemsControl")
            .Single(element =>
                (string?)element.Attribute("ItemsSource") == "{Binding Mods}");
        var tile = Assert.Single(
            mods.Descendants(presentation + "Button"));

        Assert.Equal(
            "{Binding CycleRequirementCommand}",
            (string?)tile.Attribute("Command"));
        Assert.Equal(
            "{StaticResource FarmModButton}",
            (string?)tile.Attribute("Style"));
        Assert.Empty(mods.Descendants(presentation + "ComboBox"));

        var resultMods = document.Descendants(presentation + "ItemsControl")
            .Where(element =>
                (string?)element.Attribute("ItemsSource") ==
                "{Binding ModAcronyms, Mode=OneWay}")
            .ToArray();
        Assert.NotEmpty(resultMods);
        Assert.All(
            resultMods,
            element => Assert.Equal(
                "{StaticResource FarmResultModTemplate}",
                (string?)element.Attribute("ItemTemplate")));
        var resultModTemplate = document.Descendants(presentation + "DataTemplate")
            .Single(element =>
                (string?)element.Attribute(
                    XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml") + "Key") ==
                "FarmResultModTemplate");
        Assert.Empty(resultModTemplate.Descendants(presentation + "Border"));
        var resultIcon = Assert.Single(
            resultModTemplate.Descendants(presentation + "Rectangle"));
        Assert.Equal("16", (string?)resultIcon.Attribute("Width"));
        Assert.Equal("16", (string?)resultIcon.Attribute("Height"));
        Assert.Equal("HighQuality", (string?)resultIcon.Attribute("RenderOptions.BitmapScalingMode"));
        Assert.Contains(
            resultModTemplate.Descendants(presentation + "ImageBrush"),
            element => ((string?)element.Attribute("ImageSource"))?.Contains(
                "ModAcronymToIconSourceConverter",
                StringComparison.Ordinal) == true);
    }

    [Fact]
    public void CompactInputsRetainEnoughHeightForScaledText()
    {
        var document = LoadPage();
        XNamespace presentation =
            "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        var inputStyle = document.Descendants(presentation + "Style")
            .Single(element =>
                (string?)element.Attribute(xaml + "Key") == "FarmInput");
        var height = inputStyle.Elements(presentation + "Setter")
            .Single(element =>
                (string?)element.Attribute("Property") == "Height");

        Assert.Equal("32", (string?)height.Attribute("Value"));

        var compactStyle = document.Descendants(presentation + "Style")
            .Single(element =>
                (string?)element.Attribute(xaml + "Key") == "FarmCompactInput");
        var width = compactStyle.Elements(presentation + "Setter")
            .Single(element =>
                (string?)element.Attribute("Property") == "Width");
        Assert.Equal("88", (string?)width.Attribute("Value"));

        var modStyle = document.Descendants(presentation + "Style")
            .Single(element =>
                (string?)element.Attribute(xaml + "Key") == "FarmModButton");
        var modHeight = modStyle.Elements(presentation + "Setter")
            .Single(element =>
                (string?)element.Attribute("Property") == "Height");
        Assert.Equal("68", (string?)modHeight.Attribute("Value"));
        Assert.Contains(
            document.Descendants(presentation + "TextBlock"),
            element => (string?)element.Attribute("Text") == "Click to cycle states");
    }

    [Fact]
    public void SecondaryActionsAreInMoreMenuAndFetchCacheIsPrimary()
    {
        var document = LoadPage();
        XNamespace presentation =
            "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        var build = document.Descendants(presentation + "MenuItem")
            .Single(element =>
                (string?)element.Attribute("Header") == "Build full index");
        Assert.Equal(
            "{Binding BuildFullIndexCommand}",
            (string?)build.Attribute("Command"));
        var repair = document.Descendants(presentation + "MenuItem")
            .Single(element =>
                (string?)element.Attribute("Header") ==
                "{Binding RepairScoreMetadataMenuText}");
        Assert.Equal(
            "{Binding RepairScoreMetadataCommand}",
            (string?)repair.Attribute("Command"));
        Assert.DoesNotContain(
            document.Descendants(presentation + "Button"),
            element => (string?)element.Attribute("Content") is
                "Build full index" or "osu! API setup");

        var fetch = document.Descendants(presentation + "Button")
            .Single(element =>
                (string?)element.Attribute("Content") == "Fetch cache");
        Assert.Equal(
            "{Binding FetchCacheCommand}",
            (string?)fetch.Attribute("Command"));

        Assert.Contains(
            document.Descendants(presentation + "TextBlock"),
            element =>
                (string?)element.Attribute("Text") ==
                "{Binding ProgressDetailsText}");
        Assert.Contains(
            document.Descendants(presentation + "TextBlock"),
            element =>
                (string?)element.Attribute("Text") ==
                "{Binding EstimatedTimeRemainingText}");
        Assert.DoesNotContain(
            document.Descendants(presentation + "Button"),
            element => (string?)element.Attribute("Content") == "Clear filters");

        var coverage = document.Descendants(presentation + "TextBlock")
            .Single(element =>
                (string?)element.Attribute("Text") == "{Binding CoverageText}");
        var progressDetails = document.Descendants(presentation + "TextBlock")
            .Single(element =>
                (string?)element.Attribute("Text") ==
                "{Binding ProgressDetailsText}");
        Assert.Same(coverage.Parent, progressDetails.Parent);
    }

    [Fact]
    public void IndexDetailsStartCollapsedBehindACompactSummary()
    {
        var document = LoadPage();
        XNamespace presentation =
            "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        var toggle = document.Descendants(presentation + "ToggleButton")
            .Single(element =>
                (string?)element.Attribute(xaml + "Name") == "IndexStatusToggle");
        Assert.Equal("False", (string?)toggle.Attribute("IsChecked"));
        Assert.Equal(
            "{StaticResource FarmSectionToggle}",
            (string?)toggle.Attribute("Style"));

        var details = toggle
            .ElementsAfterSelf(presentation + "StackPanel")
            .First();
        Assert.Equal(
            "{Binding IsChecked, ElementName=IndexStatusToggle, Converter={x:Static vm:BoolToVisibleConverter.Instance}}",
            (string?)details.Attribute("Visibility"));
    }

    [Fact]
    public void FilterRailSitsBesideResultsAndAdvancedOptionsAreHidden()
    {
        var document = LoadPage();
        XNamespace presentation =
            "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        var filterPanel = Assert.Single(
            document.Descendants(presentation + "Border")
                .Where(element =>
                    (string?)element.Attribute(xaml + "Name") == "FilterPanel"));
        var resultsPanel = Assert.Single(
            document.Descendants(presentation + "Border")
                .Where(element =>
                    (string?)element.Attribute(xaml + "Name") == "ResultsPanel"));

        Assert.Equal("0", (string?)filterPanel.Attribute("Grid.Column"));
        Assert.Null(filterPanel.Attribute("Grid.Row"));
        Assert.Equal("1", (string?)resultsPanel.Attribute("Grid.Row"));

        var workspace = Assert.IsType<XElement>(filterPanel.Parent);
        var columns = workspace
            .Element(presentation + "Grid.ColumnDefinitions")!
            .Elements(presentation + "ColumnDefinition")
            .Select(column => (string?)column.Attribute("Width"))
            .ToArray();
        Assert.Equal(new string?[] { "302", "*" }, columns);

        var resultsWorkspace = Assert.IsType<XElement>(resultsPanel.Parent);
        Assert.Equal("1", (string?)resultsWorkspace.Attribute("Grid.Column"));
        Assert.Same(workspace, resultsWorkspace.Parent);
        Assert.Contains(
            filterPanel.Descendants(presentation + "ScrollViewer"),
            element =>
                (string?)element.Attribute("VerticalScrollBarVisibility") == "Auto" &&
                (string?)element.Attribute("Grid.Row") == "1");
        var searchButton = filterPanel.Descendants(presentation + "Button")
            .Single(element =>
                (string?)element.Attribute("Content") == "Search cached data");
        Assert.Equal("2", (string?)searchButton.Parent?.Attribute("Grid.Row"));
        Assert.DoesNotContain(
            document.Descendants(presentation + "TextBlock"),
            element => (string?)element.Attribute("Text") is
                "DISCOVER & SORT" or "MATCHING");
        Assert.DoesNotContain(
            document.Descendants(presentation + "CheckBox"),
            element => (string?)element.Attribute("Content") is
                "NC matches DT family" or "Force top-score refresh");

        var visibleLabels = document.Descendants(presentation + "TextBlock")
            .Select(element => (string?)element.Attribute("Text"))
            .Where(text => text is not null)
            .ToArray();
        Assert.Contains("PLAYER RANK", visibleLabels);
        Assert.Contains("PP VALUE", visibleLabels);
        Assert.Contains("MAP STATS", visibleLabels);
        Assert.Contains("LENGTH", visibleLabels);
        Assert.DoesNotContain(
            visibleLabels,
            text => text!.Contains("cohort", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ResultRowsOpenDetailsBeforeExplicitOsuActions()
    {
        var document = LoadPage();
        XNamespace presentation =
            "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        var resultsGrid = Assert.Single(
            document.Descendants(presentation + "DataGrid"));
        Assert.Equal(
            "Collapsed",
            (string?)resultsGrid.Attribute("RowDetailsVisibilityMode"));
        Assert.Empty(
            resultsGrid.Elements(presentation + "DataGrid.RowDetailsTemplate"));
        Assert.Equal(
            "{Binding SelectedResult, Mode=TwoWay}",
            (string?)resultsGrid.Attribute("SelectedItem"));
        Assert.Null(resultsGrid.Attribute("PreviewMouseLeftButtonUp"));
        Assert.Null(resultsGrid.Attribute("LoadingRow"));

        var inspector = Assert.Single(
            document.Descendants(presentation + "Border"),
            element =>
                (string?)element.Attribute(xaml + "Name") == "FarmInspectorPane");
        Assert.Equal("2", (string?)inspector.Attribute("Grid.Column"));
        Assert.Contains(
            document.Descendants(presentation + "GridSplitter"),
            splitter => (string?)splitter.Attribute("Grid.Column") == "1");
        var directButton = Assert.Single(
            inspector.Descendants(presentation + "Button"),
            button => (string?)button.Attribute("Content") == "Open in osu!");
        Assert.Contains(
            "OpenBeatmapCommand",
            (string?)directButton.Attribute("Command"));

        var browserMenuItem = Assert.Single(
            document.Descendants(presentation + "MenuItem"),
            menuItem => (string?)menuItem.Attribute("Header") == "Open in browser");
        Assert.Contains(
            "OpenBeatmapInBrowserCommand",
            (string?)browserMenuItem.Attribute("Command"));
        Assert.Contains(
            "PlacementTarget.DataContext",
            (string?)browserMenuItem.Attribute("CommandParameter"));

        Assert.Contains(
            resultsGrid.Descendants(presentation + "Run"),
            run =>
                (string?)run.Attribute("Text") ==
                "{Binding Beatmap.Mapper, Mode=OneWay}");
        Assert.Contains(
            resultsGrid.Descendants(presentation + "TextBlock"),
            text =>
                (string?)text.Attribute("Text") ==
                "{Binding Beatmap.Artist, Mode=OneWay}");
        var resultCountRun = resultsGrid.Parent!.Parent!
            .Descendants(presentation + "Run")
            .Single(run =>
                (string?)run.Attribute("Text") ==
                "{Binding Results.Count, Mode=OneWay, StringFormat={}{0:N0}}");
        Assert.Equal("2", (string?)resultCountRun.Parent?.Attribute("Grid.Column"));
        Assert.Equal(
            "Right",
            (string?)resultCountRun.Parent?.Attribute("HorizontalAlignment"));
        Assert.DoesNotContain(
            document.Descendants(presentation + "Border"),
            border =>
                border.Descendants(presentation + "TextBlock").Any(textBlock =>
                    (string?)textBlock.Attribute("Text") ==
                    "{Binding Results.Count, Mode=OneWay}"));
    }

    [Fact]
    public void ResultRowsUseTheMainScoresCardLanguage()
    {
        var document = LoadPage();
        XNamespace presentation =
            "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        var results = document.Descendants(presentation + "DataGrid").Single();
        Assert.Equal("None", (string?)results.Attribute("HeadersVisibility"));
        Assert.Equal("None", (string?)results.Attribute("GridLinesVisibility"));

        var columns = results
            .Element(presentation + "DataGrid.Columns")!
            .Elements()
            .ToArray();
        Assert.Single(columns);
        Assert.Equal("*", (string?)columns[0].Attribute("Width"));

        var resultCard = Assert.Single(
            results.Descendants(presentation + "Border"),
            border =>
                (string?)border.Attribute("Style") ==
                "{StaticResource FarmResultCard}");
        var metricLabels = resultCard
            .Descendants(presentation + "TextBlock")
            .Select(text => (string?)text.Attribute("Text"))
            .Where(text => text is not null)
            .ToArray();
        Assert.Contains("PP PROFILE", metricLabels);
        Assert.DoesNotContain("PLAYERS", metricLabels);
        Assert.DoesNotContain("MAP STATS", metricLabels);
        Assert.DoesNotContain("SCORE QUALITY", metricLabels);

        var cardColumns = resultCard
            .Element(presentation + "Grid")!
            .Element(presentation + "Grid.ColumnDefinitions")!
            .Elements(presentation + "ColumnDefinition")
            .Select(column => (string?)column.Attribute("Width"))
            .ToArray();
        Assert.Equal(new string?[] { "58", "*", "72", "154" }, cardColumns);
        var ppProfile = Assert.Single(
            resultCard.Descendants(presentation + "Border"),
            border => (string?)border.Attribute(xaml + "Name") == "ResultPpProfile");
        Assert.Equal("1,0,0,0", (string?)ppProfile.Attribute("BorderThickness"));
        var average = Assert.Single(
            ppProfile.Descendants(presentation + "TextBlock"),
            text => ((string?)text.Attribute("Text"))?.Contains(
                "AveragePp",
                StringComparison.Ordinal) == true);
        Assert.Equal("NoWrap", (string?)average.Attribute("TextWrapping"));
        Assert.Equal("Right", (string?)average.Attribute("TextAlignment"));

        var inspectorLabels = document.Descendants(presentation + "TextBlock")
            .Select(text => (string?)text.Attribute("Text"))
            .Where(text => text is not null)
            .ToArray();
        Assert.Contains("PLAYERS", inspectorLabels);
        Assert.Contains("MAP STATS", inspectorLabels);
        Assert.Contains("SCORE QUALITY", inspectorLabels);
        Assert.Contains("CS", inspectorLabels);
        Assert.Contains("AR", inspectorLabels);
        Assert.Contains("OD", inspectorLabels);
        Assert.Contains("HP", inspectorLabels);
        Assert.Contains("LENGTH", inspectorLabels);
        Assert.Contains("BPM", inspectorLabels);
        Assert.Contains("DIFFICULTY", inspectorLabels);
    }

    [Fact]
    public void ResultDetailsContainAVirtualizedPpLeaderboard()
    {
        var document = LoadPage();
        XNamespace presentation =
            "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        var inspector = document.Descendants(presentation + "Border")
            .Single(element =>
                (string?)element.Attribute(xaml + "Name") == "FarmInspectorPane");
        Assert.Contains(
            inspector.Descendants(presentation + "Run"),
            run => (string?)run.Attribute("Text") == "Fetched player leaderboard");

        var leaderboard = Assert.Single(
            inspector.Descendants(presentation + "ListBox"));
        Assert.Equal(
            "{Binding ScoreGroups, Mode=OneWay}",
            (string?)leaderboard.Attribute("ItemsSource"));
        Assert.Equal(
            "True",
            (string?)leaderboard.Attribute("VirtualizingPanel.IsVirtualizing"));
        Assert.Equal(
            "Recycling",
            (string?)leaderboard.Attribute("VirtualizingPanel.VirtualizationMode"));

        var bindings = leaderboard
            .Descendants()
            .Attributes("Text")
            .Select(attribute => attribute.Value)
            .ToArray();
        Assert.Contains(bindings, binding => binding.Contains("LeaderboardRank"));
        Assert.Contains(bindings, binding => binding.Contains("Username"));
        Assert.Contains(bindings, binding => binding.Contains("CountText"));
        Assert.Contains(bindings, binding => binding.Contains("Pp"));
        Assert.Contains(bindings, binding => binding.Contains("Accuracy"));
        Assert.Contains(bindings, binding => binding.Contains("MaxCombo"));
        Assert.Contains(bindings, binding => binding.Contains("MissCount"));
        Assert.Contains(bindings, binding => binding.Contains("ScoringModeText"));
        Assert.Contains(
            leaderboard.Descendants(presentation + "ItemsControl"),
            items =>
                (string?)items.Attribute("ItemsSource") ==
                "{Binding ModAcronyms, Mode=OneWay}");
        Assert.Contains(
            leaderboard.Descendants(presentation + "ToggleButton"),
            toggle =>
                (string?)toggle.Attribute("ToolTip") ==
                "Show players with this score");
        Assert.Contains(
            leaderboard.Descendants(presentation + "ItemsControl"),
            items =>
                (string?)items.Attribute("ItemsSource") ==
                "{Binding Players, Mode=OneWay}" &&
                (string?)items.Attribute("Visibility") == "Collapsed");
        Assert.Contains(
            leaderboard.Descendants(presentation + "DataTrigger"),
            trigger =>
                ((string?)trigger.Attribute("Binding"))?.Contains(
                    "ExpandScoresButton",
                    StringComparison.Ordinal) == true &&
                (string?)trigger.Attribute("Value") == "True");
    }

    [Fact]
    public void SearchProgressAppearsInsideTheResultsField()
    {
        var document = LoadPage();
        XNamespace presentation =
            "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        var resultsPanel = document.Descendants(presentation + "Border")
            .Single(element =>
                (string?)element.Attribute(xaml + "Name") == "ResultsPanel");
        Assert.Contains(
            resultsPanel.Descendants(presentation + "Border"),
            element =>
                (string?)element.Attribute("Visibility") ==
                "{Binding IsResultsOperationActive, Converter={x:Static vm:BoolToVisibleConverter.Instance}}");
        Assert.Contains(
            resultsPanel.Descendants(presentation + "ProgressBar"),
            element =>
                (string?)element.Attribute("Style") ==
                "{StaticResource FarmSearchProgressBar}");
    }

    private static XDocument LoadPage()
    {
        var root = FindRepositoryRoot();
        var path = Path.Combine(
            root,
            "src",
            "Kumori.App",
            "FarmFinder",
            "FarmFinderPage.xaml");
        return XDocument.Load(path);
    }

    private static string FindRepositoryRoot()
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "Kumori.sln")))
                return current;
            current = Directory.GetParent(current)?.FullName ?? string.Empty;
        }

        throw new DirectoryNotFoundException("Could not locate Kumori.sln.");
    }
}
