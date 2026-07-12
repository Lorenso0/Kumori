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

        CacheActivityLog.RecordAddition(cached, "test-source", log);

        Assert.True(File.Exists(log + ".1"));
        using var entry = JsonDocument.Parse(File.ReadAllText(log));
        Assert.Equal("test-source", entry.RootElement.GetProperty("source").GetString());
        Assert.Equal(Path.GetFullPath(cached), entry.RootElement.GetProperty("path").GetString());
        Assert.Equal(3, entry.RootElement.GetProperty("bytes").GetInt64());
        Assert.True(entry.RootElement.TryGetProperty("timestamp_utc", out _));
    }

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch { }
    }
}
