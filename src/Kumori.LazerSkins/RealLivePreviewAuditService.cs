using System.Security.Cryptography;
using System.Text.Json;
using Kumori.Skins;

namespace Kumori.Tracking;

public sealed record RealLivePreviewAuditResult(
    string Verification,
    string BackupManifestPath,
    string RealmBackupPath,
    int SourceSkinCount,
    int ReferencedBlobCount,
    int CurrentSkinCount,
    Guid PreviewSkinId,
    string PreviewSkinName,
    int PreviewFileCount);

/// <summary>
/// Performs a read-only, independent verification of a real live-preview
/// backup, every backed-up source skin, and the mapped preview copy.
/// </summary>
public sealed class RealLivePreviewAuditService
{
    private readonly ILivePreviewStore store;

    public RealLivePreviewAuditService(ILivePreviewStore? store = null)
    {
        this.store = store ?? new LazerLivePreviewStore();
    }

    public RealLivePreviewAuditResult Verify(string draftManifestPath)
    {
        var normalizedDraftManifest = Path.GetFullPath(draftManifestPath);
        using var draft = readJson(normalizedDraftManifest, "draft manifest");
        var draftRoot = draft.RootElement;
        var previewId = requireGuid(draftRoot, "live_preview_skin_id");
        var expectedPreviewFingerprint =
            requireHash(draftRoot, "live_preview_fingerprint");
        var recordedBackupPath = Path.GetFullPath(
            requireString(draftRoot, "live_preview_backup_path"));
        var backupManifestPath = containedPath(
            recordedBackupPath,
            "manifest.json",
            "backup manifest");

        using var backup = readJson(backupManifestPath, "backup manifest");
        var backupRoot = backup.RootElement;
        if (!requireString(backupRoot, "verification")
                .Equals("passed", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The real-lazer backup manifest is not marked as verified.");
        }

        var playerRoot = Path.GetFullPath(
            requireString(backupRoot, "player_root"));
        var realmRelative = requireString(backupRoot, "realm_backup");
        var realmBackupPath = containedPath(
            recordedBackupPath,
            realmRelative,
            "Realm backup");
        requireFileHashAndSize(
            realmBackupPath,
            requireHash(backupRoot, "realm_sha256"),
            expectedSize: null,
            "Realm backup");

        var referencedBlobs = requireArray(backupRoot, "referenced_blobs");
        var referencedHashes = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var entry in referencedBlobs.EnumerateArray())
        {
            var hash = requireHash(entry, "hash");
            var size = requireNonNegativeInt64(entry, "size");
            if (!referencedHashes.Add(hash))
                throw new InvalidDataException(
                    $"The backup lists blob '{hash}' more than once.");
            var blobPath = containedPath(
                recordedBackupPath,
                Path.Combine("files", hash),
                "backup blob");
            requireFileHashAndSize(blobPath, hash, size, "backup blob");
        }

        var expectedSkins = requireArray(backupRoot, "skins")
            .EnumerateArray()
            .Select(parseExpectedSkin)
            .ToArray();
        if (expectedSkins.Select(skin => skin.Id).Distinct().Count()
            != expectedSkins.Length)
        {
            throw new InvalidDataException(
                "The backup skin catalog contains duplicate identifiers.");
        }

        var currentCatalog = store.LoadCatalog(playerRoot);
        var currentById = currentCatalog.ToDictionary(skin => skin.Id);
        foreach (var expected in expectedSkins)
        {
            if (!currentById.TryGetValue(expected.Id, out var current))
                throw new InvalidDataException(
                    $"Backed-up source skin {expected.Id} no longer exists.");
            if (!current.Name.Equals(expected.Name, StringComparison.Ordinal)
                || !current.Creator.Equals(
                    expected.Creator,
                    StringComparison.Ordinal)
                || !LivePreviewSyncService.Fingerprint(current).Equals(
                    expected.Fingerprint,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Backed-up source skin {expected.Id} changed.");
            }

            verifyFileCatalog(expected, current);
            foreach (var file in current.Files)
            {
                if (!referencedHashes.Contains(file.Hash))
                    throw new InvalidDataException(
                        $"Source skin {current.Id} references blob '{file.Hash}' "
                        + "which is absent from the verified backup.");
                var liveBytes = store.ReadBlob(playerRoot, file.Hash);
                if (liveBytes.LongLength != file.SizeBytes
                    || !SkinDraftWorkspaceService.Hash(liveBytes).Equals(
                        file.Hash,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"Live source blob '{file.Hash}' changed or is unreadable.");
                }
            }
        }

        if (!currentById.TryGetValue(previewId, out var preview))
            throw new InvalidDataException(
                "The mapped Kumori live-preview copy no longer exists.");
        if (!preview.Name.StartsWith(
                LivePreviewSyncService.PreviewPrefix,
                StringComparison.Ordinal)
            || !LivePreviewSyncService.Fingerprint(preview).Equals(
                expectedPreviewFingerprint,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The mapped Kumori live-preview copy changed outside Kumori.");
        }

        return new RealLivePreviewAuditResult(
            "passed",
            backupManifestPath,
            realmBackupPath,
            expectedSkins.Length,
            referencedHashes.Count,
            currentCatalog.Count,
            preview.Id,
            preview.Name,
            preview.Files.Count);
    }

    private static ExpectedSkin parseExpectedSkin(JsonElement element)
    {
        var files = requireArray(element, "files")
            .EnumerateArray()
            .Select(file => new LivePreviewFile(
                requireString(file, "filename"),
                requireHash(file, "hash"),
                requireNonNegativeInt64(file, "size")))
            .ToArray();
        return new ExpectedSkin(
            requireGuid(element, "id"),
            requireString(element, "name"),
            requireString(element, "creator"),
            requireHash(element, "fingerprint"),
            files);
    }

    private static void verifyFileCatalog(
        ExpectedSkin expected,
        LivePreviewSkin current)
    {
        static string key(LivePreviewFile file) =>
            $"{file.Filename.ToLowerInvariant()}\0"
            + $"{file.Hash.ToLowerInvariant()}\0{file.SizeBytes}";

        var expectedFiles = expected.Files
            .Select(key)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var currentFiles = current.Files
            .Select(key)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (!expectedFiles.SequenceEqual(currentFiles, StringComparer.Ordinal))
            throw new InvalidDataException(
                $"Backed-up source skin {expected.Id} has a changed file catalog.");
    }

    private static JsonDocument readJson(string path, string description)
    {
        try
        {
            return JsonDocument.Parse(File.ReadAllText(path));
        }
        catch (Exception ex) when (
            ex is IOException
            or UnauthorizedAccessException
            or JsonException)
        {
            throw new InvalidDataException(
                $"The {description} could not be read: {ex.Message}",
                ex);
        }
    }

    private static JsonElement requireArray(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"Required array '{name}' is missing.");
        return value;
    }

    private static string requireString(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidDataException($"Required value '{name}' is missing.");
        return value.GetString()!;
    }

    private static Guid requireGuid(JsonElement parent, string name)
    {
        var value = requireString(parent, name);
        if (!Guid.TryParse(value, out var parsed))
            throw new InvalidDataException($"Value '{name}' is not a GUID.");
        return parsed;
    }

    private static string requireHash(JsonElement parent, string name)
    {
        var value = requireString(parent, name);
        if (value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidDataException(
                $"Value '{name}' is not a SHA-256 hash.");
        return value.ToLowerInvariant();
    }

    private static long requireNonNegativeInt64(
        JsonElement parent,
        string name)
    {
        if (!parent.TryGetProperty(name, out var value)
            || !value.TryGetInt64(out var parsed)
            || parsed < 0)
            throw new InvalidDataException(
                $"Value '{name}' is not a non-negative integer.");
        return parsed;
    }

    private static string containedPath(
        string root,
        string relative,
        string description)
    {
        if (Path.IsPathRooted(relative))
            throw new InvalidDataException(
                $"The {description} path must be relative to its backup.");
        var normalizedRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(root));
        var path = Path.GetFullPath(relative, normalizedRoot);
        var prefix = normalizedRoot + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"The {description} path escapes its backup.");
        return path;
    }

    private static void requireFileHashAndSize(
        string path,
        string expectedHash,
        long? expectedSize,
        string description)
    {
        if (!File.Exists(path))
            throw new InvalidDataException($"{description} '{path}' is missing.");
        var info = new FileInfo(path);
        if (expectedSize is { } size && info.Length != size)
            throw new InvalidDataException(
                $"{description} '{path}' has an unexpected size.");
        using var stream = File.OpenRead(path);
        var actualHash = Convert.ToHexStringLower(SHA256.HashData(stream));
        if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"{description} '{path}' failed SHA-256 verification.");
    }

    private sealed record ExpectedSkin(
        Guid Id,
        string Name,
        string Creator,
        string Fingerprint,
        IReadOnlyList<LivePreviewFile> Files);
}
