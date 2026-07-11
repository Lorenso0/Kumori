using System.Runtime.InteropServices;
using Realms;
using Realms.Exceptions;

namespace Kumori.Tracking;

/// <summary>
/// Resolves a beatmap's named assets from osu!lazer's content-addressed store.
/// This deliberately treats lazer's Realm database as an optional, read-only source:
/// failures are handled by the normal local-file and mirror fallbacks.
/// </summary>
internal static class LazerMediaStore
{
    // Current osu!lazer client Realm schema (ppy/osu 2026.621). A later schema
    // deliberately falls back to our normal cache sources rather than risking a migration.
    private const ulong lazer_realm_schema_version = 51;

    public static IReadOnlyDictionary<string, string>? ResolveFiles(TosuMediaInfo media)
    {
        if (media.BeatmapSetId is not > 0)
        {
            return null;
        }

        foreach (var root in CandidateRoots(media))
        {
            var realmPath = Path.Combine(root, "client.realm");
            var filesRoot = Path.Combine(root, "files");
            if (!File.Exists(realmPath) || !Directory.Exists(filesRoot))
            {
                continue;
            }

            try
            {
                using var realm = Realm.GetInstance(createConfiguration(realmPath));
                var set = realm.All<LazerBeatmapSet>()
                    .FirstOrDefault(s => s.OnlineId == media.BeatmapSetId && !s.DeletePending);
                if (set is null)
                {
                    continue;
                }

                var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var usage in set.Files)
                {
                    if (string.IsNullOrWhiteSpace(usage.Filename) || string.IsNullOrWhiteSpace(usage.File.Hash))
                    {
                        continue;
                    }

                    var hash = usage.File.Hash;
                    if (hash.Length < 2)
                    {
                        continue;
                    }

                    var source = Path.Combine(filesRoot, hash[..1], hash[..2], hash);
                    if (File.Exists(source))
                    {
                        resolved[Path.GetFileName(usage.Filename)] = source;
                    }
                }

                if (resolved.Count > 0)
                {
                    return resolved;
                }
            }
            catch (Exception ex) when (ex is RealmException or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // Lazer can migrate its database while it is open. This source is best-effort.
                Serilog.Log.Debug(ex, "Could not read osu!lazer media store at {Root}", root);
            }
        }

        return null;
    }

    public static string? FindStorageRoot() => CandidateRoots(new TosuMediaInfo())
        .FirstOrDefault(root => File.Exists(Path.Combine(root, "client.realm")) && Directory.Exists(Path.Combine(root, "files")));

    public static LazerStorageDiagnostics GetDiagnostics()
    {
        var defaultRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "osu");
        var configuredRoot = ReadConfiguredStorageRoot(defaultRoot);
        var root = FindStorageRoot();
        if (string.IsNullOrWhiteSpace(root))
        {
            return new LazerStorageDiagnostics(defaultRoot, configuredRoot, null, false, false, false,
                "No lazer data root containing both client.realm and files was found.");
        }

        var realmPath = Path.Combine(root, "client.realm");
        var filesPath = Path.Combine(root, "files");
        try
        {
            using var realm = Realm.GetInstance(createConfiguration(realmPath));
            return new LazerStorageDiagnostics(defaultRoot, configuredRoot, root, true, true, true, null);
        }
        catch (Exception ex) when (ex is RealmException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new LazerStorageDiagnostics(defaultRoot, configuredRoot, root, File.Exists(realmPath), Directory.Exists(filesPath), false, ex.Message);
        }
    }

    public static bool IsBeatmapId(string path, long beatmapId)
    {
        try { return File.ReadLines(path).Any(line => line.Trim().Equals($"BeatmapID:{beatmapId}", StringComparison.OrdinalIgnoreCase)); }
        catch (IOException) { return false; }
    }

    private static RealmConfiguration createConfiguration(string realmPath) => new(realmPath)
    {
        IsReadOnly = true,
        SchemaVersion = lazer_realm_schema_version,
        Schema = new[] { typeof(LazerBeatmapSet), typeof(LazerNamedFileUsage), typeof(LazerRealmFile) },
    };

    public static bool TryLink(string source, string destination)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (File.Exists(destination))
            {
                File.Delete(destination);
            }

            if (CreateHardLink(destination, source, IntPtr.Zero))
            {
                return true;
            }

            // Hard links cannot cross volumes. A file symlink keeps the cache
            // zero-copy when lazer has been moved to another drive.
            return CreateSymbolicLink(destination, source, symbolic_link_allow_unprivileged_create);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Serilog.Log.Debug(ex, "Could not link lazer media file {Source}", source);
            return false;
        }
    }

    private static IEnumerable<string> CandidateRoots(TosuMediaInfo media)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(media.GameFolder))
        {
            seen.Add(media.GameFolder);
            yield return media.GameFolder;
        }

        var defaultRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "osu");
        var configuredRoot = ReadConfiguredStorageRoot(defaultRoot);
        if (!string.IsNullOrWhiteSpace(configuredRoot) && seen.Add(configuredRoot))
        {
            yield return configuredRoot;
        }

        if (seen.Add(defaultRoot))
        {
            yield return defaultRoot;
        }
    }

    private static string? ReadConfiguredStorageRoot(string defaultRoot)
    {
        try
        {
            var storageIni = Path.Combine(defaultRoot, "storage.ini");
            if (!File.Exists(storageIni))
            {
                return null;
            }

            var line = File.ReadLines(storageIni)
                .FirstOrDefault(value => value.StartsWith("FullPath", StringComparison.OrdinalIgnoreCase));
            var separator = line?.IndexOf('=') ?? -1;
            if (separator < 0)
            {
                return null;
            }

            var path = line![(separator + 1)..].Trim();
            return Path.IsPathRooted(path) ? path : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    [DllImport("Kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    private const int symbolic_link_allow_unprivileged_create = 0x2;

    [DllImport("Kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateSymbolicLink(string lpSymlinkFileName, string lpTargetFileName, int dwFlags);
}

[MapTo("BeatmapSet")]
internal partial class LazerBeatmapSet : RealmObject
{
    [PrimaryKey]
    [MapTo("ID")]
    public Guid Id { get; set; }

    [Indexed]
    [MapTo("OnlineID")]
    public int OnlineId { get; set; }

    [MapTo("DeletePending")]
    public bool DeletePending { get; set; }

    [MapTo("Files")]
    public IList<LazerNamedFileUsage> Files { get; } = null!;
}

[MapTo("RealmNamedFileUsage")]
internal partial class LazerNamedFileUsage : EmbeddedObject
{
    [MapTo("File")]
    public LazerRealmFile File { get; set; } = null!;

    [MapTo("Filename")]
    public string Filename { get; set; } = string.Empty;
}

[MapTo("File")]
internal partial class LazerRealmFile : RealmObject
{
    [PrimaryKey]
    [MapTo("Hash")]
    public string Hash { get; set; } = string.Empty;
}
