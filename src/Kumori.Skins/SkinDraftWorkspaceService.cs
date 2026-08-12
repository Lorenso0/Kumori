using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO.Compression;

namespace Kumori.Skins;

public enum SkinDraftChangeKind
{
    Upsert,
    Delete,
}

public enum SkinDraftSourceState
{
    None,
    Unchanged,
    Missing,
    Changed,
}

public sealed record SkinDraftSourceCheck(
    SkinDraftSourceState State,
    string? RecordedFingerprint,
    string? CurrentFingerprint);

public sealed record DeletedSkinDraft(
    string TrashName,
    Guid DraftId,
    string Name,
    DateTimeOffset DeletedAt);

public sealed record SkinDraftRecoveryCandidate(
    string DirectoryName,
    Guid? DraftId,
    bool ManifestValid,
    bool PendingManifestValid,
    string Detail);

public sealed record SkinDraftFileChange(
    string Filename,
    SkinDraftChangeKind Kind,
    string? ExpectedHash,
    string? ContentHash,
    long SizeBytes,
    string Description);

public sealed record SkinDraftFileMutation(
    string Filename,
    SkinDraftChangeKind Kind,
    byte[]? Contents,
    string? ExpectedHash,
    string Description);

public sealed record SkinDraftRevision(
    long Revision,
    DateTimeOffset CreatedAt,
    string Description,
    IReadOnlyList<SkinDraftFileChange> Changes);

public sealed record SkinDraftManifest
{
    public const int CurrentVersion = 1;

    [JsonPropertyName("format")]
    public int Format { get; init; } = CurrentVersion;

    [JsonPropertyName("draft_id")]
    public Guid DraftId { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("creator")]
    public string Creator { get; init; } = "";

    [JsonPropertyName("source_path")]
    public string? SourcePath { get; init; }

    [JsonPropertyName("origin_path")]
    public string? OriginPath { get; init; }

    [JsonPropertyName("source_fingerprint")]
    public string? SourceFingerprint { get; init; }

    [JsonPropertyName("source_lazer_skin_id")]
    public Guid? SourceLazerSkinId { get; init; }

    [JsonPropertyName("lazer_revision")]
    public string LazerRevision { get; init; } = "2026.726.0-lazer";

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset UpdatedAt { get; init; }

    [JsonPropertyName("history_index")]
    public int HistoryIndex { get; init; }

    [JsonPropertyName("history")]
    public IReadOnlyList<SkinDraftRevision> History { get; init; } = [];

    [JsonPropertyName("live_preview_skin_id")]
    public Guid? LivePreviewSkinId { get; init; }

    [JsonPropertyName("live_preview_fingerprint")]
    public string? LivePreviewFingerprint { get; init; }

    [JsonPropertyName("live_preview_backup_path")]
    public string? LivePreviewBackupPath { get; init; }

    [JsonIgnore]
    public IReadOnlyList<SkinDraftFileChange> Changes =>
        History.Count == 0 ? [] : History[HistoryIndex].Changes;

    [JsonIgnore]
    public bool CanUndo => HistoryIndex > 0;

    [JsonIgnore]
    public bool CanRedo => HistoryIndex + 1 < History.Count;
}

public sealed class SkinDraftWorkspaceService
{
    private readonly string root;

    public SkinDraftWorkspaceService(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        root = Path.GetFullPath(workspaceRoot);
    }

    public string RootPath => root;

    public SkinDraftManifest Create(
        string name,
        string creator,
        string? sourcePath = null,
        string? sourceFingerprint = null,
        bool trackOrigin = true,
        Guid? sourceLazerSkinId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Directory.CreateDirectory(root);
        var now = DateTimeOffset.UtcNow;
        var draftId = Guid.NewGuid();
        var inputSource = string.IsNullOrWhiteSpace(sourcePath)
            ? null
            : Path.GetFullPath(sourcePath);
        var originPath = trackOrigin ? inputSource : null;
        var sourceSnapshot = inputSource is null
            ? null
            : SnapshotSource(draftId, inputSource);
        var manifest = new SkinDraftManifest
        {
            DraftId = draftId,
            Name = name.Trim(),
            Creator = creator.Trim(),
            SourcePath = sourceSnapshot,
            OriginPath = originPath,
            SourceFingerprint = sourceFingerprint
                                ?? (inputSource is null
                                    ? null
                                    : SkinPackageService.Fingerprint(inputSource)),
            SourceLazerSkinId = sourceLazerSkinId,
            CreatedAt = now,
            UpdatedAt = now,
            History =
            [
                new SkinDraftRevision(0, now, "Draft created", []),
            ],
            HistoryIndex = 0,
        };
        Save(manifest);
        return manifest;
    }

    public IReadOnlyList<SkinDraftManifest> List()
    {
        var drafts = DraftsDirectory;
        if (!Directory.Exists(drafts))
            return [];

        return Directory.EnumerateDirectories(drafts)
            .Select(path => tryLoadManifest(Path.Combine(path, "manifest.json")))
            .Where(manifest => manifest is not null)
            .Select(manifest => manifest!)
            .OrderByDescending(manifest => manifest.UpdatedAt)
            .ToArray();
    }

    public IReadOnlyList<SkinDraftRecoveryCandidate> ListRecoveryCandidates()
    {
        if (!Directory.Exists(DraftsDirectory))
            return [];
        var candidates = new List<SkinDraftRecoveryCandidate>();
        foreach (var directory in Directory.EnumerateDirectories(DraftsDirectory))
        {
            var directoryName = Path.GetFileName(directory);
            var manifestPath = Path.Combine(directory, "manifest.json");
            var pendingPath = manifestPath + ".new";
            var manifest = tryLoadManifest(manifestPath);
            var pending = tryLoadManifest(pendingPath);
            if (manifest is not null && pending is null)
                continue;
            var parsedDirectoryId = Guid.TryParseExact(
                directoryName,
                "N",
                out var directoryId)
                ? directoryId
                : (Guid?)null;
            var draftId = pending?.DraftId ?? manifest?.DraftId ?? parsedDirectoryId;
            var detail = pending is not null
                ? manifest is null
                    ? "The committed manifest is missing or invalid; a valid interrupted-save manifest is recoverable."
                    : "A valid interrupted-save manifest is newer than the committed manifest."
                : "The draft manifest is missing or invalid and no valid interrupted-save manifest is available.";
            candidates.Add(new SkinDraftRecoveryCandidate(
                directoryName,
                draftId,
                manifest is not null,
                pending is not null,
                detail));
        }
        return candidates
            .OrderByDescending(candidate => candidate.PendingManifestValid)
            .ThenBy(candidate => candidate.DirectoryName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public SkinDraftManifest RecoverPendingManifest(string directoryName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryName);
        if (!string.Equals(
                Path.GetFileName(directoryName),
                directoryName,
                StringComparison.Ordinal)
            || !Guid.TryParseExact(directoryName, "N", out var directoryId))
        {
            throw new InvalidDataException("Draft recovery identifier is unsafe.");
        }
        var directory = ContainedPath(DraftsDirectory, directoryName);
        var manifestPath = ContainedPath(directory, "manifest.json");
        var pendingPath = ContainedPath(directory, "manifest.json.new");
        var pending = tryLoadManifest(pendingPath)
                      ?? throw new InvalidDataException(
                          "No valid interrupted-save manifest is available.");
        if (pending.DraftId != directoryId)
        {
            throw new InvalidDataException(
                "The interrupted-save manifest does not match its draft directory.");
        }
        var recoveryDirectory = ContainedPath(directory, "recovery-backups");
        Directory.CreateDirectory(recoveryDirectory);
        if (File.Exists(manifestPath))
        {
            File.Copy(
                manifestPath,
                ContainedPath(
                    recoveryDirectory,
                    $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fff}-manifest.json"),
                overwrite: false);
        }
        File.Move(pendingPath, manifestPath, overwrite: true);
        return LoadManifest(manifestPath);
    }

    public SkinDraftManifest Load(Guid draftId) =>
        LoadManifest(ManifestPath(draftId));

    public SkinDraftManifest Rename(
        Guid draftId,
        string name,
        string creator)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var manifest = Load(draftId);
        return Save(manifest with
        {
            Name = name.Trim(),
            Creator = creator.Trim(),
            UpdatedAt = DateTimeOffset.UtcNow,
        });
    }

    public SkinDraftManifest UpdateIdentity(
        Guid draftId,
        string name,
        string creator)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var normalizedName = name.Trim();
        var normalizedCreator = creator.Trim();
        var manifest = Load(draftId);
        var files = new SkinPackageService(this).Materialize(draftId);
        var current = files["skin.ini"];
        var document = SkinIniDocument.Parse(current);
        document.SetValue("General", "Name", normalizedName);
        document.SetValue("General", "Author", normalizedCreator);
        var bytes = document.ToBytes();
        var contentHash = Hash(bytes);
        WriteObject(draftId, contentHash, bytes);
        var changes = manifest.Changes.ToDictionary(
            change => change.Filename,
            StringComparer.OrdinalIgnoreCase);
        changes["skin.ini"] = new SkinDraftFileChange(
            "skin.ini",
            SkinDraftChangeKind.Upsert,
            Hash(current),
            contentHash,
            bytes.LongLength,
            "Update skin identity");
        return AppendRevision(
            manifest,
            changes.Values,
            "Update skin identity",
            normalizedName,
            normalizedCreator);
    }

    public SkinDraftManifest Duplicate(
        Guid draftId,
        string? name = null)
    {
        var source = Load(draftId);
        Directory.CreateDirectory(root);
        var temporaryDirectory = Path.Combine(root, "temporary");
        Directory.CreateDirectory(temporaryDirectory);
        var temporaryPackage = Path.Combine(
            temporaryDirectory,
            $"duplicate-{Guid.NewGuid():N}.osk");
        try
        {
            new SkinPackageService(this).Export(draftId, temporaryPackage);
            var now = DateTimeOffset.UtcNow;
            var duplicateId = Guid.NewGuid();
            var duplicateDirectory = DraftDirectory(duplicateId);
            Directory.CreateDirectory(duplicateDirectory);
            var snapshot = ContainedPath(duplicateDirectory, "source.osk");
            File.Copy(temporaryPackage, snapshot, overwrite: false);
            var manifest = new SkinDraftManifest
            {
                DraftId = duplicateId,
                Name = string.IsNullOrWhiteSpace(name)
                    ? $"{source.Name} copy"
                    : name.Trim(),
                Creator = source.Creator,
                SourcePath = snapshot,
                OriginPath = null,
                SourceFingerprint = SkinPackageService.Fingerprint(snapshot),
                SourceLazerSkinId = source.SourceLazerSkinId,
                LazerRevision = source.LazerRevision,
                CreatedAt = now,
                UpdatedAt = now,
                History =
                [
                    new SkinDraftRevision(0, now, $"Duplicated from {source.Name}", []),
                ],
                HistoryIndex = 0,
            };
            return Save(manifest);
        }
        finally
        {
            try { if (File.Exists(temporaryPackage)) File.Delete(temporaryPackage); } catch { }
        }
    }

    public SkinDraftManifest DiscardAll(Guid draftId)
    {
        var manifest = Load(draftId);
        if (manifest.Changes.Count == 0)
            return manifest;
        return AppendRevision(manifest, [], "Discard all staged changes");
    }

    public DeletedSkinDraft DeleteRecoverably(Guid draftId)
    {
        var manifest = Load(draftId);
        Directory.CreateDirectory(TrashDirectory);
        var deletedAt = DateTimeOffset.UtcNow;
        var trashName =
            $"{deletedAt:yyyyMMdd-HHmmss}-{draftId:N}-{Guid.NewGuid():N}";
        var destination = ContainedPath(TrashDirectory, trashName);
        Directory.Move(DraftDirectory(draftId), destination);
        return new DeletedSkinDraft(
            trashName,
            draftId,
            manifest.Name,
            deletedAt);
    }

    public IReadOnlyList<DeletedSkinDraft> ListDeleted()
    {
        if (!Directory.Exists(TrashDirectory))
            return [];
        return Directory.EnumerateDirectories(TrashDirectory)
            .Select(directory =>
            {
                var manifest = LoadManifest(Path.Combine(directory, "manifest.json"));
                var deleted = Directory.GetLastWriteTimeUtc(directory);
                return new DeletedSkinDraft(
                    Path.GetFileName(directory),
                    manifest.DraftId,
                    manifest.Name,
                    new DateTimeOffset(deleted, TimeSpan.Zero));
            })
            .OrderByDescending(item => item.DeletedAt)
            .ToArray();
    }

    public SkinDraftManifest RestoreDeleted(string trashName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trashName);
        if (!string.Equals(
                Path.GetFileName(trashName),
                trashName,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("Deleted draft identifier is unsafe.");
        }
        var source = ContainedPath(TrashDirectory, trashName);
        if (!Directory.Exists(source))
            throw new DirectoryNotFoundException("The deleted draft was not found.");
        var manifest = LoadManifest(Path.Combine(source, "manifest.json"));
        var destination = DraftDirectory(manifest.DraftId);
        if (Directory.Exists(destination))
            throw new IOException("A draft with the same identity already exists.");
        Directory.CreateDirectory(DraftsDirectory);
        Directory.Move(source, destination);
        return Load(manifest.DraftId);
    }

    public SkinDraftSourceCheck CheckSource(Guid draftId)
    {
        var manifest = Load(draftId);
        var source = manifest.OriginPath ?? manifest.SourcePath;
        if (string.IsNullOrWhiteSpace(source))
            return new SkinDraftSourceCheck(SkinDraftSourceState.None, null, null);
        if (!File.Exists(source)
            && !Directory.Exists(source))
        {
            return new SkinDraftSourceCheck(
                SkinDraftSourceState.Missing,
                manifest.SourceFingerprint,
                null);
        }

        var current = SkinPackageService.Fingerprint(source);
        return new SkinDraftSourceCheck(
            string.Equals(
                current,
                manifest.SourceFingerprint,
                StringComparison.OrdinalIgnoreCase)
                ? SkinDraftSourceState.Unchanged
                : SkinDraftSourceState.Changed,
            manifest.SourceFingerprint,
            current);
    }

    public byte[] ReadObject(Guid draftId, string contentHash)
    {
        if (!IsSha256(contentHash))
            throw new InvalidDataException("Draft object hash is not a SHA-256 digest.");
        return File.ReadAllBytes(ObjectPath(draftId, contentHash));
    }

    public SkinDraftManifest StageFile(
        Guid draftId,
        string filename,
        byte[] contents,
        string? expectedHash,
        string description)
    {
        ArgumentNullException.ThrowIfNull(contents);
        var normalized = NormalizeSkinFilename(filename);
        var manifest = Load(draftId);
        var contentHash = Hash(contents);
        WriteObject(draftId, contentHash, contents);

        var changes = manifest.Changes.ToDictionary(
            change => change.Filename,
            StringComparer.OrdinalIgnoreCase);
        changes[normalized] = new SkinDraftFileChange(
            normalized,
            SkinDraftChangeKind.Upsert,
            NormalizeOptionalHash(expectedHash),
            contentHash,
            contents.LongLength,
            NormalizeDescription(description, $"Replace {normalized}"));
        return AppendRevision(manifest, changes.Values, description);
    }

    public SkinDraftManifest StageFileMany(
        Guid draftId,
        IEnumerable<(string Filename, byte[] Contents, string? ExpectedHash)> files,
        string description)
    {
        ArgumentNullException.ThrowIfNull(files);
        return StageBatch(
            draftId,
            files.Select(file => new SkinDraftFileMutation(
                file.Filename,
                SkinDraftChangeKind.Upsert,
                file.Contents,
                file.ExpectedHash,
                description)),
            description);
    }

    public SkinDraftManifest StageBatch(
        Guid draftId,
        IEnumerable<SkinDraftFileMutation> mutations,
        string description)
    {
        ArgumentNullException.ThrowIfNull(mutations);
        var manifest = Load(draftId);
        var changes = manifest.Changes.ToDictionary(
            change => change.Filename,
            StringComparer.OrdinalIgnoreCase);
        var changed = false;
        foreach (var mutation in mutations)
        {
            var normalized = NormalizeSkinFilename(mutation.Filename);
            var expected = NormalizeOptionalHash(mutation.ExpectedHash);
            if (mutation.Kind == SkinDraftChangeKind.Delete)
            {
                if (mutation.Contents is not null)
                    throw new InvalidDataException("Delete mutations cannot contain file bytes.");
                changes[normalized] = new SkinDraftFileChange(
                    normalized,
                    SkinDraftChangeKind.Delete,
                    expected,
                    null,
                    0,
                    NormalizeDescription(mutation.Description, $"Delete {normalized}"));
            }
            else
            {
                var contents = mutation.Contents
                               ?? throw new InvalidDataException(
                                   "Upsert mutations require file bytes.");
                var contentHash = Hash(contents);
                WriteObject(draftId, contentHash, contents);
                changes[normalized] = new SkinDraftFileChange(
                    normalized,
                    SkinDraftChangeKind.Upsert,
                    expected,
                    contentHash,
                    contents.LongLength,
                    NormalizeDescription(mutation.Description, $"Replace {normalized}"));
            }
            changed = true;
        }
        return changed
            ? AppendRevision(manifest, changes.Values, description)
            : manifest;
    }

    public SkinDraftManifest StageDelete(
        Guid draftId,
        string filename,
        string? expectedHash,
        string description)
    {
        var normalized = NormalizeSkinFilename(filename);
        var manifest = Load(draftId);
        var changes = manifest.Changes.ToDictionary(
            change => change.Filename,
            StringComparer.OrdinalIgnoreCase);
        changes[normalized] = new SkinDraftFileChange(
            normalized,
            SkinDraftChangeKind.Delete,
            NormalizeOptionalHash(expectedHash),
            null,
            0,
            NormalizeDescription(description, $"Delete {normalized}"));
        return AppendRevision(manifest, changes.Values, description);
    }

    public SkinDraftManifest StageDeleteMany(
        Guid draftId,
        IEnumerable<(string Filename, string? ExpectedHash)> files,
        string description)
    {
        ArgumentNullException.ThrowIfNull(files);
        return StageBatch(
            draftId,
            files.Select(file => new SkinDraftFileMutation(
                file.Filename,
                SkinDraftChangeKind.Delete,
                null,
                file.ExpectedHash,
                description)),
            description);
    }

    public SkinDraftManifest Unstage(Guid draftId, string filename)
    {
        var normalized = NormalizeSkinFilename(filename);
        var manifest = Load(draftId);
        var changes = manifest.Changes.ToDictionary(
            change => change.Filename,
            StringComparer.OrdinalIgnoreCase);
        if (!changes.Remove(normalized))
            return manifest;
        return AppendRevision(manifest, changes.Values, $"Unstage {normalized}");
    }

    public SkinDraftManifest UnstageMany(
        Guid draftId,
        IEnumerable<string> filenames,
        string description)
    {
        ArgumentNullException.ThrowIfNull(filenames);
        var manifest = Load(draftId);
        var changes = manifest.Changes.ToDictionary(
            change => change.Filename,
            StringComparer.OrdinalIgnoreCase);
        var changed = false;
        foreach (var filename in filenames)
            changed |= changes.Remove(NormalizeSkinFilename(filename));
        return changed
            ? AppendRevision(manifest, changes.Values, description)
            : manifest;
    }

    public SkinDraftManifest Undo(Guid draftId)
    {
        var manifest = Load(draftId);
        if (!manifest.CanUndo)
            return manifest;
        return Save(manifest with
        {
            HistoryIndex = manifest.HistoryIndex - 1,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
    }

    public SkinDraftManifest Redo(Guid draftId)
    {
        var manifest = Load(draftId);
        if (!manifest.CanRedo)
            return manifest;
        return Save(manifest with
        {
            HistoryIndex = manifest.HistoryIndex + 1,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
    }

    public SkinDraftManifest SetLivePreviewState(
        Guid draftId,
        Guid? skinId,
        string? fingerprint,
        string? backupPath = null)
    {
        var manifest = Load(draftId);
        return Save(manifest with
        {
            LivePreviewSkinId = skinId,
            LivePreviewFingerprint = fingerprint,
            LivePreviewBackupPath = backupPath ?? manifest.LivePreviewBackupPath,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
    }

    public SkinDraftManifest SetLivePreviewSkin(Guid draftId, Guid? skinId) =>
        SetLivePreviewState(draftId, skinId, null);

    public static string NormalizeSkinFilename(string filename)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filename);
        var normalized = filename.Replace('\\', '/').TrimStart('/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0
            || segments.Any(segment =>
                segment is "." or ".."
                || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
        {
            throw new InvalidDataException($"Unsafe skin filename '{filename}'.");
        }
        return string.Join('/', segments);
    }

    public static string Hash(ReadOnlySpan<byte> contents) =>
        Convert.ToHexStringLower(SHA256.HashData(contents));

    private string DraftsDirectory => Path.Combine(root, "drafts");

    private string TrashDirectory => Path.Combine(root, "trash");

    private string DraftDirectory(Guid draftId) =>
        ContainedPath(DraftsDirectory, draftId.ToString("N"));

    private string ManifestPath(Guid draftId) =>
        ContainedPath(DraftDirectory(draftId), "manifest.json");

    private string ObjectPath(Guid draftId, string hash) =>
        ContainedPath(DraftDirectory(draftId), "objects", hash);

    private string SnapshotSource(Guid draftId, string sourcePath)
    {
        if (!File.Exists(sourcePath) && !Directory.Exists(sourcePath))
            throw new FileNotFoundException("Draft source skin was not found.", sourcePath);

        var directory = DraftDirectory(draftId);
        Directory.CreateDirectory(directory);
        var snapshot = ContainedPath(directory, "source.osk");
        var temporary = snapshot + ".new";
        try
        {
            if (Directory.Exists(sourcePath))
                ZipFile.CreateFromDirectory(sourcePath, temporary, CompressionLevel.Optimal, false);
            else
                File.Copy(sourcePath, temporary, overwrite: true);
            SkinPackageService.ValidatePackage(temporary);
            File.Move(temporary, snapshot, overwrite: true);
            return snapshot;
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    private SkinDraftManifest AppendRevision(
        SkinDraftManifest manifest,
        IEnumerable<SkinDraftFileChange> changes,
        string description,
        string? name = null,
        string? creator = null)
    {
        var retainedHistory = manifest.History
            .Take(manifest.HistoryIndex + 1)
            .ToList();
        var now = DateTimeOffset.UtcNow;
        retainedHistory.Add(new SkinDraftRevision(
            retainedHistory[^1].Revision + 1,
            now,
            NormalizeDescription(description, "Draft changed"),
            changes.OrderBy(change => change.Filename, StringComparer.OrdinalIgnoreCase).ToArray()));
        return Save(manifest with
        {
            Name = name ?? manifest.Name,
            Creator = creator ?? manifest.Creator,
            History = retainedHistory,
            HistoryIndex = retainedHistory.Count - 1,
            UpdatedAt = now,
        });
    }

    private SkinDraftManifest Save(SkinDraftManifest manifest)
    {
        if (manifest.Format != SkinDraftManifest.CurrentVersion)
            throw new InvalidDataException($"Unsupported draft format {manifest.Format}.");
        if (manifest.History.Count == 0
            || manifest.HistoryIndex < 0
            || manifest.HistoryIndex >= manifest.History.Count)
        {
            throw new InvalidDataException("Draft history index is invalid.");
        }

        var directory = DraftDirectory(manifest.DraftId);
        Directory.CreateDirectory(directory);
        var path = ManifestPath(manifest.DraftId);
        var temporary = path + ".new";
        try
        {
            File.WriteAllText(
                temporary,
                JsonSerializer.Serialize(manifest, SkinStudioLaunchContract.JsonOptions));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
        return manifest;
    }

    private static SkinDraftManifest LoadManifest(string path)
    {
        var manifest = JsonSerializer.Deserialize<SkinDraftManifest>(
            File.ReadAllText(path),
            SkinStudioLaunchContract.JsonOptions)
            ?? throw new InvalidDataException($"Draft manifest '{path}' was empty.");
        if (manifest.Format != SkinDraftManifest.CurrentVersion)
            throw new InvalidDataException($"Unsupported draft format {manifest.Format}.");
        if (manifest.History.Count == 0
            || manifest.HistoryIndex < 0
            || manifest.HistoryIndex >= manifest.History.Count)
        {
            throw new InvalidDataException($"Draft manifest '{path}' has invalid history.");
        }
        return manifest;
    }

    private static SkinDraftManifest? tryLoadManifest(string path)
    {
        if (!File.Exists(path))
            return null;
        try
        {
            return LoadManifest(path);
        }
        catch
        {
            return null;
        }
    }

    private void WriteObject(Guid draftId, string hash, byte[] contents)
    {
        var path = ObjectPath(draftId, hash);
        if (File.Exists(path))
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + $".{Guid.NewGuid():N}.new";
        try
        {
            File.WriteAllBytes(temporary, contents);
            try
            {
                File.Move(temporary, path);
            }
            catch (IOException) when (File.Exists(path))
            {
            }
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    private static string? NormalizeOptionalHash(string? hash)
    {
        if (string.IsNullOrWhiteSpace(hash))
            return null;
        var normalized = hash.Trim().ToLowerInvariant();
        if (!IsSha256(normalized))
            throw new InvalidDataException("Expected file hash is not a SHA-256 digest.");
        return normalized;
    }

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(Uri.IsHexDigit);

    private static string NormalizeDescription(string? description, string fallback) =>
        string.IsNullOrWhiteSpace(description) ? fallback : description.Trim();

    private static string ContainedPath(string parent, params string[] segments)
    {
        var root = Path.GetFullPath(parent);
        var candidate = Path.GetFullPath(Path.Combine([root, .. segments]));
        var prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Draft path escaped its workspace.");
        return candidate;
    }
}
