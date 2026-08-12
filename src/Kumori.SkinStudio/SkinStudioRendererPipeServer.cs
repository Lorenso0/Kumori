using System.IO.Pipes;
using Kumori.Skins;

namespace Kumori.SkinStudio;

internal sealed class SkinStudioRendererPipeServer : IDisposable
{
    private readonly string pipeName;
    private readonly Func<SkinStudioRendererRequest, Task<SkinStudioRendererResponse>> handler;
    private readonly CancellationTokenSource cancellation = new();
    private readonly object requestLock = new();
    private readonly HashSet<Task> activeRequests = [];
    private readonly Task listener;

    public SkinStudioRendererPipeServer(
        string pipeName,
        Func<SkinStudioRendererRequest, Task<SkinStudioRendererResponse>> handler)
    {
        SkinStudioRendererLaunchContract.ValidatePipeName(pipeName);
        this.pipeName = pipeName;
        this.handler = handler ?? throw new ArgumentNullException(nameof(handler));
        listener = Task.Run(listenAsync);
    }

    private async Task listenAsync()
    {
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                var pipe = new NamedPipeServerStream(
                    pipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                try
                {
                    await pipe.WaitForConnectionAsync(cancellation.Token).ConfigureAwait(false);
                }
                catch
                {
                    pipe.Dispose();
                    throw;
                }
                track(handleConnectionAsync(pipe));
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                return;
            }
            catch when (!cancellation.IsCancellationRequested)
            {
                await Task.Delay(100, cancellation.Token).ConfigureAwait(false);
            }
        }
    }

    private async Task handleConnectionAsync(NamedPipeServerStream pipe)
    {
        using (pipe)
        {
            SkinStudioRendererRequest? request = null;
            SkinStudioRendererResponse response;
            try
            {
                request = await SkinStudioRendererPipeProtocol
                    .ReadAsync<SkinStudioRendererRequest>(pipe, cancellation.Token)
                    .ConfigureAwait(false);
                response = await handler(request).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                response = new SkinStudioRendererResponse
                {
                    RequestId = request?.RequestId ?? Guid.Empty,
                    Accepted = false,
                    Message = ex.Message,
                    Event = SkinStudioRendererEventKind.RecoverableError,
                };
            }

            if (!cancellation.IsCancellationRequested && pipe.IsConnected)
            {
                try
                {
                    await SkinStudioRendererPipeProtocol.WriteAsync(
                        pipe,
                        response,
                        cancellation.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException)
                {
                    // A newer generation may have cancelled and disconnected this caller.
                }
            }
        }
    }

    private void track(Task request)
    {
        lock (requestLock)
            activeRequests.Add(request);
        _ = request.ContinueWith(
            completed =>
            {
                lock (requestLock)
                    activeRequests.Remove(completed);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    public void Dispose()
    {
        cancellation.Cancel();
        try { listener.Wait(TimeSpan.FromSeconds(2)); } catch { }
        Task[] requests;
        lock (requestLock)
            requests = activeRequests.ToArray();
        try { Task.WaitAll(requests, TimeSpan.FromSeconds(2)); } catch { }
        cancellation.Dispose();
    }
}
