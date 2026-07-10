using System.Text.Json;
using osu.Game.Beatmaps;
using osu.Game.Online.API;
using osu.Game.Replays;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets.Osu.Mods;
using osu.Game.Rulesets.Osu.Replays;
using osu.Game.Scoring;
using osu.Game.Scoring.Legacy;
using osuTK;

namespace Kumori.ReplayViewer;

/// <summary>
/// Converts Kumori's capture stream into native osu! replay frames. Exported
/// .osr files remain decoded by lazer's legacy replay decoder; this adapter is
/// only for Kumori live/OTD captures.
/// </summary>
public static class LazerReplayAdapter
{
    private const int paused_flag = 0x02;
    private const int keyboard_sample_flag = 0x08;

    public static Score? DecodedScore { get; private set; }

    public static Mod[] CreateCapturedMods(string modsKey)
    {
        var result = new List<Mod>();
        ModEntry[] entries = parseModEntries(modsKey);
        string value = modsKey.ToUpperInvariant();

        if (hasMod(entries, value, "EZ"))
            result.Add(new OsuModEasy());
        if (hasMod(entries, value, "HR"))
            result.Add(new OsuModHardRock());
        if (hasMod(entries, value, "HT"))
            result.Add(configureRate(new OsuModHalfTime(), entries, 0.75));
        if (hasMod(entries, value, "NC"))
            result.Add(configureRate(new OsuModNightcore(), entries, 1.5));
        else if (hasMod(entries, value, "DT"))
            result.Add(configureRate(new OsuModDoubleTime(), entries, 1.5));
        if (hasMod(entries, value, "DA"))
            result.Add(configureDifficultyAdjust(new OsuModDifficultyAdjust(), entries));
        if (hasMod(entries, value, "HD"))
            result.Add(new OsuModHidden());
        if (hasMod(entries, value, "FL"))
            result.Add(new OsuModFlashlight());
        if (hasMod(entries, value, "NF"))
            result.Add(new OsuModNoFail());
        if (hasMod(entries, value, "SO"))
            result.Add(new OsuModSpunOut());

        if (result.Count > 0)
            return result.ToArray();

        Mod[] apiMods = createModsFromApiJson(modsKey);
        return apiMods.Length > 0 ? apiMods : result.ToArray();
    }

    public static Mod[] CreateCapturedMods(AttemptContract attempt)
    {
        if (attempt.Mods.Count == 0)
            return CreateCapturedMods(attempt.ModsKey);

        var result = new List<Mod>();
        foreach (var entry in attempt.Mods)
        {
            var acronym = entry.Acronym.Trim().ToUpperInvariant();
            switch (acronym)
            {
                case "EZ":
                    result.Add(new OsuModEasy());
                    break;

                case "HR":
                    result.Add(new OsuModHardRock());
                    break;

                case "HT":
                case "DC":
                    result.Add(configureRate(new OsuModHalfTime(), rateFromSettings(entry.Settings) ?? 0.75));
                    break;

                case "NC":
                    result.Add(configureRate(new OsuModNightcore(), rateFromSettings(entry.Settings) ?? 1.5));
                    break;

                case "DT":
                    result.Add(configureRate(new OsuModDoubleTime(), rateFromSettings(entry.Settings) ?? 1.5));
                    break;

                case "DA":
                    result.Add(configureDifficultyAdjust(new OsuModDifficultyAdjust(), entry.Settings));
                    break;

                case "HD":
                    result.Add(new OsuModHidden());
                    break;

                case "FL":
                    result.Add(new OsuModFlashlight());
                    break;

                case "NF":
                    result.Add(new OsuModNoFail());
                    break;

                case "SO":
                    result.Add(new OsuModSpunOut());
                    break;
            }
        }

        return result.Count == 0 ? CreateCapturedMods(attempt.ModsKey) : result.ToArray();
    }

    private static Mod[] createModsFromApiJson(string modsKey)
    {
        try
        {
            APIMod[]? apiMods = Newtonsoft.Json.JsonConvert.DeserializeObject<APIMod[]>(modsKey);
            if (apiMods == null || apiMods.Length == 0)
                return [];

            var ruleset = new OsuRuleset();
            return apiMods.Select(m => m.ToMod(ruleset)).ToArray();
        }
        catch (Newtonsoft.Json.JsonException)
        {
            return [];
        }
    }

    private static bool hasMod(IReadOnlyList<ModEntry> entries, string rawUpper, string acronym)
        => entries.Count > 0
            ? entries.Any(e => e.Acronym.Equals(acronym, StringComparison.OrdinalIgnoreCase))
            : rawUpper.Contains(acronym, StringComparison.OrdinalIgnoreCase);

    private static T configureRate<T>(T mod, IReadOnlyList<ModEntry> entries, double fallback)
        where T : ModRateAdjust
    {
        double rate = entries.Select(e => e.SpeedChange)
                             .FirstOrDefault(v => v is > 0) ?? fallback;
        mod.SpeedChange.Value = rate;
        return mod;
    }

    private static T configureRate<T>(T mod, double rate)
        where T : ModRateAdjust
    {
        mod.SpeedChange.Value = rate;
        return mod;
    }

    private static OsuModDifficultyAdjust configureDifficultyAdjust(
        OsuModDifficultyAdjust mod,
        IReadOnlyList<ModEntry> entries)
    {
        ModEntry? entry = entries.FirstOrDefault(e => e.Acronym.Equals("DA", StringComparison.OrdinalIgnoreCase));
        return entry == null ? mod : configureDifficultyAdjust(mod, entry.Settings);
    }

    private static OsuModDifficultyAdjust configureDifficultyAdjust(
        OsuModDifficultyAdjust mod,
        IReadOnlyDictionary<string, JsonElement> settings)
    {
        if (difficultyValue(settings, "circle_size", "cs") is { } cs)
            mod.CircleSize.Value = cs;
        if (difficultyValue(settings, "approach_rate", "ar") is { } ar)
            mod.ApproachRate.Value = ar;
        if (difficultyValue(settings, "overall_difficulty", "od", "accuracy") is { } od)
            mod.OverallDifficulty.Value = od;
        if (difficultyValue(settings, "drain_rate", "hp", "hp_drain") is { } hp)
            mod.DrainRate.Value = hp;

        return mod;
    }

    private static double? rateFromSettings(IReadOnlyDictionary<string, JsonElement> settings)
    {
        if (settings.TryGetValue("speed_change", out var speed) &&
            speed.ValueKind == JsonValueKind.Number &&
            speed.TryGetDouble(out var parsed) &&
            parsed > 0)
        {
            return parsed;
        }

        return null;
    }

    private static float? difficultyValue(IReadOnlyDictionary<string, JsonElement> settings, params string[] keys)
    {
        foreach (string key in keys)
        {
            if (settings.TryGetValue(key, out var value) &&
                value.ValueKind == JsonValueKind.Number &&
                value.TryGetSingle(out float parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static ModEntry[] parseModEntries(string modsKey)
    {
        try
        {
            using var document = JsonDocument.Parse(modsKey);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return [];

            return document.RootElement.EnumerateArray()
                           .Where(e => e.ValueKind == JsonValueKind.Object)
                           .Select(e =>
                           {
                               string acronym = e.TryGetProperty("acronym", out var acronymElement)
                                   ? acronymElement.GetString() ?? ""
                                   : "";
                               double? speed = null;
                               var settingsMap = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
                               if (e.TryGetProperty("settings", out var settings)
                                   && settings.ValueKind == JsonValueKind.Object
                                   )
                               {
                                   foreach (var property in settings.EnumerateObject())
                                       settingsMap[property.Name] = property.Value.Clone();

                                   if (settings.TryGetProperty("speed_change", out var speedElement)
                                       && speedElement.TryGetDouble(out double parsed))
                                       speed = parsed;
                               }

                               return new ModEntry(acronym, speed, settingsMap);
                           })
                           .Where(e => !string.IsNullOrWhiteSpace(e.Acronym))
                           .ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static Replay CreateReplay(ViewerContract contract)
    {
        DecodedScore = null;
        if (!string.IsNullOrWhiteSpace(contract.ReplayPath) && File.Exists(contract.ReplayPath))
        {
            using var stream = File.OpenRead(contract.ReplayPath);
            DecodedScore = new KumoriLegacyScoreDecoder(contract.BeatmapPath).Parse(stream);
            return DecodedScore.Replay;
        }

        bool importedReplay = contract.Attempt.MovementSource.Equals("replay", StringComparison.OrdinalIgnoreCase);
        var replay = new Replay();
        var orderedSamples = normalizeSamples(contract, importedReplay);

        if (orderedSamples.Length > 0)
            replay.Frames.Add(new OsuReplayFrame(
                orderedSamples[0].MapTimeMs - 1,
                new Vector2((float)orderedSamples[0].X, (float)orderedSamples[0].Y)));

        foreach (NormalizedReplaySample sample in orderedSamples)
        {
            var actions = new List<OsuAction>();

            if (sample.LeftPressed)
                actions.Add(OsuAction.LeftButton);
            if (sample.RightPressed)
                actions.Add(OsuAction.RightButton);

            replay.Frames.Add(new OsuReplayFrame(
                sample.MapTimeMs,
                new Vector2((float)sample.X, (float)sample.Y),
                actions.ToArray()));
        }

        if (orderedSamples.Length > 0)
            replay.Frames.Add(new OsuReplayFrame(
                orderedSamples[^1].MapTimeMs + 1,
                new Vector2((float)orderedSamples[^1].X, (float)orderedSamples[^1].Y)));

        return replay;
    }

    /// <summary>
    /// Makes a captured replay cover the beatmap's full playable window.
    /// Captures can start late (attempt detection latency) or carry garbage
    /// frames stamped while osu! sat on the results screen; without this the
    /// viewer plays hit objects before the first frame with no cursor, and
    /// outlier timestamps stretch the replay past the map.
    ///
    /// - Frames far outside [first hit − 30 s, last hit + 10 s] are dropped.
    /// - A hold frame (first known position, no actions, so no phantom
    ///   presses) is prepended down to the lead-in when the capture starts
    ///   after it.
    /// - A release frame at the last known position is appended out to just
    ///   past the final hit object when the capture ends early.
    /// </summary>
    public static void FitCapturedReplay(Replay replay, double firstHitTime, double lastHitTime, double clockRate = 1)
    {
        var frames = replay.Frames.OfType<OsuReplayFrame>().OrderBy(f => f.Time).ToList();
        if (frames.Count == 0)
            return;

        frames = scaleRateAdjustedCaptureIfNeeded(frames, lastHitTime, clockRate);

        double minAllowed = firstHitTime - 30_000;
        double maxAllowed = lastHitTime + 10_000;
        frames.RemoveAll(f => f.Time < minAllowed || f.Time > maxAllowed);
        if (frames.Count == 0)
            return;

        var first = frames[0];
        double leadStart = Math.Min(first.Time, firstHitTime - 2_000);
        if (first.Time > leadStart)
            frames.Insert(0, new OsuReplayFrame(leadStart, first.Position));

        var last = frames[^1];
        double tailEnd = Math.Max(last.Time, lastHitTime + 500);
        if (last.Time < tailEnd)
            frames.Add(new OsuReplayFrame(tailEnd, last.Position));

        replay.Frames.Clear();
        replay.Frames.AddRange(frames);
    }

    private static List<OsuReplayFrame> scaleRateAdjustedCaptureIfNeeded(
        List<OsuReplayFrame> frames,
        double lastHitTime,
        double clockRate)
    {
        if (clockRate <= 0 || Math.Abs(clockRate - 1) < 0.001 || frames.Count == 0)
            return frames;

        double lastFrameTime = frames[^1].Time;
        double rawError = Math.Abs(lastFrameTime - lastHitTime);
        double scaledError = Math.Abs(lastFrameTime * clockRate - lastHitTime);
        if (scaledError + 250 >= rawError)
            return frames;

        NativeViewerLog.Write($"Scaling captured replay frame times by clock_rate={clockRate:0.###}: "
                              + $"last_frame={lastFrameTime:0.###}, last_hit={lastHitTime:0.###}");
        return frames
            .Select(f => new OsuReplayFrame(f.Time * clockRate, f.Position, f.Actions.ToArray()))
            .OrderBy(f => f.Time)
            .ToList();
    }

    private static NormalizedReplaySample[] normalizeSamples(ViewerContract contract, bool importedReplay)
    {
        bool tabletSource = contract.Attempt.MovementSource.Contains("tablet", StringComparison.OrdinalIgnoreCase);
        bool statefulReplayFrameSource = contract.Attempt.MovementSource.Contains("lazer_memory", StringComparison.OrdinalIgnoreCase)
                                         || contract.Attempt.MovementSource.Contains("replay_frame", StringComparison.OrdinalIgnoreCase);

        IEnumerable<MovementSample> source = contract.Samples;
        var unpaused = contract.Samples.Where(s => (s.Flags & paused_flag) == 0).ToArray();

        // Paused capture samples can contain arbitrary movement at a frozen
        // map timestamp. Feeding them to lazer creates huge same-frame cursor
        // warps and can overwrite button state captured at the same time.
        if (unpaused.Length > 0)
            source = unpaused;

        var ordered = source.Select((sample, index) => new IndexedSample(sample, index))
                            .OrderBy(s => s.Sample.MapTimeMs)
                            .ThenBy(s => s.Sample.MonotonicMs)
                            .ThenBy(s => s.Index)
                            .ToArray();

        if (tabletSource && !importedReplay)
            return normalizeTabletSamples(ordered);

        return ordered.GroupBy(s => s.Sample.MapTimeMs)
                      .Select(g => normalizeBucket(g.ToArray(), importedReplay, tabletSource, statefulReplayFrameSource))
                      .ToArray();
    }

    private static NormalizedReplaySample normalizeBucket(
        IReadOnlyList<IndexedSample> bucket, bool importedReplay, bool tabletSource, bool statefulReplayFrameSource)
    {
        MovementSample positionSample;

        if (tabletSource)
        {
            // Keyboard-edge samples in tablet captures are stamped by the
            // polling thread and may carry the desktop mouse position. Keep
            // their button bits, but never let them steer the tablet cursor.
            positionSample = bucket.Select(s => s.Sample)
                                   .LastOrDefault(s => (s.Flags & keyboard_sample_flag) == 0)
                             ?? bucket[^1].Sample;
        }
        else
            positionSample = bucket[^1].Sample;

        bool leftPressed;
        bool rightPressed;
        if (statefulReplayFrameSource)
        {
            leftPressed = isLeftAction(bucket[^1].Sample, importedReplay, tabletSource);
            rightPressed = isRightAction(bucket[^1].Sample, importedReplay, tabletSource);
        }
        else
        {
            leftPressed = bucket.Any(s => isLeftAction(s.Sample, importedReplay, tabletSource));
            rightPressed = bucket.Any(s => isRightAction(s.Sample, importedReplay, tabletSource));
        }

        return new NormalizedReplaySample(
            bucket[0].Sample.MapTimeMs,
            positionSample.X,
            positionSample.Y,
            leftPressed,
            rightPressed);
    }

    private static NormalizedReplaySample[] normalizeTabletSamples(IReadOnlyList<IndexedSample> ordered)
    {
        var frames = new List<NormalizedReplaySample>();
        bool leftPressed = false;
        bool rightPressed = false;
        MovementSample? lastPosition = null;
        MovementSample? lastAcceptedPosition = null;
        double? lastAcceptedTime = null;

        foreach (var group in ordered.GroupBy(s => s.Sample.MapTimeMs))
        {
            var positionSamples = new List<MovementSample>();

            foreach (IndexedSample indexed in group)
            {
                MovementSample sample = indexed.Sample;

                if ((sample.Flags & keyboard_sample_flag) != 0)
                {
                    leftPressed = (sample.Buttons & 0x10) != 0;
                    rightPressed = (sample.Buttons & 0x20) != 0;
                    continue;
                }

                // Tablet packets provide position/pressure/tip only. They do
                // not resample keyboard state, so keep the last keyboard state
                // alive across movement frames until a later key-edge sample.
                positionSamples.Add(sample);
            }

            if (positionSamples.Count > 0)
            {
                MovementSample candidate = chooseContinuousPosition(positionSamples, lastAcceptedPosition);

                if (lastAcceptedPosition == null || isPlausibleTabletStep(lastAcceptedPosition, candidate, group.Key - (lastAcceptedTime ?? group.Key)))
                {
                    lastPosition = candidate;
                    lastAcceptedPosition = candidate;
                    lastAcceptedTime = group.Key;
                }
            }

            MovementSample position = lastPosition ?? group.Last().Sample;
            frames.Add(new NormalizedReplaySample(
                group.Key,
                position.X,
                position.Y,
                leftPressed,
                rightPressed));
        }

        return frames.ToArray();
    }

    private static MovementSample chooseContinuousPosition(
        IReadOnlyList<MovementSample> candidates, MovementSample? previous)
    {
        if (previous == null)
            return candidates[^1];

        return candidates.MinBy(s => distance(previous, s))!;
    }

    private static bool isPlausibleTabletStep(MovementSample previous, MovementSample candidate, double deltaMs)
    {
        double allowed = 35 + Math.Max(0, deltaMs) * 3.5;
        return distance(previous, candidate) <= allowed;
    }

    private static double distance(MovementSample a, MovementSample b)
    {
        double dx = a.X - b.X;
        double dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static bool isLeftAction(MovementSample sample, bool importedReplay, bool tabletSource)
    {
        if (importedReplay)
            return (sample.Buttons & 0x05) != 0; // stable M1/K1.

        if (tabletSource)
            // Tablet tip/pressure is movement metadata, not a keyboard key.
            // Passing it through as LeftButton makes lazer's key counter count
            // pen contact as extra presses.
            return (sample.Buttons & 0x10) != 0;

        // Kumori live mouse captures: M1=0x01, K1=0x10.
        return (sample.Buttons & 0x11) != 0;
    }

    private static bool isRightAction(MovementSample sample, bool importedReplay, bool tabletSource)
    {
        if (importedReplay)
            return (sample.Buttons & 0x0A) != 0; // stable M2/K2.

        if (tabletSource)
            return (sample.Buttons & 0x20) != 0;

        // Kumori live mouse captures: M2=0x02, K2=0x20.
        return (sample.Buttons & 0x22) != 0;
    }

    private sealed record NormalizedReplaySample(
        double MapTimeMs,
        double X,
        double Y,
        bool LeftPressed,
        bool RightPressed);

    private sealed record IndexedSample(MovementSample Sample, int Index);

    private sealed record ModEntry(
        string Acronym,
        double? SpeedChange,
        IReadOnlyDictionary<string, JsonElement>? RawSettings = null)
    {
        public IReadOnlyDictionary<string, JsonElement> Settings => RawSettings ?? EmptySettings;

        private static readonly IReadOnlyDictionary<string, JsonElement> EmptySettings =
            new Dictionary<string, JsonElement>();
    }

    /// <summary>
    /// Supplies lazer's official LegacyScoreDecoder with Kumori's already
    /// matched beatmap. Frame parsing, stable sentinel handling, action
    /// conversion, old-map offsets, and negative-delta compatibility remain
    /// entirely inside the upstream decoder.
    /// </summary>
    private sealed class KumoriLegacyScoreDecoder(string beatmapPath) : LegacyScoreDecoder
    {
        private readonly WorkingBeatmap workingBeatmap = new FlatWorkingBeatmap(beatmapPath);

        protected override Ruleset GetRuleset(int rulesetId)
        {
            if (rulesetId != 0)
                throw new InvalidDataException($"Replay ruleset {rulesetId} is not osu!standard.");
            return new OsuRuleset();
        }

        protected override WorkingBeatmap GetBeatmap(string md5Hash) => workingBeatmap;
    }
}
