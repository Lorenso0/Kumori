using System.Text.Json;
using Xunit;

namespace Kumori.Tracking.Tests;

public sealed class WebSocketPacketSourceTests
{
    [Fact]
    public void LoopbackReconnectNeverEntersMultiSecondBackoff()
    {
        var uri = WebSocketPacketSource.DefaultUri;
        var delay = WebSocketReconnectPolicy.InitialDelay(uri);

        Assert.Equal(TimeSpan.FromMilliseconds(250), delay);
        Assert.Equal(TimeSpan.FromSeconds(1), WebSocketReconnectPolicy.ConnectTimeout(uri));
        for (var attempt = 0; attempt < 10; attempt++)
            delay = WebSocketReconnectPolicy.NextDelay(uri, delay);

        Assert.Equal(TimeSpan.FromMilliseconds(500), delay);
    }

    [Fact]
    public void RemoteReconnectRetainsBoundedExponentialBackoff()
    {
        var uri = new Uri("wss://example.test/websocket");
        var delay = WebSocketReconnectPolicy.InitialDelay(uri);

        Assert.Equal(TimeSpan.FromSeconds(1), delay);
        Assert.Equal(TimeSpan.FromSeconds(5), WebSocketReconnectPolicy.ConnectTimeout(uri));
        for (var attempt = 0; attempt < 10; attempt++)
            delay = WebSocketReconnectPolicy.NextDelay(uri, delay);

        Assert.Equal(TimeSpan.FromSeconds(15), delay);
    }

    [Fact]
    public async Task PacketRecordingWaitsForGameplayToEndAndConsumerToFinish()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"kumori-packet-recording-{Guid.NewGuid():N}");
        var source = new WebSocketPacketSource(
            recordPackets: true,
            fixtureDirectory: directory);

        try
        {
            var gameplay = new TosuPacket("{\"state\":\"play\"}", 1, 2);
            source.BeginPacketProcessing();
            source.SetGameplayActive(true);
            source.CompletePacketProcessing(gameplay);

            await Task.Delay(100);
            AssertNoRecording(directory);

            var idle = new TosuPacket(
                "{\"state\":\"songSelect\",\"title\":\"café 😀\"}",
                3,
                4);
            source.BeginPacketProcessing();
            source.SetGameplayActive(false);

            // Clearing gameplay during snapshot delivery is not sufficient:
            // the consumer must finish before diagnostic work may begin.
            await Task.Delay(100);
            AssertNoRecording(directory);

            source.CompletePacketProcessing(idle);
            await WaitForLinesAsync(directory, "*.partial", expected: 2);
            await source.DisposeAsync();
            var lines = await WaitForLinesAsync(directory, "*.jsonl", expected: 2);

            using var first = JsonDocument.Parse(lines[0]);
            using var second = JsonDocument.Parse(lines[1]);
            Assert.Equal(gameplay.Raw, first.RootElement.GetProperty("raw").GetString());
            Assert.Equal(idle.Raw, second.RootElement.GetProperty("raw").GetString());
            Assert.Equal(1, first.RootElement.GetProperty("wall").GetDouble());
            Assert.Equal(4, second.RootElement.GetProperty("mono").GetDouble());
        }
        finally
        {
            await source.DisposeAsync();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task InFlightRecordingPausesWithinOneBoundedChunkForGameplay()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"kumori-packet-recording-race-{Guid.NewGuid():N}");
        var source = new WebSocketPacketSource(
            recordPackets: true,
            fixtureDirectory: directory);
        using var writeDispatched = new ManualResetEventSlim();
        using var releaseRecorder = new ManualResetEventSlim();
        var hookInvoked = 0;
        source.RecordingWriteDispatchedForTests = () =>
        {
            if (Interlocked.Exchange(ref hookInvoked, 1) == 0)
            {
                writeDispatched.Set();
                if (!releaseRecorder.Wait(TimeSpan.FromSeconds(3)))
                {
                    throw new TimeoutException("Race test did not release the recorder write.");
                }
            }
        };

        try
        {
            // Quotes take the encoder's longest common escape path, ensuring
            // enough resumable slices and writes to exercise the transition.
            var packet = new TosuPacket(
                new string('"', 64 * 1024),
                10,
                20);
            source.BeginPacketProcessing();
            source.SetGameplayActive(false);
            source.CompletePacketProcessing(packet);

            Assert.True(
                await Task.Run(() => writeDispatched.Wait(TimeSpan.FromSeconds(3))),
                "Recorder never dispatched the bounded test write.");
            Assert.True(source.IsRecordingWorkActive);

            source.SetGameplayActive(true);
            var bytesAtGate = source.TotalRecordingBytesWritten;
            releaseRecorder.Set();
            await WaitForAsync(
                () => !source.IsRecordingWorkActive,
                "Recorder did not yield after gameplay began.");

            var bytesAfterCurrentChunk = source.TotalRecordingBytesWritten;
            Assert.InRange(
                bytesAfterCurrentChunk - bytesAtGate,
                0,
                WebSocketPacketSource.RecordingWriteChunkBytes);

            await Task.Delay(150);
            Assert.Equal(bytesAfterCurrentChunk, source.TotalRecordingBytesWritten);
            Assert.Empty(Directory.Exists(directory)
                ? Directory.EnumerateFiles(directory, "*.jsonl")
                : []);

            // This test is specifically the in-flight gameplay boundary. The
            // normal resume, completed JSON, and publication path is covered
            // by PacketRecordingWaitsForGameplayToEndAndConsumerToFinish.
            // Dispose while still blocked and verify the interrupted packet
            // is discarded rather than being exposed as a complete fixture.
            await source.DisposeAsync();
            Assert.Empty(Directory.EnumerateFiles(directory, "*.partial"));
            Assert.Empty(Directory.EnumerateFiles(directory, "*.jsonl"));
        }
        finally
        {
            releaseRecorder.Set();
            await source.DisposeAsync();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static void AssertNoRecording(string directory)
    {
        Assert.False(
            Directory.Exists(directory)
            && Directory.EnumerateFiles(directory, "*.jsonl").Any());
    }

    private static async Task<string[]> WaitForLinesAsync(
        string directory,
        string pattern,
        int expected)
    {
        // The recorder intentionally runs below normal priority. Whole-solution
        // test runs can briefly starve it while other test hosts compile and
        // execute, so this completion wait must not double as a latency bound.
        // The bounded in-flight gameplay transition is asserted separately.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var path = Directory.Exists(directory)
                ? Directory.EnumerateFiles(directory, pattern).SingleOrDefault()
                : null;
            if (path is not null)
            {
                await using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 4096,
                    useAsync: true);
                using var reader = new StreamReader(stream);
                var content = await reader.ReadToEndAsync();
                var lines = content.Split(
                    ["\r\n", "\n"],
                    StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length >= expected)
                {
                    return lines;
                }
            }

            await Task.Delay(20);
        }

        throw new TimeoutException($"Packet recorder did not write {expected} lines in time.");
    }

    private static async Task WaitForAsync(Func<bool> condition, string failure)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }
            await Task.Delay(1);
        }

        throw new TimeoutException(failure);
    }
}
