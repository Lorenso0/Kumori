using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Kumori.Skins;

public sealed record LivePreviewFile(string Filename, string Hash, long SizeBytes);

public sealed record LivePreviewSkin(
    Guid Id,
    string Name,
    string Creator,
    IReadOnlyList<LivePreviewFile> Files);

public sealed record LivePreviewMutation(
    string Filename,
    byte[] Bytes,
    string? ExpectedHash,
    bool IsDeletion = false);

public sealed record LivePreviewApplyResult(
    bool Succeeded,
    string? Message = null);

public sealed record PlayerIdleState(bool IsProvenIdle, string Detail);

public interface IPlayerIdleProbe
{
    PlayerIdleState Probe(string playerRoot);
}

public interface ILivePreviewStore
{
    IReadOnlyList<LivePreviewSkin> LoadCatalog(string playerRoot);

    LivePreviewSkin Import(
        string playerRoot,
        string name,
        string creator,
        IReadOnlyDictionary<string, byte[]> files);

    LivePreviewApplyResult Apply(
        string playerRoot,
        Guid skinId,
        IReadOnlyList<LivePreviewMutation> mutations);

    byte[] ReadBlob(string playerRoot, string hash);

    string CreateRealmBackup(string playerRoot, string destinationDirectory);
}

public sealed record LivePreviewSyncResult(
    Guid SkinId,
    string SkinName,
    string BackupPath,
    int ChangedFiles,
    bool Created);

/// <summary>
/// Synchronises only a disposable, Kumori-owned preview skin. Every first write
/// requires a verified Realm + referenced-blob backup. Callers may explicitly
/// permit Realm's transactional writes while the player is running for live edit.
/// </summary>
public sealed class LivePreviewSyncService
{
    public const string PreviewPrefix = "Kumori Live Preview — ";

    private readonly SkinDraftWorkspaceService workspace;
    private readonly SkinPackageService packages;
    private readonly ILivePreviewStore store;
    private readonly IPlayerIdleProbe idleProbe;
    private readonly string backupRoot;

    public LivePreviewSyncService(
        SkinDraftWorkspaceService workspace,
        ILivePreviewStore store,
        IPlayerIdleProbe idleProbe,
        string backupRoot)
    {
        this.workspace = workspace;
        packages = new SkinPackageService(workspace);
        this.store = store;
        this.idleProbe = idleProbe;
        this.backupRoot = Path.GetFullPath(backupRoot);
    }

    public LivePreviewSyncResult Sync(
        Guid draftId,
        string playerRoot,
        bool liveSyncPermission,
        bool allowWhilePlayerRunning = false)
    {
        if (!liveSyncPermission)
            throw new InvalidOperationException("Live preview permission is disabled.");

        var normalizedRoot = Path.GetFullPath(playerRoot);
        ensureSeparated(workspace.RootPath, normalizedRoot);
        if (!allowWhilePlayerRunning)
        {
            var idle = idleProbe.Probe(normalizedRoot);
            if (!idle.IsProvenIdle)
                throw new InvalidOperationException($"Live preview is blocked: {idle.Detail}");
        }

        var draft = workspace.Load(draftId);
        var catalog = store.LoadCatalog(normalizedRoot);
        var targetName = PreviewPrefix + draft.Name;
        LivePreviewSkin? target = null;
        if (draft.LivePreviewSkinId is { } mapped)
        {
            target = catalog.SingleOrDefault(skin => skin.Id == mapped)
                     ?? throw new InvalidOperationException(
                         "The mapped live-preview skin no longer exists. Sync stopped without writing.");
            if (!target.Name.Equals(targetName, StringComparison.Ordinal)
                || !target.Name.StartsWith(PreviewPrefix, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The mapped skin is no longer Kumori's disposable preview copy.");
            }
            var currentFingerprint = Fingerprint(target);
            if (!string.Equals(
                    currentFingerprint,
                    draft.LivePreviewFingerprint,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "The preview copy changed outside Kumori. Sync stopped without writing.");
            }
        }
        else if (catalog.Any(skin =>
                     skin.Name.Equals(targetName, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "An unmapped skin already uses this Kumori live-preview name. "
                + "Sync stopped without assuming ownership.");
        }

        var backupPath = draft.LivePreviewBackupPath;
        if (string.IsNullOrWhiteSpace(backupPath))
        {
            backupPath = createVerifiedBackup(normalizedRoot, catalog);
            draft = workspace.SetLivePreviewState(
                draftId,
                draft.LivePreviewSkinId,
                draft.LivePreviewFingerprint,
                backupPath);
        }

        var materialized = packages.Materialize(draftId);
        if (target is null)
        {
            var created = store.Import(
                normalizedRoot,
                targetName,
                string.IsNullOrWhiteSpace(draft.Creator) ? "Kumori" : draft.Creator,
                materialized);
            requireOwnedTarget(created, targetName);
            var refreshedCatalog = store.LoadCatalog(normalizedRoot);
            verifyUnchangedSources(catalog, refreshedCatalog, allowedMutation: null);
            var refreshedCreated = refreshedCatalog.SingleOrDefault(skin =>
                                       skin.Id == created.Id)
                                   ?? throw new InvalidOperationException(
                                       "The imported preview copy was not found during verification.");
            requireOwnedTarget(refreshedCreated, targetName);
            var createdFingerprint = Fingerprint(refreshedCreated);
            workspace.SetLivePreviewState(
                draftId,
                refreshedCreated.Id,
                createdFingerprint,
                backupPath);
            return new LivePreviewSyncResult(
                refreshedCreated.Id,
                refreshedCreated.Name,
                backupPath,
                materialized.Count,
                true);
        }

        var desired = materialized.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.OrdinalIgnoreCase);
        var current = target.Files.ToDictionary(
            file => file.Filename,
            StringComparer.OrdinalIgnoreCase);
        var mutations = new List<LivePreviewMutation>();
        foreach (var (filename, bytes) in desired)
        {
            var hash = SkinDraftWorkspaceService.Hash(bytes);
            current.TryGetValue(filename, out var existing);
            if (existing?.Hash.Equals(hash, StringComparison.OrdinalIgnoreCase) == true)
                continue;
            mutations.Add(new LivePreviewMutation(
                filename,
                bytes,
                existing?.Hash));
        }
        foreach (var existing in current.Values.Where(file => !desired.ContainsKey(file.Filename)))
        {
            mutations.Add(new LivePreviewMutation(
                existing.Filename,
                [],
                existing.Hash,
                IsDeletion: true));
        }

        if (mutations.Count > 0)
        {
            var applied = store.Apply(normalizedRoot, target.Id, mutations);
            if (!applied.Succeeded)
                throw new InvalidOperationException(
                    applied.Message ?? "The live-preview transaction was rolled back.");
        }

        var refreshedCatalogAfterApply = store.LoadCatalog(normalizedRoot);
        verifyUnchangedSources(
            catalog,
            refreshedCatalogAfterApply,
            target.Id);
        var refreshed = refreshedCatalogAfterApply
                             .SingleOrDefault(skin => skin.Id == target.Id)
                        ?? throw new InvalidOperationException(
                            "The preview copy was not found after the transaction.");
        requireOwnedTarget(refreshed, targetName);
        workspace.SetLivePreviewState(
            draftId,
            refreshed.Id,
            Fingerprint(refreshed),
            backupPath);
        return new LivePreviewSyncResult(
            refreshed.Id,
            refreshed.Name,
            backupPath,
            mutations.Count,
            false);
    }

    private string createVerifiedBackup(
        string playerRoot,
        IReadOnlyList<LivePreviewSkin> catalog)
    {
        var directory = Path.Combine(
            backupRoot,
            $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fff}-before-live-preview");
        Directory.CreateDirectory(directory);
        var realmBackup = store.CreateRealmBackup(
            playerRoot,
            Path.Combine(directory, "realm"));
        var entries = new List<object>();
        foreach (var file in catalog.SelectMany(skin => skin.Files)
                                    .DistinctBy(file => file.Hash, StringComparer.OrdinalIgnoreCase)
                                    .OrderBy(file => file.Hash, StringComparer.OrdinalIgnoreCase))
        {
            var bytes = store.ReadBlob(playerRoot, file.Hash);
            var actual = SkinDraftWorkspaceService.Hash(bytes);
            if (!actual.Equals(file.Hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Player blob '{file.Hash}' failed SHA-256 validation.");
            var destination = Path.Combine(directory, "files", file.Hash);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.WriteAllBytes(destination, bytes);
            entries.Add(new { hash = file.Hash, size = bytes.LongLength });
        }

        var realmHash = hashFile(realmBackup);
        var manifestPath = Path.Combine(directory, "manifest.json");
        File.WriteAllText(
            manifestPath,
            JsonSerializer.Serialize(
                new
                {
                    format = 1,
                    created_at = DateTimeOffset.UtcNow,
                    player_root = playerRoot,
                    realm_backup = Path.GetRelativePath(directory, realmBackup).Replace('\\', '/'),
                    realm_sha256 = realmHash,
                    skins = catalog
                        .OrderBy(skin => skin.Id)
                        .Select(skin => new
                        {
                            id = skin.Id,
                            name = skin.Name,
                            creator = skin.Creator,
                            fingerprint = Fingerprint(skin),
                            files = skin.Files
                                .OrderBy(
                                    file => file.Filename,
                                    StringComparer.OrdinalIgnoreCase)
                                .Select(file => new
                                {
                                    filename = file.Filename,
                                    hash = file.Hash,
                                    size = file.SizeBytes,
                                }),
                        }),
                    referenced_blobs = entries,
                    verification = "passed",
                },
                SkinStudioLaunchContract.JsonOptions));

        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        if (manifest.RootElement.GetProperty("verification").GetString() != "passed"
            || !File.Exists(realmBackup)
            || hashFile(realmBackup) != realmHash)
        {
            throw new InvalidDataException("The live-preview backup could not be verified.");
        }
        return directory;
    }

    public static string Fingerprint(LivePreviewSkin skin)
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in skin.Files.OrderBy(file => file.Filename, StringComparer.OrdinalIgnoreCase))
        {
            digest.AppendData(Encoding.UTF8.GetBytes(file.Filename.ToLowerInvariant()));
            digest.AppendData([0]);
            digest.AppendData(Encoding.ASCII.GetBytes(file.Hash.ToLowerInvariant()));
            digest.AppendData([0]);
        }
        return Convert.ToHexStringLower(digest.GetHashAndReset());
    }

    private static void verifyUnchangedSources(
        IReadOnlyList<LivePreviewSkin> before,
        IReadOnlyList<LivePreviewSkin> after,
        Guid? allowedMutation)
    {
        var afterById = after.ToDictionary(skin => skin.Id);
        foreach (var source in before)
        {
            if (source.Id == allowedMutation)
                continue;
            if (!afterById.TryGetValue(source.Id, out var current))
            {
                throw new InvalidOperationException(
                    $"Source skin {source.Id} disappeared during live-preview verification.");
            }
            if (!source.Name.Equals(current.Name, StringComparison.Ordinal)
                || !source.Creator.Equals(current.Creator, StringComparison.Ordinal)
                || !Fingerprint(source).Equals(
                    Fingerprint(current),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Source skin {source.Id} changed during live-preview verification.");
            }
        }
    }

    private static string hashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static void requireOwnedTarget(LivePreviewSkin skin, string expectedName)
    {
        if (!skin.Name.Equals(expectedName, StringComparison.Ordinal)
            || !skin.Name.StartsWith(PreviewPrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The player returned a non-Kumori preview target.");
        }
    }

    private static void ensureSeparated(string workspaceRoot, string playerRoot)
    {
        var workspace = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot))
                        + Path.DirectorySeparatorChar;
        var player = Path.TrimEndingDirectorySeparator(Path.GetFullPath(playerRoot))
                     + Path.DirectorySeparatorChar;
        if (workspace.StartsWith(player, StringComparison.OrdinalIgnoreCase)
            || player.StartsWith(workspace, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The isolated workspace and osu!lazer root must not overlap.");
        }
    }
}
