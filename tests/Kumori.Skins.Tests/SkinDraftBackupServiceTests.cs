using Kumori.Skins;
using Xunit;

namespace Kumori.Skins.Tests;

public sealed class SkinDraftBackupServiceTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        $"kumori-skin-backup-{Guid.NewGuid():N}");

    [Fact]
    public void Backup_is_hashed_verified_and_restored_as_an_independent_draft()
    {
        var workspace = new SkinDraftWorkspaceService(root);
        var original = workspace.Create("Original", "Creator");
        workspace.StageFile(original.DraftId, "cursor.png", [1, 2, 3], null, "cursor");
        var service = new SkinDraftBackupService(workspace);

        var backup = service.Create(original.DraftId, "Manual");
        service.Verify(backup);
        var restored = service.RestoreAsNewDraft(backup);

        Assert.NotEqual(original.DraftId, restored.DraftId);
        Assert.Null(restored.OriginPath);
        Assert.Equal(
            [1, 2, 3],
            new SkinPackageService(workspace).Materialize(restored.DraftId)["cursor.png"]);
    }

    [Fact]
    public void Tampered_backup_fails_verification()
    {
        var workspace = new SkinDraftWorkspaceService(root);
        var draft = workspace.Create("Original", "Creator");
        var backup = new SkinDraftBackupService(workspace).Create(draft.DraftId, "Manual");
        File.WriteAllBytes(
            backup.ArchivePath,
            [.. File.ReadAllBytes(backup.ArchivePath), 0]);

        Assert.Throws<InvalidDataException>(
            () => new SkinDraftBackupService(workspace).Verify(backup));
    }

    [Fact]
    public void Retention_prunes_only_complete_older_backup_pairs()
    {
        var workspace = new SkinDraftWorkspaceService(root);
        var draft = workspace.Create("Original", "Creator");
        var service = new SkinDraftBackupService(workspace);
        for (var revision = 0; revision < 4; revision++)
        {
            workspace.StageFile(
                draft.DraftId,
                $"asset-{revision}.png",
                [(byte)revision],
                null,
                $"revision {revision}");
            service.Create(draft.DraftId, $"backup {revision}", retention: 2);
        }

        var retained = service.List();

        Assert.Equal(2, retained.Count);
        Assert.All(retained, backup =>
        {
            Assert.True(File.Exists(backup.ArchivePath));
            Assert.True(File.Exists(Path.ChangeExtension(
                backup.ArchivePath,
                ".json")));
            service.Verify(backup);
        });
    }

    [Fact]
    public void Interrupted_temporary_backup_is_ignored_without_pruning_valid_backup()
    {
        var workspace = new SkinDraftWorkspaceService(root);
        var draft = workspace.Create("Original", "Creator");
        var service = new SkinDraftBackupService(workspace);
        var valid = service.Create(draft.DraftId, "valid", retention: 1);
        var backupRoot = Path.GetDirectoryName(valid.ArchivePath)!;
        File.WriteAllText(
            Path.Combine(backupRoot, "interrupted.json.new"),
            "{not-json");
        File.WriteAllBytes(
            Path.Combine(backupRoot, "interrupted.osk.new"),
            [1, 2, 3]);

        var retained = Assert.Single(service.List());

        Assert.Equal(valid.BackupId, retained.BackupId);
        service.Verify(retained);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
    }
}
