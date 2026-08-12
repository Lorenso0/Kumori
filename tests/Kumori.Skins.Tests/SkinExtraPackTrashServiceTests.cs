using Kumori.Skins;
using Xunit;

namespace Kumori.Skins.Tests;

public sealed class SkinExtraPackTrashServiceTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        $"kumori-extra-trash-{Guid.NewGuid():N}");

    [Fact]
    public void Pack_delete_is_recoverable_and_restore_is_contained()
    {
        var packDirectory = Path.Combine(root, "osu!", "Cursor", "Pack");
        Directory.CreateDirectory(packDirectory);
        File.WriteAllBytes(Path.Combine(packDirectory, "cursor.png"), [1]);
        var pack = new SkinExtraPackDescriptor(
            packDirectory,
            new SkinExtraPackManifest
            {
                Id = "pack",
                DisplayName = "Pack",
                FamilyId = "osu.cursor",
                Area = "osu!",
                FamilyName = "Cursor",
                Fingerprint = new string('a', 64),
            },
            false);
        var service = new SkinExtraPackTrashService();

        var deleted = service.DeleteRecoverably(root, pack);

        Assert.False(Directory.Exists(packDirectory));
        Assert.Single(service.List(root));
        Assert.Throws<InvalidDataException>(() => service.Restore(root, "../escape"));

        var restored = service.Restore(root, deleted.TrashId);
        Assert.Equal(Path.GetFullPath(packDirectory), restored);
        Assert.True(File.Exists(Path.Combine(restored, "cursor.png")));
        Assert.Empty(service.List(root));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
    }
}
