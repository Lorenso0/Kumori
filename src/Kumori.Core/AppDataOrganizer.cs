using System.Security.Cryptography;
using System.Text.Json;

namespace Kumori.Core;

public static class AppDataOrganizer
{
    public static void Organize(string? root = null, DateTimeOffset? now = null)
    {
        root ??= AppPaths.AppDataDir;
        if (!Directory.Exists(root))
        {
            return;
        }

        EnsureStructure(root);
        MoveKnownContent(root);
        EnsureStructure(root);
        DeleteObsoleteRootFiles(root);
        DeleteObsoleteToolFiles(root);
        PruneRuntime(root, now ?? DateTimeOffset.Now);
    }

    private static void EnsureStructure(string root)
    {
        foreach (var directory in new[]
        {
            Path.Combine(root, "config"),
            Path.Combine(root, "data", "tracking"),
            Path.Combine(root, "cache", "beatmaps", "media"),
            Path.Combine(root, "cache", "beatmaps", "covers"),
            Path.Combine(root, "cache", "beatmaps", "files"),
            Path.Combine(root, "assets", "skins"),
            Path.Combine(root, "skins", "Extras", "osu"),
            Path.Combine(root, "skins", "backup"),
            Path.Combine(root, "runtime", "status"),
            Path.Combine(root, "runtime", "viewer-contracts"),
            Path.Combine(root, "runtime", "fixtures"),
            Path.Combine(root, "reports"),
            Path.Combine(root, "tools", "tosu"),
            Path.Combine(root, "logs", "app"),
            Path.Combine(root, "logs", "viewer"),
            Path.Combine(root, "logs", "tosu"),
            Path.Combine(root, "logs", "legacy"),
        })
        {
            Directory.CreateDirectory(directory);
        }
    }

    public static void PruneLogs(string? root = null, DateTimeOffset? now = null, int? retentionDays = null)
    {
        root ??= AppPaths.AppDataDir;
        var days = LogRetentionPolicy.NormalizeDays(retentionDays ?? AppPaths.DefaultLogRetentionDays);
        var cutoff = (now ?? DateTimeOffset.Now).UtcDateTime.AddDays(-days);
        // Prune the complete tree rather than a list of known producers. This
        // automatically covers root logs, helper tools, and future subfolders.
        DeleteOldFiles(Path.Combine(root, "logs"), cutoff);
    }

    public static void PruneRuntime(string? root = null, DateTimeOffset? now = null)
    {
        root ??= AppPaths.AppDataDir;
        var current = now ?? DateTimeOffset.Now;
        var contracts = Path.Combine(root, "runtime", "viewer-contracts");
        DeleteOldFiles(contracts, current.UtcDateTime.AddDays(-3));
        KeepNewestFiles(contracts, 50);
        DeleteOldFiles(Path.Combine(root, "runtime", "fixtures"), current.UtcDateTime.AddDays(-3));
        var debug = Path.Combine(root, "runtime", "debug");
        DeleteFile(Path.Combine(debug, "stable-memory-latest.bin"));
        DeleteOldFiles(debug, current.UtcDateTime.AddDays(-3));

        var viewerRoot = Path.Combine(root, "runtime", "replay-viewer");
        if (!Directory.Exists(viewerRoot)) return;
        foreach (var temporary in Directory.EnumerateDirectories(viewerRoot, "*.extract-*"))
        {
            TryDeleteDirectory(temporary);
        }
        foreach (var obsolete in Directory.EnumerateDirectories(viewerRoot)
                     .Where(path => !Path.GetFileName(path).Contains(".extract-", StringComparison.OrdinalIgnoreCase))
                     .OrderByDescending(Directory.GetLastWriteTimeUtc)
                     .Skip(1))
        {
            TryDeleteDirectory(obsolete);
        }
    }

    private static void MoveKnownContent(string root)
    {
        MoveRootFiles(root, Path.Combine(root, "config"), "settings.v2.json", "settings.json", "settings.json.bak-*", "settings.v2.json.bak-*");
        MoveTrackingDatabaseSet(root, "osu_tracking.sqlite3");
        MoveTrackingDatabaseSet(root, "osu_tracking.net.sqlite3");
        MoveRootDirectory(root, "beatmap-media", Path.Combine(root, "cache", "beatmaps", "media"));
        MoveRootDirectory(root, "beatmap-covers", Path.Combine(root, "cache", "beatmaps", "covers"));
        MoveRootDirectory(root, "beatmap-files", Path.Combine(root, "cache", "beatmaps", "files"));
        MoveLegacyExtrasLibrary(root);
        MoveLegacySkinLibrary(root);
        MoveRootDirectory(root, "fixtures", Path.Combine(root, "runtime", "fixtures"));
        MoveRootDirectory(root, "viewer-contracts", Path.Combine(root, "runtime", "viewer-contracts"));
        MoveRootDirectory(root, "tosu", Path.Combine(root, "tools", "tosu"));
        MoveRootFiles(root, Path.Combine(root, "runtime", "status"), "lazer_replay_frame_status.json", "stable_replay_frame_status.json", "capture_status.json", "tracker_status.json", "otd_telemetry_status");
        MoveRootFiles(root, Path.Combine(root, "runtime", "status"), "osu-history-ui.json");
        MoveRootFiles(root, Path.Combine(root, "data", "tracking"), "osu_key_history.jsonl");
        MoveRootFiles(root, Path.Combine(root, "reports"), "diagnostics-*.txt", "problem-report-*");
        MoveRootFiles(root, Path.Combine(root, "logs", "legacy"), "*.log");
        MoveRootDirectory(Path.Combine(root, "tools", "tosu"), "logs", Path.Combine(root, "logs", "tosu"));
        MoveDirectoryFiles(Path.Combine(root, "logs"), Path.Combine(root, "logs", "app"), "*.log");
    }

    private static void DeleteObsoleteRootFiles(string root)
    {
        DeleteFile(Path.Combine(root, "cache", "beatmaps", ".lazer-linked-cache-v1"));
        foreach (var name in new[] { ".rename-migration-v1", ".shift-migration-v1", "Kumori-Gui-Singleton.pid", "Kumori-Service-Singleton.pid" })
        {
            DeleteFile(Path.Combine(root, name));
        }

        DeleteRootMatches(root, "*.tmp");
        DeleteRootMatches(root, "lazer_replay_frame_status.json.old-*");
    }

    private static void DeleteObsoleteToolFiles(string root)
    {
        var tosuDir = Path.Combine(root, "tools", "tosu");
        if (!Directory.Exists(tosuDir))
        {
            return;
        }

        DeleteFile(Path.Combine(tosuDir, "tosu-kumori.exe"));
        DeleteFile(Path.Combine(tosuDir, "tosu-kumori.download"));
        DeleteFile(Path.Combine(tosuDir, "tosu-kumori.archive.exe"));
        DeleteFile(Path.Combine(tosuDir, "tosu-kumori.new.exe"));

        if (File.Exists(Path.Combine(tosuDir, "tosu.exe")))
        {
            DeleteMatchesExcept(tosuDir, "tosu-*.exe", "tosu.exe");
            DeleteMatches(tosuDir, "tosu-*.env");
            DeleteMatches(tosuDir, "version-*.txt");
            DeleteFile(Path.Combine(tosuDir, "tosu.download"));
            DeleteFile(Path.Combine(tosuDir, "tosu.archive.exe"));
            DeleteFile(Path.Combine(tosuDir, "tosu.new.exe"));
        }
    }

    private static void MoveRootFiles(string root, string destinationDirectory, params string[] patterns)
    {
        foreach (var pattern in patterns)
        {
            foreach (var source in Directory.EnumerateFiles(root, pattern, SearchOption.TopDirectoryOnly).ToArray())
            {
                Directory.CreateDirectory(destinationDirectory);
                var destination = Path.Combine(destinationDirectory, Path.GetFileName(source));
                MoveIfMissing(source, destination);
            }
        }
    }

    private static void MoveDirectoryFiles(string sourceDirectory, string destinationDirectory, params string[] patterns)
    {
        if (!Directory.Exists(sourceDirectory))
        {
            return;
        }

        foreach (var pattern in patterns)
        {
            foreach (var source in Directory.EnumerateFiles(sourceDirectory, pattern, SearchOption.TopDirectoryOnly).ToArray())
            {
                Directory.CreateDirectory(destinationDirectory);
                MoveIfMissing(source, Path.Combine(destinationDirectory, Path.GetFileName(source)));
            }
        }
    }

    private static void MoveRootDirectory(string root, string sourceName, string destination)
    {
        var source = Path.Combine(root, sourceName);
        if (!Directory.Exists(source))
        {
            return;
        }

        if (!Directory.Exists(destination))
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                Directory.Move(source, destination);
                return;
            }
            catch
            {
                // A running companion may lock part of the tree; merge what we can.
            }
        }

        MergeDirectory(source, destination);
        TryDeleteDirectory(source);
    }

    private static void MoveLegacySkinLibrary(string root)
    {
        var source = Path.Combine(root, "skins");
        if (!Directory.Exists(source))
        {
            return;
        }

        var destination = Path.Combine(root, "assets", "skins");
        Directory.CreateDirectory(destination);

        // Skin Studio owns these persistent folders. They must never be fed
        // through the old imported-skin migration on subsequent launches.
        var protectedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Extras",
            "backup",
        };

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.TopDirectoryOnly).ToArray())
        {
            MoveIfMissing(file, Path.Combine(destination, Path.GetFileName(file)));
        }

        foreach (var directory in Directory.EnumerateDirectories(source).ToArray())
        {
            var name = Path.GetFileName(directory);
            if (protectedNames.Contains(name))
            {
                continue;
            }

            MergeDirectory(directory, Path.Combine(destination, name));
            TryDeleteDirectory(directory);
        }
    }

    private static void MoveLegacyExtrasLibrary(string root)
    {
        var container = Path.Combine(root, "skins", "Extras");
        if (!Directory.Exists(container))
        {
            return;
        }

        var destination = Path.Combine(container, "osu");
        Directory.CreateDirectory(destination);

        foreach (var file in Directory.EnumerateFiles(
                     container,
                     "*",
                     SearchOption.TopDirectoryOnly).ToArray())
        {
            MoveIfMissing(file, Path.Combine(destination, Path.GetFileName(file)));
        }

        foreach (var directory in Directory.EnumerateDirectories(container).ToArray())
        {
            var name = Path.GetFileName(directory);
            if (name.Equals("osu", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // The former osu! area is redundant now that the canonical root is
            // already Extras\osu. Keep Interface/Audio/other modes grouped.
            var target = name.Equals("osu!", StringComparison.OrdinalIgnoreCase)
                ? destination
                : Path.Combine(destination, name);
            MergeDirectory(directory, target);
            TryDeleteDirectory(directory);
        }
    }

    private static void MergeDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            MergeDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source))
        {
            MoveIfMissing(file, Path.Combine(destination, Path.GetFileName(file)));
        }
    }

    private static void MoveIfMissing(string source, string destination)
    {
        if (File.Exists(destination) || Directory.Exists(destination))
        {
            MoveToUniqueName(source, destination);
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Move(source, destination);
        }
        catch
        {
            // Best-effort cleanup must never block startup.
        }
    }

    private static void MoveTrackingDatabaseSet(string root, string databaseName)
    {
        var trackingDir = Path.Combine(root, "data", "tracking");
        Directory.CreateDirectory(trackingDir);
        RecoverOwnedDatabaseMigrations(root, trackingDir, databaseName);

        var sourceMain = Path.Combine(root, databaseName);
        if (!File.Exists(sourceMain))
        {
            return;
        }

        var canonicalMain = Path.Combine(trackingDir, databaseName);
        var targetMain = File.Exists(canonicalMain)
            ? UniqueDatabasePath(
                trackingDir,
                $"{Path.GetFileNameWithoutExtension(databaseName)}.legacy-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}",
                Path.GetExtension(databaseName))
            : canonicalMain;

        if (!File.Exists(canonicalMain)
            && DatabaseSuffixes.Skip(1).Any(suffix => File.Exists(canonicalMain + suffix)))
        {
            throw new IOException(
                $"Cannot migrate '{databaseName}' because incomplete destination sidecars already exist.");
        }

        CopyDatabaseSetAtomically(root, databaseName, targetMain);
    }

    private static void CopyDatabaseSetAtomically(
        string sourceDirectory,
        string databaseName,
        string targetMain)
    {
        var sources = DatabaseSuffixes
            .Select(suffix => (Suffix: suffix, Path: Path.Combine(sourceDirectory, databaseName + suffix)))
            .Where(item => File.Exists(item.Path))
            .ToArray();
        if (sources.Length == 0 || !sources.Any(item => item.Suffix.Length == 0))
        {
            throw new IOException($"Tracking database '{databaseName}' has no main database file.");
        }

        var targetDirectory = Path.GetDirectoryName(targetMain)!;
        Directory.CreateDirectory(targetDirectory);
        var targetPaths = sources.ToDictionary(
            item => item.Suffix,
            item => targetMain + item.Suffix,
            StringComparer.Ordinal);
        if (targetPaths.Values.Any(File.Exists))
        {
            throw new IOException($"Tracking database destination '{targetMain}' already exists.");
        }

        var migrationId = Guid.NewGuid().ToString("N");
        var temporaryPaths = targetPaths.ToDictionary(
            item => item.Key,
            item => $"{item.Value}.migrating-{migrationId}",
            StringComparer.Ordinal);
        var journalPath = $"{targetMain}.migration-{migrationId}.json";
        var sourceStreams = new List<FileStream>(sources.Length);
        var promoted = new List<string>(sources.Length);
        DatabaseMigrationJournal? journal = null;
        try
        {
            // Holding every member with FileShare.None prevents a companion
            // process from changing the main database or WAL mid-copy.
            foreach (var source in sources)
            {
                sourceStreams.Add(new FileStream(
                    source.Path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.None,
                    bufferSize: 64 * 1024,
                    FileOptions.SequentialScan));
            }

            for (var index = 0; index < sources.Length; index++)
            {
                var source = sources[index];
                using var destination = new FileStream(
                    temporaryPaths[source.Suffix],
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 64 * 1024,
                    FileOptions.WriteThrough);
                sourceStreams[index].CopyTo(destination);
                destination.Flush(flushToDisk: true);
                if (destination.Length != sourceStreams[index].Length)
                {
                    throw new IOException($"Tracking database copy verification failed for '{source.Path}'.");
                }
            }

            journal = new DatabaseMigrationJournal(
                Version: DatabaseMigrationJournalVersion,
                DatabaseName: databaseName,
                TargetFileName: Path.GetFileName(targetMain),
                MigrationId: migrationId,
                Members: sources.Select(source =>
                {
                    var temporary = temporaryPaths[source.Suffix];
                    return new DatabaseMigrationMember(
                        source.Suffix,
                        new FileInfo(temporary).Length,
                        ComputeSha256(temporary));
                }).ToArray());
            WriteMigrationJournal(journalPath, journal);

            // Publish sidecars first and the main file last. Until the final
            // rename succeeds, consumers cannot observe a partial database set.
            foreach (var suffix in DatabaseSuffixes.Skip(1).Append(string.Empty))
            {
                if (!temporaryPaths.TryGetValue(suffix, out var temporary))
                {
                    continue;
                }
                var target = targetPaths[suffix];
                File.Move(temporary, target);
                promoted.Add(target);
            }
        }
        catch
        {
            RollBackOwnedMigration(journalPath, journal, temporaryPaths, targetPaths, promoted);
            throw;
        }
        finally
        {
            foreach (var stream in sourceStreams)
            {
                stream.Dispose();
            }
        }

        // The complete destination is durable before any source member is
        // removed. Delete the main file last so an interrupted cleanup leaves
        // either a usable source set or an already-usable destination set.
        foreach (var source in sources
                     .OrderBy(item => item.Suffix.Length == 0 ? 1 : 0))
        {
            File.Delete(source.Path);
        }
        File.Delete(journalPath);
    }

    private static void RecoverOwnedDatabaseMigrations(
        string sourceDirectory,
        string trackingDirectory,
        string databaseName)
    {
        var stem = Path.GetFileNameWithoutExtension(databaseName);
        var extension = Path.GetExtension(databaseName);
        var journalPaths = Directory.EnumerateFiles(
                trackingDirectory,
                $"{databaseName}.migration-*.json",
                SearchOption.TopDirectoryOnly)
            .Concat(Directory.EnumerateFiles(
                trackingDirectory,
                $"{stem}.legacy-*{extension}.migration-*.json",
                SearchOption.TopDirectoryOnly))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var journalPath in journalPaths)
        {
            DatabaseMigrationJournal journal;
            try
            {
                if (new FileInfo(journalPath).Length > MaxDatabaseMigrationJournalBytes)
                {
                    throw new IOException(
                        $"Migration journal '{journalPath}' is oversized and was preserved for safety.");
                }
                using var journalStream = new FileStream(
                    journalPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 16 * 1024,
                    FileOptions.SequentialScan);
                journal = JsonSerializer.Deserialize<DatabaseMigrationJournal>(
                    journalStream)
                    ?? throw new IOException($"Migration journal '{journalPath}' is empty.");
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                throw new IOException(
                    $"Migration journal '{journalPath}' is invalid and was preserved for safety.",
                    ex);
            }

            if (!string.Equals(journal.DatabaseName, databaseName, StringComparison.Ordinal))
            {
                continue;
            }

            ValidateMigrationJournal(trackingDirectory, journalPath, journal);
            RecoverOwnedDatabaseMigration(sourceDirectory, trackingDirectory, journalPath, journal);
        }
    }

    private static void RecoverOwnedDatabaseMigration(
        string sourceDirectory,
        string trackingDirectory,
        string journalPath,
        DatabaseMigrationJournal journal)
    {
        var targetMain = Path.Combine(trackingDirectory, journal.TargetFileName);
        var members = journal.Members.ToDictionary(member => member.Suffix, StringComparer.Ordinal);
        var targetMainPublished = File.Exists(targetMain);

        // Validate every path before deleting anything. A name match is not proof
        // of ownership: only bytes recorded in our durable journal may be removed.
        foreach (var suffix in DatabaseSuffixes)
        {
            var target = targetMain + suffix;
            var temporary = $"{target}.migrating-{journal.MigrationId}";
            if (File.Exists(target))
            {
                if (!members.TryGetValue(suffix, out var member) || !FileMatches(target, member))
                {
                    throw new IOException(
                        $"Migration destination conflict '{target}' was preserved for safety.");
                }
            }
            if (File.Exists(temporary)
                && (!members.TryGetValue(suffix, out var temporaryMember)
                    || !FileMatches(temporary, temporaryMember)))
            {
                throw new IOException(
                    $"Migration temporary-file conflict '{temporary}' was preserved for safety.");
            }
        }

        if (targetMainPublished)
        {
            foreach (var member in journal.Members)
            {
                var target = targetMain + member.Suffix;
                if (!File.Exists(target) || !FileMatches(target, member))
                {
                    throw new IOException(
                        $"Published migration destination '{target}' is incomplete or changed.");
                }
            }

            foreach (var member in journal.Members)
            {
                DeleteRequired($"{targetMain}{member.Suffix}.migrating-{journal.MigrationId}");
            }

            // Source cleanup may itself have been interrupted. Delete the
            // remaining subset only when every remaining byte still matches the
            // journal. A changed source is left for normal legacy archiving.
            var remainingSources = DatabaseSuffixes
                .Select(suffix => (Suffix: suffix, Path: Path.Combine(sourceDirectory, journal.DatabaseName + suffix)))
                .Where(item => File.Exists(item.Path))
                .ToArray();
            var sourceStillOwned = remainingSources.All(source =>
                members.TryGetValue(source.Suffix, out var member)
                && FileMatches(source.Path, member));
            if (sourceStillOwned)
            {
                foreach (var source in remainingSources.OrderBy(item => item.Suffix.Length == 0 ? 1 : 0))
                {
                    DeleteRequired(source.Path);
                }
            }

            DeleteRequired(journalPath);
            return;
        }

        // Main is the publication marker. Without it, any matching promoted
        // sidecars are an interrupted copy and can be removed before retrying.
        foreach (var member in journal.Members.OrderByDescending(member => member.Suffix.Length))
        {
            DeleteRequired(targetMain + member.Suffix);
            DeleteRequired($"{targetMain}{member.Suffix}.migrating-{journal.MigrationId}");
        }
        DeleteRequired(journalPath);
    }

    private static void ValidateMigrationJournal(
        string trackingDirectory,
        string journalPath,
        DatabaseMigrationJournal journal)
    {
        if (journal.Version != DatabaseMigrationJournalVersion
            || string.IsNullOrWhiteSpace(journal.DatabaseName)
            || string.IsNullOrWhiteSpace(journal.TargetFileName)
            || !string.Equals(Path.GetFileName(journal.TargetFileName), journal.TargetFileName, StringComparison.Ordinal)
            || journal.MigrationId is null
            || journal.MigrationId.Length != 32
            || journal.MigrationId.Any(character => !Uri.IsHexDigit(character))
            || journal.Members is null
            || journal.Members.Count == 0
            || journal.Members.Any(member =>
                member is null
                || member.Suffix is null
                || member.Sha256 is null
                || !DatabaseSuffixes.Contains(member.Suffix, StringComparer.Ordinal)
                || member.Length < 0
                || member.Sha256.Length != 64
                || member.Sha256.Any(character => !Uri.IsHexDigit(character)))
            || !journal.Members.Any(member => member.Suffix.Length == 0)
            || journal.Members.Select(member => member.Suffix).Distinct(StringComparer.Ordinal).Count() != journal.Members.Count
            )
        {
            throw new IOException(
                $"Migration journal '{journalPath}' is invalid and was preserved for safety.");
        }

        var expectedJournalPath = Path.GetFullPath(
            Path.Combine(trackingDirectory, journal.TargetFileName)
            + $".migration-{journal.MigrationId}.json");
        if (!string.Equals(
                Path.GetFullPath(journalPath),
                expectedJournalPath,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException(
                $"Migration journal '{journalPath}' does not own its claimed destination.");
        }
    }

    private static void RollBackOwnedMigration(
        string journalPath,
        DatabaseMigrationJournal? journal,
        IReadOnlyDictionary<string, string> temporaryPaths,
        IReadOnlyDictionary<string, string> targetPaths,
        IReadOnlyCollection<string> promoted)
    {
        if (journal is null)
        {
            foreach (var temporary in temporaryPaths.Values)
            {
                try { File.Delete(temporary); } catch { }
            }
            return;
        }

        var members = journal.Members.ToDictionary(member => member.Suffix, StringComparer.Ordinal);
        var safeToRemoveJournal = true;
        foreach (var (suffix, path) in temporaryPaths.Concat(
                     targetPaths.Where(item => promoted.Contains(item.Value))))
        {
            if (!File.Exists(path))
            {
                continue;
            }
            if (!members.TryGetValue(suffix, out var member) || !FileMatches(path, member))
            {
                safeToRemoveJournal = false;
                continue;
            }
            try { File.Delete(path); } catch { safeToRemoveJournal = false; }
        }

        if (safeToRemoveJournal)
        {
            try { File.Delete(journalPath); } catch { }
        }
    }

    private static void WriteMigrationJournal(
        string journalPath,
        DatabaseMigrationJournal journal)
    {
        using var stream = new FileStream(
            journalPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 16 * 1024,
            FileOptions.WriteThrough);
        JsonSerializer.Serialize(stream, journal);
        stream.Flush(flushToDisk: true);
    }

    private static bool FileMatches(string path, DatabaseMigrationMember member)
    {
        try
        {
            return new FileInfo(path).Length == member.Length
                && string.Equals(ComputeSha256(path), member.Sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string ComputeSha256(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void DeleteRequired(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private const int DatabaseMigrationJournalVersion = 1;
    private const int MaxDatabaseMigrationJournalBytes = 64 * 1024;

    private sealed record DatabaseMigrationJournal(
        int Version,
        string DatabaseName,
        string TargetFileName,
        string MigrationId,
        IReadOnlyList<DatabaseMigrationMember> Members);

    private sealed record DatabaseMigrationMember(
        string Suffix,
        long Length,
        string Sha256);

    private static string UniqueDatabasePath(string directory, string stem, string extension)
    {
        for (var index = 0; ; index++)
        {
            var suffix = index == 0 ? string.Empty : $"-{index}";
            var candidate = Path.Combine(directory, $"{stem}{suffix}{extension}");
            if (DatabaseSuffixes.All(sidecar => !File.Exists(candidate + sidecar)))
            {
                return candidate;
            }
        }
    }

    private static readonly string[] DatabaseSuffixes = ["", "-wal", "-shm"];

    private static void DeleteRootMatches(string root, string pattern)
    {
        DeleteMatches(root, pattern);
    }

    private static void DeleteMatches(string directory, string pattern)
    {
        foreach (var file in Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly).ToArray())
        {
            DeleteFile(file);
        }
    }

    private static void DeleteMatchesExcept(string directory, string pattern, params string[] keepNames)
    {
        foreach (var file in Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly).ToArray())
        {
            if (keepNames.Any(name => string.Equals(Path.GetFileName(file), name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            DeleteFile(file);
        }
    }

    private static void DeleteOldFiles(string directory, DateTime cutoffUtc)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).ToArray())
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoffUtc)
                {
                    File.Delete(file);
                }
            }
            catch
            {
                // Retention is best-effort; locked logs can wait for the next launch.
            }
        }
    }

    private static void KeepNewestFiles(string directory, int count)
    {
        if (!Directory.Exists(directory)) return;
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                     .OrderByDescending(File.GetLastWriteTimeUtc)
                     .Skip(count))
        {
            DeleteFile(file);
        }
    }

    private static void DeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static void MoveToUniqueName(string source, string preferredDestination)
    {
        if (!File.Exists(source))
        {
            return;
        }

        try
        {
            var destination = UniquePath(preferredDestination);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Move(source, destination);
        }
        catch
        {
        }
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

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }
}
