using System.Text.Json;

namespace Kumori.Skins;

public sealed record SkinDraftBackup(
    string BackupId,
    Guid SourceDraftId,
    string Name,
    string Creator,
    long SourceRevision,
    DateTimeOffset CreatedAt,
    string ArchivePath,
    string ArchiveSha256,
    long ArchiveSize);

public sealed class SkinDraftBackupService
{
    private readonly SkinDraftWorkspaceService workspace;
    private readonly string backupRoot;

    public SkinDraftBackupService(SkinDraftWorkspaceService workspace)
    {
        this.workspace = workspace;
        backupRoot = Path.Combine(workspace.RootPath, "backups");
    }

    public SkinDraftBackup Create(
        Guid draftId,
        string reason,
        int retention = 30)
    {
        if (retention < 1)
            throw new ArgumentOutOfRangeException(nameof(retention));
        var draft = workspace.Load(draftId);
        var revision = draft.History[draft.HistoryIndex].Revision;
        var timestamp = DateTimeOffset.UtcNow;
        var backupId =
            $"{timestamp:yyyyMMdd-HHmmssfff}-{draftId:N}-r{revision}";
        Directory.CreateDirectory(backupRoot);
        var archivePath = containedPath(backupId + ".osk");
        new SkinPackageService(workspace).Export(draftId, archivePath);
        var backup = new SkinDraftBackup(
            backupId,
            draftId,
            draft.Name,
            draft.Creator,
            revision,
            timestamp,
            archivePath,
            SkinDraftWorkspaceService.Hash(File.ReadAllBytes(archivePath)),
            new FileInfo(archivePath).Length);
        writeManifest(backup, reason);
        prune(retention);
        return backup;
    }

    public IReadOnlyList<SkinDraftBackup> List()
    {
        if (!Directory.Exists(backupRoot))
            return [];
        return Directory.EnumerateFiles(backupRoot, "*.json")
            .Select(path => JsonSerializer.Deserialize<SkinDraftBackupEnvelope>(
                File.ReadAllText(path),
                SkinStudioLaunchContract.JsonOptions))
            .Where(envelope => envelope?.Backup is not null)
            .Select(envelope => envelope!.Backup)
            .OrderByDescending(backup => backup.CreatedAt)
            .ToArray();
    }

    public void Verify(SkinDraftBackup backup)
    {
        ArgumentNullException.ThrowIfNull(backup);
        var expectedArchive = containedPath(backup.BackupId + ".osk");
        if (!Path.GetFullPath(backup.ArchivePath).Equals(
                expectedArchive,
                StringComparison.OrdinalIgnoreCase)
            || !File.Exists(expectedArchive))
        {
            throw new InvalidDataException("Draft backup archive is missing or outside its workspace.");
        }
        var bytes = File.ReadAllBytes(expectedArchive);
        if (bytes.LongLength != backup.ArchiveSize
            || !SkinDraftWorkspaceService.Hash(bytes).Equals(
                backup.ArchiveSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Draft backup archive hash verification failed.");
        }
        SkinPackageService.ValidatePackage(expectedArchive);
    }

    public SkinDraftManifest RestoreAsNewDraft(SkinDraftBackup backup)
    {
        Verify(backup);
        return workspace.Create(
            $"{backup.Name} (restored)",
            backup.Creator,
            backup.ArchivePath,
            SkinPackageService.Fingerprint(backup.ArchivePath),
            trackOrigin: false);
    }

    private void writeManifest(SkinDraftBackup backup, string reason)
    {
        var path = containedPath(backup.BackupId + ".json");
        var temporary = path + ".new";
        try
        {
            File.WriteAllText(
                temporary,
                JsonSerializer.Serialize(
                    new SkinDraftBackupEnvelope(
                        backup,
                        string.IsNullOrWhiteSpace(reason) ? "Manual backup" : reason.Trim()),
                    SkinStudioLaunchContract.JsonOptions));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    private void prune(int retention)
    {
        foreach (var backup in List().Skip(retention))
        {
            var archive = containedPath(backup.BackupId + ".osk");
            var manifest = containedPath(backup.BackupId + ".json");
            try { if (File.Exists(archive)) File.Delete(archive); } catch { }
            try { if (File.Exists(manifest)) File.Delete(manifest); } catch { }
        }
    }

    private string containedPath(string filename)
    {
        if (!Path.GetFileName(filename).Equals(filename, StringComparison.Ordinal))
            throw new InvalidDataException("Unsafe draft backup identifier.");
        var root = Path.GetFullPath(backupRoot);
        var candidate = Path.GetFullPath(Path.Combine(root, filename));
        var prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Draft backup path escaped its workspace.");
        return candidate;
    }

    private sealed record SkinDraftBackupEnvelope(
        SkinDraftBackup Backup,
        string Reason);
}
