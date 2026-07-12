namespace Kumori.Tracking;

// The bridge uses the same JSON wire shape as Kumori.Tracking without taking
// a dependency on the app's storage, Realm, WPF, or native-integration graph.
public sealed record LazerReplayFrame
{
    public double MapTimeMs { get; init; }
    public double X { get; init; }
    public double Y { get; init; }
    public bool LeftPressed { get; init; }
    public bool RightPressed { get; init; }
    public bool Focused { get; init; } = true;
    public bool Paused { get; init; }
    public double? MonotonicMs { get; init; }
    public long? Sequence { get; init; }
}
