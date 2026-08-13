using System.IO;
using Kumori.Core;
using Kumori.Core.Settings;

namespace Kumori.App;

public sealed record SkinLibraryItem(
    string Name,
    string Path,
    bool IsFolder,
    long SizeBytes,
    bool IsBuiltIn = false,
    bool IsImported = true,
    bool IsAvailable = true);

public static class SkinLibraryService
{
    public const string BuiltInArgonProPath = "builtin://argon-pro";

    public static string SkinDirectory => AppPaths.SkinsDir;

    public static IReadOnlyList<SkinLibraryItem> List(string? configuredPath = null)
        => ListFromDirectory(SkinDirectory, configuredPath);

    internal static IReadOnlyList<SkinLibraryItem> ListFromDirectory(
        string skinDirectory,
        string? configuredPath = null)
    {
        Directory.CreateDirectory(skinDirectory);
        SkinLibraryItem[] builtIn =
        [
            new SkinLibraryItem("Argon Pro", BuiltInArgonProPath, false, 0,
                IsBuiltIn: true, IsImported: false),
        ];
        var files = Directory.EnumerateFiles(skinDirectory)
            .Where(path => string.Equals(Path.GetExtension(path), ".osk", StringComparison.OrdinalIgnoreCase))
            .Select(path => new SkinLibraryItem(Path.GetFileNameWithoutExtension(path), path, false, SafeFileSize(path)));
        var folders = Directory.EnumerateDirectories(skinDirectory)
            .Where(IsSkinFolder)
            .Select(path => new SkinLibraryItem(Path.GetFileName(path), path, true, SafeDirectorySize(path)));
        var items = builtIn.Concat(files).Concat(folders).ToList();
        string selection = (configuredPath ?? "").Trim();
        if (!IsBuiltInPath(selection)
            && !items.Any(item => MatchesSelection(item.Path, selection)))
        {
            bool isFile = File.Exists(selection);
            bool isFolder = Directory.Exists(selection);
            string name = isFolder
                ? Path.GetFileName(selection.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                : Path.GetFileNameWithoutExtension(selection);
            items.Add(new SkinLibraryItem(
                string.IsNullOrWhiteSpace(name) ? "Selected skin" : name,
                selection,
                isFolder,
                isFile ? SafeFileSize(selection) : isFolder ? SafeDirectorySize(selection) : 0,
                IsImported: false,
                IsAvailable: isFile || isFolder));
        }

        return items
            .OrderByDescending(item => item.IsBuiltIn)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
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
        if (!IsSkinFolder(source))
        {
            throw new InvalidDataException("The selected folder is not an osu! skin folder because it has no skin.ini file.");
        }

        Directory.CreateDirectory(SkinDirectory);
        var destination = UniquePath(Path.Combine(SkinDirectory, SafeName(Path.GetFileName(source), "skin")));
        CopyDirectory(source, destination);
        return destination;
    }

    public static void DeleteImported(string path)
    {
        if (IsBuiltInPath(path))
        {
            throw new InvalidOperationException("Argon Pro is built into the replay viewer and cannot be deleted.");
        }

        var fullPath = Path.GetFullPath(path);
        if (!IsImportedSkinPath(fullPath, SkinDirectory))
        {
            throw new InvalidOperationException("Only valid skins imported directly into Kumori's skin folder can be deleted.");
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
        if (IsBuiltInPath(path))
        {
            settings.Update(s => s.ReplayViewer.SkinPath = "");
            return;
        }

        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
        {
            throw new FileNotFoundException("The selected skin no longer exists.", fullPath);
        }

        settings.Update(s => s.ReplayViewer.SkinPath = fullPath);
    }

    public static bool IsBuiltInPath(string? path) =>
        string.IsNullOrWhiteSpace(path)
        || string.Equals(path, BuiltInArgonProPath, StringComparison.OrdinalIgnoreCase);

    public static bool MatchesSelection(string libraryPath, string? configuredPath) =>
        IsBuiltInPath(libraryPath)
            ? IsBuiltInPath(configuredPath)
            : string.Equals(libraryPath, configuredPath, StringComparison.OrdinalIgnoreCase);

    internal static bool IsImportedSkinPath(string path, string skinDirectory)
    {
        string fullPath;
        string fullSkinDirectory;
        try
        {
            fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            fullSkinDirectory = Path.GetFullPath(skinDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return false;
        }

        if (!string.Equals(Path.GetDirectoryName(fullPath), fullSkinDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return File.Exists(fullPath)
            ? string.Equals(Path.GetExtension(fullPath), ".osk", StringComparison.OrdinalIgnoreCase)
            : Directory.Exists(fullPath) && IsSkinFolder(fullPath);
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

    private static bool IsSkinFolder(string path)
    {
        try
        {
            return Directory.EnumerateFiles(path, "*", SearchOption.TopDirectoryOnly)
                .Any(file => string.Equals(Path.GetFileName(file), "skin.ini", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
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
