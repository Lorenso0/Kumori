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
        DeleteObsoleteRootFiles(root);
        DeleteObsoleteToolFiles(root);
        PruneLogs(root, now ?? DateTimeOffset.Now);
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

    public static void PruneLogs(string? root = null, DateTimeOffset? now = null)
    {
        root ??= AppPaths.AppDataDir;
        var cutoff = (now ?? DateTimeOffset.Now).UtcDateTime.AddDays(-AppPaths.LogRetentionDays);
        foreach (var dir in LogDirectories(root))
        {
            DeleteOldFiles(dir, cutoff);
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
        MoveRootDirectory(root, "skins", Path.Combine(root, "assets", "skins"));
        MoveRootDirectory(root, "fixtures", Path.Combine(root, "runtime", "fixtures"));
        MoveRootDirectory(root, "viewer-contracts", Path.Combine(root, "runtime", "viewer-contracts"));
        MoveRootDirectory(root, "tosu", Path.Combine(root, "tools", "tosu"));
        MoveRootFiles(root, Path.Combine(root, "runtime", "status"), "lazer_replay_frame_status.json", "capture_status.json", "tracker_status.json", "otd_telemetry_status");
        MoveRootFiles(root, Path.Combine(root, "runtime", "status"), "osu-history-ui.json");
        MoveRootFiles(root, Path.Combine(root, "data", "tracking"), "osu_key_history.jsonl");
        MoveRootFiles(root, Path.Combine(root, "reports"), "diagnostics-*.txt", "problem-report-*");
        MoveRootFiles(root, Path.Combine(root, "logs", "legacy"), "*.log");
        MoveRootDirectory(Path.Combine(root, "tools", "tosu"), "logs", Path.Combine(root, "logs", "tosu"));
        MoveDirectoryFiles(Path.Combine(root, "logs"), Path.Combine(root, "logs", "app"), "*.log");
    }

    private static void DeleteObsoleteRootFiles(string root)
    {
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
        var source = Path.Combine(root, databaseName);
        if (!File.Exists(source))
        {
            return;
        }

        Directory.CreateDirectory(trackingDir);
        var destination = Path.Combine(trackingDir, databaseName);
        if (!File.Exists(destination))
        {
            MoveDatabaseSidecars(root, trackingDir, databaseName);
            return;
        }

        var sourceLength = SafeLength(source);
        var destinationLength = SafeLength(destination);
        if (sourceLength > destinationLength)
        {
            MoveExistingDatabaseAside(trackingDir, databaseName);
            MoveDatabaseSidecars(root, trackingDir, databaseName);
            return;
        }

        MoveDatabaseSidecars(root, trackingDir, databaseName, legacy: true);
    }

    private static void MoveDatabaseSidecars(string sourceDir, string destinationDir, string databaseName, bool legacy = false)
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var source = Path.Combine(sourceDir, databaseName + suffix);
            if (!File.Exists(source))
            {
                continue;
            }

            var destinationName = legacy
                ? $"{Path.GetFileNameWithoutExtension(databaseName)}.legacy-{DateTimeOffset.Now:yyyyMMddHHmmss}{Path.GetExtension(databaseName)}{suffix}"
                : databaseName + suffix;
            MoveIfMissing(source, Path.Combine(destinationDir, destinationName));
        }
    }

    private static void MoveExistingDatabaseAside(string trackingDir, string databaseName)
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var path = Path.Combine(trackingDir, databaseName + suffix);
            if (!File.Exists(path))
            {
                continue;
            }

            MoveToUniqueName(path, Path.Combine(trackingDir, $"{Path.GetFileNameWithoutExtension(databaseName)}.pre-migration{Path.GetExtension(databaseName)}{suffix}"));
        }
    }

    private static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; } catch { return 0; }
    }

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

    private static IEnumerable<string> LogDirectories(string root)
    {
        yield return Path.Combine(root, "logs", "app");
        yield return Path.Combine(root, "logs", "viewer");
        yield return Path.Combine(root, "logs", "tosu");
        yield return Path.Combine(root, "logs", "legacy");
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
