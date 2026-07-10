using System.Text.Json;
using Kumori.Tracking;
using Xunit;

namespace Kumori.Tracking.Tests;

public class FixturePacketSourceTests : IDisposable
{
    private readonly string _path;

    public FixturePacketSourceTests()
    {
        _path = Path.Combine(Path.GetTempPath(), $"kumori-fixture-{Guid.NewGuid():N}.jsonl");
    }

    [Fact]
    public async Task ReadPacketsAsync_ParsesRecorderFormat()
    {
        // Exactly the format the Python _PacketRecorder writes.
        var lines = new[]
        {
            JsonSerializer.Serialize(new
            {
                wall = 1_700_000_000.5,
                mono = 123.25,
                raw = """{"state": {"name": "Play"}}""",
            }),
            "",                    // blank line tolerated
            "corrupt {{{",         // recorder corruption tolerated
            JsonSerializer.Serialize(new
            {
                wall = 1_700_000_001.0,
                mono = 123.75,
                raw = """{"state": {"name": "ResultScreen"}}""",
            }),
        };
        await File.WriteAllLinesAsync(_path, lines);

        var source = new FixturePacketSource(_path);
        var packets = new List<TosuPacket>();
        await foreach (var p in source.ReadPacketsAsync(CancellationToken.None))
        {
            packets.Add(p);
        }

        Assert.Equal(2, packets.Count);
        Assert.Equal(1, source.SkippedLines);
        Assert.Equal(123.25, packets[0].MonoTime);
        Assert.Contains("Play", packets[0].Raw);

        // End-to-end: fixture → client produces correct states.
        var client = new TosuClient();
        foreach (var p in packets)
        {
            client.Ingest(p);
        }
        Assert.Equal(2, client.PacketCount);
        Assert.True(client.LastSnapshot!.IsResults);
    }

    public void Dispose()
    {
        try { File.Delete(_path); } catch { /* best effort */ }
    }
}
