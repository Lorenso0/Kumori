using Kumori.App;
using Kumori.Core.Settings;
using Xunit;

namespace Kumori.App.Tests;

public sealed class SkinLibraryServiceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"kumori-skin-library-{Guid.NewGuid():N}");

    [Fact]
    public void ActivateBuiltInArgonPro_ClearsCustomSkinSelection()
    {
        var settings = CreateSettings();
        settings.Update(value => value.ReplayViewer.SkinPath = @"C:\skins\custom.osk");

        SkinLibraryService.Activate(settings, SkinLibraryService.BuiltInArgonProPath);

        Assert.Empty(settings.Current.ReplayViewer.SkinPath);
    }

    [Fact]
    public void DeleteImported_RejectsBuiltInArgonPro()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            SkinLibraryService.DeleteImported(SkinLibraryService.BuiltInArgonProPath));

        Assert.Contains("cannot be deleted", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void List_PreservesAndShowsUnavailableConfiguredSelection()
    {
        var settings = CreateSettings();
        string missing = Path.Combine(root, "missing.osk");
        settings.Update(value => value.ReplayViewer.SkinPath = missing);

        var items = SkinLibraryService.ListFromDirectory(
            Path.Combine(root, "library"),
            settings.Current.ReplayViewer.SkinPath);

        var selected = Assert.Single(items, item => item.Path == missing);
        Assert.False(selected.IsAvailable);
        Assert.False(selected.IsImported);
        Assert.Equal(missing, settings.Current.ReplayViewer.SkinPath);
    }

    [Fact]
    public void List_ShowsOnlyActualSkinFolders()
    {
        string library = Path.Combine(root, "library");
        string imported = Path.Combine(library, "Imported skin");
        string drafts = Path.Combine(library, "drafts");
        Directory.CreateDirectory(imported);
        Directory.CreateDirectory(drafts);
        File.WriteAllText(Path.Combine(imported, "SKIN.INI"), "[General]");
        File.WriteAllText(Path.Combine(drafts, "manifest.json"), "{}");

        var items = SkinLibraryService.ListFromDirectory(library);

        Assert.Contains(items, item => item.Path == imported && item.IsFolder);
        Assert.DoesNotContain(items, item => item.Path == drafts);
    }

    [Fact]
    public void ImportedSkinPath_RejectsInfrastructureAndNestedFolders()
    {
        string library = Path.Combine(root, "library");
        string imported = Path.Combine(library, "Imported skin");
        string infrastructure = Path.Combine(library, "backup");
        string nested = Path.Combine(imported, "nested");
        Directory.CreateDirectory(imported);
        Directory.CreateDirectory(infrastructure);
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(imported, "skin.ini"), "[General]");
        File.WriteAllText(Path.Combine(nested, "skin.ini"), "[General]");

        Assert.True(SkinLibraryService.IsImportedSkinPath(imported, library));
        Assert.False(SkinLibraryService.IsImportedSkinPath(infrastructure, library));
        Assert.False(SkinLibraryService.IsImportedSkinPath(nested, library));
    }

    private SettingsService CreateSettings()
    {
        Directory.CreateDirectory(root);
        var settings = new SettingsService(
            Path.Combine(root, "settings.v2.json"),
            Path.Combine(root, "settings.json"));
        settings.Load();
        return settings;
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
