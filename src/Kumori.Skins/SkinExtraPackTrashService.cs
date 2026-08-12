using System.Text.Json;

namespace Kumori.Skins;

public sealed record DeletedSkinExtraPack(
    string TrashId,
    string OriginalRelativePath,
    string DisplayName,
    DateTimeOffset DeletedAt);

public sealed class SkinExtraPackTrashService
{
    public DeletedSkinExtraPack DeleteRecoverably(
        string extrasRoot,
        SkinExtraPackDescriptor pack)
    {
        var root = normalizedRoot(extrasRoot);
        var source = SkinExtraPackDeletion.ResolvePackDirectory(root, pack.DirectoryPath);
        var relative = Path.GetRelativePath(root, source);
        var trashRoot = Path.Combine(root, ".kumori", "trash");
        Directory.CreateDirectory(trashRoot);
        var deletedAt = DateTimeOffset.UtcNow;
        var trashId =
            $"{deletedAt:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}";
        var destination = containedPath(trashRoot, trashId);
        var metadata = containedPath(trashRoot, trashId + ".json");
        var deleted = new DeletedSkinExtraPack(
            trashId,
            relative,
            pack.Manifest.DisplayName,
            deletedAt);
        try
        {
            Directory.Move(source, destination);
            File.WriteAllText(
                metadata,
                JsonSerializer.Serialize(
                    deleted,
                    SkinStudioLaunchContract.JsonOptions));
            SkinExtrasPersistentIndex.Invalidate(root);
            return deleted;
        }
        catch
        {
            try
            {
                if (!Directory.Exists(source) && Directory.Exists(destination))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(source)!);
                    Directory.Move(destination, source);
                }
            }
            catch { }
            throw;
        }
    }

    public IReadOnlyList<DeletedSkinExtraPack> List(string extrasRoot)
    {
        var trashRoot = Path.Combine(normalizedRoot(extrasRoot), ".kumori", "trash");
        if (!Directory.Exists(trashRoot))
            return [];
        return Directory.EnumerateFiles(trashRoot, "*.json")
            .Select(path =>
            {
                try
                {
                    return JsonSerializer.Deserialize<DeletedSkinExtraPack>(
                        File.ReadAllText(path),
                        SkinStudioLaunchContract.JsonOptions);
                }
                catch
                {
                    return null;
                }
            })
            .Where(item => item is not null)
            .Select(item => item!)
            .OrderByDescending(item => item.DeletedAt)
            .ToArray();
    }

    public string Restore(string extrasRoot, string trashId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trashId);
        if (!Path.GetFileName(trashId).Equals(trashId, StringComparison.Ordinal))
            throw new InvalidDataException("Unsafe Extras trash identifier.");
        var root = normalizedRoot(extrasRoot);
        var trashRoot = Path.Combine(root, ".kumori", "trash");
        var source = containedPath(trashRoot, trashId);
        var metadataPath = containedPath(trashRoot, trashId + ".json");
        if (!Directory.Exists(source) || !File.Exists(metadataPath))
            throw new DirectoryNotFoundException("The deleted Extras pack was not found.");
        var deleted = JsonSerializer.Deserialize<DeletedSkinExtraPack>(
                          File.ReadAllText(metadataPath),
                          SkinStudioLaunchContract.JsonOptions)
                      ?? throw new InvalidDataException("Extras trash metadata is invalid.");
        if (!deleted.TrashId.Equals(trashId, StringComparison.Ordinal))
            throw new InvalidDataException("Extras trash identity does not match its metadata.");
        var destination = Path.GetFullPath(Path.Combine(root, deleted.OriginalRelativePath));
        var rootPrefix = Path.TrimEndingDirectorySeparator(root)
                         + Path.DirectorySeparatorChar;
        if (!destination.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)
            || destination.StartsWith(
                Path.Combine(root, ".kumori") + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Extras restore path escaped its library.");
        }
        if (Directory.Exists(destination))
            throw new IOException("An Extras pack already exists at the restore destination.");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        Directory.Move(source, destination);
        File.Delete(metadataPath);
        SkinExtrasPersistentIndex.Invalidate(root);
        return destination;
    }

    private static string normalizedRoot(string extrasRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extrasRoot);
        return Path.GetFullPath(extrasRoot);
    }

    private static string containedPath(string parent, string child)
    {
        var root = Path.GetFullPath(parent);
        var candidate = Path.GetFullPath(Path.Combine(root, child));
        var prefix = Path.TrimEndingDirectorySeparator(root)
                     + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Extras trash path escaped its library.");
        return candidate;
    }
}
