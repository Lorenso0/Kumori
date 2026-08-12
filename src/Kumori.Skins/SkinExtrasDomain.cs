using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
namespace Kumori.Skins;

public sealed record SkinExtraModeVisibility(
    bool ShowCatch = false,
    bool ShowTaiko = false,
    bool ShowMania = false,
    bool LazerUsedOnly = true)
{
    public bool AllowsArea(string area) => area.ToLowerInvariant() switch
    {
        "catch" => ShowCatch,
        "taiko" => ShowTaiko,
        "mania" => ShowMania,
        _ => true,
    };
}

public sealed record SkinExtraIniKey(string Section, string Key, int? ManiaKeys = null);

public sealed class SkinExtraFamilyDefinition
{
    internal SkinExtraFamilyDefinition(
        string id,
        string area,
        string name,
        string legacyCategory,
        string[] prefixes,
        string[] exactNames,
        SkinExtraIniKey[]? iniKeys = null)
    {
        Id = id;
        Area = area;
        Name = name;
        LegacyCategory = legacyCategory;
        Prefixes = prefixes;
        ExactNames = exactNames;
        IniKeys = iniKeys ?? [];
    }

    public string Id { get; }
    public string Area { get; }
    public string Name { get; }
    public string LegacyCategory { get; }
    public IReadOnlyList<SkinExtraIniKey> IniKeys { get; }
    internal IReadOnlyList<string> Prefixes { get; }
    public IReadOnlyList<string> ExactNames { get; }

    public bool Matches(string filename)
    {
        var stem = SkinExtraFamilyRegistry.NormalizedStem(filename);
        return ExactNames.Contains(stem, StringComparer.OrdinalIgnoreCase)
               || Prefixes.Any(prefix => stem.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    internal int MatchScore(string filename)
    {
        var stem = SkinExtraFamilyRegistry.NormalizedStem(filename);
        var exact = ExactNames.Where(name => stem.Equals(name, StringComparison.OrdinalIgnoreCase))
            .Select(name => 10_000 + name.Length)
            .DefaultIfEmpty(-1)
            .Max();
        var prefix = Prefixes.Where(value => stem.StartsWith(value, StringComparison.OrdinalIgnoreCase))
            .Select(value => value.Length)
            .DefaultIfEmpty(-1)
            .Max();
        return Math.Max(exact, prefix);
    }
}

/// <summary>
/// One authoritative inventory for extraction, browsing, preview grouping, and
/// replacement scope. Families are intentionally narrower than the old editor
/// categories so applying a scorebar cannot delete unrelated interface assets.
/// </summary>
public static class SkinExtraFamilyRegistry
{
    private static SkinExtraIniKey K(string section, string key) => new(section, key);

    public static readonly IReadOnlyList<SkinExtraFamilyDefinition> All =
    [
        F("osu.cursor", "osu!", "Cursor", "Cursor", [],
            ["cursor", "cursortrail", "cursormiddle", "cursor-ripple", "cursor-smoke"],
            ini: [K("General", "CursorCentre"), K("General", "CursorExpand"),
                K("General", "CursorRotate"), K("General", "CursorTrailRotate")]),
        F("osu.hitcircles", "osu!", "Hit circles", "Hitcircles",
            [], ["hitcircle", "hitcircleoverlay", "approachcircle"],
            ini: [K("General", "HitCircleOverlayAboveNumber")]),
        F("osu.followpoints", "osu!", "Followpoints", "Hitcircles",
            ["followpoint"]),
        F("osu.slider", "osu!", "Sliders", "Sliders",
            ["slider", "reversearrow"],
            ini: [K("General", "AllowSliderBallTint"), K("General", "SliderBallFlip")]),
        F("osu.slider-colours", "osu!", "Slider colours", "Sliders", [],
            ini: [K("Colours", "SliderBall"), K("Colours", "SliderBorder"),
                K("Colours", "SliderTrackOverride")]),
        F("osu.hitbursts", "osu!", "Gameplay judgements & particles", "Judgements",
            ["hit0", "hit50", "hit100", "hit300", "particle", "lighting"]),
        F("osu.result-judgements", "osu!", "Result judgements (stable)", "Judgements",
            [], ["hit50k", "hit100k", "hit300k", "hit300g"]),
        F("osu.star-particles", "osu!", "Cursor star particles", "Interface",
            [], ["star2"]),
        F("osu.spinner", "osu!", "Spinner", "Spinner", ["spinner"],
            ini: [K("General", "SpinnerFadePlayfield"), K("General", "SpinnerFrequencyModulate"),
                K("General", "SpinnerNoBlink"), K("Colours", "SpinnerBackground")]),
        F("osu.comboburst", "osu!", "Combo bursts", "Interface", ["comboburst"],
            ini: [K("General", "ComboBurstRandom"), K("General", "CustomComboBurstSounds")]),
        F("osu.combo-colours", "osu!", "Combo colours", "Hitcircles", [],
            ini: Enumerable.Range(1, 8).Select(index => K("Colours", $"Combo{index}")).ToArray()),
        F("osu.number-font", "osu!", "Number fonts", "Numbers", []),

        F("interface.scorebar", "Interface", "Scorebar", "Scorebar", ["scorebar"]),
        F("interface.input-overlay", "Interface", "Input overlay", "Interface", ["inputoverlay"],
            ini: [K("Colours", "InputOverlayText")]),
        F("interface.countdown", "Interface", "Countdown", "Interface",
            ["count"], ["ready", "go"]),
        F("interface.playfield", "Interface", "Playfield", "Interface",
            ["playfield", "masking-"]),
        F("interface.pause", "Interface", "Pause & fail", "Interface",
            ["pause", "fail", "continue"]),
        F("interface.background", "Interface", "Background", "Interface",
            [], ["menu-background"]),
        F("interface.menu", "Interface", "Menus & buttons", "Interface",
            ["menu", "button", "selection-", "mode-", "arrow"]),
        F("interface.mod-icons", "Interface", "Mod icons", "Interface", ["selection-mod-"]),
        F("interface.song-select", "Interface", "Song select", "Interface",
            ["songselect", "section-", "multi-"], ["star"],
            ini: [K("Colours", "SongSelectActiveText"), K("Colours", "SongSelectInactiveText"),
                K("Colours", "StarBreakAdditive")]),
        F("interface.ranking", "Interface", "Ranking & results", "Interface",
            ["ranking", "replay"]),
        F("interface.leaderboard", "Interface", "Leaderboard rows & score digits", "Interface",
            ["menu-button-background", "scoreentry"]),
        F("metadata.previews", "Metadata", "Skin previews", "Skin Previews",
            ["preview", "banner", "thumbnail"]),

        F("catch.fruits", "Catch", "Fruits", "Catch", ["fruit", "drop", "droplet", "banana"]),
        F("catch.catcher", "Catch", "Catcher", "Catch", ["fruit-catcher", "catcher"],
            ["fruit-ryuuta"],
            ini: [K("CatchTheBeat", "HyperDash"), K("CatchTheBeat", "HyperDashFruit"),
                K("CatchTheBeat", "HyperDashAfterImage")]),
        F("catch.comboburst", "Catch", "Combo bursts", "Catch", [], ["comboburst-fruits"]),

        F("taiko.notes", "Taiko", "Notes & hit bursts", "Taiko",
            ["taikohit", "taiko-hit", "taiko-note", "taikobigcircle"]),
        F("taiko.rolls", "Taiko", "Drumrolls & shaker", "Taiko",
            ["taiko-roll", "taiko-slider", "taiko-shaker"]),
        F("taiko.playfield", "Taiko", "Playfield", "Taiko",
            ["taiko-bar", "taiko-background", "taiko-flower", "taiko-glow", "taiko-drum"]),
        F("taiko.pippidon", "Taiko", "Pippidon", "Taiko", ["pippidon"]),

        F("mania.stage", "Mania", "Stage & layout", "Mania",
            ["mania-stage", "stage-", "mania-warning"], mania: true),
        F("mania.keys", "Mania", "Keys", "Mania", ["mania-key", "key"], mania: true),
        F("mania.notes", "Mania", "Notes", "Mania", ["mania-note", "note"], mania: true),
        F("mania.holds", "Mania", "Hold notes", "Mania",
            ["mania-hold", "hold"], mania: true),
        F("mania.lighting", "Mania", "Lighting", "Mania",
            ["mania-light", "lightingn", "lightingl"], mania: true),
        F("mania.hitbursts", "Mania", "Hit bursts", "Mania",
            ["mania-hit", "mania-judgement"], mania: true),
        F("mania.comboburst", "Mania", "Combo bursts", "Mania", [], ["comboburst-mania"],
            mania: true),

        F("audio.hitsounds.normal", "Audio", "Hitsounds — Normal", "Sounds",
            ["normal-hit", "normal-slidertick", "normal-slider"],
            ini: [K("General", "LayeredHitSounds")]),
        F("audio.hitsounds.soft", "Audio", "Hitsounds — Soft", "Sounds",
            ["soft-hit", "soft-slidertick", "soft-slider"],
            ini: [K("General", "LayeredHitSounds")]),
        F("audio.hitsounds.drum", "Audio", "Hitsounds — Drum", "Sounds",
            ["drum-hit", "drum-slidertick", "drum-slider"],
            ini: [K("General", "LayeredHitSounds")]),
        F("audio.hitsounds.taiko", "Audio", "Hitsounds — Taiko", "Sounds",
            ["taiko-normal", "taiko-soft", "taiko-drum"],
            ini: [K("General", "LayeredHitSounds")]),
        F("audio.combobreak", "Audio", "Combobreak", "Sounds", ["combobreak"]),
        F("audio.spinner", "Audio", "Spinner sounds", "Sounds", ["spinnerspin", "spinnerbonus"]),
        F("audio.nightcore", "Audio", "Nightcore", "Sounds", ["nightcore-"]),
        F("audio.countdown", "Audio", "Countdown sounds", "Sounds",
            ["count", "ready", "go"]),
        F("audio.applause", "Audio", "Applause", "Sounds", ["applause"]),
        F("audio.failsound", "Audio", "Failsound", "Sounds", ["failsound"]),
        F("audio.seeya", "Audio", "Seeya", "Sounds", ["seeya"]),
        F("audio.welcome", "Audio", "Welcome", "Sounds", ["welcome"]),
        F("audio.sectionpass", "Audio", "Section pass", "Sounds", ["sectionpass"]),
        F("audio.sectionfail", "Audio", "Section fail", "Sounds", ["sectionfail"]),
        F("audio.gameplay", "Audio", "Gameplay sounds", "Sounds", ["pause-loop"]),
        F("audio.interface", "Audio", "Interface sounds", "Sounds",
            ["menu", "select", "click", "check", "back-button", "key-", "match-",
                "beatmap-", "shutter"]),
        F("audio.other", "Audio", "Other sounds", "Sounds", []),
        F("misc.other", "Other", "Unclassified assets", "Other", []),
    ];

    public static SkinExtraFamilyDefinition? ById(string id) =>
        All.FirstOrDefault(family => family.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public static SkinExtraFamilyDefinition? ForFile(string filename)
    {
        var isAudio = SkinMediaTypes.IsAudio(filename);
        var families = isAudio
            ? All.Where(family => family.Area == "Audio")
            : All.Where(family => family.Area != "Audio");
        return families.Select(family => (Family: family, Score: family.MatchScore(filename)))
            .Where(item => item.Score >= 0)
            .OrderByDescending(item => item.Score)
            .Select(item => item.Family)
            .FirstOrDefault();
    }

    public static IReadOnlyList<SkinExtraFamilyDefinition> ForLegacyCategory(string category) =>
        All.Where(family => family.LegacyCategory.Equals(category, StringComparison.OrdinalIgnoreCase))
            .ToArray();

    public static string NormalizedStem(string filename)
    {
        var stem = Path.GetFileNameWithoutExtension(filename).ToLowerInvariant();
        if (stem.EndsWith("@2x", StringComparison.Ordinal))
            stem = stem[..^3];
        var dash = stem.LastIndexOf('-');
        if (dash > 0
            && stem[(dash + 1)..] is { Length: > 0 } suffix
            && suffix.All(character => character is >= '0' and <= '9'))
            stem = stem[..dash];
        return stem;
    }

    private static SkinExtraFamilyDefinition F(
        string id,
        string area,
        string name,
        string legacyCategory,
        string[] prefixes,
        string[]? exact = null,
        SkinExtraIniKey[]? ini = null,
        bool mania = false)
    {
        if (mania)
        {
            var common = new[]
            {
                "Keys", "ColumnStart", "ColumnWidth", "ColumnLineWidth", "BarlineHeight",
                "HitPosition", "LightPosition", "ScorePosition", "ComboPosition",
                "JudgementLine", "SpecialStyle", "UpsideDown", "SplitStages",
            };
            ini = (ini ?? []).Concat(common.Select(key => new SkinExtraIniKey("Mania", key)))
                .ToArray();
        }
        return new SkinExtraFamilyDefinition(
            id, area, name, legacyCategory, prefixes, exact ?? [], ini);
    }
}

public sealed record SkinExtraManifestFile(
    string SourceFilename,
    string TargetFilename,
    string LogicalSlot,
    string ByteHash,
    string SemanticHash,
    string? SimilarityHash = null);

public sealed class SkinExtraPackManifest
{
    public const int CurrentSchemaVersion = 2;
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string FamilyId { get; init; }
    public required string Area { get; init; }
    public required string FamilyName { get; init; }
    public string? Variant { get; init; }
    public string? SourceSkin { get; init; }
    public string? SourceAuthor { get; init; }
    public required string Fingerprint { get; init; }
    public DateTimeOffset ExtractedAt { get; init; } = DateTimeOffset.UtcNow;
    public List<SkinExtraManifestFile> Files { get; init; } = [];
    public List<SkinExtraIniPatchEntry> IniPatch { get; init; } = [];
    public List<string> FontRoles { get; init; } = [];
}

public enum SkinExtraCompatibility
{
    LazerUsed,
    StableOnly,
    Unknown,
}

/// <summary>
/// Asset-level compatibility catalog audited against osu! 2026.702.0. This is
/// intentionally stricter than the legacy skin importer: an asset is marked as
/// used only when the pinned lazer source actually requests it from the skin.
/// </summary>
public static class SkinExtraLazerCompatibility
{
    public const string AuditedOsuVersion = "2026.702.0";
    public const string AuditedCommit = "b7774fe8d16a96690bef65b4f9562e3df393d5e4";

    private static readonly HashSet<string> fullyUsedFamilies = new(
    [
        "osu.cursor", "osu.hitcircles", "osu.followpoints", "osu.slider",
        "osu.slider-colours",
        "osu.hitbursts", "osu.number-font", "interface.scorebar",
        "interface.input-overlay", "catch.fruits", "catch.catcher",
        "taiko.notes", "taiko.rolls", "taiko.playfield", "taiko.pippidon",
        "mania.stage", "mania.keys", "mania.notes", "mania.holds",
        "mania.lighting", "mania.hitbursts", "audio.hitsounds.normal",
        "audio.hitsounds.soft", "audio.hitsounds.drum", "audio.hitsounds.taiko",
        "audio.combobreak", "audio.spinner", "audio.nightcore",
        "audio.applause", "audio.failsound", "audio.seeya", "audio.welcome",
    ], StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> fullyStableOnlyFamilies = new(
    [
        "osu.comboburst", "interface.countdown", "interface.playfield",
        "interface.pause", "interface.mod-icons", "metadata.previews",
        "osu.result-judgements",
        "catch.comboburst", "mania.comboburst", "audio.countdown",
        "audio.sectionpass", "audio.sectionfail", "audio.interface",
    ], StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> usedGameplaySounds = new(
        ["pause-loop"],
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> usedOtherSounds = new(
    [
        "rank-up", "rank-down", "fountain-shoot", "fountain-loop",
        "restart", "pause-retry-click",
        "catch-banana", "spinner-osu",
        "hitnormal", "hitwhistle", "hitfinish", "hitclap",
        "slidertick", "sliderslide", "sliderwhistle",
        "score-tick", "badge-dink", "badge-dink-max", "swoosh-up",
    ], StringComparer.OrdinalIgnoreCase);

    public static SkinExtraCompatibility Classify(
        string filename,
        string? familyId = null)
    {
        if (string.IsNullOrWhiteSpace(filename))
            return SkinExtraCompatibility.Unknown;

        var stem = SkinExtraFamilyRegistry.NormalizedStem(filename);
        familyId ??= SkinExtraFamilyRegistry.ForFile(filename)?.Id;

        if (stem.StartsWith("selection-mod-", StringComparison.OrdinalIgnoreCase))
            return SkinExtraCompatibility.StableOnly;

        if (familyId is null)
            return SkinExtraCompatibility.Unknown;

        if (fullyStableOnlyFamilies.Contains(familyId))
            return SkinExtraFamilyRegistry.ById(familyId)?.Matches(filename) == true
                ? SkinExtraCompatibility.StableOnly
                : SkinExtraCompatibility.Unknown;

        if (fullyUsedFamilies.Contains(familyId))
        {
            if (familyId.Equals("osu.number-font", StringComparison.OrdinalIgnoreCase))
                return SkinExtraCompatibility.LazerUsed;
            if (familyId.StartsWith("mania.", StringComparison.OrdinalIgnoreCase))
                return SkinExtraCompatibility.LazerUsed;
            return SkinExtraFamilyRegistry.ById(familyId)?.Matches(filename) == true
                ? SkinExtraCompatibility.LazerUsed
                : SkinExtraCompatibility.Unknown;
        }

        return familyId.ToLowerInvariant() switch
        {
            "osu.spinner" => stem.StartsWith("spinner", StringComparison.OrdinalIgnoreCase)
                ? SkinExtraCompatibility.LazerUsed
                : SkinExtraCompatibility.Unknown,
            "interface.background" => stem.Equals("menu-background", StringComparison.OrdinalIgnoreCase)
                ? SkinExtraCompatibility.LazerUsed
                : SkinExtraCompatibility.Unknown,
            "interface.menu" => stem.Equals("menu-background", StringComparison.OrdinalIgnoreCase)
                ? SkinExtraCompatibility.LazerUsed
                : KnownStableSibling(filename, familyId),
            "interface.song-select" => KnownStableSibling(filename, familyId),
            "osu.star-particles" => stem.Equals("star2", StringComparison.OrdinalIgnoreCase)
                ? SkinExtraCompatibility.LazerUsed
                : KnownStableSibling(filename, familyId),
            "interface.ranking" => stem.StartsWith("ranking-", StringComparison.OrdinalIgnoreCase)
                                   && stem.EndsWith("-small", StringComparison.OrdinalIgnoreCase)
                ? SkinExtraCompatibility.LazerUsed
                : KnownStableSibling(filename, familyId),
            "interface.leaderboard" => stem.StartsWith("scoreentry", StringComparison.OrdinalIgnoreCase)
                ? SkinExtraCompatibility.LazerUsed
                : KnownStableSibling(filename, familyId),
            "audio.gameplay" => usedGameplaySounds.Contains(stem)
                                || stem.StartsWith("applause-", StringComparison.OrdinalIgnoreCase)
                ? SkinExtraCompatibility.LazerUsed
                : KnownStableSibling(filename, familyId),
            "audio.other" => usedOtherSounds.Contains(stem)
                             || stem.StartsWith("rank-impact-", StringComparison.OrdinalIgnoreCase)
                ? SkinExtraCompatibility.LazerUsed
                : SkinExtraCompatibility.Unknown,
            "misc.other" => SkinExtraCompatibility.Unknown,
            _ => SkinExtraCompatibility.Unknown,
        };
    }

    private static SkinExtraCompatibility KnownStableSibling(
        string filename,
        string familyId) =>
        SkinExtraFamilyRegistry.ById(familyId)?.Matches(filename) == true
            ? SkinExtraCompatibility.StableOnly
            : SkinExtraCompatibility.Unknown;

    public static bool IsLazerUsed(string filename, string? familyId = null) =>
        Classify(filename, familyId) == SkinExtraCompatibility.LazerUsed;

    public static bool IsIniPatchUsed(
        string familyId,
        SkinExtraIniPatchEntry entry)
    {
        if (familyId.Equals("osu.comboburst", StringComparison.OrdinalIgnoreCase))
            return false;
        if (familyId.Equals("osu.spinner", StringComparison.OrdinalIgnoreCase)
            && entry.Key.Equals("SpinnerFadePlayfield", StringComparison.OrdinalIgnoreCase))
            return false;
        if (familyId.Equals("osu.slider", StringComparison.OrdinalIgnoreCase)
            && entry.Key.Equals("SliderBallFlip", StringComparison.OrdinalIgnoreCase))
            return false;
        if (familyId.Equals("interface.menu", StringComparison.OrdinalIgnoreCase))
            return false;
        if (familyId.Equals("interface.song-select", StringComparison.OrdinalIgnoreCase))
            return entry.Key.Equals("StarBreakAdditive", StringComparison.OrdinalIgnoreCase);
        if (fullyStableOnlyFamilies.Contains(familyId)
            || familyId.Equals("interface.ranking", StringComparison.OrdinalIgnoreCase)
            || familyId.Equals("interface.leaderboard", StringComparison.OrdinalIgnoreCase)
            || familyId.Equals("audio.gameplay", StringComparison.OrdinalIgnoreCase)
            || familyId.Equals("audio.other", StringComparison.OrdinalIgnoreCase)
            || familyId.Equals("misc.other", StringComparison.OrdinalIgnoreCase))
            return false;

        return fullyUsedFamilies.Contains(familyId)
               || familyId.Equals("osu.spinner", StringComparison.OrdinalIgnoreCase)
               || familyId.Equals("osu.combo-colours", StringComparison.OrdinalIgnoreCase);
    }

    public static bool FamilyCanContainLazerUsedContent(string familyId) =>
        !fullyStableOnlyFamilies.Contains(familyId)
        && !familyId.Equals("misc.other", StringComparison.OrdinalIgnoreCase);

    public static SkinExtraPackManifest FilterManifest(SkinExtraPackManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return new SkinExtraPackManifest
        {
            SchemaVersion = manifest.SchemaVersion,
            Id = manifest.Id,
            DisplayName = manifest.DisplayName,
            FamilyId = manifest.FamilyId,
            Area = manifest.Area,
            FamilyName = manifest.FamilyName,
            Variant = manifest.Variant,
            SourceSkin = manifest.SourceSkin,
            SourceAuthor = manifest.SourceAuthor,
            Fingerprint = manifest.Fingerprint,
            ExtractedAt = manifest.ExtractedAt,
            Files = manifest.Files
                .Where(file => IsLazerUsed(file.TargetFilename, manifest.FamilyId))
                .ToList(),
            IniPatch = manifest.IniPatch
                .Where(entry => IsIniPatchUsed(manifest.FamilyId, entry))
                .ToList(),
            FontRoles = [.. manifest.FontRoles],
        };
    }

    public static bool HasLazerUsedContent(SkinExtraPackManifest manifest)
    {
        var filtered = FilterManifest(manifest);
        return filtered.Files.Count > 0 || filtered.IniPatch.Count > 0;
    }

    public static string CompatibilityBadge(SkinExtraPackManifest manifest)
    {
        var results = manifest.Files
            .Select(file => Classify(file.TargetFilename, manifest.FamilyId))
            .Concat(manifest.IniPatch.Select(entry => IsIniPatchUsed(manifest.FamilyId, entry)
                ? SkinExtraCompatibility.LazerUsed
                : SkinExtraCompatibility.StableOnly))
            .Distinct()
            .ToArray();
        if (results.Length == 0) return "Unverified";
        if (results.All(value => value == SkinExtraCompatibility.StableOnly)) return "Stable only";
        if (results.All(value => value == SkinExtraCompatibility.Unknown)) return "Unverified";
        if (results.All(value => value == SkinExtraCompatibility.LazerUsed)) return "";
        return "Mixed compatibility";
    }
}

public static class SkinExtraManifestSerializer
{
    private static readonly JsonSerializerOptions options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static byte[] Serialize(SkinExtraPackManifest manifest) =>
        JsonSerializer.SerializeToUtf8Bytes(manifest, options);

    public static SkinExtraPackManifest? TryRead(string packDirectory)
    {
        var path = Path.Combine(packDirectory, "extras.json");
        if (!File.Exists(path)) return null;
        try
        {
            return Deserialize(File.ReadAllBytes(path));
        }
        catch
        {
            return null;
        }
    }

    internal static SkinExtraPackManifest? Deserialize(ReadOnlySpan<byte> bytes) =>
        JsonSerializer.Deserialize<SkinExtraPackManifest>(bytes, options);
}

public static class SkinExtraFingerprint
{
    public static bool IniValuesEqual(string? left, string? right) =>
        string.Equals(
            NormalizeIniValue(left),
            NormalizeIniValue(right),
            StringComparison.OrdinalIgnoreCase);

    public static bool EquivalentPackContent(
        IEnumerable<SkinExtraManifestFile> leftFiles,
        IEnumerable<SkinExtraIniPatchEntry> leftPatch,
        IEnumerable<SkinExtraManifestFile> rightFiles,
        IEnumerable<SkinExtraIniPatchEntry> rightPatch)
    {
        var leftFileArray = leftFiles.ToArray();
        var rightFileArray = rightFiles.ToArray();
        var leftPatchArray = leftPatch.ToArray();
        var rightPatchArray = rightPatch.ToArray();
        if (leftFileArray.Length != rightFileArray.Length
            || leftPatchArray.Length != rightPatchArray.Length)
            return false;

        var unmatchedFiles = rightFileArray.ToList();
        foreach (var file in leftFileArray)
        {
            var match = unmatchedFiles.FindIndex(candidate =>
                EquivalentTargetFilename(candidate.TargetFilename, file.TargetFilename)
                && EquivalentFileContent(candidate, file));
            if (match < 0) return false;
            unmatchedFiles.RemoveAt(match);
        }

        var unmatchedPatch = rightPatchArray.ToList();
        foreach (var entry in leftPatchArray)
        {
            var match = unmatchedPatch.FindIndex(candidate =>
                candidate.Section.Equals(entry.Section, StringComparison.OrdinalIgnoreCase)
                && candidate.Key.Equals(entry.Key, StringComparison.OrdinalIgnoreCase)
                && candidate.ManiaKeys == entry.ManiaKeys
                && IniValuesEqual(candidate.Value, entry.Value));
            if (match < 0) return false;
            unmatchedPatch.RemoveAt(match);
        }
        return true;
    }

    public static bool EquivalentFileContent(
        SkinExtraManifestFile left,
        SkinExtraManifestFile right) =>
        left.SemanticHash.Equals(right.SemanticHash, StringComparison.OrdinalIgnoreCase)
        || (!string.IsNullOrWhiteSpace(left.ByteHash)
            && !string.IsNullOrWhiteSpace(right.ByteHash)
            && left.ByteHash.Equals(right.ByteHash, StringComparison.OrdinalIgnoreCase))
        || (SkinMediaTypes.IsAudio(left.TargetFilename)
            && SkinMediaTypes.IsAudio(right.TargetFilename)
            && SkinAudioCanonicalizer.AreSimilar(
                left.SimilarityHash,
                right.SimilarityHash));

    public static bool EquivalentTargetFilename(string left, string right)
    {
        var sameMediaKind =
            SkinMediaTypes.IsAudio(left) && SkinMediaTypes.IsAudio(right)
            || SkinMediaTypes.IsImage(left) && SkinMediaTypes.IsImage(right);
        if (sameMediaKind)
            return Path.GetFileNameWithoutExtension(left).Equals(
                Path.GetFileNameWithoutExtension(right),
                StringComparison.OrdinalIgnoreCase);
        return left.Equals(right, StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizeIniValue(string? value)
    {
        if (value is null)
            return null;
        var trimmed = value.Trim();
        var components = trimmed.Split(',');
        return components.Length > 1
            ? string.Join(',', components.Select(component => component.Trim()))
            : trimmed;
    }

    public static SkinExtraManifestFile Describe(string sourceFilename, string targetFilename, byte[] bytes)
    {
        var byteHash = Hex(SHA256.HashData(bytes));
        var semanticHash = byteHash;
        string? similarityHash = null;
        if (SkinMediaTypes.IsImage(targetFilename))
        {
            try
            {
                var image = SkinImageAnalysis.Decode(bytes);
                if (!image.HasVisiblePixels)
                {
                    semanticHash = Hex(SHA256.HashData(
                        Encoding.UTF8.GetBytes("kumori:fully-transparent-image")));
                    similarityHash = "transparent";
                }
                else
                {
                    semanticHash = image.SemanticHash();
                    similarityHash = image.AverageHash;
                }
            }
            catch
            {
                // Corrupt/unsupported images remain byte-identifiable and can
                // still be kept in an Extras pack for manual inspection.
            }
        }
        else if (SkinMediaTypes.IsAudio(targetFilename)
                 && SkinAudioCanonicalizer.TryHash(
                     bytes,
                     byteHash,
                     out var decodedHash,
                     out var audioSimilarityHash))
        {
            semanticHash = decodedHash;
            similarityHash = audioSimilarityHash;
        }
        else if (Path.GetExtension(targetFilename).Equals(".wav", StringComparison.OrdinalIgnoreCase)
                 && TryGetWaveSemanticBytes(bytes, out var waveData))
        {
            semanticHash = Hex(SHA256.HashData(waveData));
        }

        return new SkinExtraManifestFile(
            sourceFilename.Replace('\\', '/'),
            targetFilename.Replace('\\', '/'),
            LogicalSlot(targetFilename),
            byteHash,
            semanticHash,
            similarityHash);
    }

    public static string ForPack(
        string familyId,
        IEnumerable<SkinExtraManifestFile> files,
        IEnumerable<SkinExtraIniPatchEntry>? iniPatch = null)
    {
        var canonical = new StringBuilder().Append(familyId.ToLowerInvariant()).Append('\n');
        foreach (var file in files.OrderBy(file => file.LogicalSlot, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(file => file.TargetFilename, StringComparer.OrdinalIgnoreCase))
            canonical.Append(file.LogicalSlot.ToLowerInvariant()).Append('|')
                .Append(file.SemanticHash).Append('\n');
        foreach (var entry in (iniPatch ?? [])
                     .OrderBy(entry => entry.Section, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(entry => entry.ManiaKeys)
                     .ThenBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase))
            canonical.Append(entry.Section.ToLowerInvariant()).Append('|')
                .Append(entry.ManiaKeys?.ToString() ?? "").Append('|')
                .Append(entry.Key.ToLowerInvariant()).Append('|')
                .Append(entry.Value?.Trim() ?? "<remove>").Append('\n');
        return Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    public static string LogicalSlot(string filename)
    {
        var extension = Path.GetExtension(filename).ToLowerInvariant();
        return SkinExtraFamilyRegistry.NormalizedStem(filename)
               + (filename.Contains("@2x", StringComparison.OrdinalIgnoreCase) ? "@2x" : "")
               + extension;
    }

    private static bool TryGetWaveSemanticBytes(byte[] bytes, out byte[] data)
    {
        data = [];
        if (bytes.Length < 12
            || !bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8)
            || !bytes.AsSpan(8, 4).SequenceEqual("WAVE"u8))
            return false;
        var position = 12;
        ReadOnlySpan<byte> format = default;
        ReadOnlySpan<byte> samples = default;
        while (position + 8 <= bytes.Length)
        {
            var size = BitConverter.ToInt32(bytes, position + 4);
            if (size < 0 || position + 8L + size > bytes.Length) return false;
            if (bytes.AsSpan(position, 4).SequenceEqual("fmt "u8))
                format = bytes.AsSpan(position + 8, size);
            else if (bytes.AsSpan(position, 4).SequenceEqual("data"u8))
                samples = bytes.AsSpan(position + 8, size);
            position += 8 + size + (size & 1);
        }
        if (format.IsEmpty || samples.IsEmpty) return false;
        data = new byte[format.Length + samples.Length];
        format.CopyTo(data);
        samples.CopyTo(data.AsSpan(format.Length));
        return true;
    }

    private static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);
}

public static class SkinExtraNaming
{
    private static readonly HashSet<string> reserved = new(
        ["CON", "PRN", "AUX", "NUL", .. Enumerable.Range(1, 9).Select(i => $"COM{i}"),
            .. Enumerable.Range(1, 9).Select(i => $"LPT{i}")],
        StringComparer.OrdinalIgnoreCase);

    public static string PackName(string? skinName, string? author)
    {
        var name = string.IsNullOrWhiteSpace(skinName) ? "Unnamed skin" : skinName.Trim();
        var creator = string.IsNullOrWhiteSpace(author) ? null : author.Trim();
        return Sanitize(creator is null ? name : $"{name} — {creator}");
    }

    /// <summary>
    /// Gives colour-only packs a useful, stable identity. Unlike image packs,
    /// their appearance lives entirely in skin.ini, so a short human colour
    /// description is more informative than a generic source folder name.
    /// </summary>
    public static string PackNameForFamily(
        string baseName,
        string familyId,
        IEnumerable<SkinExtraIniPatchEntry> patch)
    {
        if (!UsesColourOnlyName(familyId))
            return Sanitize(baseName);

        var colours = patch
            .Where(entry => entry.Section.Equals("Colours", StringComparison.OrdinalIgnoreCase)
                            && (familyId.Equals("osu.combo-colours", StringComparison.OrdinalIgnoreCase)
                                ? entry.Key.StartsWith("Combo", StringComparison.OrdinalIgnoreCase)
                                : entry.Key is "SliderBall" or "SliderBorder" or "SliderTrackOverride"))
            .Select(entry => TryParseRgb(entry.Value, out var rgb) ? (int[]?)rgb : null)
            .Where(rgb => rgb is not null)
            .Select(rgb => rgb!)
            .ToArray();
        if (colours.Length == 0)
            return "Unspecified colour";

        var names = colours.Select(ColourName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var signature = string.Join(" + ", names.Take(3));
        if (names.Length > 3)
            signature += " + more";
        return Sanitize(signature);
    }

    public static bool UsesColourOnlyName(string familyId) =>
        familyId.Equals("osu.combo-colours", StringComparison.OrdinalIgnoreCase)
        || familyId.Equals("osu.slider-colours", StringComparison.OrdinalIgnoreCase);

    public static string DisplayNameForPack(SkinExtraPackManifest manifest) =>
        UsesColourOnlyName(manifest.FamilyId)
            ? PackNameForFamily(
                manifest.DisplayName,
                manifest.FamilyId,
                manifest.IniPatch)
            : manifest.DisplayName;

    private static bool TryParseRgb(string? value, out int[] rgb)
    {
        rgb = [];
        var parts = value?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts is not { Length: 3 }
            || !int.TryParse(parts[0], out var red)
            || !int.TryParse(parts[1], out var green)
            || !int.TryParse(parts[2], out var blue)
            || red is < 0 or > 255
            || green is < 0 or > 255
            || blue is < 0 or > 255)
            return false;
        rgb = [red, green, blue];
        return true;
    }

    private static string ColourName(IReadOnlyList<int> rgb)
    {
        var red = rgb[0] / 255d;
        var green = rgb[1] / 255d;
        var blue = rgb[2] / 255d;
        var max = Math.Max(red, Math.Max(green, blue));
        var min = Math.Min(red, Math.Min(green, blue));
        var chroma = max - min;
        if (chroma < 0.10)
        {
            if (max < 0.18) return "Black";
            if (max > 0.88) return "White";
            return "Grey";
        }

        var hue = max == red
            ? 60 * ((green - blue) / chroma % 6)
            : max == green
                ? 60 * ((blue - red) / chroma + 2)
                : 60 * ((red - green) / chroma + 4);
        if (hue < 0) hue += 360;
        if (hue < 15 || hue >= 345) return "Red";
        if (hue < 45) return max < 0.62 ? "Brown" : "Orange";
        if (hue < 70) return "Yellow";
        if (hue < 100) return "Lime";
        if (hue < 160) return "Green";
        if (hue < 190) return "Teal";
        if (hue < 210) return "Cyan";
        if (hue < 255) return "Blue";
        if (hue < 290) return "Purple";
        if (hue < 315) return "Magenta";
        return "Pink";
    }

    public static string StorageParent(
        string extrasRoot,
        string area,
        string familyName)
    {
        var areaRoot = area.Equals("osu!", StringComparison.OrdinalIgnoreCase)
            ? extrasRoot
            : Path.Combine(extrasRoot, Sanitize(area));
        return Path.Combine(areaRoot, Sanitize(familyName));
    }

    public static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var cleaned = new string(value.Select(character =>
            invalid.Contains(character) || char.IsControl(character) ? '_' : character).ToArray())
            .Trim().TrimEnd('.', ' ');
        while (cleaned.Contains("  ", StringComparison.Ordinal))
            cleaned = cleaned.Replace("  ", " ", StringComparison.Ordinal);
        if (cleaned.Length == 0) cleaned = "Unnamed";
        if (reserved.Contains(cleaned)) cleaned = "_" + cleaned;
        return cleaned.Length <= 96 ? cleaned : cleaned[..96].TrimEnd('.', ' ');
    }
}

public sealed record SkinExtraPackDescriptor(
    string DirectoryPath,
    SkinExtraPackManifest Manifest,
    bool IsLegacy);

public static class SkinExtrasMutationGate
{
    // Extraction and portable-package imports both mutate the same index,
    // object store, and pack tree. Serialising those transactions prevents
    // two bulk operations from committing the same content simultaneously.
    public static object SyncRoot { get; } = new();
}

public static class SkinExtraPackIndex
{
    public static IReadOnlyList<SkinExtraPackDescriptor> Scan(string extrasRoot) =>
        SkinExtrasPersistentIndex.Scan(extrasRoot);

    internal static SkinExtraPackDescriptor? TryBuildDescriptor(
        string extrasRoot,
        string directory)
    {
        var manifest = SkinExtraManifestSerializer.TryRead(directory);
        if (manifest is not null)
            return new SkinExtraPackDescriptor(directory, manifest, false);

        if (Directory.EnumerateDirectories(directory).Any()) return null;
        var files = Directory.EnumerateFiles(directory)
            .Where(IsAsset)
            .ToArray();
        if (files.Length == 0) return null;
        var relative = Path.GetRelativePath(extrasRoot, directory);
        var category = relative.Split(Path.DirectorySeparatorChar)[0];
        var family = files.Select(path => SkinExtraFamilyRegistry.ForFile(path))
            .Where(value => value is not null)
            .GroupBy(value => value!.Id, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .Select(group => group.First())
            .FirstOrDefault(value => value!.LegacyCategory.Equals(
                category,
                StringComparison.OrdinalIgnoreCase))
            ?? files.Select(path => SkinExtraFamilyRegistry.ForFile(path))
                .FirstOrDefault(value => value is not null);
        family ??= category.Equals("Numbers", StringComparison.OrdinalIgnoreCase)
            ? SkinExtraFamilyRegistry.ById("osu.number-font")
            : category.Equals("Sounds", StringComparison.OrdinalIgnoreCase)
                ? SkinExtraFamilyRegistry.ById("audio.other")
                : category.Equals("Other", StringComparison.OrdinalIgnoreCase)
                    ? SkinExtraFamilyRegistry.ById("misc.other")
                    : null;
        if (family is null) return null;
        var described = files.Select(path =>
            SkinExtraFingerprint.Describe(Path.GetFileName(path), Path.GetFileName(path), File.ReadAllBytes(path)))
            .ToList();
        var fingerprint = SkinExtraFingerprint.ForPack(family.Id, described);
        return new SkinExtraPackDescriptor(
            directory,
            new SkinExtraPackManifest
            {
                Id = fingerprint[..16],
                DisplayName = Path.GetFileName(directory),
                FamilyId = family.Id,
                Area = family.Area,
                FamilyName = family.Name,
                Fingerprint = fingerprint,
                Files = described,
            },
            true);
    }

    internal static IReadOnlyList<string> FindCandidateDirectories(string extrasRoot)
    {
        if (!Directory.Exists(extrasRoot)) return [];
        var metadataRoot = Path.Combine(extrasRoot, ".kumori");
        var result = new List<string>();
        foreach (var directory in Directory.EnumerateDirectories(
                     extrasRoot,
                     "*",
                     SearchOption.AllDirectories))
        {
            if (directory.Equals(metadataRoot, StringComparison.OrdinalIgnoreCase)
                || directory.StartsWith(
                    metadataRoot + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
                continue;
            if (File.Exists(Path.Combine(directory, "extras.json"))
                || (!Directory.EnumerateDirectories(directory).Any()
                    && Directory.EnumerateFiles(directory).Any(IsAsset)))
                result.Add(directory);
        }
        return result;
    }

    private static bool IsAsset(string path) =>
        SkinMediaTypes.IsImage(path) || SkinMediaTypes.IsAudio(path);
}
