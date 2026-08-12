using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using Kumori.Skins;

namespace Kumori.App.Skins;

internal sealed class SkinStudioReloadPipeServer : IDisposable
{
    private static readonly JsonSerializerOptions protocol_json_options =
        new(SkinStudioLaunchContract.JsonOptions)
        {
            WriteIndented = false,
        };

    private readonly Action<Guid> queue;
    private readonly CancellationTokenSource cancellation = new();
    private readonly Task listener;

    public string PipeName { get; } =
        $"kumori-skin-reload-{Guid.NewGuid():N}";

    internal Exception? LastError { get; private set; }

    public SkinStudioReloadPipeServer(Action<Guid> queue)
    {
        this.queue = queue ?? throw new ArgumentNullException(nameof(queue));
        listener = Task.Run(listenAsync);
    }

    private async Task listenAsync()
    {
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellation.Token);
                using var reader = new StreamReader(pipe, leaveOpen: true);
                using var writer = new StreamWriter(pipe, leaveOpen: true)
                {
                    AutoFlush = true,
                };
                var line = await reader.ReadLineAsync(cancellation.Token);
                var request = line is null
                    ? null
                    : JsonSerializer.Deserialize<SkinStudioReloadRequest>(
                        line,
                        protocol_json_options);
                SkinStudioReloadQueueResult response;
                if (request is null || request.SkinId == Guid.Empty)
                {
                    response = new SkinStudioReloadQueueResult(
                        false,
                        "The reload request did not contain a valid skin ID.");
                }
                else
                {
                    queue(request.SkinId);
                    response = new SkinStudioReloadQueueResult(
                        true,
                        "Safe lazer reload queued; it will wait for a foreground client.");
                }
                await writer.WriteLineAsync(JsonSerializer.Serialize(
                    response,
                    protocol_json_options));
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                return;
            }
            catch (IOException ex) when (!cancellation.IsCancellationRequested)
            {
                // A disconnected client cannot poison the next request.
                LastError = ex;
            }
            catch (JsonException ex) when (!cancellation.IsCancellationRequested)
            {
                // Malformed per-launch messages are discarded.
                LastError = ex;
            }
            catch (Exception ex) when (!cancellation.IsCancellationRequested)
            {
                LastError = ex;
            }
        }
    }

    public void Dispose()
    {
        cancellation.Cancel();
        try { listener.Wait(TimeSpan.FromSeconds(2)); } catch { }
        cancellation.Dispose();
    }
}
