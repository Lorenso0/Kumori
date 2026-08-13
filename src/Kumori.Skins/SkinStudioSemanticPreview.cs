namespace Kumori.Skins;

public enum SkinStudioPreviewScene
{
    Showcase,
    Circles,
    Sliders,
    Hud,
    Cursor,
    Spinner,
    Judgements,
    Followpoints,
}

public enum SkinStudioRuleset
{
    Osu,
    Catch,
    Taiko,
    Mania,
    Interface,
    Audio,
    Unknown,
}

public enum SkinStudioSemanticPreviewKind
{
    HitCircles,
    HitCircleNumbers,
    ScoreNumbers,
    ComboNumbers,
    LeaderboardNumbers,
    FollowPoints,
    Slider,
    Cursor,
    Judgements,
    Spinner,
    Hud,
    Interface,
    Catch,
    Taiko,
    Mania,
    HitSoundLoop,
    SliderSoundLoop,
    SpinnerSoundLoop,
    AudioEvent,
    RawAsset,
}

public enum SkinStudioAnimationPolicy
{
    Static,
    Native,
    ScriptedLoop,
    OneShotLoop,
}

public enum SkinStudioAssetProvenance
{
    Skin,
    LazerFallback,
    Missing,
    Unknown,
}

public sealed record SkinStudioSemanticPreviewDescriptor(
    string Id,
    string FamilyId,
    string ComponentName,
    SkinStudioRuleset Ruleset,
    SkinStudioSemanticPreviewKind Kind,
    SkinStudioAnimationPolicy Animation,
    SkinExtraCompatibility Compatibility,
    SkinStudioPreviewScene Scene,
    int? ManiaKeyCount = null)
{
    public bool IsAudio => Ruleset == SkinStudioRuleset.Audio;
    public bool IsRaw => Kind == SkinStudioSemanticPreviewKind.RawAsset;
}

public sealed record SkinStudioCategoryPreviewPlan(
    SkinStudioSemanticPreviewDescriptor Target,
    IReadOnlyList<string> Components,
    IReadOnlyList<string> CalloutComponents);

public static class SkinStudioCategoryPreviewCatalog
{
    public static SkinStudioCategoryPreviewPlan? Resolve(
        string? categoryTitle,
        IEnumerable<string> availableComponents)
    {
        if (string.IsNullOrWhiteSpace(categoryTitle))
            return null;
        var components = availableComponents
            .Where(component => !string.IsNullOrWhiteSpace(component))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (components.Length == 0)
            return null;

        var preferred = categoryTitle.Trim().ToLowerInvariant() switch
        {
            "hit objects" => "hitcircle",
            "cursor and trail" => "cursor",
            "gameplay hud" => "scorebar-bg",
            "judgements" => "hit300",
            "spinner" => "spinner-circle",
            "countdown and prompts" => "ready",
            "ranking" => "ranking-panel",
            "menus and selection" => "mode-osu",
            "number fonts" => "default-0",
            "audio samples" => "normal-hitnormal",
            _ => components.FirstOrDefault(component =>
                !SkinStudioSemanticPreviewCatalog.Resolve(component).IsRaw),
        };
        if (string.IsNullOrWhiteSpace(preferred))
            return null;
        var target = SkinStudioSemanticPreviewCatalog.Resolve(preferred);
        if (target.IsRaw)
            return null;
        var familyComponents = components.Where(component =>
                SkinStudioSemanticPreviewCatalog.Resolve(component).FamilyId
                    .Equals(target.FamilyId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (categoryTitle.Trim().Equals(
                "number fonts",
                StringComparison.OrdinalIgnoreCase))
        {
            // The native hitcircle-number composition must not be obscured by
            // the score/combo/leaderboard contexts which share this family.
            familyComponents = familyComponents.Where(component =>
                    component.StartsWith(
                        "default-",
                        StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }
        else if (categoryTitle.Trim().Equals(
                     "hit objects",
                     StringComparison.OrdinalIgnoreCase))
        {
            // The category composition contains its real circle, followpoint,
            // and slider contexts together. Individual tile selection still
            // resolves to the narrower family-specific scene.
            familyComponents = components.Where(component =>
            {
                var family = SkinStudioSemanticPreviewCatalog.Resolve(component)
                    .FamilyId;
                return family is "osu.hitcircles"
                    or "osu.followpoints"
                    or "osu.slider"
                    or "osu.slider-colours";
            }).ToArray();
        }
        var calloutComponents = categoryTitle.Trim().ToLowerInvariant() switch
        {
            "hit objects" => existing(
                components,
                "approachcircle", "hitcircle", "hitcircleoverlay"),
            "cursor and trail" => existing(
                components,
                "cursor", "cursormiddle", "cursortrail"),
            "gameplay hud" => existing(
                components,
                "scorebar-bg", "scorebar-colour", "scorebar-marker"),
            "judgements" => existing(
                components,
                "hit0", "hit100", "hit300"),
            "spinner" => existing(
                components,
                "spinner-background", "spinner-circle",
                "spinner-approachcircle", "spinner-metre"),
            "number fonts" => existing(components, "default-0"),
            _ => existing(components, preferred),
        };
        return new SkinStudioCategoryPreviewPlan(
            target,
            familyComponents.Length > 0 ? familyComponents : [preferred],
            calloutComponents);
    }

    private static IReadOnlyList<string> existing(
        IReadOnlyCollection<string> available,
        params string[] requested) => requested
        .Where(component => available.Contains(
            component,
            StringComparer.OrdinalIgnoreCase))
        .ToArray();
}

/// <summary>
/// Authoritative semantic routing for the native studio. File discovery remains
/// owned by <see cref="SkinExtraFamilyRegistry"/>; this class defines how every
/// recognised family is presented rather than duplicating filename matching.
/// </summary>
public static class SkinStudioSemanticPreviewCatalog
{
    public const int HitCircleNumberPreviewCount = 10;
    public static IReadOnlyList<int> HitCircleNumberPreviewValues { get; } =
        Enumerable.Range(1, HitCircleNumberPreviewCount).ToArray();
    public static SkinStudioSemanticPreviewDescriptor Resolve(
        string? componentName,
        string? familyId = null,
        int? maniaKeyCount = null)
    {
        var component = normalizeComponent(componentName);
        familyId = normalizeFamily(familyId, component);
        var family = SkinExtraFamilyRegistry.ById(familyId);
        var ruleset = rulesetFor(family?.Area, familyId);
        var kind = kindFor(familyId, component);
        var scene = sceneFor(kind);
        var animation = animationFor(kind);
        var compatibility = compatibilityFor(component, familyId);
        int? keys = ruleset == SkinStudioRuleset.Mania
            ? Math.Clamp(maniaKeyCount ?? 4, 1, 18)
            : null;
        return new SkinStudioSemanticPreviewDescriptor(
            $"{familyId}/{component}".ToLowerInvariant(),
            familyId,
            component,
            ruleset,
            kind,
            animation,
            compatibility,
            scene,
            keys);
    }

    public static SkinStudioSemanticPreviewDescriptor ResolveTarget(
        string? targetId,
        string? familyId,
        string? componentName,
        SkinStudioRuleset? requestedRuleset,
        int? maniaKeyCount)
    {
        if (!string.IsNullOrWhiteSpace(targetId))
        {
            var slash = targetId.IndexOf('/');
            if (slash > 0 && slash < targetId.Length - 1)
            {
                familyId ??= targetId[..slash];
                componentName ??= targetId[(slash + 1)..];
            }
        }
        var resolved = Resolve(componentName, familyId, maniaKeyCount);
        if (requestedRuleset is { } ruleset
            && ruleset != resolved.Ruleset
            && ruleset != SkinStudioRuleset.Unknown)
        {
            throw new InvalidDataException(
                $"Preview target '{resolved.Id}' belongs to {resolved.Ruleset}, not {ruleset}.");
        }
        return resolved;
    }

    public static SkinStudioSemanticPreviewDescriptor ForElement(
        SkinStudioElementDefinition element) =>
        Resolve(element.ComponentName, element.FamilyId, element.ManiaKeyCount);

    public static IReadOnlyList<SkinStudioSemanticPreviewDescriptor> FamilyDescriptors { get; } =
        SkinExtraFamilyRegistry.All
            .Where(family => !family.Id.Equals("misc.other", StringComparison.OrdinalIgnoreCase))
            .Select(family => Resolve(
                representativeComponent(family),
                family.Id))
            .ToArray();

    private static string representativeComponent(SkinExtraFamilyDefinition family) =>
        family.Id.ToLowerInvariant() switch
        {
            "osu.slider" => "slider",
            "osu.slider-colours" => "slider",
            "osu.combo-colours" => "hitcircle",
            "osu.number-font" => "default-0",
            "interface.playfield" => "play-skip",
            "interface.background" => "menu-background",
            "audio.nightcore" => "nightcore-kick",
            "audio.countdown" => "readys",
            "audio.hitsounds.taiko" => "taiko-normal-hitnormal",
            "audio.spinner" => "spinnerspin",
            "audio.gameplay" => "pause-loop",
            "audio.interface" => "menuhit",
            "audio.other" => "rank-up",
            _ => family.ExactNames.FirstOrDefault()
                 ?? family.Prefixes.FirstOrDefault()
                 ?? family.Id[(family.Id.IndexOf('.') + 1)..],
        };

    private static string normalizeComponent(string? componentName)
    {
        if (string.IsNullOrWhiteSpace(componentName))
            return "unknown";
        var value = Path.GetFileName(componentName.Trim());
        if (Path.HasExtension(value))
            value = Path.GetFileNameWithoutExtension(value);
        if (value.EndsWith("@2x", StringComparison.OrdinalIgnoreCase))
            value = value[..^3];
        return value;
    }

    private static string normalizeFamily(string? familyId, string component)
    {
        if (!string.IsNullOrWhiteSpace(familyId)
            && SkinExtraFamilyRegistry.ById(familyId.Trim()) is not null)
            return familyId.Trim();

        if (component.StartsWith("default-", StringComparison.OrdinalIgnoreCase)
            || component.StartsWith("score-", StringComparison.OrdinalIgnoreCase)
            || component.StartsWith("combo-", StringComparison.OrdinalIgnoreCase)
            || component.StartsWith("scoreentry-", StringComparison.OrdinalIgnoreCase))
            return "osu.number-font";
        if (component.StartsWith("slider", StringComparison.OrdinalIgnoreCase)
            || component.Equals("reversearrow", StringComparison.OrdinalIgnoreCase))
        {
            return component is "sliderendmiss" or "slidertickmiss"
                ? "osu.hitbursts"
                : "osu.slider";
        }
        if (component.Equals("play-skip", StringComparison.OrdinalIgnoreCase))
            return "interface.playfield";
        if (component.Equals("fountain-star", StringComparison.OrdinalIgnoreCase))
            return "interface.menu";
        if (component.StartsWith("applause", StringComparison.OrdinalIgnoreCase))
            return "audio.applause";
        if (component is "rank-up" or "rank-down")
            return "audio.other";
        var imageFilename = component + ".png";
        var audioFilename = component + ".wav";
        var imageFamily = SkinExtraFamilyRegistry.ForFile(imageFilename);
        var audioFamily = SkinExtraFamilyRegistry.ForFile(audioFilename);
        var imageScore = imageFamily?.MatchScore(imageFilename) ?? -1;
        var audioScore = audioFamily?.MatchScore(audioFilename) ?? -1;
        if (imageScore > audioScore)
            return imageFamily!.Id;
        if (isKnownAudioComponent(component) && audioFamily is not null)
            return audioFamily.Id;
        return imageFamily?.Id ?? audioFamily?.Id ?? "misc.other";
    }

    private static SkinStudioRuleset rulesetFor(string? area, string familyId) =>
        area?.ToLowerInvariant() switch
        {
            "osu!" => SkinStudioRuleset.Osu,
            "catch" => SkinStudioRuleset.Catch,
            "taiko" => SkinStudioRuleset.Taiko,
            "mania" => SkinStudioRuleset.Mania,
            "interface" or "metadata" => SkinStudioRuleset.Interface,
            "audio" => SkinStudioRuleset.Audio,
            _ when familyId.StartsWith("audio.", StringComparison.OrdinalIgnoreCase) =>
                SkinStudioRuleset.Audio,
            _ => SkinStudioRuleset.Unknown,
        };

    private static SkinStudioSemanticPreviewKind kindFor(
        string familyId,
        string component)
    {
        if (familyId.Equals("osu.number-font", StringComparison.OrdinalIgnoreCase))
        {
            if (component.StartsWith("default-", StringComparison.OrdinalIgnoreCase))
                return SkinStudioSemanticPreviewKind.HitCircleNumbers;
            if (component.StartsWith("scoreentry-", StringComparison.OrdinalIgnoreCase))
                return SkinStudioSemanticPreviewKind.LeaderboardNumbers;
            if (component.StartsWith("combo-", StringComparison.OrdinalIgnoreCase))
                return SkinStudioSemanticPreviewKind.ComboNumbers;
            return SkinStudioSemanticPreviewKind.ScoreNumbers;
        }

        return familyId.ToLowerInvariant() switch
        {
            "osu.hitcircles" or "osu.combo-colours" =>
                SkinStudioSemanticPreviewKind.HitCircles,
            "osu.followpoints" => SkinStudioSemanticPreviewKind.FollowPoints,
            "osu.slider" or "osu.slider-colours" => SkinStudioSemanticPreviewKind.Slider,
            "osu.cursor" or "osu.star-particles" => SkinStudioSemanticPreviewKind.Cursor,
            "osu.hitbursts" or "osu.result-judgements" or "osu.comboburst" =>
                SkinStudioSemanticPreviewKind.Judgements,
            "osu.spinner" => SkinStudioSemanticPreviewKind.Spinner,
            "interface.scorebar" or "interface.input-overlay" =>
                SkinStudioSemanticPreviewKind.Hud,
            "catch.fruits" or "catch.catcher" or "catch.comboburst" =>
                SkinStudioSemanticPreviewKind.Catch,
            "taiko.notes" or "taiko.rolls" or "taiko.playfield" or "taiko.pippidon" =>
                SkinStudioSemanticPreviewKind.Taiko,
            _ when familyId.StartsWith("mania.", StringComparison.OrdinalIgnoreCase) =>
                SkinStudioSemanticPreviewKind.Mania,
            "audio.hitsounds.normal" or "audio.hitsounds.soft" or
                "audio.hitsounds.drum" or "audio.hitsounds.taiko" =>
                component.Contains("slider", StringComparison.OrdinalIgnoreCase)
                    ? SkinStudioSemanticPreviewKind.SliderSoundLoop
                    : SkinStudioSemanticPreviewKind.HitSoundLoop,
            "audio.spinner" => SkinStudioSemanticPreviewKind.SpinnerSoundLoop,
            _ when familyId.StartsWith("audio.", StringComparison.OrdinalIgnoreCase) =>
                SkinStudioSemanticPreviewKind.AudioEvent,
            _ when familyId.StartsWith("interface.", StringComparison.OrdinalIgnoreCase)
                   || familyId.StartsWith("metadata.", StringComparison.OrdinalIgnoreCase) =>
                SkinStudioSemanticPreviewKind.Interface,
            _ => SkinStudioSemanticPreviewKind.RawAsset,
        };
    }

    private static SkinStudioPreviewScene sceneFor(SkinStudioSemanticPreviewKind kind) =>
        kind switch
        {
            SkinStudioSemanticPreviewKind.HitCircles or
                SkinStudioSemanticPreviewKind.HitCircleNumbers or
                SkinStudioSemanticPreviewKind.HitSoundLoop => SkinStudioPreviewScene.Circles,
            SkinStudioSemanticPreviewKind.FollowPoints => SkinStudioPreviewScene.Followpoints,
            SkinStudioSemanticPreviewKind.Slider or
                SkinStudioSemanticPreviewKind.SliderSoundLoop => SkinStudioPreviewScene.Sliders,
            SkinStudioSemanticPreviewKind.Cursor => SkinStudioPreviewScene.Cursor,
            SkinStudioSemanticPreviewKind.Judgements or
                SkinStudioSemanticPreviewKind.ScoreNumbers or
                SkinStudioSemanticPreviewKind.ComboNumbers or
                SkinStudioSemanticPreviewKind.LeaderboardNumbers => SkinStudioPreviewScene.Judgements,
            SkinStudioSemanticPreviewKind.Spinner or
                SkinStudioSemanticPreviewKind.SpinnerSoundLoop => SkinStudioPreviewScene.Spinner,
            SkinStudioSemanticPreviewKind.Hud or
                SkinStudioSemanticPreviewKind.Interface or
                SkinStudioSemanticPreviewKind.Catch or
                SkinStudioSemanticPreviewKind.Taiko or
                SkinStudioSemanticPreviewKind.Mania or
                SkinStudioSemanticPreviewKind.AudioEvent => SkinStudioPreviewScene.Hud,
            _ => SkinStudioPreviewScene.Showcase,
        };

    private static SkinStudioAnimationPolicy animationFor(
        SkinStudioSemanticPreviewKind kind) => kind switch
        {
            SkinStudioSemanticPreviewKind.Cursor or
                SkinStudioSemanticPreviewKind.Slider or
                SkinStudioSemanticPreviewKind.Spinner => SkinStudioAnimationPolicy.Native,
            SkinStudioSemanticPreviewKind.HitSoundLoop or
                SkinStudioSemanticPreviewKind.SliderSoundLoop or
                SkinStudioSemanticPreviewKind.SpinnerSoundLoop or
                SkinStudioSemanticPreviewKind.Catch or
                SkinStudioSemanticPreviewKind.Taiko or
                SkinStudioSemanticPreviewKind.Mania => SkinStudioAnimationPolicy.ScriptedLoop,
            SkinStudioSemanticPreviewKind.AudioEvent or
                SkinStudioSemanticPreviewKind.Judgements => SkinStudioAnimationPolicy.OneShotLoop,
            _ => SkinStudioAnimationPolicy.Static,
        };

    private static SkinExtraCompatibility compatibilityFor(
        string component,
        string familyId)
    {
        if (familyId.Equals("misc.other", StringComparison.OrdinalIgnoreCase))
            return SkinExtraCompatibility.Unknown;
        if (familyId.Equals("osu.number-font", StringComparison.OrdinalIgnoreCase))
            return SkinExtraCompatibility.LazerUsed;
        var extension = SkinExtraFamilyRegistry.ById(familyId)?.Area == "Audio"
            ? ".wav"
            : ".png";
        return SkinExtraLazerCompatibility.Classify(component + extension, familyId);
    }

    private static bool isKnownAudioComponent(string component) =>
        component.Contains("hitnormal", StringComparison.OrdinalIgnoreCase)
        || component.Contains("hitwhistle", StringComparison.OrdinalIgnoreCase)
        || component.Contains("hitfinish", StringComparison.OrdinalIgnoreCase)
        || component.Contains("hitclap", StringComparison.OrdinalIgnoreCase)
        || component.Contains("slider", StringComparison.OrdinalIgnoreCase)
        || component is "combobreak" or "failsound" or "pause-loop" or
            "spinnerspin" or "spinnerbonus" or "spinnerbonus-max" or
            "count1s" or "count2s" or "count3s" or "readys" or "gos" or
            "sectionpass" or "sectionfail" or "applause" or "seeya" or "welcome"
        || component.StartsWith("nightcore-", StringComparison.OrdinalIgnoreCase)
        || component.StartsWith("menu", StringComparison.OrdinalIgnoreCase)
        || component.StartsWith("key-", StringComparison.OrdinalIgnoreCase);
}

public sealed record SkinStudioSemanticAudioPlan(
    IReadOnlyList<string> Components,
    double IntervalMilliseconds)
{
    public bool LoopsContinuously => double.IsPositiveInfinity(IntervalMilliseconds);

    public static SkinStudioSemanticAudioPlan Build(
        SkinStudioSemanticPreviewDescriptor target)
    {
        if (!target.IsAudio)
            return new SkinStudioSemanticAudioPlan([], 0);
        return target.Kind switch
        {
            SkinStudioSemanticPreviewKind.HitSoundLoop =>
                target.FamilyId.Equals("audio.hitsounds.taiko", StringComparison.OrdinalIgnoreCase)
                    ? new SkinStudioSemanticAudioPlan(
                        [
                            "taiko-normal-hitnormal", "taiko-normal-hitclap",
                            "taiko-normal-hitfinish", "taiko-normal-hitwhistle",
                        ],
                        500)
                    : hitSoundBank(target.FamilyId.Split('.').Last()),
            SkinStudioSemanticPreviewKind.SliderSoundLoop =>
                new SkinStudioSemanticAudioPlan(
                    [target.ComponentName],
                    target.ComponentName.Contains("slidertick", StringComparison.OrdinalIgnoreCase)
                        ? 500
                        : double.PositiveInfinity),
            SkinStudioSemanticPreviewKind.SpinnerSoundLoop =>
                new SkinStudioSemanticAudioPlan(
                    [target.ComponentName],
                    target.ComponentName.Equals("spinnerspin", StringComparison.OrdinalIgnoreCase)
                        ? double.PositiveInfinity
                        : 1_000),
            SkinStudioSemanticPreviewKind.AudioEvent => audioEvent(target),
            _ => new SkinStudioSemanticAudioPlan([], 0),
        };
    }

    public static IReadOnlyList<string> LayeredComponents(
        string component,
        bool layered)
    {
        var parts = component.Split('-', 2);
        if (layered
            && parts.Length == 2
            && parts[0] is "normal" or "soft" or "drum"
            && parts[1] is "hitwhistle" or "hitfinish" or "hitclap")
        {
            return [$"{parts[0]}-hitnormal", component];
        }
        return [component];
    }

    private static SkinStudioSemanticAudioPlan hitSoundBank(string bank) => new(
        [
            $"{bank}-hitnormal", $"{bank}-hitwhistle",
            $"{bank}-hitfinish", $"{bank}-hitclap",
        ],
        500);

    private static SkinStudioSemanticAudioPlan audioEvent(
        SkinStudioSemanticPreviewDescriptor target) =>
        target.FamilyId.ToLowerInvariant() switch
        {
            "audio.countdown" => new(
                ["readys", "count3s", "count2s", "count1s", "gos"],
                500),
            "audio.nightcore" => new(
                ["nightcore-kick", "nightcore-hat", "nightcore-clap", "nightcore-finish"],
                250),
            _ when target.ComponentName.Equals("pause-loop", StringComparison.OrdinalIgnoreCase) =>
                new([target.ComponentName], double.PositiveInfinity),
            _ => new([target.ComponentName], 2_500),
        };
}
