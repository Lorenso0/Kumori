using System.Security.Cryptography;
using System.Text.Json;
using Kumori.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kumori.Core.Tests;

public sealed class AppDataOrganizerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"kumori-appdata-{Guid.NewGuid():N}");

    public AppDataOrganizerTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void Organize_MigratesActiveRootContent()
    {
        Write("settings.v2.json", "{}");
        Write("settings.json", "{}");
        Write("settings.v2.json.bak-20260708-180509", "{}");
        Write("osu_tracking.sqlite3", "db");
        Write("osu_tracking.sqlite3-wal", "wal");
        Write("native-viewer.log", "legacy log");
        Write("osu-history-ui.json", "{}");
        Write("osu_key_history.jsonl", "{}");
        Write("diagnostics-20260709-120000.txt", "diagnostics");
        Write("problem-report-20260709-120000.zip", "zip");
        Write("lazer_replay_frame_status.json", "{}");
        Write(Path.Combine("beatmap-media", "key", "manifest.json"), "{}");
        Write(Path.Combine("beatmap-covers", "cover.jpg"), "jpg");
        Write(Path.Combine("beatmap-files", "123.osu"), "osu");
        Write(Path.Combine("skins", "skin.osk"), "skin");
        Write(Path.Combine("fixtures", "tosu-1.jsonl"), "{}");
        Write(Path.Combine("viewer-contracts", "1.json"), "{}");
        Write(Path.Combine("tosu", "tosu.exe"), "exe");
        Write(Path.Combine("tosu", "logs", "runtime.log"), "log");
        Write(Path.Combine("logs", "kumori-20260709.log"), "app log");

        AppDataOrganizer.Organize(_root, new DateTimeOffset(2026, 7, 9, 12, 0, 0, TimeSpan.Zero));

        Assert.True(File.Exists(Path.Combine(_root, "config", "settings.v2.json")));
        Assert.True(File.Exists(Path.Combine(_root, "config", "settings.json")));
        Assert.True(File.Exists(Path.Combine(_root, "config", "settings.v2.json.bak-20260708-180509")));
        Assert.True(File.Exists(Path.Combine(_root, "data", "tracking", "osu_tracking.sqlite3")));
        Assert.True(File.Exists(Path.Combine(_root, "data", "tracking", "osu_tracking.sqlite3-wal")));
        Assert.True(File.Exists(Path.Combine(_root, "cache", "beatmaps", "media", "key", "manifest.json")));
        Assert.True(File.Exists(Path.Combine(_root, "cache", "beatmaps", "covers", "cover.jpg")));
        Assert.True(File.Exists(Path.Combine(_root, "cache", "beatmaps", "files", "123.osu")));
        Assert.True(Directory.Exists(Path.Combine(_root, "cache", "beatmaps", "media")));
        Assert.True(File.Exists(Path.Combine(_root, "assets", "skins", "skin.osk")));
        Assert.True(File.Exists(Path.Combine(_root, "runtime", "fixtures", "tosu-1.jsonl")));
        Assert.True(File.Exists(Path.Combine(_root, "runtime", "viewer-contracts", "1.json")));
        Assert.True(File.Exists(Path.Combine(_root, "runtime", "status", "lazer_replay_frame_status.json")));
        Assert.True(File.Exists(Path.Combine(_root, "runtime", "status", "osu-history-ui.json")));
        Assert.True(File.Exists(Path.Combine(_root, "data", "tracking", "osu_key_history.jsonl")));
        Assert.True(File.Exists(Path.Combine(_root, "reports", "diagnostics-20260709-120000.txt")));
        Assert.True(File.Exists(Path.Combine(_root, "reports", "problem-report-20260709-120000.zip")));
        Assert.True(File.Exists(Path.Combine(_root, "logs", "legacy", "native-viewer.log")));
        Assert.True(File.Exists(Path.Combine(_root, "logs", "tosu", "runtime.log")));
        Assert.True(File.Exists(Path.Combine(_root, "logs", "app", "kumori-20260709.log")));
        Assert.True(File.Exists(Path.Combine(_root, "tools", "tosu", "tosu.exe")));
    }

    [Fact]
    public void Organize_DeletesDuplicateTosuArtifactsWhenCanonicalExecutableExists()
    {
        Write(Path.Combine("tools", "tosu", "tosu.exe"), "exe");
        Write(Path.Combine("tools", "tosu", "tosu-kumori.exe"), "old exe");
        Write(Path.Combine("tools", "tosu", "tosu-1.exe"), "old exe");
        Write(Path.Combine("tools", "tosu", "tosu-1.env"), "old env");
        Write(Path.Combine("tools", "tosu", "version-1.txt"), "old version");

        AppDataOrganizer.Organize(_root);

        Assert.True(File.Exists(Path.Combine(_root, "tools", "tosu", "tosu.exe")));
        Assert.False(File.Exists(Path.Combine(_root, "tools", "tosu", "tosu-kumori.exe")));
        Assert.False(File.Exists(Path.Combine(_root, "tools", "tosu", "tosu-1.exe")));
        Assert.False(File.Exists(Path.Combine(_root, "tools", "tosu", "tosu-1.env")));
        Assert.False(File.Exists(Path.Combine(_root, "tools", "tosu", "version-1.txt")));
    }

    [Fact]
    public void Organize_DeletesObsoleteRootFiles()
    {
        Write(".rename-migration-v1", "");
        Write(".shift-migration-v1", "");
        Write("Kumori-Gui-Singleton.pid", "1");
        Write("Kumori-Service-Singleton.pid", "1");
        Write("leftover.tmp", "");
        Write("lazer_replay_frame_status.json.old-20260708-180928", "{}");
        Write(Path.Combine("cache", "beatmaps", ".lazer-linked-cache-v1"), "old migration marker");

        AppDataOrganizer.Organize(_root);

        Assert.Empty(Directory.EnumerateFiles(_root));
        Assert.False(File.Exists(Path.Combine(_root, "cache", "beatmaps", ".lazer-linked-cache-v1")));
    }

    [Fact]
    public void Organize_DoesNotOverwriteExistingDestination()
    {
        Write("settings.v2.json", "old");
        Write(Path.Combine("config", "settings.v2.json"), "new");

        AppDataOrganizer.Organize(_root);

        Assert.Equal("new", File.ReadAllText(Path.Combine(_root, "config", "settings.v2.json")));
        Assert.False(File.Exists(Path.Combine(_root, "settings.v2.json")));
    }

    [Fact]
    public void Organize_DoesNotRotateActiveBeatmapCache()
    {
        Write(Path.Combine("cache", "beatmaps", "media", "old.mp3"), "old");

        AppDataOrganizer.Organize(_root);
        Write(Path.Combine("cache", "beatmaps", "media", "new.mp3"), "new");
        AppDataOrganizer.Organize(_root);

        Assert.True(File.Exists(Path.Combine(_root, "cache", "beatmaps", "media", "old.mp3")));
        Assert.True(File.Exists(Path.Combine(_root, "cache", "beatmaps", "media", "new.mp3")));
    }

    [Fact]
    public void Organize_MigratesCompleteWalDatabaseWithoutLosingCommittedRows()
    {
        var builderDirectory = Path.Combine(_root, "wal-builder");
        Directory.CreateDirectory(builderDirectory);
        var builderPath = Path.Combine(builderDirectory, "tracking.sqlite3");
        using (var connection = new SqliteConnection($"Data Source={builderPath};Pooling=False"))
        {
            connection.Open();
            Execute(connection, "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0;");
            Execute(connection, "CREATE TABLE entries(value TEXT NOT NULL);");
            Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
            Execute(connection, "INSERT INTO entries(value) VALUES('committed-in-wal');");

            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                var source = builderPath + suffix;
                if (File.Exists(source))
                {
                    File.Copy(source, Path.Combine(_root, "osu_tracking.sqlite3" + suffix));
                }
            }
        }
        Directory.Delete(builderDirectory, recursive: true);

        AppDataOrganizer.Organize(_root);

        var migrated = Path.Combine(_root, "data", "tracking", "osu_tracking.sqlite3");
        using var verification = new SqliteConnection($"Data Source={migrated};Pooling=False");
        verification.Open();
        using var command = verification.CreateCommand();
        command.CommandText = "SELECT value FROM entries";
        Assert.Equal("committed-in-wal", command.ExecuteScalar());
        Assert.False(File.Exists(Path.Combine(_root, "osu_tracking.sqlite3")));
    }

    [Fact]
    public void Organize_WhenBothDatabasesExistPreservesCanonicalAndArchivesLegacySet()
    {
        var canonical = Path.Combine(_root, "data", "tracking", "osu_tracking.sqlite3");
        var legacy = Path.Combine(_root, "osu_tracking.sqlite3");
        CreateDatabase(canonical, "canonical");
        CreateDatabase(legacy, new string('L', 32_000));

        AppDataOrganizer.Organize(_root);

        Assert.Equal("canonical", ReadDatabaseValue(canonical));
        var archived = Assert.Single(Directory.EnumerateFiles(
            Path.GetDirectoryName(canonical)!,
            "osu_tracking.legacy-*.sqlite3"));
        Assert.Equal(new string('L', 32_000), ReadDatabaseValue(archived));
        Assert.False(File.Exists(legacy));
    }

    [Fact]
    public void Organize_IncompleteDestinationLeavesSourceUntouchedAndReportsFailure()
    {
        var source = Write("osu_tracking.sqlite3", "source");
        var destinationMain = Path.Combine(_root, "data", "tracking", "osu_tracking.sqlite3");
        Write(Path.Combine("data", "tracking", "osu_tracking.sqlite3-wal"), "orphan");

        Assert.Throws<IOException>(() => AppDataOrganizer.Organize(_root));

        Assert.True(File.Exists(source));
        Assert.False(File.Exists(destinationMain));
        Assert.True(File.Exists(destinationMain + "-wal"));
    }

    [Fact]
    public void Organize_RecoversOwnedInterruptedSidecarPromotionAndRetries()
    {
        var sourceMain = Write("osu_tracking.sqlite3", "source-main");
        var sourceWal = Write("osu_tracking.sqlite3-wal", "source-wal");
        var targetMain = Path.Combine(_root, "data", "tracking", "osu_tracking.sqlite3");
        var migrationId = Guid.NewGuid().ToString("N");
        var promotedWal = Write(
            Path.Combine("data", "tracking", "osu_tracking.sqlite3-wal"),
            File.ReadAllText(sourceWal));
        var temporaryMain = Write(
            Path.Combine("data", "tracking", $"osu_tracking.sqlite3.migrating-{migrationId}"),
            File.ReadAllText(sourceMain));
        var journal = WriteMigrationJournal(
            targetMain,
            migrationId,
            ("", sourceMain),
            ("-wal", sourceWal));

        AppDataOrganizer.Organize(_root);

        Assert.Equal("source-main", File.ReadAllText(targetMain));
        Assert.Equal("source-wal", File.ReadAllText(targetMain + "-wal"));
        Assert.False(File.Exists(sourceMain));
        Assert.False(File.Exists(sourceWal));
        Assert.False(File.Exists(promotedWal + $".migrating-{migrationId}"));
        Assert.False(File.Exists(temporaryMain));
        Assert.False(File.Exists(journal));
    }

    [Fact]
    public void Organize_PreservesUnknownSidecarThatConflictsWithOwnedJournal()
    {
        var sourceMain = Write("osu_tracking.sqlite3", "source-main");
        var sourceWal = Write("osu_tracking.sqlite3-wal", "source-wal");
        var targetMain = Path.Combine(_root, "data", "tracking", "osu_tracking.sqlite3");
        var migrationId = Guid.NewGuid().ToString("N");
        var conflictingWal = Write(
            Path.Combine("data", "tracking", "osu_tracking.sqlite3-wal"),
            "unknown-owner");
        var temporaryMain = Write(
            Path.Combine("data", "tracking", $"osu_tracking.sqlite3.migrating-{migrationId}"),
            File.ReadAllText(sourceMain));
        var journal = WriteMigrationJournal(
            targetMain,
            migrationId,
            ("", sourceMain),
            ("-wal", sourceWal));

        Assert.Throws<IOException>(() => AppDataOrganizer.Organize(_root));

        Assert.Equal("source-main", File.ReadAllText(sourceMain));
        Assert.Equal("source-wal", File.ReadAllText(sourceWal));
        Assert.Equal("unknown-owner", File.ReadAllText(conflictingWal));
        Assert.True(File.Exists(temporaryMain));
        Assert.True(File.Exists(journal));
        Assert.False(File.Exists(targetMain));
    }

    [Fact]
    public void PruneLogs_RecursivelyDeletesAnyLogOlderThanConfiguredRetention()
    {
        var now = new DateTimeOffset(2026, 7, 9, 12, 0, 0, TimeSpan.Zero);
        var old = Write(Path.Combine("logs", "app", "old.log"), "old");
        var recent = Write(Path.Combine("logs", "viewer", "recent.log"), "recent");
        var futureProducer = Write(Path.Combine("logs", "future-tool", "nested", "old.jsonl"), "old");
        var rootLog = Write(Path.Combine("logs", "old-root.log"), "old");
        File.SetLastWriteTimeUtc(old, now.UtcDateTime.AddDays(-4));
        File.SetLastWriteTimeUtc(recent, now.UtcDateTime.AddDays(-2));
        File.SetLastWriteTimeUtc(futureProducer, now.UtcDateTime.AddDays(-4));
        File.SetLastWriteTimeUtc(rootLog, now.UtcDateTime.AddDays(-4));

        AppDataOrganizer.PruneLogs(_root, now, retentionDays: 3);

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
        Assert.False(File.Exists(futureProducer));
        Assert.False(File.Exists(rootLog));
    }

    [Fact]
    public void PruneRuntime_RetainsCurrentViewerAndExpiresPrivateData()
    {
        var now = new DateTimeOffset(2026, 7, 9, 12, 0, 0, TimeSpan.Zero);
        var oldestViewer = Write(Path.Combine("runtime", "replay-viewer", "v1", "viewer.exe"), "old");
        var previousViewer = Write(Path.Combine("runtime", "replay-viewer", "v2", "viewer.exe"), "previous");
        var currentViewer = Write(Path.Combine("runtime", "replay-viewer", "v3", "viewer.exe"), "current");
        Directory.SetLastWriteTimeUtc(Path.GetDirectoryName(oldestViewer)!, now.UtcDateTime.AddDays(-3));
        Directory.SetLastWriteTimeUtc(Path.GetDirectoryName(previousViewer)!, now.UtcDateTime.AddDays(-2));
        Directory.SetLastWriteTimeUtc(Path.GetDirectoryName(currentViewer)!, now.UtcDateTime.AddDays(-1));
        var oldContract = Write(Path.Combine("runtime", "viewer-contracts", "old.json"), "private");
        var oldFixture = Write(Path.Combine("runtime", "fixtures", "old.jsonl"), "private");
        var debugSnapshot = Write(Path.Combine("runtime", "debug", "stable-memory-latest.bin"), "private");
        File.SetLastWriteTimeUtc(oldContract, now.UtcDateTime.AddDays(-8));
        File.SetLastWriteTimeUtc(oldFixture, now.UtcDateTime.AddDays(-4));

        AppDataOrganizer.PruneRuntime(_root, now);

        Assert.False(Directory.Exists(Path.GetDirectoryName(oldestViewer)));
        Assert.False(File.Exists(previousViewer));
        Assert.True(File.Exists(currentViewer));
        Assert.False(File.Exists(oldContract));
        Assert.False(File.Exists(oldFixture));
        Assert.False(File.Exists(debugSnapshot));
    }

    private string Write(string relativePath, string content)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static void CreateDatabase(string path, string value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        Execute(connection, "CREATE TABLE entries(value TEXT NOT NULL)");
        using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO entries(value) VALUES(@value)";
        insert.Parameters.AddWithValue("@value", value);
        insert.ExecuteNonQuery();
    }

    private static string ReadDatabaseValue(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM entries";
        return Assert.IsType<string>(command.ExecuteScalar());
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string WriteMigrationJournal(
        string targetMain,
        string migrationId,
        params (string Suffix, string SourcePath)[] members)
    {
        var journalPath = $"{targetMain}.migration-{migrationId}.json";
        Directory.CreateDirectory(Path.GetDirectoryName(journalPath)!);
        var payload = new
        {
            Version = 1,
            DatabaseName = "osu_tracking.sqlite3",
            TargetFileName = Path.GetFileName(targetMain),
            MigrationId = migrationId,
            Members = members.Select(member => new
            {
                member.Suffix,
                Length = new FileInfo(member.SourcePath).Length,
                Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(member.SourcePath))),
            }).ToArray(),
        };
        File.WriteAllText(journalPath, JsonSerializer.Serialize(payload));
        return journalPath;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
