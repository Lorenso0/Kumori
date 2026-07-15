using System.Text.Json;
using Serilog;

namespace Kumori.Tracking;

/// <summary>
/// Consumes packets from an <see cref="ITosuPacketSource"/>, parses them, and
/// raises typed events. Parsing helpers mirror the legacy tracker
/// (_state, _normalized_state, _mode_is_standard, _beatmap_values identity)
/// so fixture replay produces identical decisions.
/// </summary>
public sealed partial class TosuClient
{
    private const int ReplayLatchClearPacketCount = 10;
    internal const int MaximumHitErrorsPerPacket = 4_096;
    internal const int MaximumHitErrorsPerAttempt = 200_000;
    internal const int MaximumNormalizedStateCharacters = 128;
    internal const int MaximumParsedMods = 64;
    private static readonly HashSet<string> PlayingStates = new() { "play", "playing", "gameplay" };
    private static readonly HashSet<string> ResultStates = new()
    {
        "result", "results", "resultscreen", "resultsscreen", "ranking", "rank",
    };
    private readonly IReplayPlaybackDetector? replayPlaybackDetector;
    private bool watchedReplayLatched;
    private bool replayGameplayGenerationActive;
    private bool completedResultsAwaitingNewGameplay;
    private int consecutiveNonGameplayPackets;
    private List<double> hitErrorsCache = [];
    private int hitErrorsSourceCursor;
    private string? hitErrorsIdentity;
    private long? hitErrorsLastLiveTimeMs;
    private bool hitErrorsWasPlaying;
    private bool hitErrorsWasResults;

    public TosuClient(IReplayPlaybackDetector? replayPlaybackDetector = null)
    {
        this.replayPlaybackDetector = replayPlaybackDetector;
    }

    public event Action<TosuSnapshot>? SnapshotReceived;
    public event Action<string>? PacketInvalid;
    public event Action<TosuSnapshot>? BeatmapChanged;

    public long PacketCount { get; private set; }
    public long InvalidPacketCount { get; private set; }
    public double? LastPacketMonoTime { get; private set; }
    public TosuSnapshot? LastSnapshot { get; private set; }

    public async Task RunAsync(ITosuPacketSource source, CancellationToken cancellationToken)
    {
        await foreach (var packet in source.ReadPacketsAsync(cancellationToken))
        {
            try
            {
                Ingest(packet);
            }
            catch (Exception ex)
            {
                // A consumer must not be able to terminate the websocket loop.
                // The next packet is often enough to recover from a transient
                // database lock or an optional tracking integration failure.
                Log.Error(ex, "Unhandled error while processing a tosu packet");
            }
        }
    }

    /// <summary>Synchronous ingest of one packet (also used by tests directly).</summary>
    public void Ingest(TosuPacket packet)
    {
        TosuSnapshot snapshot;
        try
        {
            using var doc = JsonDocument.Parse(packet.Raw);
            snapshot = ParseSnapshot(
                doc.RootElement,
                packet,
                LastSnapshot?.IsStandardMode ?? false,
                LastSnapshot?.ClientKind ?? OsuClientKind.Unknown,
                LastSnapshot?.Mods ?? []);

            // Results belongs to the completed attempt, so its replay latch is
            // retained for result handling. A following gameplay packet is a
            // confirmed new attempt, however. Retire the completed detector
            // generation before asking for a fresh native result; otherwise a
            // cached replay=true can suppress an immediate genuine retry.
            if (snapshot.IsPlaying
                && (LastSnapshot?.IsResults == true || completedResultsAwaitingNewGameplay))
            {
                ResetReplayPlaybackGeneration(snapshot.ClientKind);
                Log.Debug("Replay playback detector generation cleared at results-to-gameplay boundary");
            }

            var nativeReplayDetected = false;
            if (snapshot.IsPlaying && !snapshot.IsWatchedReplay && replayPlaybackDetector is not null)
            {
                try
                {
                    if (replayPlaybackDetector.IsWatchingReplay(snapshot.ClientKind))
                        nativeReplayDetected = true;
                }
                catch (Exception ex)
                {
                    // Replay detection is a defensive guard. A stale native
                    // offset must never stop ordinary tracking packets.
                    Log.Debug(ex, "Native replay-playback detection was unavailable");
                }
            }
            else if (!snapshot.IsPlaying && !snapshot.IsResults && replayPlaybackDetector is not null)
            {
                try
                {
                    // Keep asynchronous native state warm in stable menu
                    // telemetry; the result is intentionally ignored here.
                    _ = replayPlaybackDetector.IsWatchingReplay(snapshot.ClientKind);
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "Native replay-playback prewarm was unavailable");
                }
            }

            snapshot = ApplyReplayPlaybackLatch(snapshot, nativeReplayDetected);
        }
        catch (JsonException ex)
        {
            InvalidPacketCount++;
            Log.Warning(ex, "Invalid tosu packet");
            PacketInvalid?.Invoke(ex.Message);
            return;
        }
        PacketCount++;
        LastPacketMonoTime = packet.MonoTime;

        var previousIdentity = LastSnapshot?.BeatmapIdentity;
        LastSnapshot = snapshot;
        SnapshotReceived?.Invoke(snapshot);
        if (snapshot.BeatmapIdentity != previousIdentity)
        {
            BeatmapChanged?.Invoke(snapshot);
        }
    }

    /// <summary>
    /// Clears replay state when osu! itself is known to have stopped. A websocket
    /// reconnect alone intentionally does not clear it because tosu can reconnect
    /// in the middle of replay playback.
    /// </summary>
    public void ResetReplayPlaybackState()
    {
        ResetReplayPlaybackGeneration(LastSnapshot?.ClientKind ?? OsuClientKind.Unknown);
    }

    private void ResetReplayPlaybackGeneration(OsuClientKind clientKind)
    {
        watchedReplayLatched = false;
        replayGameplayGenerationActive = false;
        completedResultsAwaitingNewGameplay = false;
        consecutiveNonGameplayPackets = 0;
        try
        {
            replayPlaybackDetector?.ResetAfterGameplay(clientKind);
        }
        catch (Exception ex)
        {
            // Reset is defensive native bookkeeping. A detector failure must
            // never prevent the new gameplay packet from reaching tracking.
            Log.Debug(ex, "Native replay-playback generation reset was unavailable");
        }
    }

    private TosuSnapshot ApplyReplayPlaybackLatch(TosuSnapshot snapshot, bool nativeReplayDetected)
    {
        var replayDetected = snapshot.IsWatchedReplay || nativeReplayDetected;
        if (snapshot.IsPlaying)
        {
            replayGameplayGenerationActive = true;
            consecutiveNonGameplayPackets = 0;
            if (replayDetected && !watchedReplayLatched)
            {
                watchedReplayLatched = true;
                Log.Information(
                    "Replay playback latched for {ClientKind} gameplay ({Source})",
                    snapshot.ClientKind,
                    nativeReplayDetected ? "native state" : "tosu payload/player identity");
            }

            // Native reads can briefly fail while lazer swaps screens or the GC
            // moves the Player/DrawableRuleset object graph. Once replay playback
            // has been positively identified it cannot become a live attempt
            // without leaving gameplay, so retain the signal for the whole run.
            if (watchedReplayLatched && !snapshot.IsWatchedReplay)
                snapshot = snapshot with { IsWatchedReplay = true };

            return snapshot;
        }

        if (snapshot.IsResults)
        {
            // Results conclusively closes the gameplay generation. Remember
            // that boundary through a short/sparse run of menu telemetry so an
            // immediate new play cannot inherit the completed replay latch.
            completedResultsAwaitingNewGameplay = true;
            consecutiveNonGameplayPackets = 0;
            return watchedReplayLatched && !snapshot.IsWatchedReplay
                ? snapshot with { IsWatchedReplay = true }
                : snapshot;
        }

        // Do not clear on a single non-gameplay packet. During lazer screen
        // transitions tosu can briefly publish an incomplete/unknown state.
        // Always close the detector generation after ten stable menu packets,
        // even when its asynchronous positive result arrived too late to latch
        // on a gameplay packet. Otherwise that stale result can suppress the
        // next genuine play as replay playback.
        if (replayGameplayGenerationActive
            && ++consecutiveNonGameplayPackets >= ReplayLatchClearPacketCount)
        {
            ResetReplayPlaybackGeneration(snapshot.ClientKind);
            Log.Debug("Replay playback detector generation cleared after a stable non-gameplay state");
        }
        else if (!replayGameplayGenerationActive)
        {
            consecutiveNonGameplayPackets = 0;
        }

        return snapshot;
    }

    private TosuSnapshot ParseSnapshot(
        JsonElement root,
        TosuPacket packet,
        bool fallbackStandardMode,
        OsuClientKind fallbackClientKind,
        IReadOnlyList<AttemptMod> fallbackMods)
    {
        var state = NormalizedState(root);
        var isPlaying = PlayingStates.Contains(state);
        var isResults = ResultStates.Contains(state);
        var parsedClientKind = ParseClientKind(root);
        var clientKind = parsedClientKind == OsuClientKind.Unknown ? fallbackClientKind : parsedClientKind;
        var beatmap = root.TryGetProperty("beatmap", out var bm) && bm.ValueKind == JsonValueKind.Object
            ? bm
            : default;

        string? artist = null, title = null, difficulty = null, checksum = null, mapper = null;
        long? beatmapId = null, beatmapSetId = null, liveTimeMs = null, firstObjectMs = null, lastObjectMs = null;
        if (beatmap.ValueKind == JsonValueKind.Object)
        {
            artist = GetString(beatmap, "artist");
            title = GetString(beatmap, "title");
            difficulty = GetString(beatmap, "version");
            checksum = GetString(beatmap, "checksum");
            mapper = GetString(beatmap, "mapper");
            beatmapId = GetLong(beatmap, "id");
            beatmapSetId = GetLong(beatmap, "set");
            if (beatmap.TryGetProperty("time", out var time) &&
                time.ValueKind == JsonValueKind.Object)
            {
                liveTimeMs = GetLong(time, "live");
                firstObjectMs = GetLong(time, "firstObject");
                lastObjectMs = GetLong(time, "lastObject");
            }
        }

        var beatmapIdentity = BeatmapIdentity(checksum, beatmapId, artist, title, difficulty, mapper);
        TosuSnapshot? previousSnapshot = LastSnapshot;
        var continuousGameplay = isPlaying
            && previousSnapshot?.IsPlaying == true
            && previousSnapshot.ClientKind == clientKind
            && string.Equals(previousSnapshot.BeatmapIdentity, beatmapIdentity, StringComparison.Ordinal)
            && (liveTimeMs is not { } currentLiveTime
                || previousSnapshot.LiveTimeMs is not { } priorLiveTime
                || currentLiveTime >= priorLiveTime);
        var stats = continuousGameplay && previousSnapshot!.BeatmapStats.RawJson != "{}"
            ? previousSnapshot.BeatmapStats
            : beatmap.ValueKind == JsonValueKind.Object
                ? ParseBeatmapStats(beatmap)
                : new BeatmapStats();

        var play = root.TryGetProperty("play", out var p) && p.ValueKind == JsonValueKind.Object
            ? p
            : default;
        var profile = root.TryGetProperty("profile", out var profileValue) && profileValue.ValueKind == JsonValueKind.Object
            ? profileValue
            : default;
        var hits = play.ValueKind == JsonValueKind.Object &&
                   play.TryGetProperty("hits", out var h) &&
                   h.ValueKind == JsonValueKind.Object
            ? h
            : default;
        var combo = play.ValueKind == JsonValueKind.Object &&
                    play.TryGetProperty("combo", out var c) &&
                    c.ValueKind == JsonValueKind.Object
            ? c
            : default;
        var health = play.ValueKind == JsonValueKind.Object &&
                     play.TryGetProperty("healthBar", out var hb) &&
                     hb.ValueKind == JsonValueKind.Object
            ? hb
            : default;
        var performance = ParsePerformance(play, root, isResults);

        var score = GetLong(play, "score") ?? GetLong(root, "score") ?? GetNestedLong(root, "resultsScreen", "score") ?? 0;
        var grade = GetString(play, "grade")
            ?? GetString(play, "rank")
            ?? GetString(root, "grade")
            ?? GetString(root, "rank")
            ?? GetNestedString(root, "resultsScreen", "grade")
            ?? GetNestedString(root, "resultsScreen", "rank")
            ?? GetNestedString(root, "score", "rank");
        var profileName = GetString(profile, "name");
        var playerName = GetString(play, "playerName")
            ?? GetNestedString(root, "resultsScreen", "playerName");
        var progress = GetDouble(play, "progress") ?? GetNestedDouble(root, "beatmap", "progress");
        if (progress is null && liveTimeMs is { } live && lastObjectMs is > 0)
        {
            progress = Math.Clamp(live / (double)lastObjectMs.Value, 0, 1);
        }
        if (isResults)
        {
            progress = 1;
        }
        var parsedMods = ParseMods(play);
        if (parsedMods.Count == 0 && !HasExplicitMods(play) && fallbackMods.Count > 0)
            parsedMods = fallbackMods;
        var mods = NormalizeMods(parsedMods, clientKind);
        var modsKey = mods.Count == 0 ? "NM" : string.Concat(mods.Select(mod => mod.Acronym));
        var hitErrors = ParseHitErrors(play, beatmapIdentity, liveTimeMs, isPlaying, isResults);
        var richHits = ParseRichHits(play, root);
        var explicitReplay = GetBool(play, "isReplay")
            || GetBool(play, "isWatchingReplay")
            || GetBool(root, "isWatchingReplay")
            || GetNestedBool(root, "game", "isWatchingReplay");

        return new TosuSnapshot
        {
            State = state,
            ClientKind = clientKind,
            IsPlaying = isPlaying,
            IsResults = isResults,
            IsStandardMode = TryGetStandardMode(root, out var isStandardMode)
                ? isStandardMode
                : fallbackStandardMode,
            Artist = artist,
            Title = title,
            Difficulty = difficulty,
            BeatmapIdentity = beatmapIdentity,
            LiveTimeMs = liveTimeMs,
            WallTime = packet.WallTime,
            MonoTime = packet.MonoTime,
            Mapper = mapper,
            BeatmapId = beatmapId,
            BeatmapSetId = beatmapSetId,
            Checksum = checksum,
            FirstObjectMs = firstObjectMs,
            LastObjectMs = lastObjectMs,
            BeatmapStats = stats,
            Media = continuousGameplay
                    && !string.IsNullOrWhiteSpace(previousSnapshot!.Media?.BeatmapFile)
                ? previousSnapshot.Media
                : ParseMedia(root, checksum, beatmapId, beatmapSetId),
            Score = score,
            Grade = grade,
            ProfileName = profileName,
            Profile = ParseProfile(profile),
            PlayerName = playerName,
            IsWatchedReplay = explicitReplay || NamesDiffer(profileName, playerName),
            HasAutoMod = HasAutoMod(mods),
            Pp = performance.Current ?? 0,
            FcPp = performance.Fc ?? 0,
            MaxPp = performance.Max ?? 0,
            ModsKey = modsKey,
            Mods = mods,
            Play = new JudgementCapture.PlayValues
            {
                Hit300 = GetDouble(hits, "300") ?? 0,
                Hit100 = GetDouble(hits, "100") ?? 0,
                Hit50 = GetDouble(hits, "50") ?? 0,
                Miss = GetDouble(hits, "0") ?? 0,
                Geki = richHits.Geki,
                Katu = richHits.Katu,
                // Stable does not expose lazer's rich slider judgement model.
                // Zero is retained for storage compatibility, but the client kind/CL
                // marker tells consumers that these values are unavailable.
                SliderBreak = clientKind == OsuClientKind.Stable ? 0 : GetDouble(hits, "sliderBreaks") ?? 0,
                LargeTickHit = clientKind == OsuClientKind.Stable ? 0 : richHits.LargeTickHits,
                LargeTickMiss = clientKind == OsuClientKind.Stable ? 0 : richHits.LargeTickMisses,
                SmallTickHit = clientKind == OsuClientKind.Stable ? 0 : richHits.SmallTickHits,
                SmallTickMiss = clientKind == OsuClientKind.Stable ? 0 : richHits.SmallTickMisses,
                SliderTailHit = clientKind == OsuClientKind.Stable ? 0 : richHits.SliderTailHits,
                SliderTailMiss = clientKind == OsuClientKind.Stable ? 0 : richHits.SliderTailMisses,
                Combo = GetDouble(combo, "max") ?? GetDouble(play, "combo") ?? 0,
                PpPeak = performance.Max ?? performance.Current ?? 0,
                PpCurrent = performance.Current ?? 0,
                Accuracy = GetDouble(play, "accuracy") ?? 0,
                Health = GetDouble(health, "normal") ?? GetDouble(play, "healthBar") ?? 1,
                UnstableRate = GetDouble(play, "unstableRate") ?? 0,
                Progress = progress,
                HitErrors = hitErrors,
            },
        };
    }

}
