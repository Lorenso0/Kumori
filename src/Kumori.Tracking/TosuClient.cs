using System.Text.Json;
using Serilog;

namespace Kumori.Tracking;

/// <summary>
/// Consumes packets from an <see cref="ITosuPacketSource"/>, parses them, and
/// raises typed events. Parsing helpers mirror the legacy tracker
/// (_state, _normalized_state, _mode_is_standard, _beatmap_values identity)
/// so fixture replay produces identical decisions.
/// </summary>
public sealed class TosuClient
{
    private static readonly HashSet<string> PlayingStates = new() { "play", "playing", "gameplay" };
    private static readonly HashSet<string> ResultStates = new()
    {
        "result", "results", "resultscreen", "resultsscreen", "ranking", "rank",
    };

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
            Ingest(packet);
        }
    }

    /// <summary>Synchronous ingest of one packet (also used by tests directly).</summary>
    public void Ingest(TosuPacket packet)
    {
        TosuSnapshot snapshot;
        try
        {
            using var doc = JsonDocument.Parse(packet.Raw);
            snapshot = ParseSnapshot(doc.RootElement, packet);
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

    private static TosuSnapshot ParseSnapshot(JsonElement root, TosuPacket packet)
    {
        var state = NormalizedState(root);
        var beatmap = root.TryGetProperty("beatmap", out var bm) && bm.ValueKind == JsonValueKind.Object
            ? bm
            : default;

        string? artist = null, title = null, difficulty = null, checksum = null, mapper = null;
        long? beatmapId = null, beatmapSetId = null, liveTimeMs = null, firstObjectMs = null, lastObjectMs = null;
        BeatmapStats stats = new();
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
            stats = ParseBeatmapStats(beatmap);
        }

        var play = root.TryGetProperty("play", out var p) && p.ValueKind == JsonValueKind.Object
            ? p
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
        var pp = play.ValueKind == JsonValueKind.Object &&
                 play.TryGetProperty("pp", out var ppElement) &&
                 ppElement.ValueKind == JsonValueKind.Object
            ? ppElement
            : default;
        var health = play.ValueKind == JsonValueKind.Object &&
                     play.TryGetProperty("healthBar", out var hb) &&
                     hb.ValueKind == JsonValueKind.Object
            ? hb
            : default;

        var score = GetLong(play, "score") ?? GetLong(root, "score") ?? GetNestedLong(root, "resultsScreen", "score") ?? 0;
        var grade = GetString(play, "grade")
            ?? GetString(play, "rank")
            ?? GetString(root, "grade")
            ?? GetString(root, "rank")
            ?? GetNestedString(root, "resultsScreen", "grade")
            ?? GetNestedString(root, "resultsScreen", "rank")
            ?? GetNestedString(root, "score", "rank");
        var progress = GetDouble(play, "progress") ?? GetNestedDouble(root, "beatmap", "progress");
        if (progress is null && liveTimeMs is { } live && lastObjectMs is > 0)
        {
            progress = Math.Clamp(live / (double)lastObjectMs.Value, 0, 1);
        }
        if (ResultStates.Contains(state))
        {
            progress = 1;
        }
        var mods = ParseMods(play);
        var hitErrors = ParseHitErrors(play);
        var richHits = ParseRichHits(play, root);

        return new TosuSnapshot
        {
            State = state,
            IsPlaying = PlayingStates.Contains(state),
            IsResults = ResultStates.Contains(state),
            IsStandardMode = IsStandardMode(root),
            Artist = artist,
            Title = title,
            Difficulty = difficulty,
            BeatmapIdentity = BeatmapIdentity(checksum, beatmapId, artist, title, difficulty, mapper),
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
            Media = ParseMedia(root, checksum, beatmapId, beatmapSetId),
            Score = score,
            Grade = grade,
            Pp = GetDouble(pp, "current") ?? GetDouble(root, "pp") ?? 0,
            FcPp = GetDouble(pp, "fc") ?? GetNestedDouble(root, "performance", "fcPp") ?? 0,
            MaxPp = GetDouble(pp, "maxThisPlay") ?? GetDouble(pp, "maxAchievedThisPlay") ?? 0,
            ModsKey = mods.Count == 0 ? "NM" : string.Concat(mods.Select(m => m.Acronym)),
            Mods = mods,
            Play = new JudgementCapture.PlayValues
            {
                Hit300 = GetDouble(hits, "300") ?? 0,
                Hit100 = GetDouble(hits, "100") ?? 0,
                Hit50 = GetDouble(hits, "50") ?? 0,
                Miss = GetDouble(hits, "0") ?? 0,
                Geki = richHits.Geki,
                Katu = richHits.Katu,
                SliderBreak = GetDouble(hits, "sliderBreaks") ?? 0,
                LargeTickHit = richHits.LargeTickHits,
                LargeTickMiss = richHits.LargeTickMisses,
                SmallTickHit = richHits.SmallTickHits,
                SmallTickMiss = richHits.SmallTickMisses,
                SliderTailHit = richHits.SliderTailHits,
                SliderTailMiss = richHits.SliderTailMisses,
                Combo = GetDouble(combo, "max") ?? GetDouble(play, "combo") ?? 0,
                PpPeak = GetDouble(pp, "maxAchievedThisPlay") ?? GetDouble(pp, "current") ?? 0,
                PpCurrent = GetDouble(pp, "current") ?? 0,
                Accuracy = GetDouble(play, "accuracy") ?? 0,
                Health = GetDouble(health, "normal") ?? GetDouble(play, "healthBar") ?? 1,
                UnstableRate = GetDouble(play, "unstableRate") ?? 0,
                Progress = progress,
                HitErrors = hitErrors,
            },
        };
    }

    /// <summary>Port of _normalized_state: state.name (or scalar), casefolded, alphanumeric only.</summary>
    internal static string NormalizedState(JsonElement root)
    {
        string raw = "";
        if (root.TryGetProperty("state", out var state))
        {
            raw = state.ValueKind switch
            {
                JsonValueKind.Object => GetString(state, "name") ?? "",
                JsonValueKind.String => state.GetString() ?? "",
                JsonValueKind.Number => state.GetRawText(),
                _ => "",
            };
        }
        var lowered = raw.ToLowerInvariant();
        return string.Concat(lowered.Where(char.IsLetterOrDigit));
    }

    /// <summary>Port of _mode_is_standard: play.mode ?? profile.mode; object → number==0; scalar → osu/standard/0.</summary>
    internal static bool IsStandardMode(JsonElement root)
    {
        JsonElement mode = default;
        if (root.TryGetProperty("play", out var play) && play.ValueKind == JsonValueKind.Object &&
            play.TryGetProperty("mode", out var playMode) && playMode.ValueKind != JsonValueKind.Null)
        {
            mode = playMode;
        }
        else if (root.TryGetProperty("profile", out var profile) && profile.ValueKind == JsonValueKind.Object &&
                 profile.TryGetProperty("mode", out var profileMode) && profileMode.ValueKind != JsonValueKind.Null)
        {
            mode = profileMode;
        }
        if (mode.ValueKind == JsonValueKind.Undefined)
        {
            return false;
        }
        if (mode.ValueKind == JsonValueKind.Object)
        {
            return (GetLong(mode, "number") ?? 0) == 0;
        }
        var text = (mode.ValueKind == JsonValueKind.String ? mode.GetString() : mode.GetRawText())?
            .ToLowerInvariant();
        return text is "0" or "osu" or "standard";
    }

    /// <summary>
    /// Port of the identity expression in _beatmap_values:
    /// checksum, else "id:{id}", else "artist|title|version|mapper", else "unknown".
    /// </summary>
    internal static string BeatmapIdentity(
        string? checksum, long? beatmapId, string? artist, string? title,
        string? difficulty, string? mapper = null)
    {
        var trimmed = checksum?.Trim();
        if (!string.IsNullOrEmpty(trimmed))
        {
            return trimmed;
        }
        if (beatmapId is > 0)
        {
            return $"id:{beatmapId}";
        }
        var joined = $"{artist ?? ""}|{title ?? ""}|{difficulty ?? ""}|{mapper ?? ""}";
        return joined == "|||" ? "unknown" : joined;
    }

    private static string? GetString(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object
            || !obj.TryGetProperty(name, out var v)
            || v.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = v.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static long? GetLong(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object &&
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number &&
        v.TryGetInt64(out var result)
            ? result
            : null;

    private static double? GetDouble(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object &&
        obj.TryGetProperty(name, out var v) &&
        v.ValueKind == JsonValueKind.Number &&
        v.TryGetDouble(out var result)
            ? result
            : null;

    private static string? GetNestedString(JsonElement root, string objectName, string name) =>
        root.TryGetProperty(objectName, out var obj) && obj.ValueKind == JsonValueKind.Object
            ? GetString(obj, name)
            : null;

    private static long? GetNestedLong(JsonElement root, string objectName, string name) =>
        root.TryGetProperty(objectName, out var obj) && obj.ValueKind == JsonValueKind.Object
            ? GetLong(obj, name)
            : null;

    private static double? GetNestedDouble(JsonElement root, string objectName, string name) =>
        root.TryGetProperty(objectName, out var obj) && obj.ValueKind == JsonValueKind.Object
            ? GetDouble(obj, name)
            : null;

    private static bool TryGetObject(JsonElement root, string name, out JsonElement obj)
    {
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(name, out obj)
            && obj.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        obj = default;
        return false;
    }

    private static bool TryGetNestedObject(JsonElement root, string objectName, string name, out JsonElement obj)
    {
        if (TryGetObject(root, objectName, out var parent)
            && TryGetObject(parent, name, out obj))
        {
            return true;
        }

        obj = default;
        return false;
    }

    private static IReadOnlyList<AttemptMod> ParseMods(JsonElement play)
    {
        if (play.ValueKind != JsonValueKind.Object ||
            !play.TryGetProperty("mods", out var mods))
        {
            return Array.Empty<AttemptMod>();
        }

        if (mods.ValueKind == JsonValueKind.Object
            && mods.TryGetProperty("array", out var array)
            && array.ValueKind == JsonValueKind.Array)
        {
            mods = array;
        }

        if (mods.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<AttemptMod>();
        }

        var result = new List<AttemptMod>();
        foreach (var mod in mods.EnumerateArray())
        {
            if (mod.ValueKind == JsonValueKind.String)
            {
                var acronym = mod.GetString();
                if (!string.IsNullOrWhiteSpace(acronym))
                {
                    result.Add(new AttemptMod(acronym));
                }
                continue;
            }
            if (mod.ValueKind == JsonValueKind.Object)
            {
                var acronym = GetString(mod, "acronym") ?? GetString(mod, "name");
                if (!string.IsNullOrWhiteSpace(acronym))
                {
                    var settings = mod.TryGetProperty("settings", out var settingsElement)
                        ? settingsElement.GetRawText()
                        : "{}";
                    result.Add(new AttemptMod(acronym, settings));
                }
            }
        }
        return result;
    }

    private static RichHitCounts ParseRichHits(JsonElement play, JsonElement root)
    {
        var hits = CandidateHitObjects(play, root).ToArray();
        return new RichHitCounts
        {
            Geki = FirstNumber(hits, "geki") ?? 0,
            Katu = FirstNumber(hits, "katu") ?? 0,
            LargeTickHits = FirstNumber(hits, "largeTickHits", "large_tick_hits") ?? 0,
            LargeTickMisses = FirstNumber(hits, "largeTickMisses", "large_tick_misses") ?? 0,
            SmallTickHits = FirstNumber(hits, "smallTickHits", "small_tick_hits") ?? 0,
            SmallTickMisses = FirstNumber(hits, "smallTickMisses", "small_tick_misses") ?? 0,
            SliderTailHits = FirstNumber(hits, "sliderTailHits", "sliderEndHits", "slider_tail_hits", "slider_end_hits") ?? 0,
            SliderTailMisses = FirstNumber(hits, "sliderTailMisses", "sliderEndMisses", "slider_tail_misses", "slider_end_misses") ?? 0,
        };
    }

    private static IEnumerable<JsonElement> CandidateHitObjects(JsonElement play, JsonElement root)
    {
        if (TryGetObject(play, "hits", out var playHits))
        {
            yield return playHits;
        }

        if (TryGetNestedObject(root, "resultsScreen", "hits", out var resultsHits))
        {
            yield return resultsHits;
        }

        if (TryGetNestedObject(root, "result", "hits", out var resultHits))
        {
            yield return resultHits;
        }

        if (TryGetNestedObject(root, "score", "hits", out var scoreHits))
        {
            yield return scoreHits;
        }

        if (TryGetNestedObject(root, "score", "result", out var scoreResult)
            && TryGetObject(scoreResult, "hits", out var scoreResultHits))
        {
            yield return scoreResultHits;
        }
    }

    private static double? FirstNumber(IEnumerable<JsonElement> objects, params string[] names)
    {
        foreach (var obj in objects)
        {
            foreach (var name in names)
            {
                if (GetDouble(obj, name) is { } value)
                {
                    return value;
                }
            }
        }

        return null;
    }

    private static IReadOnlyList<double> ParseHitErrors(JsonElement play)
    {
        if (play.ValueKind != JsonValueKind.Object
            || !play.TryGetProperty("hitErrorArray", out var array)
            || array.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<double>();
        }

        var result = new List<double>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Number && item.TryGetDouble(out var value))
            {
                result.Add(value);
            }
        }
        return result;
    }

    private static BeatmapStats ParseBeatmapStats(JsonElement beatmap)
    {
        if (!beatmap.TryGetProperty("stats", out var stats)
            || stats.ValueKind != JsonValueKind.Object)
        {
            return new BeatmapStats();
        }

        var stars = stats.TryGetProperty("stars", out var starsElement) ? starsElement : default;
        var bpm = stats.TryGetProperty("bpm", out var bpmElement) ? bpmElement : default;
        var objects = stats.TryGetProperty("objects", out var objectsElement) ? objectsElement : default;

        var totalStars = GetDouble(stars, "total") ?? GetDouble(stars, "converted") ?? GetDouble(stars, "original");

        return new BeatmapStats
        {
            // `original` is the map's unmodified rating; `total` includes the
            // currently selected mods. Keep both because a beatmap record is
            // shared by all attempts, while the latter is attempt-specific.
            BaseStars = GetDouble(stars, "original") ?? totalStars,
            Stars = totalStars,
            ApproachRate = GetNestedStat(stats, "ar", "original") ?? GetNestedStat(stats, "ar", "converted"),
            CircleSize = GetNestedStat(stats, "cs", "original") ?? GetNestedStat(stats, "cs", "converted"),
            OverallDifficulty = GetNestedStat(stats, "od", "original") ?? GetNestedStat(stats, "od", "converted"),
            DrainRate = GetNestedStat(stats, "hp", "original") ?? GetNestedStat(stats, "hp", "converted"),
            Bpm = GetDouble(bpm, "common") ?? GetDouble(bpm, "realtime"),
            MaxCombo = GetLong(stats, "maxCombo"),
            CircleCount = GetLong(objects, "circles"),
            SliderCount = GetLong(objects, "sliders"),
            SpinnerCount = GetLong(objects, "spinners"),
            RawJson = stats.GetRawText(),
        };
    }

    private static double? GetNestedStat(JsonElement stats, string name, string valueName)
        => stats.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Object
            ? GetDouble(element, valueName)
            : TryGetDouble(element);

    private static double? TryGetDouble(JsonElement element)
        => element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var value) ? value : null;

    private static TosuMediaInfo? ParseMedia(
        JsonElement root,
        string? checksum,
        long? beatmapId,
        long? beatmapSetId)
    {
        if (!root.TryGetProperty("directPath", out var direct)
            || direct.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        root.TryGetProperty("folders", out var folders);
        return new TosuMediaInfo
        {
            Checksum = checksum,
            BeatmapId = beatmapId,
            BeatmapSetId = beatmapSetId,
            SongsFolder = GetString(folders, "songs"),
            GameFolder = GetString(folders, "game"),
            BeatmapFile = GetString(direct, "beatmapFile"),
            BeatmapFolder = GetString(direct, "beatmapFolder"),
            BackgroundFile = GetString(direct, "beatmapBackground"),
            AudioFile = GetString(direct, "beatmapAudio"),
            SkinFolder = GetString(direct, "skinFolder"),
        };
    }
}

/// <summary>Parsed view of one tosu packet - what the GUI/status layer needs.</summary>
public sealed record TosuSnapshot
{
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
    public double Pp { get; init; }
    public double FcPp { get; init; }
    public double MaxPp { get; init; }
    public string ModsKey { get; init; } = "NM";
    public IReadOnlyList<AttemptMod> Mods { get; init; } = Array.Empty<AttemptMod>();
    public JudgementCapture.PlayValues Play { get; init; } = new();

    public string? BeatmapDisplay =>
        Artist is null && Title is null
            ? null
            : $"{Artist} — {Title}" + (Difficulty is null ? "" : $" [{Difficulty}]");
}

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

internal sealed record RichHitCounts
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
