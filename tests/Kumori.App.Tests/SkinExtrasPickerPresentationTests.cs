using System.Xml.Linq;
using Xunit;

namespace Kumori.App.Tests;

public sealed class SkinExtrasPickerPresentationTests
{
    [Fact]
    public void Family_and_pack_lists_use_equivalent_pixel_scroll_hosts()
    {
        var root = FindRepositoryRoot();
        var path = Path.Combine(
            root,
            "src",
            "Kumori.App",
            "Skins",
            "SkinExtrasPickerWindow.xaml");
        var document = XDocument.Load(path);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        var family = document.Descendants(presentation + "ListBox")
            .Single(element => (string?)element.Attribute(xaml + "Name") == "FamilyList");
        var packs = document.Descendants(presentation + "ItemsControl")
            .Single(element => (string?)element.Attribute(xaml + "Name") == "PackList");

        AssertPixelScrollHost(family, presentation);
        AssertPixelScrollHost(packs, presentation);
    }

    [Fact]
    public void Pack_workspace_separates_preview_elements_and_details()
    {
        var document = LoadPickerDocument();
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        var renderer = document.Descendants(presentation + "ContentControl")
            .Single(element => (string?)element.Attribute(xaml + "Name") == "RendererMount");
        Assert.Equal("1", (string?)renderer.Attribute("Grid.Row"));
        Assert.Null(renderer.Attribute("Grid.RowSpan"));

        var previewBorder = renderer.Ancestors(presentation + "Border").First();
        Assert.Equal("470", (string?)previewBorder.Attribute("MaxHeight"));

        var workspace = NamedElement(document, xaml, "PackWorkspaceGrid");
        Assert.Equal(
            3,
            workspace.Element(presentation + "Grid.ColumnDefinitions")!
                .Elements(presentation + "ColumnDefinition")
                .Count());

        var elements = NamedElement(document, xaml, "IncludedElementsCard");
        Assert.NotNull(elements.Ancestors(presentation + "Grid")
            .SingleOrDefault(element =>
                (string?)element.Attribute(xaml + "Name") == "PreviewAndElementsPane"));

        var inspector = NamedElement(document, xaml, "PackDetailsInspector");
        Assert.Equal("2", (string?)inspector.Attribute("Grid.Column"));
    }

    [Fact]
    public void Primary_action_lives_in_pack_header_not_utility_bar()
    {
        var document = LoadPickerDocument();
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        var primary = NamedElement(document, xaml, "UsePackButton");
        Assert.NotNull(primary.Ancestors(presentation + "Grid")
            .SingleOrDefault(element =>
                (string?)element.Attribute(xaml + "Name") == "SelectedPackHeader"));

        var utilityBar = NamedElement(document, xaml, "UtilityBar");
        Assert.DoesNotContain(
            utilityBar.Descendants(),
            element => (string?)element.Attribute(xaml + "Name") == "UsePackButton");
    }

    [Fact]
    public void Pack_selection_keeps_header_preview_controls_and_elements_geometry_stable()
    {
        var document = LoadPickerDocument();
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        var header = NamedElement(document, xaml, "SelectedPackHeader");
        var detailRoot = header.Parent!;
        var detailRows = detailRoot.Element(presentation + "Grid.RowDefinitions")!
            .Elements(presentation + "RowDefinition")
            .ToArray();
        Assert.Equal("88", (string?)detailRows[0].Attribute("Height"));

        var previewAndElements = NamedElement(document, xaml, "PreviewAndElementsPane");
        var workspaceRows = previewAndElements
            .Element(presentation + "Grid.RowDefinitions")!
            .Elements(presentation + "RowDefinition")
            .ToArray();
        Assert.Equal("300", (string?)workspaceRows[2].Attribute("Height"));

        var modeBar = NamedElement(document, xaml, "PreviewModeBar");
        Assert.Equal("Hidden", (string?)modeBar.Attribute("Visibility"));
    }

    private static void AssertPixelScrollHost(
        XElement control,
        XNamespace presentation)
    {
        Assert.Equal(
            "True",
            (string?)control.Attribute(
                "VirtualizingPanel.IsVirtualizingWhenGrouping"));
        Assert.Equal(
            "Pixel",
            (string?)control.Attribute("VirtualizingPanel.ScrollUnit"));

        var template = control.Elements()
            .Single(element => element.Name.LocalName.EndsWith(
                ".Template",
                StringComparison.Ordinal));
        var scrollViewer = Assert.Single(
            template.Descendants(presentation + "ScrollViewer"));
        Assert.Equal(
            "LibraryList_PreviewMouseWheel",
            (string?)scrollViewer.Attribute("PreviewMouseWheel"));

        var itemsPanel = control.Elements()
            .Single(element => element.Name.LocalName.EndsWith(
                ".ItemsPanel",
                StringComparison.Ordinal));
        Assert.Single(itemsPanel.Descendants(
            presentation + "VirtualizingStackPanel"));

        var groupStyle = Assert.Single(
            control.Descendants(presentation + "GroupStyle"));
        var groupPanel = groupStyle.Elements()
            .Single(element => element.Name.LocalName == "GroupStyle.Panel");
        Assert.Single(groupPanel.Descendants(
            presentation + "VirtualizingStackPanel"));
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

    private static XDocument LoadPickerDocument()
    {
        var root = FindRepositoryRoot();
        return XDocument.Load(Path.Combine(
            root,
            "src",
            "Kumori.App",
            "Skins",
            "SkinExtrasPickerWindow.xaml"));
    }

    private static XElement NamedElement(
        XDocument document,
        XNamespace xaml,
        string name) => document.Descendants()
            .Single(element => (string?)element.Attribute(xaml + "Name") == name);
}
