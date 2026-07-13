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
    public void EnsureValidSelection_ClearsMissingCustomSkin()
    {
        var settings = CreateSettings();
        settings.Update(value => value.ReplayViewer.SkinPath = Path.Combine(root, "missing.osk"));

        Assert.True(SkinLibraryService.EnsureValidSelection(settings));
        Assert.Empty(settings.Current.ReplayViewer.SkinPath);
        Assert.False(SkinLibraryService.EnsureValidSelection(settings));
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
