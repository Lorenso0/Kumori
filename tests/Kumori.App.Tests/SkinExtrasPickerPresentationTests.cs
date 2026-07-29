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
}
