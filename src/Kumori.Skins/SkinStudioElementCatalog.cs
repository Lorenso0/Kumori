namespace Kumori.Skins;

public sealed record SkinStudioElementDefinition(
    string Label,
    string ComponentName,
    bool IsAudio,
    SkinStudioPreviewScene? PreviewScene,
    string? FamilyId = null,
    string Area = "osu!",
    int? ManiaKeyCount = null)
{
    public SkinStudioSemanticPreviewDescriptor SemanticPreview =>
        SkinStudioSemanticPreviewCatalog.ForElement(this);
}

public sealed record SkinStudioElementCategory(
    string Title,
    string Description,
    bool IsAudio,
    IReadOnlyList<SkinStudioElementDefinition> Elements);

public static class SkinStudioElementCatalog
{
    public static IReadOnlyList<SkinStudioElementCategory> Categories { get; } =
    [
        Images("Hit objects", "Circles, overlays, approach timing, follow points, and slider endpoints.",
            ("Approach circle", "approachcircle", SkinStudioPreviewScene.Circles),
            ("Hit circle", "hitcircle", SkinStudioPreviewScene.Circles),
            ("Hit overlay", "hitcircleoverlay", SkinStudioPreviewScene.Circles),
            ("Slider start", "sliderstartcircle", SkinStudioPreviewScene.Sliders),
            ("Start overlay", "sliderstartcircleoverlay", SkinStudioPreviewScene.Sliders),
            ("Slider end", "sliderendcircle", SkinStudioPreviewScene.Sliders),
            ("End overlay", "sliderendcircleoverlay", SkinStudioPreviewScene.Sliders),
            ("Reverse arrow", "reversearrow", SkinStudioPreviewScene.Sliders),
            ("Follow point", "followpoint", SkinStudioPreviewScene.Sliders),
            ("Slider score point", "sliderscorepoint", SkinStudioPreviewScene.Sliders),
            ("Legacy slider tick", "sliderpoint10", SkinStudioPreviewScene.Sliders),
            ("Legacy slider repeat", "sliderpoint30", SkinStudioPreviewScene.Sliders),
            ("Slider ball", "sliderb", SkinStudioPreviewScene.Sliders),
            ("Slider ball frame zero", "sliderb0", SkinStudioPreviewScene.Sliders),
            ("Slider ball normal map", "sliderb-nd", SkinStudioPreviewScene.Sliders),
            ("Slider ball specular map", "sliderb-spec", SkinStudioPreviewScene.Sliders),
            ("Follow circle", "sliderfollowcircle", SkinStudioPreviewScene.Sliders)),
        Images("Cursor and trail", "Interactive cursor components and trail resources.",
            ("Cursor", "cursor", SkinStudioPreviewScene.Cursor),
            ("Cursor middle", "cursormiddle", SkinStudioPreviewScene.Cursor),
            ("Cursor trail", "cursortrail", SkinStudioPreviewScene.Cursor),
            ("Cursor ripple", "cursor-ripple", SkinStudioPreviewScene.Cursor),
            ("Cursor particles", "star2", SkinStudioPreviewScene.Cursor),
            ("Cursor smoke", "cursor-smoke", SkinStudioPreviewScene.Cursor)),
        Images("Gameplay HUD", "Health, scorebar, input overlay, skip, pause, fail, and section markers.",
            ("Scorebar background", "scorebar-bg", SkinStudioPreviewScene.Hud),
            ("Scorebar colour", "scorebar-colour", SkinStudioPreviewScene.Hud),
            ("Scorebar marker", "scorebar-marker", SkinStudioPreviewScene.Hud),
            ("Ki", "scorebar-ki", SkinStudioPreviewScene.Hud),
            ("Ki danger", "scorebar-kidanger", SkinStudioPreviewScene.Hud),
            ("Ki danger 2", "scorebar-kidanger2", SkinStudioPreviewScene.Hud),
            ("Input background", "inputoverlay-background", SkinStudioPreviewScene.Hud),
            ("Input key", "inputoverlay-key", SkinStudioPreviewScene.Hud),
            ("Skip", "play-skip", SkinStudioPreviewScene.Hud),
            ("Pause overlay", "pause-overlay", SkinStudioPreviewScene.Hud),
            ("Fail background", "fail-background", SkinStudioPreviewScene.Hud),
            ("Section pass", "section-pass", SkinStudioPreviewScene.Hud),
            ("Section fail", "section-fail", SkinStudioPreviewScene.Hud)),
        Images("Judgements", "Legacy osu!standard judgement sprites.",
            ("Miss", "hit0", SkinStudioPreviewScene.Judgements),
            ("50", "hit50", SkinStudioPreviewScene.Judgements),
            ("100", "hit100", SkinStudioPreviewScene.Judgements),
            ("100 katu", "hit100k", SkinStudioPreviewScene.Judgements),
            ("300", "hit300", SkinStudioPreviewScene.Judgements),
            ("300 geki", "hit300g", SkinStudioPreviewScene.Judgements),
            ("300 katu", "hit300k", SkinStudioPreviewScene.Judgements),
            ("Slider end miss", "sliderendmiss", SkinStudioPreviewScene.Judgements),
            ("Slider tick miss", "slidertickmiss", SkinStudioPreviewScene.Judgements),
            ("50 particle", "particle50", SkinStudioPreviewScene.Judgements),
            ("100 particle", "particle100", SkinStudioPreviewScene.Judgements),
            ("300 particle", "particle300", SkinStudioPreviewScene.Judgements)),
        Images("Spinner", "Old-style and new-style spinner layers.",
            ("Background", "spinner-background", SkinStudioPreviewScene.Spinner),
            ("Circle", "spinner-circle", SkinStudioPreviewScene.Spinner),
            ("Metre", "spinner-metre", SkinStudioPreviewScene.Spinner),
            ("Approach", "spinner-approachcircle", SkinStudioPreviewScene.Spinner),
            ("Bottom", "spinner-bottom", SkinStudioPreviewScene.Spinner),
            ("Glow", "spinner-glow", SkinStudioPreviewScene.Spinner),
            ("Middle", "spinner-middle", SkinStudioPreviewScene.Spinner),
            ("Middle 2", "spinner-middle2", SkinStudioPreviewScene.Spinner),
            ("Top", "spinner-top", SkinStudioPreviewScene.Spinner),
            ("Clear", "spinner-clear", SkinStudioPreviewScene.Spinner),
            ("Spin", "spinner-spin", SkinStudioPreviewScene.Spinner),
            ("RPM", "spinner-rpm", SkinStudioPreviewScene.Spinner)),
        Images("Countdown and prompts", "Ready/go prompts, countdown frames, warnings, and combo bursts.",
            ("Ready", "ready", SkinStudioPreviewScene.Circles),
            ("Count 3", "count3", SkinStudioPreviewScene.Circles),
            ("Count 2", "count2", SkinStudioPreviewScene.Circles),
            ("Count 1", "count1", SkinStudioPreviewScene.Circles),
            ("Go", "go", SkinStudioPreviewScene.Circles),
            ("Warning arrow", "arrow-warning", SkinStudioPreviewScene.Circles),
            ("Combo burst", "comboburst", SkinStudioPreviewScene.Judgements)),
        Images("Ranking", "Result screen panels, labels, grades, and perfect-combo resources.",
            ("Ranking panel", "ranking-panel", null), ("Ranking graph", "ranking-graph", null),
            ("Max combo", "ranking-maxcombo", null), ("Accuracy", "ranking-accuracy", null),
            ("Grade XH", "ranking-XH", null), ("Grade X", "ranking-X", null),
            ("Grade SH", "ranking-SH", null), ("Grade S", "ranking-S", null),
            ("Grade A", "ranking-A", null), ("Grade B", "ranking-B", null),
            ("Grade C", "ranking-C", null), ("Grade D", "ranking-D", null),
            ("Small grade XH", "ranking-XH-small", null), ("Small grade X", "ranking-X-small", null),
            ("Small grade SH", "ranking-SH-small", null), ("Small grade S", "ranking-S-small", null),
            ("Small grade A", "ranking-A-small", null), ("Small grade B", "ranking-B-small", null),
            ("Small grade C", "ranking-C-small", null), ("Small grade D", "ranking-D-small", null),
            ("Perfect", "ranking-perfect", null)),
        Images("Menus and selection", "Mode selection, pause actions, song-selection controls, and menu background.",
            ("Menu background", "menu-background", null), ("Menu fountain star", "Menu/fountain-star", null),
            ("Mode osu!", "mode-osu", null), ("Selection mode", "selection-mode", null),
            ("Selection mods", "selection-mods", null), ("Selection random", "selection-random", null),
            ("Selection options", "selection-options", null), ("Pause back", "pause-back", null),
            ("Pause continue", "pause-continue", null), ("Pause retry", "pause-retry", null)),
        NumberFonts(),
        CatchElements(),
        TaikoElements(),
        ManiaElements(),
        AudioSamples(),
    ];

    /// <summary>
    /// The semantic groups and ordering used by Kumori's established WPF Skin
    /// Studio sidebar. The neutral definitions remain authoritative; this is a
    /// presentation projection rather than a second element catalogue.
    /// </summary>
    public static IReadOnlyList<SkinStudioElementCategory> LegacySidebarCategories { get; } =
        buildLegacySidebarCategories();

    public static SkinStudioElementDefinition? Find(string componentName) =>
        Categories.SelectMany(category => category.Elements).FirstOrDefault(element =>
            element.ComponentName.Equals(componentName, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<SkinStudioElementCategory> buildLegacySidebarCategories()
    {
        var hitObjects = category("Hit objects").Elements;
        var sliderComponents = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "sliderstartcircle", "sliderstartcircleoverlay", "sliderendcircle",
            "sliderendcircleoverlay", "reversearrow", "sliderscorepoint",
            "sliderpoint10", "sliderpoint30", "sliderb", "sliderb0",
            "sliderb-nd", "sliderb-spec", "sliderfollowcircle",
        };
        SkinStudioElementCategory Group(
            string title,
            string description,
            params string[] sourceTitles) => new(
                title,
                description,
                sourceTitles.Select(category).All(source => source.IsAudio),
                sourceTitles.SelectMany(source => category(source).Elements).ToArray());

        return
        [
            new SkinStudioElementCategory(
                "Hit objects",
                "Hit circles, approach circles, and follow points.",
                false,
                hitObjects.Where(element => !sliderComponents.Contains(element.ComponentName)).ToArray()),
            new SkinStudioElementCategory(
                "Sliders",
                "Slider endpoints, balls, ticks, reverse arrows, and follow circles.",
                false,
                hitObjects.Where(element => sliderComponents.Contains(element.ComponentName)).ToArray()),
            Group("Cursor", "Cursor, middle, trail, particles, and smoke.", "Cursor and trail"),
            Group("Judgements", "Gameplay judgement sprites and particles.", "Judgements"),
            Group(
                "HUD & interface",
                "Gameplay HUD, prompts, ranking, pause, and menu resources.",
                "Gameplay HUD", "Countdown and prompts", "Ranking", "Menus and selection"),
            Group("Numbers", "Hit-circle, score, combo, and score-entry fonts.", "Number fonts"),
            Group("Spinner", "Old-style and new-style spinner layers.", "Spinner"),
            Group("Catch", "Fruits, droplets, bananas, catcher states, and bursts.", "Catch"),
            Group("Taiko", "Notes, rolls, playfield furniture, explosions, and pippidon.", "Taiko"),
            Group("Mania", "Stage, columns, keys, notes, holds, lighting, and judgements.", "Mania"),
            Group("Modes & other", "Audio samples and remaining specialised assets.", "Audio samples"),
        ];
    }

    private static SkinStudioElementCategory category(string title) =>
        Categories.Single(category => category.Title.Equals(title, StringComparison.Ordinal));

    private static SkinStudioElementCategory Images(
        string title,
        string description,
        params (string Label, string Component, SkinStudioPreviewScene? Scene)[] elements) =>
        new(title, description, false, elements.Select(element =>
            new SkinStudioElementDefinition(element.Label, element.Component, false, element.Scene)).ToArray());

    private static SkinStudioElementCategory NumberFonts()
    {
        var groups = new[] { ("default", "Hit"), ("score", "Score"), ("combo", "Combo"), ("scoreentry", "Entry") };
        var elements = groups.SelectMany(group => Enumerable.Range(0, 10)
                .Select(number => new SkinStudioElementDefinition(
                    $"{group.Item2} {number}", $"{group.Item1}-{number}", false, SkinStudioPreviewScene.Judgements))
                .Concat(new[] { "comma", "dot", "percent", "x" }.Select(suffix =>
                    new SkinStudioElementDefinition(
                        $"{group.Item2} {suffix}", $"{group.Item1}-{suffix}", false, SkinStudioPreviewScene.Judgements))))
            .ToArray();
        return new SkinStudioElementCategory("Number fonts", "Hit-circle, score, combo, and score-entry glyph sets.", false, elements);
    }

    private static SkinStudioElementCategory CatchElements() => SemanticImages(
        "Catch",
        "Native catch fruit, catcher, hyperdash, explosion, and combo-burst contexts.",
        "Catch",
        [
            ("Apple", "fruit-apple", "catch.fruits"),
            ("Apple overlay", "fruit-apple-overlay", "catch.fruits"),
            ("Grapes", "fruit-grapes", "catch.fruits"),
            ("Grapes overlay", "fruit-grapes-overlay", "catch.fruits"),
            ("Orange", "fruit-orange", "catch.fruits"),
            ("Orange overlay", "fruit-orange-overlay", "catch.fruits"),
            ("Pear", "fruit-pear", "catch.fruits"),
            ("Pear overlay", "fruit-pear-overlay", "catch.fruits"),
            ("Banana", "fruit-bananas", "catch.fruits"),
            ("Droplet", "fruit-drop", "catch.fruits"),
            ("Droplet overlay", "fruit-drop-overlay", "catch.fruits"),
            ("Catcher idle", "fruit-catcher-idle", "catch.catcher"),
            ("Catcher kiai", "fruit-catcher-kiai", "catch.catcher"),
            ("Catcher fail", "fruit-catcher-fail", "catch.catcher"),
            ("Legacy catcher", "fruit-ryuuta", "catch.catcher"),
            ("Combo burst", "comboburst-fruits", "catch.comboburst"),
        ]);

    private static SkinStudioElementCategory TaikoElements() => SemanticImages(
        "Taiko",
        "Native taiko lane, note, roll, swell, explosion, glow, and mascot contexts.",
        "Taiko",
        [
            ("Hit circle", "taikohitcircle", "taiko.notes"),
            ("Hit circle overlay", "taikohitcircleoverlay", "taiko.notes"),
            ("Big circle", "taikobigcircle", "taiko.notes"),
            ("Big circle overlay", "taikobigcircleoverlay", "taiko.notes"),
            ("Miss explosion", "taiko-hit0", "taiko.notes"),
            ("100 explosion", "taiko-hit100", "taiko.notes"),
            ("100 strong explosion", "taiko-hit100k", "taiko.notes"),
            ("300 explosion", "taiko-hit300", "taiko.notes"),
            ("300 strong explosion", "taiko-hit300k", "taiko.notes"),
            ("Roll middle", "taiko-roll-middle", "taiko.rolls"),
            ("Roll end", "taiko-roll-end", "taiko.rolls"),
            ("Scroller", "taiko-slider", "taiko.rolls"),
            ("Bar left", "taiko-bar-left", "taiko.playfield"),
            ("Bar right", "taiko-bar-right", "taiko.playfield"),
            ("Bar line", "taiko-barline", "taiko.playfield"),
            ("Kiai glow", "taiko-glow", "taiko.playfield"),
            ("Pippidon idle", "pippidon-idle", "taiko.pippidon"),
            ("Pippidon kiai", "pippidon-kiai", "taiko.pippidon"),
            ("Pippidon fail", "pippidon-fail", "taiko.pippidon"),
            ("Pippidon clear", "pippidon-clear", "taiko.pippidon"),
        ]);

    private static SkinStudioElementCategory ManiaElements() => SemanticImages(
        "Mania",
        "Configured mania stage, column, key, note, hold, light, and judgement contexts (4K fallback).",
        "Mania",
        [
            ("Stage left", "mania-stage-left", "mania.stage"),
            ("Stage right", "mania-stage-right", "mania.stage"),
            ("Stage bottom", "mania-stage-bottom", "mania.stage"),
            ("Stage hint", "mania-stage-hint", "mania.stage"),
            ("Warning arrow", "mania-warningarrow", "mania.stage"),
            ("Key", "mania-key", "mania.keys"),
            ("Pressed key", "mania-key-down", "mania.keys"),
            ("Note", "mania-note", "mania.notes"),
            ("Hold head", "mania-hold-head", "mania.holds"),
            ("Hold body", "mania-hold-body", "mania.holds"),
            ("Hold tail", "mania-hold-tail", "mania.holds"),
            ("Column light", "lightingN", "mania.lighting"),
            ("Stage light", "lightingL", "mania.lighting"),
            ("Miss", "mania-hit0", "mania.hitbursts"),
            ("50", "mania-hit50", "mania.hitbursts"),
            ("100", "mania-hit100", "mania.hitbursts"),
            ("200", "mania-hit200", "mania.hitbursts"),
            ("300", "mania-hit300", "mania.hitbursts"),
            ("Max", "mania-hit300g", "mania.hitbursts"),
            ("Combo burst", "comboburst-mania", "mania.comboburst"),
        ], maniaKeys: 4);

    private static SkinStudioElementCategory SemanticImages(
        string title,
        string description,
        string area,
        IReadOnlyList<(string Label, string Component, string Family)> elements,
        int? maniaKeys = null) => new(
            title,
            description,
            false,
            elements.Select(element =>
            {
                var descriptor = SkinStudioSemanticPreviewCatalog.Resolve(
                    element.Component,
                    element.Family,
                    maniaKeys);
                return new SkinStudioElementDefinition(
                    element.Label,
                    element.Component,
                    false,
                    descriptor.Scene,
                    element.Family,
                    area,
                    maniaKeys);
            }).ToArray());

    private static SkinStudioElementCategory AudioSamples()
    {
        string[] components =
        [
            "normal-hitnormal", "normal-hitwhistle", "normal-hitfinish", "normal-hitclap",
            "soft-hitnormal", "soft-hitwhistle", "soft-hitfinish", "soft-hitclap",
            "drum-hitnormal", "drum-hitwhistle", "drum-hitfinish", "drum-hitclap",
            "normal-slidertick", "normal-sliderslide", "normal-sliderwhistle",
            "soft-slidertick", "soft-sliderslide", "soft-sliderwhistle",
            "drum-slidertick", "drum-sliderslide", "drum-sliderwhistle", "combobreak",
            "failsound", "pause-loop", "spinnerspin", "spinnerbonus", "spinnerbonus-max", "count1s", "count2s",
            "count3s", "readys", "gos", "sectionpass", "sectionfail", "nightcore-kick",
            "nightcore-clap", "nightcore-hat", "nightcore-finish", "applause", "applause-XH",
            "applause-X", "applause-SH", "applause-S", "applause-A", "applause-B",
            "applause-C", "applause-D", "seeya", "welcome", "menuhit", "menuback",
            "menu-play-click", "menu-back-click", "key-confirm", "key-delete", "key-movement",
            "rank-up", "rank-down",
        ];
        return new SkinStudioElementCategory(
            "Audio samples",
            "Audition samples through lazer's skinnable audio pipeline.",
            true,
            components.Select(component =>
            {
                var descriptor = SkinStudioSemanticPreviewCatalog.Resolve(component);
                return new SkinStudioElementDefinition(
                    component.Replace('-', ' '),
                    component,
                    true,
                    descriptor.Scene,
                    descriptor.FamilyId,
                    "Audio");
            }).ToArray());
    }
}
