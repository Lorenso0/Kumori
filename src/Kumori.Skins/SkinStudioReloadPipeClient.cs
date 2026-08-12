using System.IO.Pipes;
using System.Text.Json;

namespace Kumori.Skins;

public sealed record SkinStudioReloadQueueResult(bool Accepted, string Message);

public static class SkinStudioReloadPipeClient
{
    internal static JsonSerializerOptions ProtocolJsonOptions { get; } =
        new(SkinStudioLaunchContract.JsonOptions)
        {
            WriteIndented = false,
        };

    public static SkinStudioReloadQueueResult Queue(
        string? pipeName,
        Guid skinId,
        int timeoutMilliseconds = 2000)
    {
        if (string.IsNullOrWhiteSpace(pipeName))
        {
            return new SkinStudioReloadQueueResult(
                false,
                "No reload channel is available; select the preview skin manually.");
        }
        if (skinId == Guid.Empty)
            throw new ArgumentException("A non-empty skin ID is required.", nameof(skinId));
        if (timeoutMilliseconds is < 1 or > 10_000)
            throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds));

        try
        {
            using var pipe = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            pipe.Connect(timeoutMilliseconds);
            using var writer = new StreamWriter(pipe, leaveOpen: true)
            {
                AutoFlush = true,
            };
            using var reader = new StreamReader(pipe, leaveOpen: true);
            writer.WriteLine(JsonSerializer.Serialize(
                new SkinStudioReloadRequest(skinId),
                ProtocolJsonOptions));
            var responseLine = readLineWithTimeout(reader, timeoutMilliseconds);
            var response = JsonSerializer.Deserialize<SkinStudioReloadQueueResult>(
                               responseLine,
                               ProtocolJsonOptions)
                           ?? throw new InvalidDataException(
                               "Kumori returned an empty reload response.");
            return response;
        }
        catch (Exception ex) when (
            ex is IOException
                or TimeoutException
                or UnauthorizedAccessException
                or InvalidDataException
                or JsonException)
        {
            return new SkinStudioReloadQueueResult(
                false,
                $"Could not queue the safe lazer reload: {ex.Message}");
        }
    }

    private static string readLineWithTimeout(
        StreamReader reader,
        int timeoutMilliseconds)
    {
        using var cancellation = new CancellationTokenSource(timeoutMilliseconds);
        try
        {
            return reader.ReadLineAsync(cancellation.Token)
                         .AsTask()
                         .GetAwaiter()
                         .GetResult()
                   ?? throw new InvalidDataException(
                       "Kumori closed the reload channel without a response.");
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException(
                "Kumori did not acknowledge the reload request in time.");
        }
    }
}

public sealed record SkinStudioReloadRequest(Guid SkinId);
