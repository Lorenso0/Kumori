using System.IO.Compression;
using System.Text.Json;
using Kumori.Core;
using Kumori.Core.Settings;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kumori.Storage.Tests;

public sealed class BackupServiceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"kumori-backup-tests-{Guid.NewGuid():N}");

    [Fact]
    public void Create_ProducesCompleteRestorableArchiveAndListIgnoresPartialZip()
    {
        Directory.CreateDirectory(root);
        var database = Path.Combine(root, "tracking.sqlite3");
        var settingsFile = Path.Combine(root, "settings.v2.json");
        var backupDirectory = Path.Combine(root, "backups");
        var pendingRestore = Path.Combine(root, "pending-restore");
        CreateDatabase(database);
        File.WriteAllText(settingsFile, "{\"test\":true}");
        var settings = new KumoriSettings.BackupSettings { Directory = backupDirectory };
        var service = new BackupService(database, settingsFile, pendingRestore);

        // Match the application: the source database remains open in WAL mode
        // while the backup service takes its consistent SQLite snapshot.
        using var liveWriter = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ConnectionString);
        liveWriter.Open();
        using (var write = liveWriter.CreateCommand())
        {
            write.CommandText = "PRAGMA journal_mode=WAL; INSERT INTO sample(value) VALUES('committed while open');";
            write.ExecuteNonQuery();
        }

        var archivePath = service.Create(settings);

        Assert.True(File.Exists(archivePath));
        using (var archive = ZipFile.OpenRead(archivePath))
        {
            Assert.Contains(archive.Entries, entry => entry.FullName == "osu_tracking.sqlite3" && entry.Length > 0);
            Assert.Contains(archive.Entries, entry => entry.FullName == "settings.v2.json");
            Assert.Contains(archive.Entries, entry => entry.FullName == "manifest.json");
        }

        var partial = Path.Combine(backupDirectory, "kumori-backup-20000101-000000-000.zip");
        using (var archive = ZipFile.Open(partial, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(archive.CreateEntry("manifest.json").Open()))
            writer.Write("{}");

        var listed = Assert.Single(service.List(settings));
        Assert.Equal(archivePath, listed.Path);

        service.StageRestore(archivePath);
        var restoredDatabase = Path.Combine(pendingRestore, "osu_tracking.sqlite3");
        using var restored = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = restoredDatabase,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ConnectionString);
        restored.Open();
        using var command = restored.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sample";
        Assert.Equal(2L, command.ExecuteScalar());
    }

    [Fact]
    public void StageRestore_DisablesExternalActionsFromBackedUpSettings()
    {
        Directory.CreateDirectory(root);
        var database = Path.Combine(root, "tracking.sqlite3");
        var settingsFile = Path.Combine(root, "settings.v2.json");
        var backupDirectory = Path.Combine(root, "backups");
        var pendingRestore = Path.Combine(root, "pending-restore");
        CreateDatabase(database);

        var dangerous = new KumoriSettings();
        dangerous.Appearance.ThemeId = "pulse";
        dangerous.OpenTabletDriver.AutoLaunch = true;
        dangerous.OpenTabletDriver.InstallPath = @"\\attacker\share\payload.exe";
        dangerous.Startup.RunAtLogin = true;
        dangerous.Startup.StartMinimized = true;
        dangerous.Startup.ExecutablePath = @"C:\Temp\payload.exe";
        dangerous.Display.AutoSwitchDualMode = true;
        dangerous.Tracking.MinimumAttemptSeconds = int.MaxValue;
        dangerous.Tracking.RetentionDays = 1;
        dangerous.Tracking.PacketRecordingEnabled = true;
        dangerous.Media.PrimaryMirror = "https://attacker.invalid";
        dangerous.Media.FallbackMirrors.Add("https://fallback.invalid");
        dangerous.ReplayViewer.SkinPath = @"\\attacker\share\skin.osk";
        dangerous.Backup.Directory = @"\\attacker\drop";
        dangerous.Backup.IntervalHours = 1;
        dangerous.Backup.RetentionCount = 1;
        dangerous.Developer.LogRetentionDays = 1;
        dangerous.Developer.ForceReplayRecoveryNextPlay = true;
        File.WriteAllText(settingsFile, JsonSerializer.Serialize(dangerous));

        var service = new BackupService(database, settingsFile, pendingRestore);
        var archive = service.Create(new KumoriSettings.BackupSettings { Directory = backupDirectory });
        service.StageRestore(archive);

        var restored = JsonSerializer.Deserialize<KumoriSettings>(
            File.ReadAllText(Path.Combine(pendingRestore, "settings.v2.json")))!;
        Assert.Equal("pulse", restored.Appearance.ThemeId);
        Assert.False(restored.OpenTabletDriver.AutoLaunch);
        Assert.Empty(restored.OpenTabletDriver.InstallPath);
        Assert.False(restored.Startup.RunAtLogin);
        Assert.False(restored.Startup.StartMinimized);
        Assert.Empty(restored.Startup.ExecutablePath);
        Assert.False(restored.Display.AutoSwitchDualMode);
        Assert.Equal(3, restored.Tracking.MinimumAttemptSeconds);
        Assert.Equal(0, restored.Tracking.RetentionDays);
        Assert.False(restored.Tracking.PacketRecordingEnabled);
        Assert.Equal("https://api.rai.moe", restored.Media.PrimaryMirror);
        Assert.Empty(restored.Media.FallbackMirrors);
        Assert.Empty(restored.ReplayViewer.SkinPath);
        Assert.Empty(restored.Backup.Directory);
        Assert.Equal(24, restored.Backup.IntervalHours);
        Assert.Equal(14, restored.Backup.RetentionCount);
        Assert.Equal(AppPaths.DefaultLogRetentionDays, restored.Developer.LogRetentionDays);
        Assert.False(restored.Developer.ForceReplayRecoveryNextPlay);
    }

    [Fact]
    public void Create_WithInvalidDatabaseLeavesNoVisibleArchive()
    {
        Directory.CreateDirectory(root);
        var database = Path.Combine(root, "invalid.sqlite3");
        var backupDirectory = Path.Combine(root, "backups");
        File.WriteAllText(database, "not a database");
        var settings = new KumoriSettings.BackupSettings { Directory = backupDirectory };
        var service = new BackupService(database);

        Assert.ThrowsAny<SqliteException>(() => service.Create(settings));
        Assert.Empty(Directory.EnumerateFiles(backupDirectory, "*.zip"));
        Assert.Empty(Directory.EnumerateFiles(backupDirectory, "*.new"));
    }

    [Fact]
    public void CreateAutomaticIfDue_WithCancellationLeavesNoArchiveArtifacts()
    {
        Directory.CreateDirectory(root);
        var database = Path.Combine(root, "tracking.sqlite3");
        var backupDirectory = Path.Combine(root, "backups");
        CreateDatabase(database);
        var settings = new KumoriSettings.BackupSettings { Directory = backupDirectory };
        var service = new BackupService(database);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            service.CreateAutomaticIfDue(settings, cancelled.Token));
        Assert.False(Directory.Exists(backupDirectory));
    }

    [Fact]
    public void CreateAutomaticIfDue_InvalidNewerZipCannotSuppressBackup()
    {
        Directory.CreateDirectory(root);
        var database = Path.Combine(root, "tracking.sqlite3");
        var backupDirectory = Path.Combine(root, "backups");
        CreateDatabase(database);
        var settings = new KumoriSettings.BackupSettings
        {
            Directory = backupDirectory,
            IntervalHours = 1,
        };
        var service = new BackupService(database);
        var now = DateTimeOffset.UtcNow;
        WriteCatalogArchive(
            backupDirectory,
            "kumori-backup-old-valid.zip",
            now.AddDays(-2));
        var invalid = WriteCatalogArchive(
            backupDirectory,
            "kumori-backup-newer-invalid.zip",
            now.AddDays(2),
            format: 2);
        File.SetLastWriteTimeUtc(invalid, now.AddDays(2).UtcDateTime);

        var created = service.CreateAutomaticIfDue(settings);

        Assert.NotNull(created);
        Assert.True(File.Exists(created));
        Assert.True(File.Exists(invalid));
        Assert.Equal(2, service.List(settings).Count);
    }

    [Fact]
    public void Create_InvalidZipNeverCountsTowardRetention()
    {
        Directory.CreateDirectory(root);
        var database = Path.Combine(root, "tracking.sqlite3");
        var backupDirectory = Path.Combine(root, "backups");
        CreateDatabase(database);
        var settings = new KumoriSettings.BackupSettings
        {
            Directory = backupDirectory,
            RetentionCount = 2,
        };
        var service = new BackupService(database);
        var now = DateTimeOffset.UtcNow;
        var oldest = WriteCatalogArchive(
            backupDirectory,
            "kumori-backup-oldest.zip",
            now.AddDays(-3));
        var newer = WriteCatalogArchive(
            backupDirectory,
            "kumori-backup-newer.zip",
            now.AddDays(-2));
        var invalid = WriteCatalogArchive(
            backupDirectory,
            "kumori-backup-invalid.zip",
            now.AddDays(2),
            format: 2);
        File.SetLastWriteTimeUtc(oldest, now.AddDays(-3).UtcDateTime);
        File.SetLastWriteTimeUtc(newer, now.AddDays(-2).UtcDateTime);
        File.SetLastWriteTimeUtc(invalid, now.AddDays(2).UtcDateTime);

        service.Create(settings);

        Assert.False(File.Exists(oldest));
        Assert.True(File.Exists(newer));
        Assert.True(File.Exists(invalid));
        Assert.Equal(2, service.List(settings).Count);
    }

    [Fact]
    public void List_UsesManifestCreatedAtInsteadOfTouchedFileTime()
    {
        var backupDirectory = Path.Combine(root, "backups");
        var settings = new KumoriSettings.BackupSettings { Directory = backupDirectory };
        var service = new BackupService();
        var olderCreatedAt = new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var newerCreatedAt = olderCreatedAt.AddDays(1);
        var older = WriteCatalogArchive(
            backupDirectory,
            "kumori-backup-older.zip",
            olderCreatedAt);
        var newer = WriteCatalogArchive(
            backupDirectory,
            "kumori-backup-newer.zip",
            newerCreatedAt);
        File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddDays(10));
        File.SetLastWriteTimeUtc(newer, DateTime.UtcNow.AddDays(-10));

        var listed = service.List(settings);

        Assert.Equal([newer, older], listed.Select(backup => backup.Path));
        Assert.Equal([newerCreatedAt, olderCreatedAt], listed.Select(backup => backup.CreatedAt));
    }

    [Fact]
    public void List_RequiresUniqueNonEmptyDatabaseAndSmallValidManifest()
    {
        var backupDirectory = Path.Combine(root, "backups");
        var settings = new KumoriSettings.BackupSettings { Directory = backupDirectory };
        var service = new BackupService();
        var createdAt = new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var valid = WriteCatalogArchive(backupDirectory, "kumori-backup-valid.zip", createdAt);
        WriteCatalogArchive(
            backupDirectory,
            "kumori-backup-empty-database.zip",
            createdAt,
            emptyDatabase: true);
        WriteCatalogArchive(
            backupDirectory,
            "kumori-backup-duplicate-database.zip",
            createdAt,
            databaseCopies: 2);
        WriteCatalogArchive(
            backupDirectory,
            "kumori-backup-duplicate-manifest.zip",
            createdAt,
            manifestCopies: 2);
        WriteCatalogArchive(
            backupDirectory,
            "kumori-backup-invalid-format.zip",
            createdAt,
            format: 2);
        WriteCatalogArchive(
            backupDirectory,
            "kumori-backup-invalid-date.zip",
            createdAt,
            manifestJson: """{"format":1,"created_at":"not-a-date"}""");
        WriteCatalogArchive(
            backupDirectory,
            "kumori-backup-future-date.zip",
            DateTimeOffset.UtcNow.AddDays(1));
        WriteCatalogArchive(
            backupDirectory,
            "kumori-backup-large-manifest.zip",
            createdAt,
            manifestJson: JsonSerializer.Serialize(new
            {
                format = 1,
                created_at = createdAt,
                padding = new string('x', 64 * 1024),
            }));

        var listed = Assert.Single(service.List(settings));

        Assert.Equal(valid, listed.Path);
        Assert.Equal(8, Directory.EnumerateFiles(backupDirectory, "*.zip").Count());
    }

    [Fact]
    public void CopyStream_StopsBeforeWritingAChunkThatTriggeredCancellation()
    {
        using var cancelled = new CancellationTokenSource();
        using var source = new CancellingReadStream(cancelled);
        using var destination = new MemoryStream();

        Assert.Throws<OperationCanceledException>(() =>
            BackupService.CopyStream(source, destination, cancelled.Token));
        Assert.Equal(0, destination.Length);
    }

    private static void CreateDatabase(string path)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE sample(id INTEGER PRIMARY KEY, value TEXT); INSERT INTO sample(value) VALUES('complete snapshot');";
        command.ExecuteNonQuery();
    }

    private static string WriteCatalogArchive(
        string directory,
        string fileName,
        DateTimeOffset createdAt,
        int format = 1,
        bool emptyDatabase = false,
        int databaseCopies = 1,
        int manifestCopies = 1,
        string? manifestJson = null)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        for (var i = 0; i < databaseCopies; i++)
        {
            using var database = archive.CreateEntry("osu_tracking.sqlite3").Open();
            if (!emptyDatabase)
            {
                database.WriteByte(1);
            }
        }

        manifestJson ??= JsonSerializer.Serialize(new { format, created_at = createdAt });
        for (var i = 0; i < manifestCopies; i++)
        {
            using var writer = new StreamWriter(archive.CreateEntry("manifest.json").Open());
            writer.Write(manifestJson);
        }

        return path;
    }

    public void Dispose()
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
    }

    private sealed class CancellingReadStream(CancellationTokenSource cancellation) : MemoryStream(new byte[128 * 1024])
    {
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = base.Read(buffer, offset, count);
            cancellation.Cancel();
            return read;
        }
    }
}
