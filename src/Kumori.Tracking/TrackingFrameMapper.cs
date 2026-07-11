namespace Kumori.Tracking;

internal static class TrackingFrameMapper
{
    public static SessionTracker.Frame ToSessionFrame(TosuSnapshot snapshot, bool osuRunning = true) => new()
    {
        WallTime = snapshot.WallTime,
        MonoTime = snapshot.MonoTime,
        IsStandardMode = snapshot.IsStandardMode,
        IsPlaying = snapshot.IsPlaying && !snapshot.IsWatchedReplay && !snapshot.HasAutoMod,
        OsuRunning = osuRunning,
    };

    public static AttemptTracker.Frame ToAttemptFrame(TosuSnapshot snapshot) => new()
    {
        WallTime = snapshot.WallTime,
        IsStandardMode = snapshot.IsStandardMode,
        Artist = snapshot.Artist,
        Title = snapshot.Title,
        Mapper = snapshot.Mapper,
        Difficulty = snapshot.Difficulty,
        BeatmapId = snapshot.BeatmapId,
        BeatmapSetId = snapshot.BeatmapSetId,
        Checksum = snapshot.Checksum,
        BeatmapStats = snapshot.BeatmapStats,
        ModsKey = snapshot.ModsKey,
        Mods = snapshot.Mods,
        Score = checked((int)Math.Min(snapshot.Score, int.MaxValue)),
        Grade = snapshot.Grade,
        Pp = snapshot.Pp,
        FcPp = snapshot.FcPp,
        MaxPp = snapshot.MaxPp,
        IsWatchedReplay = snapshot.IsWatchedReplay,
        HasAutoMod = snapshot.HasAutoMod,
        Play = snapshot.Play,
        Packet = new AttemptStateMachine.PacketView
        {
            MonoTime = snapshot.MonoTime,
            State = snapshot.State,
            IsPlaying = snapshot.IsPlaying,
            IsResults = snapshot.IsResults,
            Identity = snapshot.BeatmapIdentity,
            LiveTimeMs = snapshot.LiveTimeMs ?? 0,
            Grade = snapshot.Grade,
            Health = snapshot.Play.Health,
        },
    };
}
