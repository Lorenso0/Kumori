using System.Runtime.CompilerServices;
using System.Text.Json;
using Serilog;

namespace Kumori.Tracking;

/// <summary>
/// Replays a golden fixture recorded by the Python tracker
/// (%APPDATA%/Kumori/fixtures/tosu-*.jsonl; one tosu JSON object per line with
/// "wall", "mono", "raw"). By default replays as fast as possible; pass
/// <paramref name="realTime"/> = true to reproduce original packet spacing.
/// </summary>
public sealed class FixturePacketSource : ITosuPacketSource
{
    private readonly string _path;
    private readonly bool _realTime;

    public FixturePacketSource(string path, bool realTime = false)
    {
        _path = path;
        _realTime = realTime;
    }

    /// <summary>Lines that fail to parse (recorder corruption) — visible to tests.</summary>
    public int SkippedLines { get; private set; }

    public async IAsyncEnumerable<TosuPacket> ReadPacketsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        double? previousMono = null;
        using var reader = new StreamReader(_path);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }
            TosuPacket? packet = null;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                packet = new TosuPacket(
                    root.GetProperty("raw").GetString() ?? "",
                    root.GetProperty("wall").GetDouble(),
                    root.GetProperty("mono").GetDouble());
            }
            catch (JsonException ex)
            {
                Log.Warning(ex, "Fixture packet line failed to parse in {Path}", _path);
                SkippedLines++;
            }
            if (packet is null)
            {
                continue;
            }
            if (_realTime && previousMono is { } prev)
            {
                var delay = packet.MonoTime - prev;
                if (delay > 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(delay, 5)), cancellationToken);
                }
            }
            previousMono = packet.MonoTime;
            yield return packet;
        }
    }
}
