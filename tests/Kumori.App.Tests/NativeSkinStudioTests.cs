using Kumori.App.Skins;
using Kumori.SkinStudio;
using Kumori.Skins;
using Kumori.Tracking;
using osu.Framework.Graphics;
using osuTK;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Kumori.App.Tests;

public sealed class NativeSkinStudioTests
{
    [Fact]
    public void EmbeddedArgumentsRequireOpaqueGuidSession()
    {
        var session = Guid.NewGuid().ToString("N");
        var contract = Path.GetFullPath("embedded-contract.json");

        var parsed = StudioArguments.Parse(
        [
            "--contract",
            contract,
            "--embedded",
            "--embedded-session",
            session,
        ]);

        Assert.True(parsed.Embedded);
        Assert.Equal(session, parsed.EmbeddedSession);
        Assert.Equal(contract, parsed.ContractPath);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("")]
    public void EmbeddedArgumentsRejectInvalidSession(string session)
    {
        Assert.Throws<InvalidDataException>(() =>
            StudioArguments.Parse(
            [
                "--embedded",
                "--embedded-session",
                session,
            ]));
    }

    [Fact]
    public void VisualAcceptancePlanCoversEveryWorkbenchAndGameplayFamily()
    {
        var targets = StudioVisualAcceptancePlan.Targets;
        var workbench = targets
            .Where(target => target.Kind == "workbench")
            .Select(target => target.Name)
            .ToHashSet();
        var gameplay = targets
            .Where(target => target.Kind == "gameplay")
            .Select(target => target.Name)
            .ToHashSet();
        var mockup = targets
            .Where(target => target.Kind == "mockup")
            .Select(target => target.Name)
            .ToHashSet();
        var semantic = targets
            .Where(target => target.Kind == "semantic")
            .Select(target => target.Name)
            .ToHashSet();

        Assert.Equal(
            StudioSkinCoverageCatalog.Categories
                .Select(category => category.Title)
                .ToHashSet(),
            workbench);
        Assert.Subset(
            new HashSet<string>
            {
                "circle-and-hud",
                "slider-and-follow-points",
                "break-and-hud",
                "curved-slider-and-cursor",
                "spinner",
                "combo-colours-and-judgements",
            },
            gameplay);
        Assert.Equal(["gameplay-mockup"], mockup);
        Assert.Subset(
            semantic,
            new HashSet<string>
            {
                "hitcircle-numbers-1-through-10",
                "followpoints-only",
                "interface-ranking",
                "catch-fruits",
                "taiko-notes",
                "mania-keys-4k",
                "hitsound-hitcircle-loop",
            });
        Assert.All(targets, target => Assert.True(target.Time >= 0));
        Assert.Equal(
            StudioVisualAcceptancePlan.NativeMockupTime,
            targets.Single(target => target.Kind == "mockup").Time);
    }

    [Fact]
    public void VisualAcceptanceArgumentsUseAnExplicitAbsoluteOutput()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        var parsed = StudioArguments.Parse(
        [
            "--acceptance-output",
            path,
        ]);

        Assert.Equal(Path.GetFullPath(path), parsed.AcceptanceOutputPath);
        Assert.Throws<InvalidDataException>(() =>
            StudioArguments.Parse(
            [
                "--embedded",
                "--embedded-session",
                Guid.NewGuid().ToString("N"),
                "--acceptance-output",
                path,
            ]));
    }

    [Fact]
    public void CommandAcceptanceArgumentsAreStandaloneAndAbsolute()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        var parsed = StudioArguments.Parse(
        [
            "--command-acceptance-output",
            path,
        ]);

        Assert.Equal(
            Path.GetFullPath(path),
            parsed.CommandAcceptanceOutputPath);
        Assert.Throws<InvalidDataException>(() =>
            StudioArguments.Parse(
            [
                "--embedded",
                "--embedded-session",
                Guid.NewGuid().ToString("N"),
                "--command-acceptance-output",
                path,
            ]));
        Assert.Throws<InvalidDataException>(() =>
            StudioArguments.Parse(
            [
                "--acceptance-output",
                path,
                "--command-acceptance-output",
                path,
            ]));
    }

    [Fact]
    public void PublishAcceptanceArgumentsAreStandaloneAndAbsolute()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            Guid.NewGuid().ToString("N"));

        var parsed = StudioArguments.Parse(
        [
            "--publish-acceptance-output",
            path,
        ]);

        Assert.Equal(
            Path.GetFullPath(path),
            parsed.PublishAcceptanceOutputPath);
        Assert.Throws<InvalidDataException>(() =>
            StudioArguments.Parse(
            [
                "--embedded",
                "--embedded-session",
                Guid.NewGuid().ToString("N"),
                "--publish-acceptance-output",
                path,
            ]));
        Assert.Throws<InvalidDataException>(() =>
            StudioArguments.Parse(
            [
                "--command-acceptance-output",
                path,
                "--publish-acceptance-output",
                path,
            ]));
    }

    [Fact]
    public void LivePreviewAuditArgumentsAreStandaloneAndAbsolute()
    {
        var path = Path.Combine(Path.GetTempPath(), "draft-manifest.json");

        var parsed = StudioArguments.Parse(
        [
            "--audit-live-preview",
            path,
        ]);

        Assert.Equal(
            Path.GetFullPath(path),
            parsed.AuditLivePreviewDraftPath);
        Assert.Throws<InvalidDataException>(() =>
            StudioArguments.Parse(
            [
                "--probe",
                "--audit-live-preview",
                path,
            ]));
    }

    [Fact]
    public void LazerCatalogInspectionIsStandaloneAndAbsolute()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "lazer-player-root");

        var parsed = StudioArguments.Parse(
        [
            "--inspect-lazer-catalog",
            root,
        ]);

        Assert.Equal(
            Path.GetFullPath(root),
            parsed.InspectLazerCatalogRoot);
        Assert.Throws<InvalidDataException>(() =>
            StudioArguments.Parse(
            [
                "--probe",
                "--inspect-lazer-catalog",
                root,
            ]));
    }

    [Fact]
    public void WorkbenchCatalogCoversAllRequiredElementFamilies()
    {
        var categories = StudioSkinCoverageCatalog.Categories;
        var titles = categories.Select(category => category.Title).ToHashSet();
        var components = categories
            .SelectMany(category => category.Elements)
            .Select(element => element.ComponentName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Subset(
            new HashSet<string>
            {
                "Hit objects",
                "Cursor and trail",
                "Gameplay HUD",
                "Judgements",
                "Spinner",
                "Countdown and prompts",
                "Ranking",
                 "Menus and selection",
                 "Number fonts",
                 "Catch",
                 "Taiko",
                 "Mania",
                 "Audio samples",
            },
            titles);
        Assert.True(
            categories.Sum(category => category.Elements.Count) >= 135,
            "The all-elements workbench unexpectedly lost coverage.");
        var requiredComponents = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "approachcircle",
                "hitcircle",
                "hitcircleoverlay",
                "sliderstartcircle",
                "sliderendcircle",
                "reversearrow",
                "followpoint",
                "sliderscorepoint",
                "sliderpoint10",
                "sliderpoint30",
                "sliderb",
                "sliderb0",
                "sliderb-nd",
                "sliderb-spec",
                "sliderfollowcircle",
                "cursor",
                "cursormiddle",
                "cursortrail",
                "cursor-ripple",
                "star2",
                "scorebar-bg",
                "scorebar-colour",
                "scorebar-marker",
                "inputoverlay-background",
                "hit0",
                "hit50",
                "hit100",
                "hit300",
                "sliderendmiss",
                "slidertickmiss",
                "particle50",
                "particle100",
                "particle300",
                "spinner-background",
                "spinner-circle",
                "spinner-top",
                "spinner-middle",
                "spinner-rpm",
                "ready",
                "count1",
                "count2",
                "count3",
                "go",
                "ranking-panel",
                "ranking-XH",
                "ranking-D",
                "ranking-XH-small",
                "ranking-D-small",
                "Menu/fountain-star",
                "mode-osu",
                "selection-mods",
                "pause-retry",
                "default-0",
                "default-9",
                "score-0",
                "combo-0",
                "scoreentry-0",
                "normal-hitnormal",
                "normal-hitwhistle",
                "normal-hitfinish",
                "normal-hitclap",
                "soft-hitnormal",
                "drum-hitnormal",
                "normal-slidertick",
                "combobreak",
                "failsound",
                "spinnerspin",
                "spinnerbonus",
                "nightcore-kick",
            };
        var missing = requiredComponents.Except(components).ToArray();
        Assert.True(
            missing.Length == 0,
            $"The workbench catalog is missing: {string.Join(", ", missing)}");
    }

    [Fact]
    public void EveryWorkbenchCategoryHasDescriptionAndElements()
    {
        foreach (var category in StudioSkinCoverageCatalog.Categories)
        {
            Assert.False(string.IsNullOrWhiteSpace(category.Title));
            Assert.False(string.IsNullOrWhiteSpace(category.Description));
            Assert.NotEmpty(category.Elements);
            Assert.All(category.Elements, element =>
            {
                Assert.False(string.IsNullOrWhiteSpace(element.Label));
                Assert.False(string.IsNullOrWhiteSpace(element.ComponentName));
                Assert.Equal(category.IsAudio, element.SampleFactory is not null);
            });
        }
    }

    [Fact]
    public void WorkbenchPreviewPreservesNativeDrawableSizing()
    {
        var nativeDrawable = new osu.Framework.Graphics.Shapes.Box
        {
            Size = new osuTK.Vector2(113),
        };

        var preview = StudioAssetTile.FitVisualForPreview(
            nativeDrawable,
            featured: false);

        Assert.IsType<StudioAssetPreview>(preview);
        Assert.Equal(Axes.None, nativeDrawable.RelativeSizeAxes);
        Assert.Equal(new osuTK.Vector2(113), nativeDrawable.Size);
        Assert.Equal(Anchor.Centre, nativeDrawable.Anchor);
        Assert.Equal(Anchor.Centre, nativeDrawable.Origin);
    }

    [Theory]
    [InlineData("hitcircle.png", "hitcircle")]
    [InlineData("hitcircle@2x.png", "hitcircle")]
    [InlineData("followpoint-12@2x.png", "followpoint")]
    [InlineData("mania/mania-key1.png", "mania/mania-key1")]
    [InlineData("custom-audio.ogg", "custom-audio")]
    public void DynamicWorkbenchGroupsFramesAndResolutionVariants(
        string filename,
        string expected)
    {
        Assert.Equal(expected, StudioSkinWorkbench.ComponentName(filename));
    }

    [Fact]
    public void WorkbenchSearchMatchesCategoryLabelsAndComponentNames()
    {
        var categories = StudioSkinCoverageCatalog.Categories;

        var byCategory = StudioSkinWorkbench.FilterCategories(categories, "spinner");
        var byFilename = StudioSkinWorkbench.FilterCategories(categories, "cursortrail");
        var byLabel = StudioSkinWorkbench.FilterCategories(categories, "reverse arrow");
        var missing = StudioSkinWorkbench.FilterCategories(categories, "does-not-exist");

        Assert.Contains(byCategory, category => category.Title == "Spinner");
        Assert.Single(byFilename.SelectMany(category => category.Elements));
        Assert.Equal(
            "cursortrail",
            byFilename.SelectMany(category => category.Elements).Single().ComponentName);
        Assert.Equal(
            "reversearrow",
            byLabel.SelectMany(category => category.Elements).Single().ComponentName);
        Assert.Empty(missing);
    }

    [Fact]
    public void WorkbenchCategoryAndFallbackFiltersCompose()
    {
        var supplied = new HashSet<string>(
            ["spinner-circle", "spinner-top"],
            StringComparer.OrdinalIgnoreCase);

        var filtered = StudioSkinWorkbench.FilterCategories(
            StudioSkinCoverageCatalog.Categories,
            query: "spinner",
            categoryTitle: "Spinner",
            hideFallbackOnly: true,
            supplied.Contains);

        var category = Assert.Single(filtered);
        Assert.Equal("Spinner", category.Title);
        Assert.Equal(
            ["spinner-circle", "spinner-top"],
            category.Elements.Select(element => element.ComponentName));
    }

    [Fact]
    public void WorkbenchStartsWithOneLazyCategoryRatherThanEntireLibrary()
    {
        Assert.Equal("Hit objects", StudioSkinWorkbench.DefaultCategoryTitle);

        var filtered = StudioSkinWorkbench.FilterCategories(
            StudioSkinCoverageCatalog.Categories,
            query: null,
            categoryTitle: StudioSkinWorkbench.DefaultCategoryTitle);

        Assert.Single(filtered);
        Assert.Equal(
            "Hit objects",
            filtered.Single().Title);
        Assert.True(
            filtered.Single().Elements.Count
            < StudioSkinCoverageCatalog.Categories.Sum(category =>
                category.Elements.Count));
    }

    [Fact]
    public void NativeSkinCacheReusesOnlyExactDraftRevision()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"kumori-native-skin-cache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var cache = new StudioNativeSkinCache(root);
            var draftId = Guid.NewGuid();
            var skinId = Guid.NewGuid();

            cache.Set(draftId, revision: 7, skinId);

            Assert.True(cache.TryGet(draftId, revision: 7, out var cached));
            Assert.Equal(skinId, cached);
            Assert.False(cache.TryGet(draftId, revision: 8, out _));

            cache.Remove(draftId);
            Assert.False(cache.TryGet(draftId, revision: 7, out _));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("#FF0080", 255, 0, 128)]
    [InlineData("102030", 16, 32, 48)]
    public void ImageTransformColourInputIsStrictAndDeterministic(
        string value,
        byte red,
        byte green,
        byte blue)
    {
        Assert.True(StudioImageTransformOverlay.TryParseHexColour(value, out var colour));
        Assert.Equal(new SkinRgb(red, green, blue), colour);
        Assert.False(StudioImageTransformOverlay.TryParseHexColour("#xyz", out _));
    }

    [Fact]
    public void ImageTransformOverlayConstructsWithoutAutosizeContractViolations()
    {
        var overlay = new StudioImageTransformOverlay(Path.GetTempPath());

        Assert.False(overlay.IsPresent);
    }

    [Theory]
    [InlineData("#000000")]
    [InlineData("#35A7FF")]
    [InlineData("#FFFFFF")]
    public void GraphicalColourPickerFormatsRgbWithoutFloatingPointFormatErrors(
        string expected)
    {
        Assert.Equal(
            expected,
            StudioImageTransformOverlay.ToRgbHex(
                Colour4.FromHex(expected)));
    }

    [Fact]
    public void WorkbenchSkinOnlyFilterExcludesTransparentPlaceholders()
    {
        static byte[] png(Rgba32 colour)
        {
            using var image = new Image<Rgba32>(1, 1, colour);
            using var output = new MemoryStream();
            image.SaveAsPng(output);
            return output.ToArray();
        }

        var supplied = StudioSkinWorkbench.VisibleSuppliedComponents(
            new Dictionary<string, byte[]>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["cursor.png"] = png(new Rgba32(255, 255, 255, 255)),
                ["cursormiddle.png"] = png(new Rgba32(0, 0, 0, 0)),
                ["normal-hitnormal.wav"] = [1, 2, 3],
            });

        Assert.Contains("cursor", supplied);
        Assert.DoesNotContain("cursormiddle", supplied);
        Assert.Contains("normal-hitnormal", supplied);
    }

    [Fact]
    public void DisabledNativeActionIsVisiblyDimmedUntilEnabled()
    {
        var button = new StudioActionButton(
            "Unavailable",
            () => { },
            enabled: false,
            disabledReason: "Select an asset first.");

        Assert.False(button.ActionEnabled);
        Assert.InRange(button.Alpha, 0.4f, 0.5f);
        Assert.Equal("Select an asset first.", button.TooltipText.ToString());

        button.SetEnabled(true);

        Assert.True(button.ActionEnabled);
        Assert.Equal(1, button.Alpha);
        Assert.True(string.IsNullOrEmpty(button.TooltipText.ToString()));

        button.SetEnabled(false, "Wait for catalog synchronization to finish.");

        Assert.Equal(
            "Wait for catalog synchronization to finish.",
            button.TooltipText.ToString());
    }

    [Fact]
    public void ExtrasPreselectionMapsLogicalAnimationAndResolutionFamilies()
    {
        var hitCircles = SkinExtraFamilyRegistry.ById("osu.hitcircles")!;
        var cursor = SkinExtraFamilyRegistry.ById("osu.cursor")!;
        var families = new[]
        {
            new SkinExtractionFamily
            {
                Definition = hitCircles,
                Files =
                [
                    new SkinExtractionFile("hitcircle-0.png", [1]),
                    new SkinExtractionFile("hitcircle-1@2x.png", [2]),
                    new SkinExtractionFile("approachcircle.png", [3]),
                ],
                IniPatch = [],
            },
            new SkinExtractionFamily
            {
                Definition = cursor,
                Files =
                [
                    new SkinExtractionFile("cursor.png", [4]),
                ],
                IniPatch = [],
            },
        };

        var selected =
            StudioExtrasExtractionOverlay.SelectionIdsForComponents(
                families,
                ["hitcircle"]);

        Assert.Equal([families[0].SelectionId], selected);
    }

    [Fact]
    public void RawSkinIniEditorPreservesOrderAndExplicitTrailingNewline()
    {
        var rendered = StudioRawSkinIniOverlay.ComposeRawText(
        [
            "; heading",
            "[General]",
            "Name: Kumori",
            "<delete>",
            "UnknownProperty: preserved",
        ], endedWithNewline: true);

        Assert.Equal(
            "; heading\n[General]\nName: Kumori\nUnknownProperty: preserved\n",
            rendered);
    }

    [Fact]
    public void RawSkinIniLineCommandsInsertMoveAndRemoveDeterministically()
    {
        IReadOnlyList<string> lines =
        [
            "; heading",
            "[General]",
            "Name: Original",
        ];

        lines = StudioRawSkinIniOverlay.InsertRawLine(
            lines,
            2,
            "Author: Kumori");
        Assert.Equal(
            ["; heading", "[General]", "Author: Kumori", "Name: Original"],
            lines);

        lines = StudioRawSkinIniOverlay.MoveRawLine(lines, 3, -1);
        Assert.Equal(
            ["; heading", "[General]", "Name: Original", "Author: Kumori"],
            lines);

        lines = StudioRawSkinIniOverlay.RemoveRawLine(lines, 0);
        Assert.Equal(
            ["[General]", "Name: Original", "Author: Kumori"],
            lines);

        Assert.Equal(
            lines,
            StudioRawSkinIniOverlay.MoveRawLine(lines, 0, -1));
        Assert.Equal(
            lines,
            StudioRawSkinIniOverlay.RemoveRawLine(lines, 99));
    }

    [Fact]
    public void ExtrasCompositionReadinessRequiresACompleteVisualFamily()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "kumori-composition-readiness",
            Guid.NewGuid().ToString("N"));
        try
        {
            var workspace = new SkinDraftWorkspaceService(root);
            var draft = workspace.Create("Composition", "Kumori");
            var packages = new SkinPackageService(workspace);

            var empty = StudioExtrasCompositionSummary.Build(
                draft,
                packages.Materialize(draft.DraftId));
            Assert.True(empty.HasSkinIni);
            Assert.False(empty.HasVisualFamily);
            Assert.False(empty.ExportReady);

            draft = workspace.StageFile(
                draft.DraftId,
                "cursor.png",
                Convert.FromBase64String(
                    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJ"
                    + "AAAADUlEQVR42mP8z8BQDwAFgwJ/lv8+YQAAAABJRU5ErkJggg=="),
                null,
                "cursor");
            var ready = StudioExtrasCompositionSummary.Build(
                draft,
                packages.Materialize(draft.DraftId));

            Assert.True(ready.HasVisualFamily);
            Assert.True(ready.ExportReady);
            Assert.True(ready.FamilyCount > 0);
            Assert.Equal(1, ready.ImageFiles);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RawSkinIniDeleteMarkerIsCaseInsensitiveButNotSubstringBased()
    {
        var rendered = StudioRawSkinIniOverlay.ComposeRawText(
        [
            " <DELETE> ",
            "Value: <delete>",
            "",
        ], endedWithNewline: false);

        Assert.Equal("Value: <delete>\n", rendered);
    }

    [Fact]
    public void RawAndStructuredUnsavedBuffersRoundTripWithoutLosingUnknownContent()
    {
        var original = SkinIniDocument.ParseText(
            "; heading\r\n[General]\r\nName: Before\r\nUnknown: keep\r\n");
        var raw = StudioRawSkinIniOverlay.ComposeRawText(
            [
                "; heading",
                "[General]",
                "Name: During raw",
                "Unknown: keep",
            ],
            endedWithNewline: true);
        var switched = original.WithText(raw);

        switched.SetValue("General", "Author", "During structured");
        var rendered = switched.ToText();

        Assert.Contains("; heading", rendered);
        Assert.Contains("Name: During raw", rendered);
        Assert.Contains("Unknown: keep", rendered);
        Assert.Contains("Author: During structured", rendered);
        Assert.DoesNotContain("\n", rendered.Replace("\r\n", ""));
    }

    [Theory]
    [InlineData("General", "CursorRotate", "1", "cursor")]
    [InlineData("Colours", "Combo4", "255,0,0", "hitcircle")]
    [InlineData("Fonts", "ScorePrefix", "score", "score-0")]
    [InlineData("Mania", "NoteImage0", "mania-note1", "mania-note1")]
    [InlineData("General", "Name", "Kumori", null)]
    public void StructuredSkinIniContextLinksMapToWorkbenchComponents(
        string section,
        string key,
        string value,
        string? expected)
    {
        Assert.Equal(
            expected,
            StudioSkinIniOverlay.ContextComponent(section, key, value));
    }

    [Fact]
    public void DraftBrowserSearchesIdentityAndSourceAndUsesRecentOrder()
    {
        var older = new SkinDraftManifest
        {
            DraftId = Guid.NewGuid(),
            Name = "Cotton Candy",
            Creator = "Lorenzo",
            SourcePath = @"C:\skins\pink.osk",
            UpdatedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
        };
        var newer = new SkinDraftManifest
        {
            DraftId = Guid.NewGuid(),
            Name = "Midnight",
            Creator = "Kumori",
            SourcePath = @"C:\skins\dark.osk",
            UpdatedAt = DateTimeOffset.Parse("2026-02-01T00:00:00Z"),
        };

        Assert.Equal(
            [newer.DraftId, older.DraftId],
            StudioDraftBrowserOverlay.FilterDrafts([older, newer], "")
                .Select(draft => draft.DraftId));
        Assert.Equal(
            older.DraftId,
            Assert.Single(StudioDraftBrowserOverlay.FilterDrafts(
                [older, newer],
                "pink.osk")).DraftId);
        Assert.Equal(
            newer.DraftId,
            Assert.Single(StudioDraftBrowserOverlay.FilterDrafts(
                [older, newer],
                "Kumori")).DraftId);
    }

    [Theory]
    [InlineData("HitPosition", "402", SkinIniValueType.Integer)]
    [InlineData("Colour1", "255, 128, 0", SkinIniValueType.Rgb)]
    [InlineData("ColumnWidth", "30,30,30,30", SkinIniValueType.Text)]
    public void StructuredEditorInfersSafeManiaFieldValidation(
        string key,
        string value,
        SkinIniValueType expected)
    {
        var definition = StudioSkinIniOverlay.ManiaDefinition(key, value);

        Assert.Equal("Mania", definition.Section);
        Assert.Equal(key, definition.Key);
        Assert.Equal(expected, definition.Type);
    }

    [Theory]
    [InlineData(null, "none")]
    [InlineData("", "none")]
    [InlineData("1234567890abcdef", "1234567890ab")]
    [InlineData("short", "short")]
    public void ChangeReviewUsesStableCompactHashes(
        string? hash,
        string expected)
    {
        Assert.Equal(expected, StudioChangeReviewOverlay.ShortHash(hash));
    }

    [Fact]
    public void InstalledSkinBrowserSearchesNameAndCreator()
    {
        var pink = new LazerSkinInfo(
            Guid.NewGuid(),
            "Pink",
            "Lorenzo",
            []);
        var dark = new LazerSkinInfo(
            Guid.NewGuid(),
            "Dark",
            "Kumori",
            []);

        Assert.Equal(
            [dark.Id, pink.Id],
            StudioInstalledSkinBrowserOverlay.Filter([pink, dark], "")
                .Select(skin => skin.Id));
        Assert.Equal(
            pink.Id,
            Assert.Single(StudioInstalledSkinBrowserOverlay.Filter(
                [pink, dark],
            "Lorenzo")).Id);
    }

    [Fact]
    public void OpeningSkinChooserSearchesDraftsAndInstalledSkinsTogether()
    {
        var draft = new SkinDraftManifest
        {
            DraftId = Guid.NewGuid(),
            Name = "Cotton Candy Draft",
            Creator = "Kumori",
            UpdatedAt = DateTimeOffset.Parse("2026-03-01T00:00:00Z"),
        };
        var installed = new LazerSkinInfo(
            Guid.NewGuid(),
            "Midnight",
            "Lorenzo",
            []);

        var draftResult = StudioOpeningSkinOverlay.Filter(
            [draft],
            [installed],
            "Cotton");
        Assert.Single(draftResult.Drafts);
        Assert.Empty(draftResult.Installed);

        var installedResult = StudioOpeningSkinOverlay.Filter(
            [draft],
            [installed],
            "Lorenzo");
        Assert.Empty(installedResult.Drafts);
        Assert.Single(installedResult.Installed);
    }

    [Theory]
    [InlineData("SDL_app", 1200, 800, true, 1_000_960)]
    [InlineData("helper", 1200, 800, true, 960)]
    [InlineData("SDL_app", 1200, 800, false, 0)]
    [InlineData("SDL_app", 1, 1, true, 0)]
    public void EmbeddedNativeWindowDiscoveryPrefersVisibleSdlSurface(
        string className,
        int width,
        int height,
        bool visible,
        int expected)
    {
        Assert.Equal(
            expected,
            EmbeddedWindowActivationMonitor.WindowCandidateScore(
                className,
                width,
                height,
                visible));
    }

    [Theory]
    [InlineData(0x0021, true)]
    [InlineData(0x0201, false)]
    [InlineData(0x0204, false)]
    [InlineData(0x0207, false)]
    [InlineData(0x020B, false)]
    [InlineData(0x0200, false)]
    [InlineData(0x0202, false)]
    public void EmbeddedNativeWindowActivatesBeforeButtonDownMessages(
        uint message,
        bool expected)
    {
        Assert.Equal(
            expected,
            EmbeddedWindowActivationMonitor.IsPreButtonActivationMessage(
                message));
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public void StudioCursorIsVisibleOnlyWhileNativeWindowIsActive(
        bool active,
        float expected)
    {
        Assert.Equal(expected, KumoriSkinStudioGame.CursorAlpha(active));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void RendererOnlySurfaceUsesInteractiveSkinnedCursor(
        bool rendererOnly,
        bool expected)
    {
        Assert.Equal(
            expected,
            KumoriSkinStudioGame.UsesInteractiveRendererCursor(rendererOnly));
    }

    [Fact]
    public void StudioSceneDoesNotUseReplayPipeline()
    {
        using var player = new StudioScenePlayer();

        Assert.False(StudioScenePlayer.UsesReplayPipeline);
        Assert.False(typeof(osu.Game.Screens.Play.ReplayPlayer)
            .IsAssignableFrom(typeof(StudioScenePlayer)));
        Assert.Contains(
            typeof(StudioScenePlayer).GetFields(
                System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic),
            field => typeof(osu.Game.Screens.Play.Leaderboards.IGameplayLeaderboardProvider)
                         .IsAssignableFrom(field.FieldType)
                     && field.GetCustomAttributes(
                         typeof(osu.Framework.Allocation.CachedAttribute),
                         inherit: true).Length > 0);
        Assert.False(player.Configuration.ShowLeaderboard);
        Assert.False(player.Configuration.ShowResults);
        Assert.True(player.Configuration.AllowUserInteraction);
        Assert.False(player.Configuration.AllowPause);
        Assert.False(player.Configuration.AllowRestart);
        Assert.False(player.Configuration.AllowSkipping);
    }

    [Fact]
    public void GameplayOverviewContainsARealSliderAndTrimsPaletteCircles()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "kumori-skin-preview.osu");
        var beatmap = StudioWorkingBeatmap.DecodePreview(path, 3);

        Assert.Contains(
            beatmap.HitObjects,
            hitObject => hitObject.GetType().Name.Contains(
                             "Slider",
                             StringComparison.Ordinal)
                         && Math.Abs(
                             hitObject.StartTime
                             - StudioScenePlayer.ShowcaseMidSliderStartTime) < 1);
        var sliderReferences = beatmap.HitObjects
            .Where(hitObject => hitObject.GetType().Name.Contains(
                "Slider",
                StringComparison.Ordinal))
            .ToArray();
        Assert.Contains(sliderReferences, hitObject =>
            Math.Abs(hitObject.StartTime - StudioScenePlayer.StationarySliderStartTime) < 1);
        Assert.Contains(sliderReferences, hitObject =>
            Math.Abs(hitObject.StartTime - 2_500) < 1);
        Assert.Contains(sliderReferences, hitObject =>
            Math.Abs(
                hitObject.StartTime
                - StudioScenePlayer.ShowcaseWaitingSliderStartTime) < 1);
        Assert.False(StudioScenePlayer.AdvancesGameplay(
            SkinStudioPreviewScene.Showcase,
            motionRequested: true));
        Assert.True(StudioScenePlayer.AdvancesGameplay(
            SkinStudioPreviewScene.Sliders,
            motionRequested: true));
        Assert.True(StudioScenePlayer.AdvancesGameplay(
            SkinStudioPreviewScene.Cursor,
            motionRequested: true));
        Assert.Equal(
            3,
            beatmap.HitObjects.Count(hitObject =>
                hitObject.StartTime is >= StudioScenePlayer.ShowcasePaletteStartTime
                    and <= StudioScenePlayer.ShowcasePaletteEndTime
                && hitObject.GetType().Name.Contains(
                    "HitCircle",
                    StringComparison.Ordinal)));
    }

    [Fact]
    public void ShowcaseCursorPathIsSmoothAndClosed()
    {
        var start = StudioSceneCursorPath.PositionAt(
            SkinStudioPreviewScene.Showcase,
            StudioScenePlayer.ShowcaseCursorCycleStart);
        var end = StudioSceneCursorPath.PositionAt(
            SkinStudioPreviewScene.Showcase,
            StudioScenePlayer.ShowcaseCursorCycleEnd);
        var beforeJoin = StudioSceneCursorPath.PositionAt(
            SkinStudioPreviewScene.Showcase,
            StudioScenePlayer.ShowcaseCursorCycleEnd - 1);
        var afterJoin = StudioSceneCursorPath.PositionAt(
            SkinStudioPreviewScene.Showcase,
            StudioScenePlayer.ShowcaseCursorCycleStart + 1);

        Assert.True(Vector2.Distance(start, end) < 0.001f);
        Assert.True(Vector2.Distance(beforeJoin, afterJoin) < 5);
    }

    [Theory]
    [InlineData(100, 100, false, true)]
    [InlineData(100, 200, true, true)]
    [InlineData(100, 200, false, false)]
    [InlineData(0, 100, true, false)]
    [InlineData(100, 0, true, false)]
    public void EmbeddedCursorUsesTheNativeChildFocusWindow(
        int studioWindow,
        int focusedWindow,
        bool focusedWindowIsChild,
        bool expected)
    {
        Assert.Equal(
            expected,
            EmbeddedWindowActivationMonitor.FocusBelongsToStudio(
                studioWindow,
                focusedWindow,
                focusedWindowIsChild));
    }

    [Fact]
    public void OpeningChooserReservesNonOverlappingHeaderAndFooter()
    {
        Assert.True(StudioOpeningSkinOverlay.HeaderHeight > 0);
        Assert.True(StudioOpeningSkinOverlay.FooterHeight > 0);
        Assert.Equal(
            -266,
            -(StudioOpeningSkinOverlay.HeaderHeight
              + StudioOpeningSkinOverlay.FooterHeight));
    }

    [Theory]
    [InlineData(10_000, -5_000, 30_000, 5_000)]
    [InlineData(2_000, -5_000, 30_000, 0)]
    [InlineData(28_000, 5_000, 30_000, 30_000)]
    public void AudioTransportSeekIsBounded(
        double current,
        double delta,
        double length,
        double expected)
    {
        Assert.Equal(
            expected,
            StudioAudioTransportOverlay.ClampSeek(current, delta, length));
    }

    [Fact]
    public void AudioTransportAndInspectorUseDeterministicTimeAndWaveformText()
    {
        Assert.Equal(
            "01:05.432",
            StudioAudioTransportOverlay.FormatPosition(65_432));
        var analysis = new SkinAudioAnalysis(
            44_100,
            2,
            65_432,
            1,
            [0, 0.25f, 0.5f, 0.75f, 1]);

        var text = KumoriSkinStudioGame.FormatAudioAnalysis(analysis);

        Assert.Contains("44,100 Hz · 2 ch · 01:05.432 · peak 1.000", text);
        Assert.EndsWith("▁▃▅▆█", text);
    }

    [Fact]
    public void CustomBeatmapImportCopiesOnlyReferencedMediaIntoIsolatedStore()
    {
        var root = NewBeatmapTestDirectory();
        try
        {
            var source = Path.Combine(root, "source");
            Directory.CreateDirectory(Path.Combine(source, "audio"));
            Directory.CreateDirectory(Path.Combine(source, "art"));
            File.WriteAllBytes(Path.Combine(source, "audio", "song.wav"), [1, 2, 3]);
            File.WriteAllBytes(Path.Combine(source, "art", "bg.png"), [4, 5, 6]);
            var map = Path.Combine(source, "map.osu");
            File.WriteAllText(
                map,
                BeatmapText(
                    mode: 0,
                    audio: @"audio\song.wav",
                    background: @"art\bg.png"));

            var result = StudioBeatmapImportService.Prepare(
                map,
                Path.Combine(root, "isolated"));

            Assert.Equal(1, result.HitObjectCount);
            Assert.Equal(
                ["art\\bg.png", "audio\\song.wav"],
                result.CopiedMedia.Order(StringComparer.OrdinalIgnoreCase));
            Assert.Empty(result.MissingMedia);
            Assert.True(File.Exists(result.BeatmapPath));
            Assert.True(File.Exists(
                Path.Combine(Path.GetDirectoryName(result.BeatmapPath)!, "audio", "song.wav")));
            Assert.True(File.Exists(
                Path.Combine(Path.GetDirectoryName(result.BeatmapPath)!, "art", "bg.png")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void CustomBeatmapImportReportsMissingMediaWithoutLeavingIsolation()
    {
        var root = NewBeatmapTestDirectory();
        try
        {
            var map = Path.Combine(root, "map.osu");
            File.WriteAllText(
                map,
                BeatmapText(
                    mode: 0,
                    audio: "missing.mp3",
                    background: "missing.png"));

            var result = StudioBeatmapImportService.Prepare(
                map,
                Path.Combine(root, "isolated"));

            Assert.Empty(result.CopiedMedia);
            Assert.Equal(
                ["missing.mp3", "missing.png"],
                result.MissingMedia.Order(StringComparer.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void CustomBeatmapImportRejectsMalformedAndEmptyBeatmaps()
    {
        var root = NewBeatmapTestDirectory();
        try
        {
            var map = Path.Combine(root, "broken.osu");
            File.WriteAllText(map, "this is not an osu beatmap");

            Assert.ThrowsAny<InvalidDataException>(() =>
                StudioBeatmapImportService.Prepare(
                    map,
                    Path.Combine(root, "isolated")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void CustomBeatmapImportRejectsUnsupportedRulesets()
    {
        var root = NewBeatmapTestDirectory();
        try
        {
            var map = Path.Combine(root, "mania.osu");
            File.WriteAllText(map, BeatmapText(mode: 3));

            Assert.Throws<NotSupportedException>(() =>
                StudioBeatmapImportService.Prepare(
                    map,
                    Path.Combine(root, "isolated")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void CustomBeatmapImportRejectsMediaPathTraversal()
    {
        var root = NewBeatmapTestDirectory();
        try
        {
            var source = Path.Combine(root, "source");
            Directory.CreateDirectory(source);
            File.WriteAllBytes(Path.Combine(root, "outside.wav"), [1, 2, 3]);
            var map = Path.Combine(source, "map.osu");
            File.WriteAllText(
                map,
                BeatmapText(mode: 0, audio: @"..\outside.wav"));

            Assert.Throws<InvalidDataException>(() =>
                StudioBeatmapImportService.Prepare(
                    map,
                    Path.Combine(root, "isolated")));
            Assert.False(Directory.Exists(Path.Combine(root, "isolated"))
                         && Directory.EnumerateFiles(
                                 Path.Combine(root, "isolated"),
                                 "outside.wav",
                                 SearchOption.AllDirectories)
                             .Any());
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task LivePreviewReloadPipeQueuesOnlyTheOpaqueSkinId()
    {
        var expected = Guid.NewGuid();
        var queued = new TaskCompletionSource<Guid>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = new SkinStudioReloadPipeServer(queued.SetResult);

        var response = await Task.Run(() =>
            SkinStudioReloadPipeClient.Queue(server.PipeName, expected));

        Assert.True(
            response.Accepted,
            $"{response.Message} Server: {server.LastError}");
        var received = await queued.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(expected, received);
        Assert.Contains("foreground", response.Message);
    }

    [Fact]
    public void LaunchContractRejectsUntrustedReloadPipeNames()
    {
        var contract = new SkinStudioLaunchContract
        {
            WorkspacePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
            ReloadPipeName = @"..\some-other-pipe",
        };

        Assert.Throws<InvalidDataException>(contract.Normalize);
    }

    [Fact]
    public void MissingReloadPipeFailsClosedWithoutThrowing()
    {
        var result = SkinStudioReloadPipeClient.Queue(null, Guid.NewGuid());

        Assert.False(result.Accepted);
        Assert.Contains("manually", result.Message);
    }

    private static string NewBeatmapTestDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"kumori-native-beatmap-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string BeatmapText(
        int mode,
        string? audio = null,
        string? background = null)
    {
        var audioLine = $"AudioFilename:{audio ?? string.Empty}";
        var events = background is null
            ? string.Empty
            : $"0,0,\"{background}\",0,0";
        return $"""
                osu file format v14

                [General]
                {audioLine}
                Mode:{mode}

                [Metadata]
                Title:Kumori Import Test
                Artist:Kumori
                Creator:Kumori
                Version:Test
                BeatmapID:0
                BeatmapSetID:-1

                [Difficulty]
                HPDrainRate:5
                CircleSize:4
                OverallDifficulty:5
                ApproachRate:5
                SliderMultiplier:1.4
                SliderTickRate:1

                [Events]
                {events}

                [TimingPoints]
                0,500,4,2,1,70,1,0

                [HitObjects]
                256,192,1000,1,0,0:0:0:0:
                """;
    }

}
