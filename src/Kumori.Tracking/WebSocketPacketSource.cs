using System.Diagnostics;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using Kumori.Core;
using Serilog;

namespace Kumori.Tracking;

/// <summary>
/// Live packet source: connects to tosu's websocket and yields raw messages.
/// Kumori runs managed vanilla tosu on dedicated port 24051, 8 MB max message.
/// Reconnects with backoff; connection state is surfaced via events so the
/// tracking service can publish health.
/// </summary>
public sealed class WebSocketPacketSource : ITosuPacketSource, IAsyncDisposable
{
    public const int TosuPort = 24051;
    public static readonly Uri DefaultUri = new($"ws://127.0.0.1:{TosuPort}/websocket/v2");

    private const int MaxMessageBytes = 8 * 1024 * 1024;
    private const long MaxRecordingBytes = 25L * 1024 * 1024;
    private static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(15);

    private readonly Uri _uri;
    private readonly bool _recordPackets;
    private readonly string _fixtureDirectory;
    private StreamWriter? _recording;
    private long _recordingBytes;
    private DateTimeOffset _lastRecordingFlush = DateTimeOffset.MinValue;

    public event Action? Connected;
    public event Action<string?>? Disconnected;

    public WebSocketPacketSource(Uri? uri = null, bool recordPackets = false, string? fixtureDirectory = null)
    {
        _uri = uri ?? DefaultUri;
        _recordPackets = recordPackets;
        _fixtureDirectory = fixtureDirectory ?? AppPaths.FixturesDir;
    }

    public async IAsyncEnumerable<TosuPacket> ReadPacketsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var backoff = MinBackoff;
        while (!cancellationToken.IsCancellationRequested)
        {
            using var socket = new ClientWebSocket();
            socket.Options.SetBuffer(64 * 1024, 64 * 1024);
            var connected = false;
            try
            {
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                connectCts.CancelAfter(TimeSpan.FromSeconds(5));
                await socket.ConnectAsync(_uri, connectCts.Token);
                connected = true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                yield break;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "tosu websocket connect failed for {Uri}", _uri);
                Disconnected?.Invoke(ex.Message);
            }

            if (connected)
            {
                Connected?.Invoke();
                backoff = MinBackoff;
                await foreach (var packet in ReceiveLoopAsync(socket, cancellationToken))
                {
                    Record(packet);
                    yield return packet;
                }
                if (cancellationToken.IsCancellationRequested)
                {
                    yield break;
                }
                Disconnected?.Invoke("connection closed");
            }

            try
            {
                await Task.Delay(backoff, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
            backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, MaxBackoff.TotalSeconds));
        }
    }

    private void Record(TosuPacket packet)
    {
        if (!_recordPackets)
        {
            return;
        }
        try
        {
            if (_recording is null)
            {
                Directory.CreateDirectory(_fixtureDirectory);
                var path = Path.Combine(_fixtureDirectory, $"tosu-{DateTimeOffset.Now:yyyyMMdd-HHmmss-fff}.jsonl");
                _recording = new StreamWriter(path, append: false, Encoding.UTF8, 64 * 1024);
                _recordingBytes = 0;
                _lastRecordingFlush = DateTimeOffset.UtcNow;
            }
            var line = System.Text.Json.JsonSerializer.Serialize(new
            {
                wall = packet.WallTime,
                mono = packet.MonoTime,
                raw = packet.Raw,
            });
            _recording.WriteLine(line);
            _recordingBytes += Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;
            var now = DateTimeOffset.UtcNow;
            if (now - _lastRecordingFlush >= TimeSpan.FromSeconds(2))
            {
                _recording.Flush();
                _lastRecordingFlush = now;
            }
            if (_recordingBytes >= MaxRecordingBytes)
            {
                _recording.Dispose();
                _recording = null;
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "tosu packet recording failed");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_recording is null) return;
        await _recording.FlushAsync();
        _recording.Dispose();
        _recording = null;
    }

    private static async IAsyncEnumerable<TosuPacket> ReceiveLoopAsync(
        ClientWebSocket socket,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        var message = new MemoryStream();
        while (!cancellationToken.IsCancellationRequested &&
               socket.State == WebSocketState.Open)
        {
            message.SetLength(0);
            string? raw = null;
            try
            {
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        yield break;
                    }
                    message.Write(buffer, 0, result.Count);
                    if (message.Length > MaxMessageBytes)
                    {
                        yield break; // oversized/garbled stream: force reconnect
                    }
                } while (!result.EndOfMessage);
                raw = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
            catch (WebSocketException ex)
            {
                Log.Warning(ex, "tosu websocket receive failed");
                yield break; // reconnect via outer loop
            }
            if (raw is not null)
            {
                yield return new TosuPacket(
                    raw,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
                    Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
            }
        }
    }
}
