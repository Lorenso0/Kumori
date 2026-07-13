using System.Text.Json;
using Kumori.Core;
using Xunit;

namespace Kumori.Core.Tests;

public sealed class CacheActivityLogTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "kumori-cache-log-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void RecordAddition_WritesStructuredEntryAndRotatesBoundedLog()
    {
        Directory.CreateDirectory(root);
        var cached = Path.Combine(root, "cache", "map.osu");
        Directory.CreateDirectory(Path.GetDirectoryName(cached)!);
        File.WriteAllBytes(cached, [1, 2, 3]);

        var log = Path.Combine(root, "logs", "cache-additions.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        using (var stream = File.Create(log))
        {
            stream.SetLength(5L * 1024 * 1024);
        }

        CacheActivityLog.RecordAddition(
            cached,
            "test-source",
            log,
            reason: "The tracked map needed replay-viewer media.",
            beatmapId: 123,
            beatmapSetId: 456,
            cacheKey: "abc");

        Assert.True(File.Exists(log + ".1"));
        using var entry = JsonDocument.Parse(File.ReadAllText(log));
        Assert.Equal("test-source", entry.RootElement.GetProperty("source").GetString());
        Assert.Equal("beatmap", entry.RootElement.GetProperty("category").GetString());
        Assert.Equal("The tracked map needed replay-viewer media.", entry.RootElement.GetProperty("reason").GetString());
        Assert.Equal(123, entry.RootElement.GetProperty("beatmap_id").GetInt64());
        Assert.Equal(Path.GetFullPath(cached), entry.RootElement.GetProperty("path").GetString());
        Assert.Equal(3, entry.RootElement.GetProperty("bytes").GetInt64());
        Assert.True(entry.RootElement.TryGetProperty("timestamp_utc", out _));

        CacheActivityEntry recent = Assert.Single(CacheActivityLog.ReadRecent(logFile: log));
        Assert.Equal(123, recent.BeatmapId);
        Assert.Equal("abc", recent.CacheKey);
        Assert.Equal("map.osu", recent.FileName);
    }

    [Fact]
    public void RecordAddition_RotatesLogAfterConfiguredNumberOfDays()
    {
        Directory.CreateDirectory(root);
        var cached = Path.Combine(root, "cache", "map.osu");
        Directory.CreateDirectory(Path.GetDirectoryName(cached)!);
        File.WriteAllText(cached, "map");

        var log = Path.Combine(root, "logs", "cache-additions.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        File.WriteAllText(log, JsonSerializer.Serialize(new
        {
            timestamp_utc = DateTimeOffset.UtcNow.AddDays(-8),
            category = "file",
            source = "old-source",
            path = cached,
            file_name = "old.osu",
        }) + Environment.NewLine);

        try
        {
            CacheActivityLog.ConfigureRotationDays(7);
            CacheActivityLog.RecordAddition(cached, "new-source", log);

            Assert.True(File.Exists(log + ".1"));
            Assert.Equal(2, CacheActivityLog.ReadRecent(logFile: log).Count);
            Assert.Equal("new-source", CacheActivityLog.ReadRecent(logFile: log)[0].Source);
        }
        finally
        {
            CacheActivityLog.ConfigureRotationDays(30);
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch { }
    }
}
