using Kumori.Core;
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

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
