namespace Kumori.Tracking;

/// <summary>
/// One raw tosu websocket message with capture timestamps.
/// WallTime is Unix seconds; MonoTime is a monotonic seconds value used for
/// packet-age math (matches the Python fixture recorder's "wall"/"mono").
/// </summary>
public sealed record TosuPacket(string Raw, double WallTime, double MonoTime);

/// <summary>
/// Source of tosu packets. Production uses the websocket; tests replay the
/// golden JSONL fixtures recorded by the Python tracker, so the entire
/// pipeline downstream of this interface is regression-testable.
/// </summary>
public interface ITosuPacketSource
{
    IAsyncEnumerable<TosuPacket> ReadPacketsAsync(CancellationToken cancellationToken);
}
