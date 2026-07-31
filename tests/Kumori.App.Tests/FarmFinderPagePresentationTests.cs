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
            "{StaticResource FarmGridHeader}",
            (string?)resultsGrid.Attribute("ColumnHeaderStyle"));
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
            .Single(element =>
                (string?)element.Attribute("ItemsSource") ==
                "{Binding ModAcronyms, Mode=OneWay}");
        Assert.Contains(
            resultMods.Descendants(presentation + "ImageBrush"),
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
    public void ResultRowsOpenOsuWithoutExpandingInAppDetails()
    {
        var document = LoadPage();
        XNamespace presentation =
            "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        var resultsGrid = Assert.Single(
            document.Descendants(presentation + "DataGrid"));
        Assert.Equal(
            "Collapsed",
            (string?)resultsGrid.Attribute("RowDetailsVisibilityMode"));
        Assert.Equal(
            "ResultsGrid_PreviewMouseLeftButtonUp",
            (string?)resultsGrid.Attribute("PreviewMouseLeftButtonUp"));
        Assert.Null(resultsGrid.Attribute("SelectedItem"));

        Assert.Contains(
            resultsGrid.Descendants(presentation + "Run"),
            run =>
                (string?)run.Attribute("Text") ==
                "{Binding Beatmap.Mapper, Mode=OneWay}");
        var metadataRuns = resultsGrid
            .Descendants(presentation + "Run")
            .Where(run =>
                (string?)run.Attribute("Text") is
                    "{Binding Beatmap.Mapper, Mode=OneWay}" or
                    "{Binding Beatmap.Artist, Mode=OneWay}")
            .ToArray();
        Assert.Equal(
            "{Binding Beatmap.Mapper, Mode=OneWay}",
            (string?)metadataRuns[0].Attribute("Text"));
        Assert.Equal(
            "{Binding Beatmap.Artist, Mode=OneWay}",
            (string?)metadataRuns[1].Attribute("Text"));
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
    public void ResultColumnsGroupRelatedMetrics()
    {
        var document = LoadPage();
        XNamespace presentation =
            "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        var results = document.Descendants(presentation + "DataGrid").Single();
        var columns = results
            .Element(presentation + "DataGrid.Columns")!
            .Elements()
            .Select(element => (string?)element.Attribute("Header"))
            .ToArray();

        Assert.Equal(
            new string?[]
            {
                "Beatmap",
                "Mods",
                "Players ↓",
                "PP profile",
                "Map stats",
                "Score quality",
            },
            columns);
        Assert.DoesNotContain("PP range", columns);
        Assert.DoesNotContain("Median acc.", columns);
        Assert.DoesNotContain("FC", columns);
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
