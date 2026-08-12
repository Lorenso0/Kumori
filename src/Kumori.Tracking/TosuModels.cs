namespace Kumori.Tracking;

/// <summary>Parsed view of one tosu packet - what the GUI/status layer needs.</summary>
public enum OsuClientKind
{
    Unknown,
    Stable,
    Lazer,
}

public sealed record TosuSnapshot
{
    public OsuClientKind ClientKind { get; init; }
    public string State { get; init; } = "";
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
    public long? FirstObjectMs { get; init; }
    public long? LastObjectMs { get; init; }
    public BeatmapStats BeatmapStats { get; init; } = new();
    public TosuMediaInfo? Media { get; init; }
    public string BeatmapIdentity { get; init; } = "unknown";
    public long? LiveTimeMs { get; init; }
    public double WallTime { get; init; }
    public double MonoTime { get; init; }
    public long Score { get; init; }
    public string? Grade { get; init; }
    public string? ProfileName { get; init; }
    public TosuProfile? Profile { get; init; }
    public string? PlayerName { get; init; }
    public bool IsWatchedReplay { get; init; }
    public bool HasAutoMod { get; init; }
    public double Pp { get; init; }
    public double FcPp { get; init; }
    public double MaxPp { get; init; }
    public string ModsKey { get; init; } = "NM";
    public IReadOnlyList<AttemptMod> Mods { get; init; } = Array.Empty<AttemptMod>();
    public bool ModsAreAuthoritativeResult { get; init; }
    public JudgementCapture.PlayValues Play { get; init; } = new();

    public string? BeatmapDisplay =>
        Artist is null && Title is null
            ? null
            : $"{Artist} — {Title}" + (Difficulty is null ? "" : $" [{Difficulty}]");
}

/// <summary>Account statistics reported by tosu for the locally logged-in osu! user.</summary>
public sealed record TosuProfile
{
    public long Id { get; init; }
    public string Name { get; init; } = "";
    public double? TotalPp { get; init; }
    public long? GlobalRank { get; init; }
    public long? CountryRank { get; init; }
    public double? Accuracy { get; init; }
    public long? PlayCount { get; init; }
    public double? Level { get; init; }
    public long? RankedScore { get; init; }
    public string? CountryCode { get; init; }
}

/// <summary>Optional consumer for account telemetry carried by tosu packets.</summary>
public interface IProfileTelemetrySink
{
    void Ingest(TosuSnapshot snapshot);
}

public sealed record OsuProfileIdentity(long PlayerId, string PlayerName);

public sealed record BeatmapStats
{
    public double? BaseStars { get; init; }
    public double? Stars { get; init; }
    public double? ApproachRate { get; init; }
    public double? CircleSize { get; init; }
    public double? OverallDifficulty { get; init; }
    public double? DrainRate { get; init; }
    public double? Bpm { get; init; }
    public long? MaxCombo { get; init; }
    public long? CircleCount { get; init; }
    public long? SliderCount { get; init; }
    public long? SpinnerCount { get; init; }
    public string RawJson { get; init; } = "{}";
}

public sealed record TosuMediaInfo
{
    public string? Checksum { get; init; }
    public long? BeatmapId { get; init; }
    public long? BeatmapSetId { get; init; }
    public string? SongsFolder { get; init; }
    public string? GameFolder { get; init; }
    public string? BeatmapFile { get; init; }
    public string? BeatmapFolder { get; init; }
    public string? BackgroundFile { get; init; }
    public string? AudioFile { get; init; }
    public string? SkinFolder { get; init; }
}

internal readonly record struct RichHitCounts
{
    public double Geki { get; init; }
    public double Katu { get; init; }
    public double LargeTickHits { get; init; }
    public double LargeTickMisses { get; init; }
    public double SmallTickHits { get; init; }
    public double SmallTickMisses { get; init; }
    public double SliderTailHits { get; init; }
    public double SliderTailMisses { get; init; }
}
