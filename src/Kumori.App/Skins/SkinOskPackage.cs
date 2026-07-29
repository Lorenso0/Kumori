using System.IO;
using System.IO.Compression;
using Kumori.Tracking;

namespace Kumori.App.Skins;

internal static class SkinOskPackage
{
    public static string Export(
        string destinationDirectory,
        string skinName,
        IReadOnlyList<LazerSkinImportFile> files,
        DateTimeOffset? timestamp = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(skinName);
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count == 0)
            throw new ArgumentException("A skin package must contain at least one file.", nameof(files));

        Directory.CreateDirectory(destinationDirectory);
        var safeName = SafeFilename(skinName);
        var createdAt = timestamp ?? DateTimeOffset.Now;
        var path = Path.Combine(
            destinationDirectory,
            $"{safeName}-{createdAt:yyyyMMdd-HHmmss-fff}.osk");
        var pending = path + $".kumori-{Guid.NewGuid():N}.tmp";

        try
        {
            using (var stream = new FileStream(
                       pending,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var file in files.OrderBy(file => file.Filename, StringComparer.OrdinalIgnoreCase))
                {
                    var filename = SafeEntryName(file.Filename);
                    if (!seen.Add(filename))
                        throw new InvalidDataException(
                            $"The skin contains duplicate filename '{filename}'.");
                    var entry = archive.CreateEntry(filename, CompressionLevel.Optimal);
                    using var output = entry.Open();
                    output.Write(file.Bytes);
                }
            }

            File.Move(pending, path);
            return path;
        }
        finally
        {
            try { File.Delete(pending); } catch { }
        }
    }

    private static string SafeEntryName(string filename)
    {
        var normalized = filename.Replace('\\', '/').Trim('/');
        if (normalized.Length == 0
            || Path.IsPathRooted(normalized)
            || normalized.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw new InvalidDataException(
                $"'{filename}' is not a safe relative skin filename.");
        }
        return normalized;
    }

    private static string SafeFilename(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var result = new string(value
            .Trim()
            .Select(character => invalid.Contains(character) ? '_' : character)
            .ToArray());
        return string.IsNullOrWhiteSpace(result) ? "skin" : result;
    }
}
