using System.IO.Compression;
using System.Text.Json;
using Kumori.Core;
using Kumori.Core.Settings;
using Microsoft.Data.Sqlite;

namespace Kumori.Storage;

public sealed record BackupInfo(string Path, DateTimeOffset CreatedAt, long SizeBytes);

public sealed class BackupService
{
    private readonly string trackingDatabase;
    private readonly string settingsFile;
    private readonly string pendingRestoreDirectory;

    public BackupService(
        string? trackingDatabase = null,
        string? settingsFile = null,
        string? pendingRestoreDirectory = null)
    {
        this.trackingDatabase = trackingDatabase ?? AppPaths.TrackingDatabase;
        this.settingsFile = settingsFile ?? AppPaths.SettingsFile;
        this.pendingRestoreDirectory = pendingRestoreDirectory ?? AppPaths.PendingRestoreDir;
    }

    public IReadOnlyList<BackupInfo> List(KumoriSettings.BackupSettings settings)
    {
        var directory = ResolveDirectory(settings);
        if (!Directory.Exists(directory)) return [];
        var backups = new List<BackupInfo>();
        foreach (var path in Directory.EnumerateFiles(directory, "kumori-backup-*.zip"))
        {
            try
            {
                // Failed versions of Create() could leave a ZIP containing only
                // manifest.json. Never present those partial files as restorable.
                ValidateArchive(path);
                var file = new FileInfo(path);
                backups.Add(new BackupInfo(file.FullName, file.LastWriteTimeUtc, file.Length));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
            }
        }

        return backups.OrderByDescending(backup => backup.CreatedAt).ToArray();
    }

    public string Create(KumoriSettings.BackupSettings settings)
    {
        var directory = ResolveDirectory(settings);
        Directory.CreateDirectory(directory);
        if (!File.Exists(trackingDatabase))
            throw new InvalidOperationException("The tracking database does not exist yet, so there is nothing to back up.");

        var destination = Path.Combine(directory, $"kumori-backup-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fff}.zip");
        var pendingArchive = destination + ".new";
        var temporary = Path.Combine(Path.GetTempPath(), $"kumori-backup-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(temporary);
            var snapshot = Path.Combine(temporary, "osu_tracking.sqlite3");
            var sourceBuilder = new SqliteConnectionStringBuilder
            {
                DataSource = trackingDatabase,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
                DefaultTimeout = 10,
            };
            var targetBuilder = new SqliteConnectionStringBuilder
            {
                DataSource = snapshot,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
                DefaultTimeout = 10,
            };
            using (var source = new SqliteConnection(sourceBuilder.ConnectionString))
            using (var target = new SqliteConnection(targetBuilder.ConnectionString))
            {
                source.Open();
                target.Open();
                source.BackupDatabase(target);
                using var check = target.CreateCommand();
                check.CommandText = "PRAGMA quick_check";
                if (!string.Equals(check.ExecuteScalar() as string, "ok", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The database snapshot failed its integrity check.");
            }

            if (File.Exists(settingsFile)) File.Copy(settingsFile, Path.Combine(temporary, "settings.v2.json"));
            File.WriteAllText(Path.Combine(temporary, "manifest.json"), JsonSerializer.Serialize(new
            {
                format = 1,
                created_at = DateTimeOffset.UtcNow,
                app_version = typeof(BackupService).Assembly.GetName().Version?.ToString(),
            }));
            ZipFile.CreateFromDirectory(temporary, pendingArchive, CompressionLevel.Optimal, false);
            ValidateArchive(pendingArchive);
            File.Move(pendingArchive, destination);
            Prune(settings);
            return destination;
        }
        finally
        {
            try { if (File.Exists(pendingArchive)) File.Delete(pendingArchive); } catch { }
            try { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); } catch { }
        }
    }

    public string? CreateAutomaticIfDue(KumoriSettings.BackupSettings settings)
    {
        if (!settings.AutomaticEnabled || !File.Exists(trackingDatabase)) return null;
        var newest = List(settings).FirstOrDefault();
        return newest is null || DateTimeOffset.UtcNow - newest.CreatedAt >= TimeSpan.FromHours(Math.Clamp(settings.IntervalHours, 1, 24 * 30))
            ? Create(settings)
            : null;
    }

    public void StageRestore(string archivePath)
    {
        if (!File.Exists(archivePath)) throw new FileNotFoundException("Backup archive was not found.", archivePath);
        var staging = pendingRestoreDirectory + ".new";
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        try
        {
            ValidateArchive(archivePath);
            Directory.CreateDirectory(staging);
            ZipFile.ExtractToDirectory(archivePath, staging);
            var database = Path.Combine(staging, "osu_tracking.sqlite3");
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = database,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ConnectionString))
            {
                connection.Open();
                using var check = connection.CreateCommand();
                check.CommandText = "PRAGMA integrity_check";
                if (!string.Equals(check.ExecuteScalar() as string, "ok", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Backup database failed its integrity check.");
            }
            if (Directory.Exists(pendingRestoreDirectory)) Directory.Delete(pendingRestoreDirectory, true);
            Directory.Move(staging, pendingRestoreDirectory);
        }
        catch
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { }
            throw;
        }
    }

    public static void ApplyPendingRestore()
    {
        if (!Directory.Exists(AppPaths.PendingRestoreDir)) return;
        var database = Path.Combine(AppPaths.PendingRestoreDir, "osu_tracking.sqlite3");
        var settings = Path.Combine(AppPaths.PendingRestoreDir, "settings.v2.json");
        var databaseRollback = AppPaths.TrackingDatabase + ".before-restore";
        var wal = AppPaths.TrackingDatabase + "-wal";
        var shm = AppPaths.TrackingDatabase + "-shm";
        var walRollback = wal + ".before-restore";
        var shmRollback = shm + ".before-restore";
        var settingsRollback = AppPaths.SettingsFile + ".before-restore";
        var hadDatabase = File.Exists(AppPaths.TrackingDatabase);
        var hadWal = File.Exists(wal);
        var hadShm = File.Exists(shm);
        var hadSettings = File.Exists(AppPaths.SettingsFile);
        Directory.CreateDirectory(AppPaths.TrackingDataDir);
        Directory.CreateDirectory(AppPaths.ConfigDir);
        ValidateDatabaseFile(database);
        try
        {
            CopyForRollback(AppPaths.TrackingDatabase, databaseRollback);
            CopyForRollback(wal, walRollback);
            CopyForRollback(shm, shmRollback);
            CopyForRollback(AppPaths.SettingsFile, settingsRollback);
            DeleteIfExists(wal);
            DeleteIfExists(shm);
            File.Move(database, AppPaths.TrackingDatabase, true);
            if (File.Exists(settings)) File.Move(settings, AppPaths.SettingsFile, true);
            Directory.Delete(AppPaths.PendingRestoreDir, true);
        }
        catch (Exception restoreFailure)
        {
            var rollbackFailures = new List<Exception>();
            TryRollback(() => RestoreRollback(databaseRollback, AppPaths.TrackingDatabase, hadDatabase), rollbackFailures);
            TryRollback(() => RestoreRollback(walRollback, wal, hadWal), rollbackFailures);
            TryRollback(() => RestoreRollback(shmRollback, shm, hadShm), rollbackFailures);
            TryRollback(() => RestoreRollback(settingsRollback, AppPaths.SettingsFile, hadSettings), rollbackFailures);
            if (rollbackFailures.Count > 0)
            {
                throw new AggregateException(
                    "The backup restore failed and one or more original files could not be restored.",
                    [restoreFailure, .. rollbackFailures]);
            }
            throw;
        }

        // The replacement is committed once the pending directory is gone.
        // Rollback-file cleanup must not undo a successful restore merely
        // because antivirus or indexing briefly holds one of these copies.
        TryDelete(databaseRollback);
        TryDelete(walRollback);
        TryDelete(shmRollback);
        TryDelete(settingsRollback);
    }

    private static void ValidateDatabaseFile(string path)
    {
        if (!File.Exists(path))
            throw new InvalidDataException("The staged restore does not contain a tracking database.");

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ConnectionString);
        connection.Open();
        using var check = connection.CreateCommand();
        check.CommandText = "PRAGMA integrity_check";
        if (!string.Equals(check.ExecuteScalar() as string, "ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Staged backup database failed its integrity check.");
    }

    private static void ValidateArchive(string archivePath)
    {
        const long maximumDatabaseBytes = 4L * 1024 * 1024 * 1024;
        const long maximumSettingsBytes = 16L * 1024 * 1024;
        var allowed = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
        {
            ["osu_tracking.sqlite3"] = maximumDatabaseBytes,
            ["settings.v2.json"] = maximumSettingsBytes,
            ["manifest.json"] = maximumSettingsBytes,
        };
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > allowed.Count)
            throw new InvalidDataException("Backup contains unexpected files.");
        foreach (var entry in archive.Entries)
        {
            if (!allowed.TryGetValue(entry.FullName.Replace('\\', '/'), out var maximum) || entry.Length > maximum)
                throw new InvalidDataException($"Backup entry '{entry.FullName}' is not valid.");
        }
        if (!archive.Entries.Any(entry => string.Equals(entry.FullName, "osu_tracking.sqlite3", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Backup does not contain a tracking database.");
    }

    private static void CopyForRollback(string source, string rollback)
    {
        DeleteIfExists(rollback);
        if (File.Exists(source)) File.Copy(source, rollback);
    }

    private static void RestoreRollback(string rollback, string destination, bool existedBefore)
    {
        if (File.Exists(rollback)) File.Move(rollback, destination, true);
        else if (!existedBefore) DeleteIfExists(destination);
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    private static void TryDelete(string path)
    {
        try { DeleteIfExists(path); } catch { }
    }

    private static void TryRollback(Action rollback, ICollection<Exception> failures)
    {
        try { rollback(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failures.Add(ex);
        }
    }

    private void Prune(KumoriSettings.BackupSettings settings)
    {
        foreach (var backup in List(settings).Skip(Math.Clamp(settings.RetentionCount, 1, 365)))
            try { File.Delete(backup.Path); } catch { }
    }

    private static string ResolveDirectory(KumoriSettings.BackupSettings settings) =>
        string.IsNullOrWhiteSpace(settings.Directory) ? AppPaths.BackupsDir : Path.GetFullPath(settings.Directory);
}
