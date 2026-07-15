using System.Text.Json;

namespace Kumori.Tracking;

public sealed partial class TosuClient
{
    private IReadOnlyList<double> ParseHitErrors(
        JsonElement play,
        string beatmapIdentity,
        long? liveTimeMs,
        bool isPlaying,
        bool isResults)
    {
        if (play.ValueKind != JsonValueKind.Object
            || !play.TryGetProperty("hitErrorArray", out var array)
            || array.ValueKind != JsonValueKind.Array)
        {
            if (isPlaying && (!hitErrorsWasPlaying || hitErrorsIdentity != beatmapIdentity))
                ResetHitErrors(beatmapIdentity);
            UpdateHitErrorContinuity(liveTimeMs, isPlaying, isResults);
            return Array.Empty<double>();
        }

        // A malformed/local-plugin payload must not turn one websocket callback
        // into an unbounded loop or a single large List allocation. Ordinary
        // packets append only a handful of errors; reconnect catch-up advances
        // over later packets using the same cache cursor.
        var count = Math.Min(array.GetArrayLength(), MaximumHitErrorsPerAttempt);
        var continuousAttempt = string.Equals(hitErrorsIdentity, beatmapIdentity, StringComparison.Ordinal)
            && ((isPlaying && hitErrorsWasPlaying
                 && liveTimeMs is { } live
                 && hitErrorsLastLiveTimeMs is { } previousLive
                 && live >= previousLive)
                || (isResults && (hitErrorsWasPlaying || hitErrorsWasResults)));

        if (!continuousAttempt || count < hitErrorsSourceCursor)
            ResetHitErrors(beatmapIdentity, Math.Min(count, MaximumHitErrorsPerPacket));

        if (count == hitErrorsSourceCursor && hitErrorsCache.Count > 0
            && TryReadHitError(array[count - 1], out var last)
            && hitErrorsCache[^1] == last)
        {
            UpdateHitErrorContinuity(liveTimeMs, isPlaying, isResults);
            return hitErrorsCache;
        }

        if (count == hitErrorsSourceCursor && count > 0)
            ResetHitErrors(beatmapIdentity, count);

        var appendEnd = Math.Min(count, hitErrorsSourceCursor + MaximumHitErrorsPerPacket);
        for (var index = hitErrorsSourceCursor; index < appendEnd; index++)
        {
            if (TryReadHitError(array[index], out var value))
                hitErrorsCache.Add(value);
        }
        hitErrorsSourceCursor = appendEnd;

        hitErrorsIdentity = beatmapIdentity;
        UpdateHitErrorContinuity(liveTimeMs, isPlaying, isResults);
        return hitErrorsCache;
    }

    private void ResetHitErrors(string identity, int capacity = 0)
    {
        hitErrorsCache = capacity > 0 ? new List<double>(capacity) : [];
        hitErrorsSourceCursor = 0;
        hitErrorsIdentity = identity;
    }

    private void UpdateHitErrorContinuity(long? liveTimeMs, bool isPlaying, bool isResults)
    {
        hitErrorsLastLiveTimeMs = liveTimeMs;
        hitErrorsWasPlaying = isPlaying;
        hitErrorsWasResults = isResults;
    }

    private static bool TryReadHitError(JsonElement item, out double value)
    {
        value = 0;
        return item.ValueKind == JsonValueKind.Number && item.TryGetDouble(out value);
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

