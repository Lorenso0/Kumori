using System.Diagnostics;
using System.Buffers;
using System.Globalization;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
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
    internal const int RecordingWriteChunkBytes = 16 * 1024;
    internal const int RecordingEncodeSliceChars = 2 * 1024;
    private const int MaxRecordedPacketChars = 64 * 1024;
    // Packet recording is diagnostic and best-effort. Cap the entire retained
    // raw queue, not only its item count, so gameplay cannot promote tens or
    // hundreds of megabytes of packet strings under GC pressure.
    private const int MaxQueuedRecordedPacketChars = 512 * 1024;
    private const int GameplayActiveBit = 1;
    private const int ConsumerActiveBit = 2;
    private const int RecorderActiveBit = 4;
    private const int RecordingBlockedBits = GameplayActiveBit | ConsumerActiveBit;
    private static readonly byte[] NewLineBytes = Encoding.UTF8.GetBytes(Environment.NewLine);
    private static long _recordingFileSequence;

    private readonly Uri _uri;
    private readonly bool _recordPackets;
    private readonly string _fixtureDirectory;
    private readonly Channel<TosuPacket>? _recordingChannel;
    private readonly CancellationTokenSource? _recordingCts;
    private readonly SemaphoreSlim? _recordingSignal;
    private readonly TaskCompletionSource? _recordingCompletion;
    private int _recordingState;
    private int _recordingDisposeStarted;
    private int _queuedRecordingChars;
    private long _totalRecordingBytesWritten;

    internal bool IsRecordingWorkActive =>
        (Volatile.Read(ref _recordingState) & RecorderActiveBit) != 0;

    internal long TotalRecordingBytesWritten =>
        Interlocked.Read(ref _totalRecordingBytesWritten);

    // Recorder-thread-only synchronization hook for the in-flight race test.
    // Production never assigns it; packet delivery never invokes callbacks.
    internal Action? RecordingWriteDispatchedForTests { get; set; }

    public event Action? Connected;
    public event Action<string?>? Disconnected;

    public WebSocketPacketSource(Uri? uri = null, bool recordPackets = false, string? fixtureDirectory = null)
    {
        _uri = uri ?? DefaultUri;
        _recordPackets = recordPackets;
        _fixtureDirectory = fixtureDirectory ?? AppPaths.FixturesDir;
        if (_recordPackets)
        {
            _recordingChannel = Channel.CreateBounded<TosuPacket>(new BoundedChannelOptions(64)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false,
            });
            _recordingCts = new CancellationTokenSource();
            _recordingSignal = new SemaphoreSlim(0, 1);
            _recordingCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var recordingThread = new Thread(RecordingThreadMain)
            {
                IsBackground = true,
                Name = "Kumori packet recorder",
            };
            recordingThread.Start();
        }
    }

    public async IAsyncEnumerable<TosuPacket> ReadPacketsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var backoff = WebSocketReconnectPolicy.InitialDelay(_uri);
        while (!cancellationToken.IsCancellationRequested)
        {
            using var socket = new ClientWebSocket();
            socket.Options.SetBuffer(64 * 1024, 64 * 1024);
            var connected = false;
            try
            {
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                connectCts.CancelAfter(WebSocketReconnectPolicy.ConnectTimeout(_uri));
                await socket.ConnectAsync(_uri, connectCts.Token);
                connected = true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                yield break;
            }
            catch (OperationCanceledException)
            {
                // A linked connect token expiring is an ordinary "tosu is not
                // listening yet" state, not an actionable cancellation error.
                Log.Debug("tosu websocket connection timed out for {Uri}", _uri);
                Disconnected?.Invoke("not reachable");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "tosu websocket connect failed for {Uri}", _uri);
                Disconnected?.Invoke(ex.Message);
            }

            if (connected)
            {
                Connected?.Invoke();
                backoff = WebSocketReconnectPolicy.InitialDelay(_uri);
                await foreach (var packet in ReceiveLoopAsync(socket, cancellationToken))
                {
                    BeginPacketProcessing();
                    var processed = false;
                    try
                    {
                        yield return packet;
                        // An async iterator resumes here only after the
                        // consumer has finished handling the yielded packet.
                        processed = true;
                    }
                    finally
                    {
                        CompletePacketProcessing(packet, processed);
                    }
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
            backoff = WebSocketReconnectPolicy.NextDelay(_uri, backoff);
        }
    }

    /// <summary>
    /// Called synchronously from snapshot delivery before any persistence or
    /// UI work. It is deliberately only an atomic state transition: gameplay
    /// cannot inherit recorder callbacks, waits, or cancellation work.
    /// </summary>
    internal void SetGameplayActive(bool active)
    {
        if (!_recordPackets)
        {
            return;
        }

        if (active)
        {
            Interlocked.Or(ref _recordingState, GameplayActiveBit);
            return;
        }

        Interlocked.And(ref _recordingState, ~GameplayActiveBit);
        if ((Volatile.Read(ref _recordingState) & ConsumerActiveBit) == 0)
        {
            SignalRecorder();
        }
    }

    internal void BeginPacketProcessing()
    {
        if (_recordPackets)
        {
            Interlocked.Or(ref _recordingState, ConsumerActiveBit);
        }
    }

    internal void CompletePacketProcessing(TosuPacket packet, bool record = true)
    {
        if (!_recordPackets)
        {
            return;
        }

        try
        {
            // Developer recording must never serialize or write on the
            // websocket/consumer thread. Oversized or saturated diagnostics
            // packets are intentionally dropped rather than delaying tracking.
            if (record)
            {
                TryQueueRecordingPacket(packet);
            }
        }
        finally
        {
            Interlocked.And(ref _recordingState, ~ConsumerActiveBit);
            if ((Volatile.Read(ref _recordingState) & GameplayActiveBit) == 0)
            {
                SignalRecorder();
            }
        }
    }

    private void SignalRecorder()
    {
        try
        {
            _recordingSignal?.Release();
        }
        catch (SemaphoreFullException)
        {
            // Signals are coalesced. The single recorder drains all work that
            // is safe at the time it wakes.
        }
        catch (ObjectDisposedException)
        {
            // Shutdown raced a final packet completion.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_recordingChannel is null
            || _recordingCts is null
            || _recordingCompletion is null)
        {
            return;
        }
        if (Interlocked.Exchange(ref _recordingDisposeStarted, 1) != 0)
        {
            await _recordingCompletion.Task.ConfigureAwait(false);
            return;
        }

        _recordingChannel.Writer.TryComplete();
        try
        {
            await _recordingCts.CancelAsync().ConfigureAwait(false);
        }
        finally
        {
            SignalRecorder();
            await _recordingCompletion.Task.ConfigureAwait(false);
            _recordingSignal?.Dispose();
            _recordingCts.Dispose();
        }
    }

    private void RecordingThreadMain()
    {
        try
        {
            // Keep serialization off the shared thread pool and below the
            // input/tracking threads. Priority may be unsupported on a future
            // runtime, in which case the gameplay gate remains authoritative.
            try { Thread.CurrentThread.Priority = ThreadPriority.BelowNormal; }
            catch (PlatformNotSupportedException) { }

            RecordingLoop(_recordingCts!.Token);
        }
        catch (OperationCanceledException) when (_recordingCts!.IsCancellationRequested)
        {
            // Normal recorder shutdown.
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "tosu packet recording stopped unexpectedly");
        }
        finally
        {
            _recordingCompletion!.TrySetResult();
        }
    }

    private void RecordingLoop(CancellationToken cancellationToken)
    {
        FileStream? recording = null;
        string? partialPath = null;
        string? finalPath = null;
        long recordingBytes = 0;
        long lastCompletePacketBytes = 0;
        PendingRecording? pending = null;
        Exception? deferredFailure = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                _recordingSignal!.Wait(cancellationToken);

                while (!cancellationToken.IsCancellationRequested)
                {
                    if (deferredFailure is not null)
                    {
                        if (!TryBeginRecordingWork())
                        {
                            break;
                        }

                        try
                        {
                            Log.Debug(deferredFailure, "tosu packet recording failed");
                            pending?.Dispose();
                            pending = null;
                            PublishRecording(
                                ref recording,
                                ref partialPath,
                                ref finalPath,
                                ref recordingBytes,
                                ref lastCompletePacketBytes);
                            deferredFailure = null;
                        }
                        finally
                        {
                            EndRecordingWork();
                        }
                        continue;
                    }

                    if (pending is null)
                    {
                        if (!TryBeginRecordingWork())
                        {
                            break;
                        }

                        var foundPacket = false;
                        try
                        {
                            if (_recordingChannel!.Reader.TryRead(out var packet))
                            {
                                Interlocked.Add(ref _queuedRecordingChars, -packet.Raw.Length);
                                pending = new PendingRecording(packet);
                                foundPacket = true;
                            }
                        }
                        finally
                        {
                            EndRecordingWork();
                        }

                        if (!foundPacket)
                        {
                            break;
                        }
                        continue;
                    }

                    if (pending.IsComplete)
                    {
                        if (!TryBeginRecordingWork())
                        {
                            break;
                        }

                        try
                        {
                            lastCompletePacketBytes = recordingBytes;
                            pending.Dispose();
                            pending = null;

                            if (recordingBytes >= MaxRecordingBytes)
                            {
                                PublishRecording(
                                    ref recording,
                                    ref partialPath,
                                    ref finalPath,
                                    ref recordingBytes,
                                    ref lastCompletePacketBytes);
                            }
                        }
                        finally
                        {
                            EndRecordingWork();
                        }
                        continue;
                    }

                    if (!pending.HasChunkToWrite)
                    {
                        if (!TryBeginRecordingWork())
                        {
                            break;
                        }

                        try
                        {
                            pending.EncodeNextSlice();
                        }
                        catch (Exception ex)
                        {
                            deferredFailure = ex;
                        }
                        finally
                        {
                            EndRecordingWork();
                        }
                        continue;
                    }

                    if (!TryBeginRecordingWork())
                    {
                        break;
                    }

                    try
                    {
                        if (recording is null)
                        {
                            Directory.CreateDirectory(_fixtureDirectory);
                            var sequence = Interlocked.Increment(ref _recordingFileSequence);
                            finalPath = Path.Combine(
                                _fixtureDirectory,
                                $"tosu-{DateTimeOffset.Now:yyyyMMdd-HHmmss-fff}-{sequence:D4}.jsonl");
                            // Only publish the .jsonl name at a confirmed packet
                            // boundary. A gameplay pause, I/O cancellation, or
                            // process shutdown can never expose a malformed line.
                            partialPath = finalPath + ".partial";
                            recording = new FileStream(partialPath, new FileStreamOptions
                            {
                                Mode = FileMode.CreateNew,
                                Access = FileAccess.Write,
                                Share = FileShare.Read,
                                BufferSize = 1,
                                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                            });
                            recordingBytes = 0;
                            lastCompletePacketBytes = 0;
                        }

                        // Opening a new diagnostics file can outlive the gate
                        // acquisition. Re-check before dispatching the first
                        // chunk so gameplay can win that transition as well.
                        if (IsRecordingBlocked())
                        {
                            continue;
                        }

                        var chunk = pending.CurrentChunk;
                        var write = recording.WriteAsync(chunk, cancellationToken);
                        RecordingWriteDispatchedForTests?.Invoke();
                        write.AsTask().GetAwaiter().GetResult();
                        recordingBytes += chunk.Length;
                        Interlocked.Add(ref _totalRecordingBytesWritten, chunk.Length);
                        pending.CompleteChunkWrite();
                        if (pending.IsComplete)
                        {
                            // The newline is durable in the async stream now.
                            // Publish/shutdown may safely retain this packet
                            // even if cancellation wins before finalization.
                            lastCompletePacketBytes = recordingBytes;
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        // Cleanup can require truncation and a rename. Defer it
                        // until the gameplay/consumer gate can be acquired, so
                        // an I/O failure cannot create more gameplay-side work.
                        deferredFailure = ex;
                    }
                    finally
                    {
                        EndRecordingWork();
                    }
                }
            }
        }
        finally
        {
            pending?.Dispose();
            PublishRecording(
                ref recording,
                ref partialPath,
                ref finalPath,
                ref recordingBytes,
                ref lastCompletePacketBytes);
        }
    }

    private bool TryBeginRecordingWork()
    {
        while (true)
        {
            var state = Volatile.Read(ref _recordingState);
            if ((state & (RecordingBlockedBits | RecorderActiveBit)) != 0)
            {
                return false;
            }

            if (Interlocked.CompareExchange(
                    ref _recordingState,
                    state | RecorderActiveBit,
                    state) == state)
            {
                return true;
            }
        }
    }

    private bool IsRecordingBlocked() =>
        (Volatile.Read(ref _recordingState) & RecordingBlockedBits) != 0;

    private void EndRecordingWork() =>
        Interlocked.And(ref _recordingState, ~RecorderActiveBit);

    private bool TryQueueRecordingPacket(TosuPacket packet)
    {
        var length = packet.Raw.Length;
        if (length <= 0
            || length > MaxRecordedPacketChars
            || length > MaxQueuedRecordedPacketChars)
            return false;

        while (true)
        {
            var queued = Volatile.Read(ref _queuedRecordingChars);
            if (queued > MaxQueuedRecordedPacketChars - length)
                return false;
            if (Interlocked.CompareExchange(
                    ref _queuedRecordingChars,
                    queued + length,
                    queued) == queued)
            {
                break;
            }
        }

        if (_recordingChannel!.Writer.TryWrite(packet))
            return true;

        Interlocked.Add(ref _queuedRecordingChars, -length);
        return false;
    }

    private static void PublishRecording(
        ref FileStream? recording,
        ref string? partialPath,
        ref string? finalPath,
        ref long recordingBytes,
        ref long lastCompletePacketBytes)
    {
        try
        {
            if (recording is not null)
            {
                if (recording.Length != lastCompletePacketBytes)
                {
                    recording.SetLength(lastCompletePacketBytes);
                }
                recording.Dispose();
                recording = null;
            }

            if (partialPath is not null && finalPath is not null)
            {
                if (lastCompletePacketBytes > 0)
                {
                    File.Move(partialPath, finalPath, overwrite: false);
                }
                else
                {
                    File.Delete(partialPath);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "tosu packet recording could not publish its completed data");
            recording?.Dispose();
            recording = null;
        }
        finally
        {
            partialPath = null;
            finalPath = null;
            recordingBytes = 0;
            lastCompletePacketBytes = 0;
        }
    }

    private sealed class PendingRecording(TosuPacket packet) : IDisposable
    {
        private static ReadOnlySpan<byte> Hex => "0123456789ABCDEF"u8;

        private byte[]? _buffer;
        private int _bufferLength;
        private int _rawIndex;
        private bool _headerWritten;
        private bool _encodingComplete;

        public bool HasChunkToWrite => _bufferLength > 0;
        public bool IsComplete => _encodingComplete && _bufferLength == 0;

        public ReadOnlyMemory<byte> CurrentChunk =>
            (_buffer ?? throw new InvalidOperationException("No encoded packet chunk is available."))
                .AsMemory(0, _bufferLength);

        public void EncodeNextSlice()
        {
            if (_bufferLength != 0 || _encodingComplete)
            {
                throw new InvalidOperationException("The previous packet chunk must be written first.");
            }
            if (!double.IsFinite(packet.WallTime) || !double.IsFinite(packet.MonoTime))
            {
                throw new InvalidDataException("Packet recording timestamps must be finite.");
            }

            _buffer ??= ArrayPool<byte>.Shared.Rent(RecordingWriteChunkBytes);
            if (!_headerWritten)
            {
                AppendAscii(FormattableString.Invariant(
                    $"{{\"wall\":{packet.WallTime:R},\"mono\":{packet.MonoTime:R},\"raw\":\""));
                _headerWritten = true;
            }

            var sliceEnd = Math.Min(packet.Raw.Length, _rawIndex + RecordingEncodeSliceChars);
            while (_rawIndex < sliceEnd)
            {
                AppendEscaped(packet.Raw);
            }

            if (_rawIndex == packet.Raw.Length)
            {
                AppendAscii("\"}");
                AppendBytes(NewLineBytes);
                _encodingComplete = true;
            }

            if (_bufferLength > RecordingWriteChunkBytes)
            {
                throw new InvalidDataException("Encoded packet slice exceeded its bounded buffer.");
            }
        }

        public void CompleteChunkWrite()
        {
            if (_bufferLength == 0)
            {
                throw new InvalidOperationException("No packet chunk was written.");
            }
            _bufferLength = 0;
        }

        public void Dispose()
        {
            if (_buffer is null)
            {
                return;
            }

            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = null;
            _bufferLength = 0;
        }

        private void AppendEscaped(string raw)
        {
            var value = raw[_rawIndex++];
            switch (value)
            {
                case '"': AppendAscii("\\\""); return;
                case '\\': AppendAscii("\\\\"); return;
                case '\b': AppendAscii("\\b"); return;
                case '\f': AppendAscii("\\f"); return;
                case '\n': AppendAscii("\\n"); return;
                case '\r': AppendAscii("\\r"); return;
                case '\t': AppendAscii("\\t"); return;
            }

            if (value < 0x20)
            {
                EnsureCapacity(6);
                _buffer![_bufferLength++] = (byte)'\\';
                _buffer[_bufferLength++] = (byte)'u';
                _buffer[_bufferLength++] = (byte)'0';
                _buffer[_bufferLength++] = (byte)'0';
                _buffer[_bufferLength++] = Hex[value >> 4];
                _buffer[_bufferLength++] = Hex[value & 0x0F];
                return;
            }
            if (value <= 0x7F)
            {
                EnsureCapacity(1);
                _buffer![_bufferLength++] = (byte)value;
                return;
            }

            Rune rune;
            if (char.IsHighSurrogate(value)
                && _rawIndex < raw.Length
                && char.IsLowSurrogate(raw[_rawIndex])
                && Rune.TryCreate(value, raw[_rawIndex], out rune))
            {
                _rawIndex++;
            }
            else if (!Rune.TryCreate(value, out rune))
            {
                rune = Rune.ReplacementChar;
            }

            Span<byte> encoded = stackalloc byte[4];
            var encodedLength = rune.EncodeToUtf8(encoded);
            AppendBytes(encoded[..encodedLength]);
        }

        private void AppendAscii(string value)
        {
            EnsureCapacity(value.Length);
            foreach (var character in value)
            {
                if (character > 0x7F)
                {
                    throw new InvalidDataException("Packet recording metadata must be ASCII.");
                }
                _buffer![_bufferLength++] = (byte)character;
            }
        }

        private void AppendBytes(ReadOnlySpan<byte> value)
        {
            EnsureCapacity(value.Length);
            value.CopyTo(_buffer.AsSpan(_bufferLength));
            _bufferLength += value.Length;
        }

        private void EnsureCapacity(int additionalBytes)
        {
            _buffer ??= ArrayPool<byte>.Shared.Rent(RecordingWriteChunkBytes);
            if (_bufferLength > RecordingWriteChunkBytes - additionalBytes)
            {
                throw new InvalidDataException("Encoded packet slice exceeded its bounded buffer.");
            }
        }
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

internal static class WebSocketReconnectPolicy
{
    private static readonly TimeSpan LocalInitialDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan LocalMaximumDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan RemoteInitialDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RemoteMaximumDelay = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan LocalConnectTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RemoteConnectTimeout = TimeSpan.FromSeconds(5);

    public static TimeSpan ConnectTimeout(Uri uri) =>
        uri.IsLoopback ? LocalConnectTimeout : RemoteConnectTimeout;

    public static TimeSpan InitialDelay(Uri uri) =>
        uri.IsLoopback ? LocalInitialDelay : RemoteInitialDelay;

    public static TimeSpan NextDelay(Uri uri, TimeSpan current)
    {
        var maximum = uri.IsLoopback ? LocalMaximumDelay : RemoteMaximumDelay;
        return TimeSpan.FromMilliseconds(Math.Min(current.TotalMilliseconds * 2, maximum.TotalMilliseconds));
    }
}
