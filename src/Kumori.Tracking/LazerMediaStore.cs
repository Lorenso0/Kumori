using System.Runtime.InteropServices;
using Kumori.Core;
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

    public static string? ResolveReplayFile(string beatmapHash, DateTimeOffset startedAt, string? gameFolder = null, DateTimeOffset? endedAt = null)
        => ResolveReplayFiles(beatmapHash, startedAt, gameFolder, endedAt).FirstOrDefault();

    public static IReadOnlyList<string> ResolveReplayFiles(string beatmapHash, DateTimeOffset startedAt, string? gameFolder = null, DateTimeOffset? endedAt = null)
    {
        if (string.IsNullOrWhiteSpace(beatmapHash)) return [];
        var media = new TosuMediaInfo { GameFolder = gameFolder };
        var resolved = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var earliest = startedAt.AddSeconds(-30);
        var latest = (endedAt ?? startedAt).AddMinutes(5);
        var targetTime = endedAt ?? startedAt;
        foreach (var root in CandidateRoots(media))
        {
            var realmPath = Path.Combine(root, "client.realm");
            var filesRoot = Path.Combine(root, "files");
            if (!File.Exists(realmPath) || !Directory.Exists(filesRoot)) continue;
            try
            {
                using var realm = Realm.GetInstance(createScoreConfiguration(realmPath));
                // Current lazer stores a SHA-256 BeatmapHash while tosu and the
                // legacy .osr header identify the beatmap by MD5. Keep an exact
                // match first for older schemas, then expose nearby scores so the
                // caller can validate each replay header against the requested MD5.
                var scores = realm.All<LazerScore>()
                    .Where(s => !s.DeletePending && s.Date >= earliest && s.Date <= latest)
                    .AsEnumerable()
                    .Where(s => s.Files.Any(file => file.Filename.EndsWith(".osr", StringComparison.OrdinalIgnoreCase)))
                    .OrderByDescending(s => string.Equals(s.BeatmapHash, beatmapHash, StringComparison.OrdinalIgnoreCase))
                    .ThenBy(s => Math.Abs((s.Date - targetTime).TotalMilliseconds))
                    .Take(24)
                    .ToArray();

                foreach (var score in scores)
                {
                    var replay = score.Files.FirstOrDefault(file => file.Filename.EndsWith(".osr", StringComparison.OrdinalIgnoreCase));
                    var hash = replay?.File.Hash;
                    if (string.IsNullOrWhiteSpace(hash) || hash.Length < 2)
                        continue;
                    var path = Path.Combine(filesRoot, hash[..1], hash[..2], hash);
                    if (File.Exists(path) && seen.Add(path))
                        resolved.Add(path);
                }
            }
            catch (Exception ex) when (ex is RealmException or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                Serilog.Log.Debug(ex, "Could not read osu!lazer replay store at {Root}", root);
            }
        }
        return resolved;
    }

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

    private static RealmConfiguration createScoreConfiguration(string realmPath) => new(realmPath)
    {
        IsReadOnly = true,
        SchemaVersion = lazer_realm_schema_version,
        Schema = new[] { typeof(LazerScore), typeof(LazerNamedFileUsage), typeof(LazerRealmFile) },
    };

    public static bool TryLink(
        string source,
        string destination,
        string hardLinkSource = "local-file-hardlink",
        string symbolicLinkSource = "local-file-symlink",
        string? reason = "Kumori referenced an existing local file instead of duplicating it.")
    {
        try
        {
            if (!File.Exists(source))
                return false;

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var isNew = !File.Exists(destination);
            var pending = destination + $".link-{Guid.NewGuid():N}";
            try
            {
                if (CreateHardLink(pending, source, IntPtr.Zero))
                {
                    File.Move(pending, destination, overwrite: true);
                    if (isNew) CacheActivityLog.RecordAddition(destination, hardLinkSource, reason: reason);
                    return true;
                }

                // Hard links cannot cross volumes. A file symlink keeps the cache
                // zero-copy when lazer has been moved to another drive.
                var linked = CreateSymbolicLink(pending, source, symbolic_link_allow_unprivileged_create);
                if (!linked || !File.Exists(pending))
                    return false;

                File.Move(pending, destination, overwrite: true);
                if (isNew) CacheActivityLog.RecordAddition(destination, symbolicLinkSource, reason: reason);
                return true;
            }
            finally
            {
                try { File.Delete(pending); } catch { }
            }
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

[MapTo("Score")]
internal partial class LazerScore : RealmObject
{
    [PrimaryKey]
    [MapTo("ID")]
    public Guid Id { get; set; }
    [Indexed]
    public string BeatmapHash { get; set; } = string.Empty;
    public DateTimeOffset Date { get; set; }
    public bool DeletePending { get; set; }
    public IList<LazerNamedFileUsage> Files { get; } = null!;
}
