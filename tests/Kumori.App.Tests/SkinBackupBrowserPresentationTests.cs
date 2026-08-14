using System.Xml.Linq;
using Xunit;

namespace Kumori.App.Tests;

public sealed class SkinBackupBrowserPresentationTests
{
    [Fact]
    public void Browser_exposes_backup_timeline_file_selection_and_staging_action()
    {
        var document = XDocument.Load(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "Kumori.App",
            "Skins",
            "SkinBackupBrowserWindow.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        Assert.NotNull(Named(document, xaml, "BackupList"));
        Assert.NotNull(Named(document, xaml, "BackupFilesList"));
        var restore = Named(document, xaml, "RestoreButton");
        Assert.Equal("Add selected to Changes", (string?)restore.Attribute("Content"));
        Assert.Single(
            Named(document, xaml, "BackupFilesList")
                .Descendants(presentation + "CheckBox"));
    }

    private static XElement Named(
        XDocument document,
        XNamespace xaml,
        string name) => document.Descendants()
            .Single(element => (string?)element.Attribute(xaml + "Name") == name);

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
