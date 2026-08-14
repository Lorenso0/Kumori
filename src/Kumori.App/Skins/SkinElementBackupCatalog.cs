using System.Globalization;
using System.IO;

namespace Kumori.App.Skins;

internal sealed record SkinElementBackupFile(
    string FullPath,
    string Filename,
    long Size);

internal sealed record SkinElementBackupSession(
    string DirectoryPath,
    DateTimeOffset CreatedAt,
    IReadOnlyList<SkinElementBackupFile> Files,
    bool HasRealmRestorePoint);

internal sealed record SkinElementBackupSelection(
    SkinElementBackupSession Session,
    IReadOnlyList<SkinElementBackupFile> Files);

internal static class SkinElementBackupCatalog
{
    public static IReadOnlyList<SkinElementBackupSession> Scan(
        string backupRoot,
        Guid expectedSkinId)
    {
        var root = Path.GetFullPath(backupRoot);
        if (!Directory.Exists(root))
            return [];

        var sessions = new List<SkinElementBackupSession>();
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            try
            {
                if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint))
                    continue;

                var session = readSession(root, directory, expectedSkinId);
                if (session is not null)
                    sessions.Add(session);
            }
            catch (Exception ex) when (ex is IOException
                                       or UnauthorizedAccessException
                                       or InvalidDataException)
            {
                // A damaged or incomplete backup should not hide healthy ones.
            }
        }

        return sessions
            .OrderByDescending(session => session.CreatedAt)
            .ThenByDescending(
                session => Path.GetFileName(session.DirectoryPath),
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static SkinElementBackupSession? readSession(
        string root,
        string directory,
        Guid expectedSkinId)
    {
        var session = Path.GetFullPath(directory);
        if (!isContained(root, session))
            return null;

        var manifestPath = Path.Combine(session, "backup.txt");
        var elementsPath = Path.Combine(session, "elements");
        if (!File.Exists(manifestPath) || !Directory.Exists(elementsPath))
            return null;

        var manifest = File.ReadAllLines(manifestPath);
        if (!Guid.TryParse(value(manifest, "Skin ID"), out var skinId)
            || skinId != expectedSkinId)
        {
            return null;
        }

        var created = DateTimeOffset.TryParse(
            value(manifest, "Created"),
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var parsed)
            ? parsed
            : new DateTimeOffset(Directory.GetCreationTimeUtc(session));
        var files = enumerateFiles(elementsPath);
        if (files.Count == 0)
            return null;

        return new SkinElementBackupSession(
            session,
            created,
            files,
            Directory.Exists(Path.Combine(session, "realm")));
    }

    private static IReadOnlyList<SkinElementBackupFile> enumerateFiles(
        string elementsPath)
    {
        var root = Path.GetFullPath(elementsPath);
        var pending = new Stack<string>();
        var files = new List<SkinElementBackupFile>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                var fullChild = Path.GetFullPath(child);
                if (isContained(root, fullChild)
                    && !File.GetAttributes(fullChild).HasFlag(FileAttributes.ReparsePoint))
                {
                    pending.Push(fullChild);
                }
            }

            foreach (var path in Directory.EnumerateFiles(directory))
            {
                var fullPath = Path.GetFullPath(path);
                if (!isContained(root, fullPath)
                    || File.GetAttributes(fullPath).HasFlag(FileAttributes.ReparsePoint))
                {
                    continue;
                }

                var filename = Path.GetRelativePath(root, fullPath).Replace('\\', '/');
                files.Add(new SkinElementBackupFile(
                    fullPath,
                    filename,
                    new FileInfo(fullPath).Length));
            }
        }

        return files
            .OrderBy(file => file.Filename, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string? value(IEnumerable<string> lines, string key) =>
        lines.FirstOrDefault(line => line.StartsWith(
                key + ":",
                StringComparison.OrdinalIgnoreCase))?
            .Split(':', 2)[1]
            .Trim();

    private static bool isContained(string root, string candidate)
    {
        var normalizedRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return Path.GetFullPath(candidate).StartsWith(
            normalizedRoot,
            StringComparison.OrdinalIgnoreCase);
    }
}
