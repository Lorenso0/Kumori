using System.Text.Json;

namespace Kumori.Tracking;

public sealed partial class TosuClient
{
    private static TosuProfile? ParseProfile(JsonElement profile)
    {
        var id = GetLong(profile, "id");
        var name = GetString(profile, "name");
        if (id is not > 0 || string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        // tosu v2 uses the camelCase names below.  The aliases retain
        // compatibility with older tosu payloads and recorded fixtures.
        return new TosuProfile
        {
            Id = id.Value,
            Name = name,
            TotalPp = GetDouble(profile, "performancePoints") ?? GetDouble(profile, "totalPp") ?? GetDouble(profile, "total_pp") ?? GetDouble(profile, "pp"),
            GlobalRank = GetLong(profile, "rank") ?? GetLong(profile, "globalRank") ?? GetLong(profile, "global_rank"),
            CountryRank = GetLong(profile, "countryRank") ?? GetLong(profile, "country_rank"),
            Accuracy = GetDouble(profile, "accuracy"),
            PlayCount = GetLong(profile, "playCount") ?? GetLong(profile, "play_count"),
            Level = GetDouble(profile, "level"),
            RankedScore = GetLong(profile, "rankedScore") ?? GetLong(profile, "ranked_score"),
            CountryCode = GetString(profile, "countryCode") ?? GetString(profile, "country_code"),
        };
    }

    internal static OsuClientKind ParseClientKind(JsonElement root)
    {
        var client = GetString(root, "client");
        if (client?.Equals("stable", StringComparison.OrdinalIgnoreCase) == true)
        {
            return OsuClientKind.Stable;
        }
        if (client?.Equals("lazer", StringComparison.OrdinalIgnoreCase) == true)
        {
            return OsuClientKind.Lazer;
        }
        return OsuClientKind.Unknown;
    }

    internal static IReadOnlyList<AttemptMod> NormalizeMods(
        IReadOnlyList<AttemptMod> mods,
        OsuClientKind clientKind)
    {
        if (clientKind != OsuClientKind.Stable ||
            mods.Any(mod => mod.Acronym.Equals("CL", StringComparison.OrdinalIgnoreCase)))
        {
            return mods;
        }

        return mods.Concat([new AttemptMod("CL")]).ToArray();
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
        if (raw.Length == 0)
            return "";

        int inputLength = Math.Min(raw.Length, MaximumNormalizedStateCharacters);
        Span<char> normalized = stackalloc char[inputLength];
        var written = 0;
        foreach (char value in raw.AsSpan(0, inputLength))
        {
            char lowered = char.ToLowerInvariant(value);
            if (char.IsLetterOrDigit(lowered))
                normalized[written++] = lowered;
        }
        return new string(normalized[..written]);
    }

    /// <summary>Port of _mode_is_standard: play.mode ?? profile.mode; object → number==0; scalar → osu/standard/0.</summary>
    internal static bool IsStandardMode(JsonElement root) =>
        TryGetStandardMode(root, out var isStandardMode) && isStandardMode;

    private static bool TryGetStandardMode(JsonElement root, out bool isStandardMode)
    {
        isStandardMode = false;
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
            isStandardMode = (GetLong(mode, "number") ?? 0) == 0;
            return true;
        }
        var text = (mode.ValueKind == JsonValueKind.String ? mode.GetString() : mode.GetRawText())?
            .ToLowerInvariant();
        isStandardMode = text is "0" or "osu" or "standard";
        return true;
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

    private static bool NamesDiffer(string? profileName, string? playerName) =>
        !string.IsNullOrWhiteSpace(profileName)
        && !string.IsNullOrWhiteSpace(playerName)
        && !string.Equals(profileName.Trim(), playerName.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool GetBool(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value))
            return false;

        return value.ValueKind == JsonValueKind.True
               || value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number != 0
               || value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var parsed) && parsed;
    }

    private static bool GetNestedBool(JsonElement element, string parent, string property)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(parent, out var nested)
           && GetBool(nested, property);

    private static bool HasAutoMod(IReadOnlyList<AttemptMod> mods) => mods.Any(mod =>
        mod.Acronym.Equals("AT", StringComparison.OrdinalIgnoreCase)
        || mod.Acronym.Equals("Auto", StringComparison.OrdinalIgnoreCase)
        || mod.Acronym.Equals("Autoplay", StringComparison.OrdinalIgnoreCase));

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

    /// <summary>
    /// Tosu exposes live performance under play.pp, but versions and skins can
    /// move the final value to result/score payloads. Read the same fields from
    /// every known score container so a results packet does not look like 0pp.
    /// </summary>
    private static PerformanceValues ParsePerformance(JsonElement play, JsonElement root, bool isResults)
    {
        // The live play object can remain populated on the results screen.  In
        // that case its PP is the last in-game estimate, while the result
        // payload contains the finalized value. Prefer result containers only
        // for result packets; during gameplay play.pp remains authoritative.
        double? current = null, fc = null, max = null;
        if (!isResults)
        {
            AccumulatePerformance(play, ref current, ref fc, ref max);
        }
        if (TryGetObject(root, "resultsScreen", out var resultsScreen))
            AccumulatePerformance(resultsScreen, ref current, ref fc, ref max);
        if (TryGetObject(root, "result", out var result))
            AccumulatePerformance(result, ref current, ref fc, ref max);
        if (TryGetObject(root, "score", out var score))
            AccumulatePerformance(score, ref current, ref fc, ref max);
        if (TryGetObject(root, "performance", out var performance))
            AccumulatePerformance(performance, ref current, ref fc, ref max);
        if (isResults)
        {
            AccumulatePerformance(play, ref current, ref fc, ref max);
        }

        // Older tosu payloads use a scalar root pp value.
        if (root.TryGetProperty("pp", out var rootPp))
        {
            current ??= TryGetDouble(rootPp);
        }
        fc ??= GetNestedDouble(root, "performance", "fcPp");
        return new PerformanceValues(current, fc, max);
    }

    private static void AccumulatePerformance(
        JsonElement source,
        ref double? current,
        ref double? fc,
        ref double? max)
    {
        var pp = TryGetPpObject(source, out var nested) ? nested : source;
        current ??= GetDouble(pp, "current") ?? GetDouble(pp, "value") ?? GetDouble(pp, "pp");
        fc ??= GetDouble(pp, "fc") ?? GetDouble(pp, "fcPp") ?? GetDouble(pp, "fc_pp");
        max ??= GetDouble(pp, "maxThisPlay") ?? GetDouble(pp, "maxAchievedThisPlay")
            ?? GetDouble(pp, "max_pp");
    }

    private static bool TryGetPpObject(JsonElement source, out JsonElement pp)
    {
        if (source.ValueKind == JsonValueKind.Object
            && source.TryGetProperty("pp", out pp)
            && pp.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        pp = default;
        return false;
    }

    private readonly record struct PerformanceValues(double? Current, double? Fc, double? Max);
    private readonly record struct ParsedModPayload(
        IReadOnlyList<AttemptMod> Mods,
        bool IsExplicit,
        bool IsAuthoritativeResult);

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
            var parsedArray = ParseModArray(array);
            if (parsedArray.Count > 0)
            {
                return parsedArray;
            }

            // Stable may expose an empty array while still supplying its
            // legacy packed acronym string (for example "NF" or "HDHR").
            var packed = GetString(mods, "name") ?? GetString(mods, "str");
            return ParsePackedMods(packed);
        }

        if (mods.ValueKind == JsonValueKind.Object)
        {
            return ParsePackedMods(GetString(mods, "name") ?? GetString(mods, "str"));
        }

        if (mods.ValueKind == JsonValueKind.String)
        {
            return ParsePackedMods(mods.GetString());
        }

        if (mods.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<AttemptMod>();
        }

        return ParseModArray(mods);
    }

    /// <summary>
    /// Reads lazer mods from the completed ScoreInfo containers before falling
    /// back to the live play object. Unlike tosu's positional menu mod mapping,
    /// these objects originate from ScoreInfo.ModsJson and retain custom mods
    /// and their serialized settings.
    /// </summary>
    private static ParsedModPayload ParseModsPayload(JsonElement root, JsonElement play, bool isResults)
    {
        var explicitPayloadSeen = false;

        if (isResults)
        {
            foreach (JsonElement candidate in ResultModSources(root))
            {
                if (!candidate.TryGetProperty("mods", out _))
                    continue;

                explicitPayloadSeen = true;
                IReadOnlyList<AttemptMod> parsed = ParseMods(candidate);
                return new ParsedModPayload(parsed, true, true);
            }
        }

        if (play.ValueKind == JsonValueKind.Object && play.TryGetProperty("mods", out _))
        {
            explicitPayloadSeen = true;
            IReadOnlyList<AttemptMod> parsed = ParseMods(play);
            if (parsed.Count > 0)
                return new ParsedModPayload(parsed, true, false);
        }

        return new ParsedModPayload([], explicitPayloadSeen, false);
    }

    private static IEnumerable<JsonElement> ResultModSources(JsonElement root)
    {
        foreach (string containerName in new[] { "resultsScreen", "result" })
        {
            if (!TryGetObject(root, containerName, out JsonElement container))
                continue;

            if (TryGetObject(container, "score", out JsonElement nestedScore))
                yield return nestedScore;

            yield return container;
        }

        if (TryGetObject(root, "score", out JsonElement score))
            yield return score;
    }

    private static IReadOnlyList<AttemptMod> PreserveLazerBpmMods(
        IReadOnlyList<AttemptMod> parsedMods,
        IReadOnlyList<AttemptMod> previousMods,
        OsuClientKind clientKind,
        bool continuousAttemptTelemetry,
        bool authoritativeResult)
    {
        if (clientKind != OsuClientKind.Lazer
            || !continuousAttemptTelemetry
            || authoritativeResult)
            return parsedMods;

        AttemptMod? previousBpm = previousMods.FirstOrDefault(IsBpmAdjust);
        if (previousBpm is null)
            return parsedMods;

        int incomingBpmIndex = -1;
        for (int i = 0; i < parsedMods.Count; i++)
        {
            if (IsBpmAdjust(parsedMods[i]))
            {
                incomingBpmIndex = i;
                break;
            }
        }

        // Mods cannot change during an active play. If a transition packet
        // substitutes tosu's hardcoded menu interpretation (FR/empty on custom
        // builds), retain the ScoreInfo-derived set captured during gameplay.
        if (incomingBpmIndex < 0)
            return previousMods;

        // Some transition payloads retain the acronym but omit default or
        // lazily-populated settings. Keep the richer gameplay settings so the
        // target BPM and stat-scaling choice survive finalization.
        if (HasSettings(parsedMods[incomingBpmIndex].SettingsJson)
            || !HasSettings(previousBpm.SettingsJson))
        {
            return parsedMods;
        }

        var merged = parsedMods.ToArray();
        merged[incomingBpmIndex] = previousBpm;
        return merged;
    }

    private static bool IsBpmAdjust(AttemptMod mod)
        => mod.Acronym.Equals("BPM", StringComparison.OrdinalIgnoreCase);

    private static bool HasSettings(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson))
            return false;

        try
        {
            using var document = JsonDocument.Parse(settingsJson);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.EnumerateObject().Any();
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static IReadOnlyList<AttemptMod> ParseModArray(JsonElement mods)
    {
        var result = new List<AttemptMod>();
        var elementsSeen = 0;
        foreach (var mod in mods.EnumerateArray())
        {
            if (elementsSeen++ >= MaximumParsedMods)
                break;
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

    private static IReadOnlyList<AttemptMod> ParsePackedMods(string? packed)
    {
        if (string.IsNullOrWhiteSpace(packed) || packed.Equals("NM", StringComparison.OrdinalIgnoreCase))
            return [];
        ReadOnlySpan<char> value = packed.AsSpan().Trim();
        var result = new List<AttemptMod>(Math.Min(MaximumParsedMods, value.Length / 2));
        Span<char> acronym = stackalloc char[2];
        var acronymLength = 0;
        foreach (char input in value)
        {
            if (input == ' ')
                continue;
            acronym[acronymLength++] = char.ToUpperInvariant(input);
            if (acronymLength < 2)
                continue;

            result.Add(new AttemptMod(new string(acronym)));
            acronymLength = 0;
            if (result.Count >= MaximumParsedMods)
                break;
        }
        return result;
    }

    private static RichHitCounts ParseRichHits(JsonElement play, JsonElement root)
    {
        var hits = CandidateHitObjects(play, root);
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

    private static HitObjectSources CandidateHitObjects(JsonElement play, JsonElement root)
    {
        var result = new HitObjectSources();
        if (TryGetObject(play, "hits", out var playHits))
            result.Add(playHits);

        if (TryGetNestedObject(root, "resultsScreen", "hits", out var resultsHits))
            result.Add(resultsHits);

        if (TryGetNestedObject(root, "result", "hits", out var resultHits))
            result.Add(resultHits);

        if (TryGetNestedObject(root, "score", "hits", out var scoreHits))
            result.Add(scoreHits);

        if (TryGetNestedObject(root, "score", "result", out var scoreResult)
            && TryGetObject(scoreResult, "hits", out var scoreResultHits))
            result.Add(scoreResultHits);
        return result;
    }

    private static double? FirstNumber(
        HitObjectSources objects,
        string first,
        string? second = null,
        string? third = null,
        string? fourth = null)
    {
        for (var index = 0; index < objects.Count; index++)
        {
            JsonElement obj = objects[index];
            if (GetDouble(obj, first) is { } firstValue)
                return firstValue;
            if (second is not null && GetDouble(obj, second) is { } secondValue)
                return secondValue;
            if (third is not null && GetDouble(obj, third) is { } thirdValue)
                return thirdValue;
            if (fourth is not null && GetDouble(obj, fourth) is { } fourthValue)
                return fourthValue;
        }

        return null;
    }

    private struct HitObjectSources
    {
        private JsonElement first;
        private JsonElement second;
        private JsonElement third;
        private JsonElement fourth;
        private JsonElement fifth;

        public int Count { get; private set; }

        public JsonElement this[int index] => index switch
        {
            0 => first,
            1 => second,
            2 => third,
            3 => fourth,
            4 => fifth,
            _ => throw new ArgumentOutOfRangeException(nameof(index)),
        };

        public void Add(JsonElement value)
        {
            switch (Count++)
            {
                case 0: first = value; break;
                case 1: second = value; break;
                case 2: third = value; break;
                case 3: fourth = value; break;
                case 4: fifth = value; break;
                default: throw new InvalidOperationException("Too many rich-hit sources.");
            }
        }
    }

}
