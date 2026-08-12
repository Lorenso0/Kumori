using System.IO.Compression;

namespace Kumori.Skins;

public sealed class SkinPackageService
{
    private const int max_entries = 20_000;
    private const long max_expanded_bytes = 2L * 1024 * 1024 * 1024;

    private readonly SkinDraftWorkspaceService workspace;

    public SkinPackageService(SkinDraftWorkspaceService workspace)
    {
        this.workspace = workspace;
    }

    public string Export(Guid draftId, string destination)
    {
        return Export(Materialize(draftId), destination);
    }

    public string Export(
        IReadOnlyDictionary<string, byte[]> files,
        string destination,
        CompressionLevel compressionLevel = CompressionLevel.Optimal)
    {
        ArgumentNullException.ThrowIfNull(files);
        var fullDestination = Path.GetFullPath(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(fullDestination)!);
        var temporary = fullDestination + ".new";
        try
        {
            using (var stream = new FileStream(
                       temporary,
                       FileMode.Create,
                       FileAccess.Write,
                       FileShare.None))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                foreach (var (filename, bytes) in files.OrderBy(
                             item => item.Key,
                             StringComparer.OrdinalIgnoreCase))
                {
                    var entry = archive.CreateEntry(
                        SkinDraftWorkspaceService.NormalizeSkinFilename(filename),
                        compressionLevel);
                    using var output = entry.Open();
                    output.Write(bytes);
                }
            }
            ValidatePackage(temporary);
            File.Move(temporary, fullDestination, overwrite: true);
            return fullDestination;
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    public IReadOnlyDictionary<string, byte[]> Materialize(Guid draftId)
    {
        var manifest = workspace.Load(draftId);
        var files = ReadSource(manifest.SourcePath);
        foreach (var change in manifest.Changes)
        {
            if (change.Kind == SkinDraftChangeKind.Delete)
            {
                files.Remove(change.Filename);
                continue;
            }

            if (change.ContentHash is null)
                throw new InvalidDataException($"Draft change '{change.Filename}' has no content hash.");
            files[change.Filename] = workspace.ReadObject(draftId, change.ContentHash);
        }

        if (!files.ContainsKey("skin.ini"))
        {
            files["skin.ini"] =
                "[General]\r\nName: Kumori draft\r\nAuthor: Kumori\r\nVersion: 2.7\r\n"u8.ToArray();
        }

        return files.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.ToArray(),
            StringComparer.OrdinalIgnoreCase);
    }

    public static string Fingerprint(string sourcePath)
    {
        var files = ReadSource(sourcePath);
        using var digest = System.Security.Cryptography.IncrementalHash.CreateHash(
            System.Security.Cryptography.HashAlgorithmName.SHA256);
        foreach (var (filename, bytes) in files.OrderBy(
                     item => item.Key,
                     StringComparer.OrdinalIgnoreCase))
        {
            digest.AppendData(System.Text.Encoding.UTF8.GetBytes(filename.ToLowerInvariant()));
            digest.AppendData([0]);
            digest.AppendData(bytes);
            digest.AppendData([0]);
        }
        return Convert.ToHexStringLower(digest.GetHashAndReset());
    }

    public static void ValidatePackage(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        if (archive.Entries.Count == 0 || archive.Entries.Count > max_entries)
            throw new InvalidDataException("Skin archive entry count is invalid.");

        long expanded = 0;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            var name = SkinDraftWorkspaceService.NormalizeSkinFilename(entry.FullName);
            if (!names.Add(name))
                throw new InvalidDataException($"Skin archive contains duplicate '{name}'.");
            expanded = checked(expanded + entry.Length);
            if (expanded > max_expanded_bytes)
                throw new InvalidDataException("Skin archive expands beyond the safety limit.");
        }
    }

    private static Dictionary<string, byte[]> ReadSource(string? sourcePath)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(sourcePath))
            return result;

        var fullPath = Path.GetFullPath(sourcePath);
        if (Directory.Exists(fullPath))
        {
            foreach (var path in Directory.EnumerateFiles(fullPath, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(fullPath, path).Replace('\\', '/');
                result[SkinDraftWorkspaceService.NormalizeSkinFilename(relative)] =
                    File.ReadAllBytes(path);
            }
            return result;
        }

        if (!File.Exists(fullPath))
            throw new FileNotFoundException("Draft source skin was not found.", fullPath);
        using var archive = ZipFile.OpenRead(fullPath);
        if (archive.Entries.Count > max_entries
            || archive.Entries.Sum(entry => entry.Length) > max_expanded_bytes)
        {
            throw new InvalidDataException("Source skin archive exceeds the safety limit.");
        }
        foreach (var entry in archive.Entries.Where(entry => !string.IsNullOrEmpty(entry.Name)))
        {
            var name = SkinDraftWorkspaceService.NormalizeSkinFilename(entry.FullName);
            using var input = entry.Open();
            using var output = new MemoryStream();
            input.CopyTo(output);
            result[name] = output.ToArray();
        }
        return result;
    }
}
