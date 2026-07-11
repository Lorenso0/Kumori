namespace Kumori.Core.State;

/// <summary>
/// Immutable snapshot of application state. Services publish new snapshots
/// through <see cref="AppStateStore"/>; views observe and never mutate.
/// </summary>
public sealed record AppState
{
    public TrackingStatus Tracking { get; init; } = new();
    public CaptureStatus Capture { get; init; } = new();
    public CompanionStatus Companions { get; init; } = new();
    public MediaStatus Media { get; init; } = new();
    public ActiveSessionInfo? ActiveSession { get; init; }
    public IReadOnlyList<string> Notifications { get; init; } = Array.Empty<string>();
}

public enum HealthLevel { Unknown, Ok, Degraded, Error }

public sealed record TrackingStatus
{
    public bool TosuConnected { get; init; }
    public long? LatestAttemptId { get; init; }
    public long? LatestReplayAttemptId { get; init; }
    public double? LastPacketAgeSeconds { get; init; }
    public string? CurrentBeatmap { get; init; }
    public bool OsuRunning { get; init; }
    public HealthLevel Health { get; init; } = HealthLevel.Unknown;
    public string? Detail { get; init; }
    public TosuTelemetry? LatestTelemetry { get; init; }
}

/// <summary>Latest values received from tosu, retained for the diagnostics UI.</summary>
public sealed record TosuTelemetry
{
    public DateTimeOffset ReceivedAt { get; init; }
    public string State { get; init; } = "unknown";
    public bool IsPlaying { get; init; }
    public bool IsResults { get; init; }
    public bool IsStandardMode { get; init; }
    public string? Artist { get; init; }
    public string? Title { get; init; }
    public string? Mapper { get; init; }
    public string? Difficulty { get; init; }
    public long? BeatmapId { get; init; }
    public long? BeatmapSetId { get; init; }
    public string? Checksum { get; init; }
    public long? LiveTimeMs { get; init; }
    public long Score { get; init; }
    public string? Grade { get; init; }
    public double Accuracy { get; init; }
    public double Combo { get; init; }
    public double? MaxCombo { get; init; }
    public double Progress { get; init; }
    public double Health { get; init; }
    public double Pp { get; init; }
    public double FcPp { get; init; }
    public double MaxPp { get; init; }
    public string ModsKey { get; init; } = "NM";
    public double Hit300 { get; init; }
    public double Hit100 { get; init; }
    public double Hit50 { get; init; }
    public double Miss { get; init; }
    public double Geki { get; init; }
    public double Katu { get; init; }
    public double SliderBreaks { get; init; }
    public double LargeTickHits { get; init; }
    public double LargeTickMisses { get; init; }
    public double SmallTickHits { get; init; }
    public double SmallTickMisses { get; init; }
    public double SliderTailHits { get; init; }
    public double SliderTailMisses { get; init; }
    public double UnstableRate { get; init; }
}

public sealed record CaptureStatus
{
    public bool Running { get; init; }
    public string Source { get; init; } = "lazer_replay_frame";
    public long FramesReceived { get; init; }
    public long FramesBuffered { get; init; }
    public long FramesStored { get; init; }
    public double? LastFrameMapTimeMs { get; init; }
    public HealthLevel Health { get; init; } = HealthLevel.Unknown;
    public string? Error { get; init; }
}

public sealed record CompanionStatus
{
    public bool OsuRunning { get; init; }
    public bool OpenTabletDriverEnabled { get; init; }
    public bool OpenTabletDriverLaunched { get; init; }
    public string? OpenTabletDriverDetail { get; init; }
    public bool DualModeEnabled { get; init; }
    public bool DualModeCommandSent { get; init; }
    public bool DualModeActive { get; init; }
    public string? DualModeDetail { get; init; }
}

public sealed record MediaStatus
{
    public string BeatmapFile { get; init; } = "unknown";
    public string Audio { get; init; } = "unknown";
    public string Background { get; init; } = "unknown";
    public string Mirror { get; init; } = "unknown";
    public string? LastError { get; init; }
}

public sealed record ActiveSessionInfo
{
    public long SessionId { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public string? PlayerName { get; init; }
}
