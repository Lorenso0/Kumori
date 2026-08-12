using System.IO.Compression;
using System.Security.Cryptography;
using Kumori.Skins;

namespace Kumori.Tracking;

public static class LazerInstalledSkinSnapshotService
{
    private const int max_files = 4096;
    private const long max_bytes = 512L * 1024 * 1024;
    private static readonly DateTimeOffset archiveTimestamp =
        new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static string CreateVerifiedOsk(
        LazerSkinInfo skin,
        Func<string, byte[]> readBlob,
        string destination)
    {
        ArgumentNullException.ThrowIfNull(skin);
        ArgumentNullException.ThrowIfNull(readBlob);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        if (skin.Files.Count is 0 or > max_files)
            throw new InvalidDataException(
                $"Installed skin file count must be between 1 and {max_files}.");
        if (skin.Files.Sum(file => file.SizeBytes) > max_bytes)
            throw new InvalidDataException("Installed skin exceeds the 512 MB snapshot limit.");

        var path = Path.GetFullPath(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + $".{Guid.NewGuid():N}.new";
        try
        {
            var filenames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var archive = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                long actualBytes = 0;
                foreach (var file in skin.Files.OrderBy(
                             file => file.Filename,
                             StringComparer.OrdinalIgnoreCase))
                {
                    var filename = SkinDraftWorkspaceService.NormalizeSkinFilename(
                        file.Filename);
                    if (!filenames.Add(filename))
                    {
                        throw new InvalidDataException(
                            $"Installed skin contains duplicate filename '{filename}'.");
                    }
                    var bytes = readBlob(file.Hash);
                    actualBytes += bytes.LongLength;
                    if (actualBytes > max_bytes)
                        throw new InvalidDataException(
                            "Installed skin exceeds the 512 MB snapshot limit.");
                    var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
                    if (!hash.Equals(file.Hash, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException(
                            $"Installed skin blob '{filename}' changed while it was being snapshotted.");
                    }
                    var entry = archive.CreateEntry(
                        filename,
                        CompressionLevel.Optimal);
                    entry.LastWriteTime = archiveTimestamp;
                    using var output = entry.Open();
                    output.Write(bytes);
                }
            }
            SkinPackageService.ValidatePackage(temporary);
            using (var archive = ZipFile.OpenRead(temporary))
            {
                if (archive.Entries.Count != skin.Files.Count)
                    throw new InvalidDataException(
                        "Installed skin snapshot entry count did not verify.");
            }
            File.Move(temporary, path, overwrite: true);
            return path;
        }
        finally
        {
            try { File.Delete(temporary); } catch { }
        }
    }
}
