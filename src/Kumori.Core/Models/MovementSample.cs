namespace Kumori.Core.Models;

public sealed record MovementSample
{
    public double MapTimeMs { get; init; }
    public double MonotonicMs { get; init; }
    public double X { get; init; }
    public double Y { get; init; }
    public short RawX { get; init; }
    public short RawY { get; init; }
    public int Buttons { get; init; }
    public int Flags { get; init; }
    public uint Pressure { get; init; }
}

public sealed record MovementMetadata
{
    public string Source { get; init; } = "live";
    public double SampleRate { get; init; }
    public int SampleCount { get; init; }
    public int DroppedSamples { get; init; }
    public string ReplayStatus { get; init; } = "not_checked";
    public string CalibrationJson { get; init; } = "{}";
}
