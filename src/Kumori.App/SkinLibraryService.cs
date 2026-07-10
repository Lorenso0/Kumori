using System.IO;
using Kumori.Core;
using Kumori.Core.Settings;

namespace Kumori.App;

public sealed record SkinLibraryItem(string Name, string Path, bool IsFolder, long SizeBytes);

public static class SkinLibraryService
{
    public static string SkinDirectory => AppPaths.SkinsDir;

    public static IReadOnlyList<SkinLibraryItem> List()
    {
        Directory.CreateDirectory(SkinDirectory);
        var files = Directory.EnumerateFiles(SkinDirectory, "*.osk")
            .Select(path => new SkinLibraryItem(Path.GetFileNameWithoutExtension(path), path, false, SafeFileSize(path)));
        var folders = Directory.EnumerateDirectories(SkinDirectory)
            .Select(path => new SkinLibraryItem(Path.GetFileName(path), path, true, SafeDirectorySize(path)));
        return files.Concat(folders)
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static string ImportFile(string source)
    {
        Directory.CreateDirectory(SkinDirectory);
        var safe = SafeName(Path.GetFileNameWithoutExtension(source), "skin");
        var destination = UniquePath(Path.Combine(SkinDirectory, $"{safe}.osk"));
        File.Copy(source, destination);
        return destination;
    }

    public static string ImportFolder(string source)
    {
        Directory.CreateDirectory(SkinDirectory);
        var destination = UniquePath(Path.Combine(SkinDirectory, SafeName(Path.GetFileName(source), "skin")));
        CopyDirectory(source, destination);
        return destination;
    }

    public static void DeleteImported(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var fullSkinDir = Path.GetFullPath(SkinDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(fullSkinDir, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Only skins imported into Kumori's skin folder can be deleted.");
        }
        if (Directory.Exists(fullPath))
        {
            Directory.Delete(fullPath, recursive: true);
        }
        else if (File.Exists(fullPath))
        {
            File.Delete(fullPath);
        }
    }

    public static void Activate(SettingsService settings, string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
        {
            throw new FileNotFoundException("The selected skin no longer exists.", fullPath);
        }

        settings.Update(s => s.ReplayViewer.SkinPath = fullPath);
    }

    private static string SafeName(string? source, string fallback)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = string.Concat((source ?? "").Select(ch => invalid.Contains(ch) ? '_' : ch)).Trim();
        return string.IsNullOrWhiteSpace(safe) ? fallback : safe;
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return path;
        }
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 1; ; i++)
        {
            var candidate = Path.Combine(dir, $"{name}-{i}{ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, dir)));
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)));
        }
    }

    private static long SafeFileSize(string path)
    {
        try { return new FileInfo(path).Length; } catch { return 0; }
    }

    private static long SafeDirectorySize(string path)
    {
        try
        {
            return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                .Sum(file => SafeFileSize(file));
        }
        catch
        {
            return 0;
        }
    }
}
