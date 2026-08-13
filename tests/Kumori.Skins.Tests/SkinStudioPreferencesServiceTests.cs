using Xunit;

namespace Kumori.Skins.Tests;

public sealed class SkinStudioPreferencesServiceTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "kumori-studio-preferences-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void MissingPreferencesUseSafeAutomaticBackupDefaults()
    {
        var preferences = new SkinStudioPreferencesService(root).Load();

        Assert.True(preferences.AutomaticEditBackups);
        Assert.Equal(30, preferences.BackupRetention);
        Assert.False(preferences.ShowOtherRulesetElements);
        Assert.Equal(
            SkinStudioPreferencesService.CurrentFormatVersion,
            preferences.FormatVersion);
    }

    [Fact]
    public void PreferencesPersistAtomicallyAndReplacePreviousValues()
    {
        var service = new SkinStudioPreferencesService(root);

        service.Save(new SkinStudioPreferences(
            AutomaticEditBackups: false,
            BackupRetention: 12));
        service.Save(new SkinStudioPreferences(
            AutomaticEditBackups: true,
            BackupRetention: 7));

        var loaded = service.Load();
        Assert.True(loaded.AutomaticEditBackups);
        Assert.Equal(7, loaded.BackupRetention);
        Assert.False(File.Exists(Path.Combine(
            root,
            "studio-preferences.json.new")));
    }

    [Fact]
    public void NativeWorkbenchRestoresDraftTargetAndCursorPreferences()
    {
        var service = new SkinStudioPreferencesService(root);
        var draftId = Guid.NewGuid();

        service.Save(new SkinStudioPreferences(
            LastDraftId: draftId,
            LastSelectedComponent: "  sliderb  ",
            SmoothCursorTrail: true,
            AutoMoveCursor: false,
            ShowOtherRulesetElements: true));

        var loaded = service.Load();
        Assert.Equal(draftId, loaded.LastDraftId);
        Assert.Equal("sliderb", loaded.LastSelectedComponent);
        Assert.True(loaded.SmoothCursorTrail);
        Assert.False(loaded.AutoMoveCursor);
        Assert.True(loaded.ShowOtherRulesetElements);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(201)]
    public void InvalidRetentionFailsClosed(int retention)
    {
        var service = new SkinStudioPreferencesService(root);

        Assert.Throws<InvalidDataException>(() =>
            service.Save(new SkinStudioPreferences(
                BackupRetention: retention)));
        Assert.False(File.Exists(Path.Combine(
            root,
            "studio-preferences.json")));
    }

    [Fact]
    public void UnsupportedSettingsVersionFailsClosedWithoutRewrite()
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "studio-preferences.json");
        File.WriteAllText(
            path,
            """
            {
              "format_version": 999,
              "automatic_edit_backups": false,
              "backup_retention": 4
            }
            """);
        var original = File.ReadAllBytes(path);

        Assert.Throws<InvalidDataException>(() =>
            new SkinStudioPreferencesService(root).Load());
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}
