using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;

namespace Kumori.Native;

public sealed record SingleInstanceActivationRequest(string? ImportPath);

/// <summary>
/// Named-mutex single-instance guard with a same-user pipe that forwards
/// activation payloads to the primary process.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string DefaultMutexName = "Kumori.App.SingleInstance";
    private const string DefaultPipeName = "Kumori.App.Activation.v1";
    private const int MaxPayloadBytes = 32 * 1024;

    private readonly Mutex mutex;
    private readonly string pipeName;
    private readonly CancellationTokenSource cancellation = new();
    private readonly ConcurrentQueue<SingleInstanceActivationRequest> queued = new();
    private readonly object handlerGate = new();
    private readonly Task? serverTask;
    private Action<SingleInstanceActivationRequest>? handler;
    private bool disposed;

    public bool IsPrimaryInstance { get; }

    public SingleInstance()
        : this(DefaultMutexName, DefaultPipeName)
    {
    }

    internal SingleInstance(string mutexName, string pipeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mutexName);
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        this.pipeName = pipeName;
        mutex = new Mutex(initiallyOwned: true, mutexName, out bool createdNew);
        IsPrimaryInstance = createdNew;
        if (IsPrimaryInstance)
            serverTask = Task.Run(() => ServerLoopAsync(cancellation.Token));
    }

    public void ListenForActivation(Action onActivateRequested)
    {
        ArgumentNullException.ThrowIfNull(onActivateRequested);
        ListenForActivation(_ => onActivateRequested());
    }

    public void ListenForActivation(Action<SingleInstanceActivationRequest> onActivateRequested)
    {
        ArgumentNullException.ThrowIfNull(onActivateRequested);
        if (!IsPrimaryInstance)
            return;
        lock (handlerGate)
        {
            handler = onActivateRequested;
            while (queued.TryDequeue(out SingleInstanceActivationRequest? request))
                QueueHandler(onActivateRequested, request);
        }
    }

    public void SignalPrimaryInstance(string? importPath = null)
    {
        if (IsPrimaryInstance)
            return;
        byte[] payload = string.IsNullOrWhiteSpace(importPath)
            ? []
            : Encoding.UTF8.GetBytes(importPath);
        if (payload.Length > MaxPayloadBytes)
            throw new ArgumentOutOfRangeException(nameof(importPath), "The activation path is too long.");

        using var client = new NamedPipeClientStream(
            serverName: ".",
            pipeName: pipeName,
            direction: PipeDirection.Out,
            options: PipeOptions.None);
        client.Connect(timeout: 5000);
        using var writer = new BinaryWriter(client, Encoding.UTF8, leaveOpen: true);
        writer.Write(payload.Length);
        if (payload.Length > 0)
            writer.Write(payload);
        writer.Flush();
    }

    private async Task ServerLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    pipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(cancellationToken);
                var lengthBytes = new byte[sizeof(int)];
                await ReadExactlyAsync(server, lengthBytes, cancellationToken);
                int length = BitConverter.ToInt32(lengthBytes);
                if (length is < 0 or > MaxPayloadBytes)
                    continue;
                var payload = new byte[length];
                if (length > 0)
                    await ReadExactlyAsync(server, payload, cancellationToken);
                string? importPath = length == 0 ? null : Encoding.UTF8.GetString(payload);
                Dispatch(new SingleInstanceActivationRequest(importPath));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (IOException)
            {
                if (cancellationToken.IsCancellationRequested)
                    return;
            }
        }
    }

    private static async Task ReadExactlyAsync(
        Stream stream,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken);
            if (read == 0)
                throw new EndOfStreamException("Activation request was truncated.");
            offset += read;
        }
    }

    private void Dispatch(SingleInstanceActivationRequest request)
    {
        lock (handlerGate)
        {
            if (handler is null)
                queued.Enqueue(request);
            else
                QueueHandler(handler, request);
        }
    }

    private static void QueueHandler(
        Action<SingleInstanceActivationRequest> callback,
        SingleInstanceActivationRequest request) =>
        ThreadPool.QueueUserWorkItem(
            static state =>
            {
                var (handler, activation) =
                    ((Action<SingleInstanceActivationRequest>, SingleInstanceActivationRequest))state!;
                handler(activation);
            },
            (callback, request));

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        cancellation.Cancel();
        if (IsPrimaryInstance)
        {
            try { mutex.ReleaseMutex(); } catch { }
        }
        mutex.Dispose();
        cancellation.Dispose();
    }
}
