using System.Security.Cryptography;
using System.Text.Json;
using Kumori.Skins;

namespace Kumori.Tracking;

public sealed record VerifiedLazerCatalogBackup(
    string DirectoryPath,
    string ManifestPath,
    string RealmBackupPath,
    int SkinCount,
    int ReferencedBlobCount);

public sealed class LazerCatalogBackupService
{
    private readonly ILivePreviewStore store;

    public LazerCatalogBackupService(ILivePreviewStore? store = null)
    {
        this.store = store ?? new LazerLivePreviewStore();
    }

    public VerifiedLazerCatalogBackup CreateVerified(
        string playerRoot,
        string backupRoot,
        string purpose)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playerRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(backupRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        var normalizedPlayerRoot = Path.GetFullPath(playerRoot);
        var normalizedBackupRoot = Path.GetFullPath(backupRoot);
        if (!SkinStudioWriteBoundary.IsNormalWriteAllowed(
                normalizedPlayerRoot,
                normalizedBackupRoot))
        {
            throw new InvalidOperationException(
                "The lazer backup destination must be outside the player root.");
        }

        var catalog = store.LoadCatalog(normalizedPlayerRoot);
        var directory = Path.Combine(
            normalizedBackupRoot,
            $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fff}-{safePurpose(purpose)}");
        Directory.CreateDirectory(directory);
        var realmBackup = store.CreateRealmBackup(
            normalizedPlayerRoot,
            Path.Combine(directory, "realm"));
        var realmHash = hashFile(realmBackup);
        var blobEntries = new List<object>();
        foreach (var file in catalog
                     .SelectMany(skin => skin.Files)
                     .DistinctBy(file => file.Hash, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(file => file.Hash, StringComparer.OrdinalIgnoreCase))
        {
            var bytes = store.ReadBlob(normalizedPlayerRoot, file.Hash);
            var actualHash = SkinDraftWorkspaceService.Hash(bytes);
            if (!actualHash.Equals(file.Hash, StringComparison.OrdinalIgnoreCase)
                || bytes.LongLength != file.SizeBytes)
            {
                throw new InvalidDataException(
                    $"Player blob '{file.Hash}' failed backup verification.");
            }
            var destination = Path.Combine(directory, "files", file.Hash);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.WriteAllBytes(destination, bytes);
            blobEntries.Add(new
            {
                hash = file.Hash.ToLowerInvariant(),
                size = bytes.LongLength,
            });
        }

        var manifestPath = Path.Combine(directory, "manifest.json");
        File.WriteAllText(
            manifestPath,
            JsonSerializer.Serialize(
                new
                {
                    format = 1,
                    created_at = DateTimeOffset.UtcNow,
                    purpose,
                    player_root = normalizedPlayerRoot,
                    realm_backup = Path.GetRelativePath(
                            directory,
                            realmBackup)
                        .Replace('\\', '/'),
                    realm_sha256 = realmHash,
                    skins = catalog
                        .OrderBy(skin => skin.Id)
                        .Select(skin => new
                        {
                            id = skin.Id,
                            name = skin.Name,
                            creator = skin.Creator,
                            fingerprint =
                                LivePreviewSyncService.Fingerprint(skin),
                            files = skin.Files
                                .OrderBy(
                                    file => file.Filename,
                                    StringComparer.OrdinalIgnoreCase)
                                .Select(file => new
                                {
                                    filename = file.Filename,
                                    hash = file.Hash.ToLowerInvariant(),
                                    size = file.SizeBytes,
                                }),
                        }),
                    referenced_blobs = blobEntries,
                    verification = "passed",
                },
                SkinStudioLaunchContract.JsonOptions));

        using var manifest = JsonDocument.Parse(
            File.ReadAllText(manifestPath));
        if (manifest.RootElement.GetProperty("verification").GetString()
                != "passed"
            || !File.Exists(realmBackup)
            || !hashFile(realmBackup).Equals(
                realmHash,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The lazer catalog backup could not be reopened and verified.");
        }
        return new VerifiedLazerCatalogBackup(
            directory,
            manifestPath,
            realmBackup,
            catalog.Count,
            blobEntries.Count);
    }

    private static string safePurpose(string purpose)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var safe = new string(purpose
            .Trim()
            .ToLowerInvariant()
            .Select(character =>
                invalid.Contains(character) || char.IsWhiteSpace(character)
                    ? '-'
                    : character)
            .ToArray());
        return safe.Trim('-');
    }

    private static string hashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
