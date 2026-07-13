using System.IO.Compression;
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

    public void Dispose()
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
    }
}
