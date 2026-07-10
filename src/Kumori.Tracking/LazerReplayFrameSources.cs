using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;

namespace Kumori.Tracking;

public sealed class JsonlLazerReplayFrameSource : ILazerReplayFrameSource
{
    private readonly Func<Stream> _openStream;

    public JsonlLazerReplayFrameSource(string path)
        : this(() => File.OpenRead(path))
    {
    }

    public JsonlLazerReplayFrameSource(Func<Stream> openStream)
    {
        _openStream = openStream;
    }

    public async IAsyncEnumerable<LazerReplayFrame> ReadFramesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var stream = _openStream();
        using var reader = new StreamReader(stream);
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                yield break;
            }
            if (LazerReplayFrameJson.TryParse(line, out var frame))
            {
                yield return frame;
            }
        }
    }
}

public sealed class TcpLazerReplayFrameSource : ILazerReplayFrameSource, IAsyncDisposable
{
    private readonly TcpListener _listener;

    public TcpLazerReplayFrameSource(int port = 16029)
    {
        _listener = new TcpListener(IPAddress.Loopback, port);
    }

    public async IAsyncEnumerable<LazerReplayFrame> ReadFramesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        _listener.Start();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                using var client = await _listener.AcceptTcpClientAsync(cancellationToken);
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream);
                while (!cancellationToken.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(cancellationToken);
                    if (line is null)
                    {
                        break;
                    }
                    if (LazerReplayFrameJson.TryParse(line, out var frame))
                    {
                        yield return frame;
                    }
                }
            }
        }
        finally
        {
            _listener.Stop();
        }
    }

    public ValueTask DisposeAsync()
    {
        _listener.Stop();
        return ValueTask.CompletedTask;
    }
}
