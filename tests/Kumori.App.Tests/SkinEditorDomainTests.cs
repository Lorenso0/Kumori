using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Kumori.App.Skins;
using Kumori.Core;
using Kumori.Tracking;
using Xunit;

namespace Kumori.App.Tests;

public sealed class SkinEditorDomainTests
{
    [Fact]
    public void Color_chaos_randomizes_hitcircle_palette_and_both_slider_colours()
    {
        var colours = SkinEditorPage.ColorChaosIniColours(new Random(42));

        Assert.Equal(10, colours.Count);
        Assert.All(Enumerable.Range(1, 8), index =>
            Assert.True(colours.ContainsKey($"Combo{index}")));
        Assert.True(colours.ContainsKey("SliderBorder"));
        Assert.True(colours.ContainsKey("SliderTrackOverride"));
        Assert.All(colours.Values, value =>
        {
            var channels = value.Split(',').Select(int.Parse).ToArray();
            Assert.Equal(3, channels.Length);
            Assert.All(channels, channel => Assert.InRange(channel, 48, 255));
        });
    }

    [Fact]
    public void Draft_session_keeps_bytes_local_and_supports_undo_redo()
    {
        var session = new SkinDraftSession(Guid.NewGuid());
        var bytes = new byte[] { 1, 2, 3 };

        session.Stage("cursor.png", new string('a', 64), bytes, "cursor.png (recolour)");
        bytes[0] = 99;

        var change = Assert.Single(session.Changes);
        Assert.Equal(new byte[] { 1, 2, 3 }, change.Bytes);
        Assert.True(session.CanUndo);
        Assert.True(session.Undo());
        Assert.Empty(session.Changes);
        Assert.True(session.CanRedo);
        Assert.True(session.Redo());
        Assert.Single(session.Changes);
    }

    [Fact]
    public void Draft_session_preserves_uncommitted_entries_after_partial_apply()
    {
        var session = new SkinDraftSession(Guid.NewGuid());
        session.Stage("cursor.png", new string('a', 64), [1], "cursor");
        session.Stage("hitcircle.png", new string('b', 64), [2], "hitcircle");

        session.AcceptCommitted(["cursor.png"]);

        var remaining = Assert.Single(session.Changes);
        Assert.Equal("hitcircle.png", remaining.Filename);
        Assert.False(session.CanUndo);
    }

    [Fact]
    public void Draft_history_reuses_immutable_payloads_across_undo_steps()
    {
        var session = new SkinDraftSession(Guid.NewGuid());
        session.Stage("cursor.png", new string('a', 64), new byte[1024], "cursor");
        var stagedBytes = Assert.Single(session.Changes).Bytes;

        session.Stage("cursortrail.png", new string('b', 64), [1], "trail");
        Assert.True(session.Undo());

        Assert.Same(stagedBytes, Assert.Single(session.Changes).Bytes);
    }

    [Fact]
    public void Draft_session_tracks_element_deletion_as_an_undoable_operation()
    {
        var session = new SkinDraftSession(Guid.NewGuid());
        session.StageDeletion(
            "cursor.png",
            new string('a', 64),
            "cursor.png (delete)");

        var deletion = Assert.Single(session.Changes);
        Assert.True(deletion.IsDeletion);
        Assert.Equal(SkinDraftOperation.Delete, deletion.Operation);
        Assert.Empty(deletion.Bytes);

        Assert.True(session.Undo());
        Assert.Empty(session.Changes);
        Assert.True(session.Redo());
        Assert.True(Assert.Single(session.Changes).IsDeletion);
    }

    [Fact]
    public void Draft_action_can_be_discarded_as_one_undoable_history_step()
    {
        var session = new SkinDraftSession(Guid.NewGuid());
        session.StageRange([
            new SkinDraftChange("cursor.png", null, [1], "cursor", ActionId: "mix", ActionLabel: "Random mix"),
            new SkinDraftChange("cursortrail.png", null, [2], "trail", ActionId: "mix", ActionLabel: "Random mix"),
        ]);

        Assert.True(session.RemoveRange(session.Changes.Select(change => change.Filename)));
        Assert.Empty(session.Changes);
        Assert.True(session.Undo());
        Assert.Equal(2, session.Changes.Count);
        Assert.All(session.Changes, change => Assert.Equal("Random mix", change.GroupLabel));
    }

    [Fact]
    public void File_replacement_keeps_the_target_source_filename()
    {
        var target = File("cursor.png");

        var replacement = SkinFileReplacementPlanner.Build(
            target,
            @"C:\incoming\pink-glow.png",
            [1, 2, 3]);

        Assert.Equal("cursor.png", replacement.Filename);
        Assert.Equal(target.Hash, replacement.ExpectedHash);
        Assert.Equal([1, 2, 3], replacement.Bytes);
        Assert.Contains("pink-glow.png", replacement.Description);
    }

    [Fact]
    public void Extras_cursor_pack_replaces_cursor_and_deletes_every_unsupplied_cursor_file()
    {
        var current = new[]
        {
            File("cursor.png"),
            File("cursor@2x.png"),
            File("cursortrail.png"),
            File("cursormiddle.png"),
            File("hitcircle.png"),
        };
        var incoming = new[]
        {
            new SkinExtraPackFile("cursor.png", [1, 2, 3]),
            new SkinExtraPackFile("cursortrail@2x.png", [4, 5, 6]),
        };

        var changes = SkinExtraPackPlanner.BuildChanges(
            "Cursor",
            current,
            incoming,
            "Green");

        Assert.Contains(changes, change =>
            change.Filename == "cursor.png"
            && !change.IsDeletion
            && change.ExpectedHash == current[0].Hash);
        Assert.Contains(changes, change =>
            change.Filename == "cursortrail@2x.png"
            && !change.IsDeletion
            && change.ExpectedHash is null);
        Assert.Contains(changes, change =>
            change.Filename == "cursormiddle.png" && change.IsDeletion);
        Assert.Contains(changes, change =>
            change.Filename == "cursor@2x.png" && change.IsDeletion);
        Assert.Contains(changes, change =>
            change.Filename == "cursortrail.png" && change.IsDeletion);
        Assert.DoesNotContain(changes, change => change.Filename == "hitcircle.png");
    }

    [Fact]
    public void Replacing_category_drafts_is_one_undoable_history_step()
    {
        var session = new SkinDraftSession(Guid.NewGuid());
        session.Stage("cursor-old.png", null, [1], "old cursor draft");

        session.ReplaceWhere(
            change => SkinElementCategorizer.CategoryFor(change.Filename) == "Cursor",
            [
                new SkinDraftChange("cursor.png", null, [2], "new cursor"),
                new SkinDraftChange(
                    "cursormiddle.png",
                    new string('a', 64),
                    [],
                    "delete middle",
                    SkinDraftOperation.Delete),
            ]);

        Assert.DoesNotContain(session.Changes, change => change.Filename == "cursor-old.png");
        Assert.Equal(2, session.Count);
        Assert.True(session.Undo());
        Assert.Equal("cursor-old.png", Assert.Single(session.Changes).Filename);
    }

    [Fact]
    public void Rich_editor_metadata_keeps_schema_order_and_classifies_visual_groups()
    {
        var ordered = SkinIniSchema.Sections().SelectMany(section => section.Keys).ToArray();
        var rendered = ordered.Select(definition => (definition, SkinIniRichEditor.Describe(definition))).ToArray();

        Assert.Equal(ordered, rendered.Select(item => item.definition));
        Assert.Equal(SkinIniPreviewKind.ComboPalette,
            SkinIniRichEditor.Describe(SkinIniSchema.Colours[0]).Preview);
        Assert.Equal(SkinIniVisualGroup.Slider,
            SkinIniRichEditor.Describe(SkinIniSchema.Colours.Single(key => key.Key == "SliderBorder")).Group);
        Assert.Equal(SkinIniPreviewKind.Catch,
            SkinIniRichEditor.Describe(SkinIniSchema.CatchTheBeat[0]).Preview);
    }

    [Theory]
    [InlineData("hitcircle.png", "Hit objects")]
    [InlineData("sliderb0.png", "Sliders")]
    [InlineData("star2.png", "HUD & interface")]
    [InlineData("cursortrail.png", "Cursor")]
    [InlineData("scorebar-bg.png", "HUD & interface")]
    [InlineData("mania-note1.png", "Modes & other")]
    public void Semantic_element_groups_preserve_existing_category_mapping(string filename, string expectedGroup)
    {
        var category = SkinElementCategorizer.CategoryFor(filename);

        Assert.Equal(expectedGroup, SkinElementSemanticGroups.ForCategory(category).Name);
    }

    [Theory]
    [InlineData("default-0.png")]
    [InlineData("score-9@2x.png")]
    [InlineData("combo-3.png")]
    [InlineData("scoreentry-7.png")]
    public void Number_font_digits_are_kept_in_the_numbers_category(string filename)
    {
        Assert.Equal("Numbers", SkinElementCategorizer.CategoryFor(filename));
    }

    [Theory]
    [InlineData("hitcircle@2x.png", (int)SkinElementCompositionKind.HitObject)]
    [InlineData("approachcircle.png", (int)SkinElementCompositionKind.HitObject)]
    [InlineData("followpoint-3@2x.png", (int)SkinElementCompositionKind.Followpoints)]
    [InlineData("sliderb0.png", (int)SkinElementCompositionKind.Slider)]
    [InlineData("cursortrail.png", (int)SkinElementCompositionKind.Cursor)]
    [InlineData("spinner-top.png", (int)SkinElementCompositionKind.Spinner)]
    [InlineData("default-7.png", (int)SkinElementCompositionKind.Numbers)]
    [InlineData("scorebar-bg.png", (int)SkinElementCompositionKind.Scorebar)]
    [InlineData("ranking-S.png", (int)SkinElementCompositionKind.Context)]
    public void Element_preview_selects_the_matching_whole_element_composition(
        string filename,
        int expected)
    {
        Assert.Equal(
            (SkinElementCompositionKind)expected,
            SkinEditorPage.CompositionKindFor(filename));
    }

    [Theory]
    [InlineData("default-0", "default", "0")]
    [InlineData("score-9", "score", "9")]
    [InlineData("combo-x", "combo", "x")]
    [InlineData("score-percent", "score", "percent")]
    public void Number_preview_recognizes_every_number_font_prefix(
        string stem,
        string expectedPrefix,
        string expectedSuffix)
    {
        var parts = SkinEditorPage.NumberGlyphParts(stem);

        Assert.NotNull(parts);
        Assert.Equal(expectedPrefix, parts.Value.Prefix);
        Assert.Equal(expectedSuffix, parts.Value.Suffix);
    }

    [Fact]
    public void Number_preview_rejects_non_glyph_assets()
    {
        Assert.Null(SkinEditorPage.NumberGlyphParts("scorebar-bg"));
        Assert.Null(SkinEditorPage.NumberGlyphParts("ranking-panel"));
    }

    [Fact]
    public void Slider_composition_geometry_aligns_assets_to_the_rendered_path()
    {
        var geometry = SkinEditorPage.SliderCompositionGeometryFor();

        Assert.Equal(590, geometry.BodyWidth);
        Assert.InRange(geometry.BodyHeight, 220, 222);
        Assert.InRange(geometry.Start.X, 118, 120);
        Assert.InRange(geometry.Start.Y, 259, 261);
        Assert.InRange(geometry.End.X, 523, 525);
        Assert.InRange(geometry.End.Y, 203, 206);
        Assert.InRange(geometry.CircleDiameter, 67, 69);
        Assert.InRange(geometry.BallDiameter, 67, 69);
        Assert.InRange(geometry.FollowDiameter, 104, 107);
        Assert.InRange(geometry.ReverseDiameter, 55, 57);
        Assert.True(Math.Abs(geometry.ReverseRotation) > 140);
    }

    [Theory]
    [InlineData((int)SkinElementCompositionKind.HitObject, true)]
    [InlineData((int)SkinElementCompositionKind.Slider, true)]
    [InlineData((int)SkinElementCompositionKind.Cursor, true)]
    [InlineData((int)SkinElementCompositionKind.Spinner, true)]
    [InlineData((int)SkinElementCompositionKind.Scorebar, true)]
    [InlineData((int)SkinElementCompositionKind.Followpoints, false)]
    [InlineData((int)SkinElementCompositionKind.Numbers, false)]
    [InlineData((int)SkinElementCompositionKind.Context, false)]
    public void Fixed_element_scenes_are_cached_between_layer_selections(
        int kind,
        bool expected)
    {
        Assert.Equal(
            expected,
            SkinEditorPage.IsCacheableElementComposition(
                (SkinElementCompositionKind)kind));
    }

    [Theory]
    [InlineData(false, false, 0)]
    [InlineData(false, true, 2)]
    [InlineData(true, false, 1)]
    [InlineData(true, true, 1)]
    public void Spinner_preview_matches_osu_legacy_style_precedence(
        bool hasBackground,
        bool hasTop,
        int expected)
    {
        Assert.Equal((LegacySpinnerPreviewStyle)expected, LegacySpinnerPreview.Resolve(hasBackground, hasTop));
    }

    [Fact]
    public void Categorize_orders_root_families_and_groups_subfolders()
    {
        var files = new[]
        {
            File("z/extra.png"),
            File("cursor@2x.png"),
            File("hit300-0.png"),
            File("scorebar-bg.png"),
            File("normal-hitnormal.wav"),
            File("skin.ini"),
        };

        var categories = SkinElementCategorizer.Categorize(files);

        Assert.Equal(
            ["Cursor", "Judgements", "Scorebar", "Sounds", "z"],
            categories.Select(category => category.Name));
        Assert.True(categories[^1].IsSubfolder);
        Assert.DoesNotContain(categories.SelectMany(category => category.Files),
            entry => entry.Filename == "skin.ini");
    }

    [Fact]
    public void Categorize_combines_standard_and_2x_files_and_prefers_2x()
    {
        var categories = SkinElementCategorizer.Categorize(
        [
            File("approachcircle.png"),
            File("approachcircle@2x.png"),
        ]);

        var entry = Assert.Single(Assert.Single(categories).Files);
        Assert.Equal("approachcircle@2x.png", entry.Filename);
        Assert.True(entry.HasPairedResolution);
        Assert.Equal("approachcircle.png", Assert.Single(entry.ResolutionVariants).Filename);
        Assert.Contains("1× + 2×", entry.ResolutionVariantLabel);
    }

    [Fact]
    public void Logical_element_synchronizes_recolor_edits_to_both_resolutions()
    {
        var categories = SkinElementCategorizer.Categorize(
        [
            File("cursor.png"),
            File("cursor@2x.png"),
        ]);
        var entry = Assert.Single(Assert.Single(categories).Files);
        entry.Mode = SkinRecolorMode.HueSaturation;
        entry.HueShiftDegrees = 75;
        entry.SaturationMultiplier = 1.4;
        entry.LightnessMultiplier = 0.8;

        entry.SynchronizeEditsToVariants();

        var standard = Assert.Single(entry.ResolutionVariants);
        Assert.Equal(entry.Mode, standard.Mode);
        Assert.Equal(75, standard.HueShiftDegrees);
        Assert.Equal(1.4, standard.SaturationMultiplier);
        Assert.Equal(0.8, standard.LightnessMultiplier);
        Assert.True(standard.HasEdits);
    }

    [Fact]
    public void Visible_pixel_detection_rejects_fully_transparent_placeholders()
    {
        Assert.False(SkinImageTools.HasVisiblePixels(
        [
            255, 255, 255, 0,
            0, 0, 0, 0,
        ]));
        Assert.True(SkinImageTools.HasVisiblePixels(
        [
            0, 0, 0, 0,
            255, 255, 255, 1,
        ]));
    }

    [Fact]
    public void Transparent_replacement_preserves_requested_png_dimensions()
    {
        var bytes = SkinImageTools.CreateTransparentPng(32, 48);
        var image = SkinImageTools.Decode(bytes);

        Assert.Equal(32, image.PixelWidth);
        Assert.Equal(48, image.PixelHeight);
        Assert.True(SkinImageTools.IsFullyTransparentImage(bytes));
    }

    [Fact]
    public void Preview_decode_downsamples_large_images_without_enlarging_small_assets()
    {
        var tiny = SkinImageTools.ToBitmap([255, 255, 255, 255], 1, 1, 4);
        var tinyDecoded = SkinImageTools.Decode(
            SkinImageTools.Encode(tiny, "tiny.png"),
            160);
        Assert.Equal(1, tinyDecoded.PixelWidth);
        Assert.Equal(1, tinyDecoded.PixelHeight);

        var large = SkinImageTools.ToBitmap(new byte[320 * 40 * 4], 320, 40, 320 * 4);
        var largeDecoded = SkinImageTools.Decode(
            SkinImageTools.Encode(large, "large.png"),
            160);
        Assert.Equal(160, largeDecoded.PixelWidth);
        Assert.Equal(20, largeDecoded.PixelHeight);
    }

    [Fact]
    public void Extras_two_x_upscaler_doubles_image_dimensions()
    {
        var source = SkinImageTools.ToBitmap(
        [
            0, 0, 255, 255,
            0, 255, 0, 255,
        ], 2, 1, 8);

        var upscaled = SkinImageTools.Decode(
            SkinImageTools.Upscale2X(
                SkinImageTools.Encode(source, "cursor.png"),
                "cursor@2x.png"));

        Assert.Equal(4, upscaled.PixelWidth);
        Assert.Equal(2, upscaled.PixelHeight);
        Assert.True(SkinImageTools.HasVisiblePixels(SkinImageTools.Pixels(upscaled, out _)));
    }

    [Fact]
    public void Extras_resolution_planner_finds_only_selected_one_x_conflicts()
    {
        var mismatches = SkinExtraResolutionPlanner.FindMismatches(
        [
            "cursor@2x.png",
            "cursortrail@2x.png",
            "hitcircle@2x.png",
        ],
        [
            "cursor.png",
            "cursortrail.png",
            "cursortrail@2x.png",
        ]);

        var mismatch = Assert.Single(mismatches);
        Assert.Equal("cursor.png", mismatch.OneXFilename);
        Assert.Equal("cursor@2x.png", mismatch.ExistingTwoXFilename);
    }

    [Fact]
    public void Successful_skin_batch_projects_new_files_into_the_current_view()
    {
        var skin = new LazerSkinInfo(
            Guid.NewGuid(),
            "Test",
            "Creator",
        [
            File("cursor.png"),
            File("cursortrail.png"),
        ]);
        LazerSkinBatchMutation[] mutations =
        [
            new("cursor.png", [1, 2, 3], skin.Files[0].Hash),
            new("cursortrail.png", [], skin.Files[1].Hash, IsDeletion: true),
            new("cursormiddle.png", [4, 5], null),
        ];
        var result = new LazerSkinBatchWriteResult(
            true,
        [
            new LazerSkinWriteResult(LazerSkinWriteStatus.Replaced, new string('c', 64)),
            new LazerSkinWriteResult(LazerSkinWriteStatus.Deleted, skin.Files[1].Hash),
            new LazerSkinWriteResult(LazerSkinWriteStatus.Added, new string('d', 64)),
        ]);

        var refreshed = SkinEditorCatalogProjection.ApplyBatch(skin, mutations, result);

        Assert.Equal(2, refreshed.Files.Count);
        Assert.Equal(new string('c', 64), refreshed.Files.Single(file =>
            file.Filename == "cursor.png").Hash);
        Assert.Equal(3, refreshed.Files.Single(file =>
            file.Filename == "cursor.png").SizeBytes);
        Assert.Contains(refreshed.Files, file => file.Filename == "cursormiddle.png");
        Assert.DoesNotContain(refreshed.Files, file => file.Filename == "cursortrail.png");
    }

    [Fact]
    public void SkinIni_round_trips_unknown_comments_and_repeated_mania_sections()
    {
        const string source =
            "// header\r\n[General]\r\nName: Example\r\nUnknownFuture: keep\r\n\r\n"
            + "[Mania]\r\nKeys: 4\r\nColumnStart: 100\r\n[Mania]\r\nKeys: 7\r\n";
        var document = SkinIniDocument.Parse(Encoding.UTF8.GetBytes(source));

        document.SetValue("General", "Name", "Edited");
        document.SetValue("Colours", "Combo1", "1,2,3");

        var result = document.ToText();
        Assert.Contains("// header\r\n", result);
        Assert.Contains("UnknownFuture: keep", result);
        Assert.Equal(2, result.Split("[Mania]").Length - 1);
        Assert.Contains("Name: Edited", result);
        Assert.Contains("[Colours]\r\nCombo1: 1,2,3", result);
    }

    [Fact]
    public void SkinIni_repeated_keys_use_the_last_value_like_osu()
    {
        var document = SkinIniDocument.ParseText(
            "[Fonts]\nComboPrefix: score\n"
            + "[Fonts]\nComboPrefix: default\n");

        Assert.Equal("default", document.GetValue("Fonts", "ComboPrefix"));

        document.SetValue("Fonts", "ComboPrefix", "combo");

        Assert.Equal("combo", document.GetValue("Fonts", "ComboPrefix"));
        Assert.Contains("ComboPrefix: score", document.ToText());
        Assert.EndsWith("ComboPrefix: combo\n", document.ToText());
    }

    [Theory]
    [InlineData("255,0,128", true)]
    [InlineData("256,0,0", false)]
    [InlineData("1,2", false)]
    public void SkinIni_validates_rgb_channels(string value, bool expected)
    {
        var definition = SkinIniSchema.Colours[0];
        Assert.Equal(expected, SkinIniDocument.TryValidate(definition, value, out _));
    }

    [Fact]
    public void Recolor_operations_preserve_alpha()
    {
        var colorized = new byte[] { 20, 40, 80, 123 };
        var tinted = (byte[])colorized.Clone();
        var shifted = (byte[])colorized.Clone();

        SkinImageTools.ApplyColorize(colorized, Color.FromRgb(255, 0, 0));
        SkinImageTools.ApplyTint(tinted, Color.FromRgb(0, 255, 0));
        SkinImageTools.ApplyHueSaturation(shifted, 90, 1.2, 0.8);

        Assert.Equal(123, colorized[3]);
        Assert.Equal(123, tinted[3]);
        Assert.Equal(123, shifted[3]);
        Assert.NotEqual(new byte[] { 20, 40, 80 }, colorized[..3]);
        Assert.NotEqual(new byte[] { 20, 40, 80 }, tinted[..3]);
        Assert.NotEqual(new byte[] { 20, 40, 80 }, shifted[..3]);
    }

    [Fact]
    public void Colorize_white_makes_every_visible_pixel_white_and_preserves_alpha()
    {
        var pixels = new byte[]
        {
            20, 40, 80, 123,
            10, 15, 25, 1,
            7, 8, 9, 0,
        };

        SkinImageTools.ApplyColorize(pixels, Colors.White);

        Assert.Equal(new byte[] { 255, 255, 255, 123 }, pixels[..4]);
        Assert.Equal(new byte[] { 255, 255, 255, 1 }, pixels[4..8]);
        Assert.Equal(new byte[] { 7, 8, 9, 0 }, pixels[8..12]);
    }

    [Fact]
    public void Extras_element_tinting_recolours_only_the_selected_logical_element()
    {
        var cursor = new SkinExtraPackFile(
            "cursor.png",
            SkinImageTools.Encode(
                SkinImageTools.ToBitmap([20, 40, 80, 123], 1, 1, 4),
                "cursor.png"));
        var trail = new SkinExtraPackFile(
            "cursortrail.png",
            SkinImageTools.Encode(
                SkinImageTools.ToBitmap([30, 50, 90, 200], 1, 1, 4),
                "cursortrail.png"));

        var result = SkinExtraElementTinting.Apply(
            "osu.cursor",
            [cursor, trail],
            new Dictionary<string, SkinRgb>(StringComparer.OrdinalIgnoreCase)
            {
                ["cursor"] = new SkinRgb(255, 0, 0),
            });

        Assert.NotSame(cursor, result[0]);
        Assert.Same(trail, result[1]);
        var pixels = SkinImageTools.Pixels(
            SkinImageTools.Decode(result[0].Bytes),
            out _);
        Assert.Equal(new byte[] { 0, 0, 255, 123 }, pixels);
    }

    [Fact]
    public void Cursor_extras_use_the_expected_collection_hierarchy()
    {
        Assert.Equal(
        [
            "Cursors with cursortrail",
            "Cursors with long cursortrail",
            "Cursors without cursortrail",
        ], SkinExtrasCatalog.CursorCollections);
    }

    [Fact]
    public void Cursormiddle_preview_uses_the_shared_dense_trail_path()
    {
        var classic = SkinExtrasPickerWindow.BuildTrailPoints(continuous: false);
        var continuous = SkinExtrasPickerWindow.BuildTrailPoints(continuous: true);

        Assert.Equal(10, classic.Count);
        Assert.Equal(31, continuous.Count);
        Assert.Equal(classic[0].X, continuous[0].X, precision: 6);
        Assert.Equal(classic[0].Y, continuous[0].Y, precision: 6);
        Assert.Equal(classic[^1].X, continuous[^1].X, precision: 6);
        Assert.Equal(classic[^1].Y, continuous[^1].Y, precision: 6);
        Assert.True(continuous[1].X - continuous[0].X
                    < classic[1].X - classic[0].X);
    }

    [Theory]
    [InlineData("osu.hitcircles", 380, 210)]
    [InlineData("osu.slider", 380, 210)]
    [InlineData("interface.background", 380, 210)]
    [InlineData("osu.cursor", 640, 480)]
    public void Extras_preview_canvas_matches_the_family_coordinate_system(
        string familyId,
        double expectedWidth,
        double expectedHeight)
    {
        var dimensions = SkinExtrasPickerWindow.PreviewCanvasDimensions(familyId);

        Assert.Equal(expectedWidth, dimensions.Width);
        Assert.Equal(expectedHeight, dimensions.Height);
    }

    [Fact]
    public void Extras_library_scroll_accumulates_small_steps_without_runaway_queueing()
    {
        const double current = 300;
        var target = ExtrasLibraryScrollPhysics.TargetOffset(
            current,
            current,
            120,
            1000);
        Assert.Equal(248, target);

        for (var index = 0; index < 10; index++)
        {
            target = ExtrasLibraryScrollPhysics.TargetOffset(
                current,
                target,
                120,
                1000);
        }

        Assert.Equal(80, target);
        Assert.Equal(
            0,
            ExtrasLibraryScrollPhysics.TargetOffset(20, 20, 120, 1000));
        Assert.Equal(
            1000,
            ExtrasLibraryScrollPhysics.TargetOffset(980, 980, -120, 1000));
    }

    [Fact]
    public void Extras_library_scroll_moves_continuously_toward_its_target()
    {
        var firstFrame = ExtrasLibraryScrollPhysics.NextOffset(300, 200, 1d / 60);
        var secondFrame = ExtrasLibraryScrollPhysics.NextOffset(firstFrame, 200, 1d / 60);

        Assert.InRange(firstFrame, 200.01, 299.99);
        Assert.InRange(secondFrame, 200.01, firstFrame - 0.01);
        Assert.True(
            ExtrasLibraryScrollPhysics.IsSettled(200.2, 200));
        Assert.False(
            ExtrasLibraryScrollPhysics.IsSettled(firstFrame, 200));
    }

    [Fact]
    public void Cursor_compare_treats_missing_or_transparent_cursormiddle_as_absent()
    {
        var transparent = SkinImageTools.ToBitmap([0, 0, 0, 0], 1, 1, 4);
        var legacyOpaquePlaceholder = SkinImageTools.ToBitmap(
            [255, 255, 255, 255],
            1,
            1,
            4);
        var alphaNoise = SkinImageTools.ToBitmap([255, 255, 255, 1], 1, 1, 4);
        var visible = SkinImageTools.ToBitmap(
            [
                255, 255, 255, 255,
                255, 255, 255, 255,
            ],
            2,
            1,
            8);

        Assert.False(SkinExtrasPickerWindow.HasVisibleCursorMiddle(null));
        Assert.False(SkinExtrasPickerWindow.HasVisibleCursorMiddle(transparent));
        Assert.False(SkinExtrasPickerWindow.HasVisibleCursorMiddle(legacyOpaquePlaceholder));
        Assert.False(SkinExtrasPickerWindow.HasVisibleCursorMiddle(alphaNoise));
        Assert.True(SkinExtrasPickerWindow.HasVisibleCursorMiddle(visible));
        Assert.False(SkinExtrasPickerWindow.UsesSmoothCursorTrail(null));
        Assert.True(SkinExtrasPickerWindow.UsesSmoothCursorTrail(transparent));
        Assert.True(SkinExtrasPickerWindow.UsesSmoothCursorTrail(legacyOpaquePlaceholder));
        Assert.True(SkinExtrasPickerWindow.UsesSmoothCursorTrail(visible));
        Assert.True(SkinExtrasPickerWindow.IsSmoothTrailPlaceholder(transparent));
        Assert.False(SkinExtrasPickerWindow.IsSmoothTrailPlaceholder(legacyOpaquePlaceholder));
        Assert.False(SkinExtrasPickerWindow.IsSmoothTrailPlaceholder(alphaNoise));
        Assert.False(SkinExtrasPickerWindow.IsSmoothTrailPlaceholder(visible));
        Assert.Equal(
            10,
            SkinExtrasPickerWindow.BuildTrailPoints(
                SkinExtrasPickerWindow.UsesSmoothCursorTrail(null)).Count);
        Assert.Equal(
            31,
            SkinExtrasPickerWindow.BuildTrailPoints(
                SkinExtrasPickerWindow.UsesSmoothCursorTrail(transparent)).Count);
        Assert.Equal(
            31,
            SkinExtrasPickerWindow.BuildTrailPoints(
                SkinExtrasPickerWindow.UsesSmoothCursorTrail(
                    legacyOpaquePlaceholder)).Count);
    }

    [Theory]
    [InlineData("cursor.png", true)]
    [InlineData("cursor@2x.png", true)]
    [InlineData(@"Extras\Cursors\cursor.png", false)]
    [InlineData("Extras/Cursors/cursortrail@2x.png", false)]
    public void Compare_only_treats_root_skin_files_as_active_gameplay_assets(
        string filename,
        bool expected)
    {
        Assert.Equal(expected, SkinExtrasPickerWindow.IsRootSkinFile(filename));
    }

    [Fact]
    public void Shared_cursor_resolver_ignores_nested_assets_and_prefers_root_2x()
    {
        var resolved = SkinCursorPreview.Resolve(
        [
            @"Extras\Cursors\cursor.png",
            "cursor.png",
            "cursor@2x.png",
            @"archive\cursortrail@2x.png",
            "cursortrail.png",
            "cursormiddle.png",
        ]);

        Assert.Equal("cursor@2x.png", resolved.CursorFilename);
        Assert.Equal("cursortrail.png", resolved.TrailFilename);
        Assert.Equal("cursormiddle.png", resolved.MiddleFilename);
        Assert.True(resolved.UsesSmoothTrail);
    }

    [Fact]
    public void Shared_cursor_composition_is_the_complete_render_contract()
    {
        var classic = SkinCursorPreview.Compose(
            hasCursor: true,
            hasTrail: true,
            hasMiddle: false,
            renderMiddle: false);
        var smooth = SkinCursorPreview.Compose(
            hasCursor: true,
            hasTrail: true,
            hasMiddle: true,
            renderMiddle: false);

        Assert.Equal(11, classic.Count);
        Assert.Equal(32, smooth.Count);
        Assert.Equal(
            SkinCursorPreviewLayerKind.Cursor,
            Assert.Single(classic.Where(layer =>
                layer.Kind == SkinCursorPreviewLayerKind.Cursor)).Kind);
        Assert.All(
            classic.Where(layer => layer.Kind == SkinCursorPreviewLayerKind.Trail),
            layer => Assert.Equal(110, layer.MaxWidth));
        Assert.All(
            classic.Where(layer => layer.Kind == SkinCursorPreviewLayerKind.Trail),
            layer => Assert.Equal(1, layer.Opacity));
        Assert.All(
            smooth.Where(layer => layer.Kind == SkinCursorPreviewLayerKind.Trail),
            layer => Assert.Equal(52, layer.MaxWidth));
    }

    [Fact]
    public void Duplicate_export_writes_a_complete_osk_with_relative_skin_paths()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"kumori-osk-{Guid.NewGuid():N}");
        try
        {
            var path = SkinOskPackage.Export(
                directory,
                "Test: copy",
                [
                    new LazerSkinImportFile("skin.ini", [1, 2]),
                    new LazerSkinImportFile(@"nested\cursor.png", [3, 4]),
                ],
                new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero));

            Assert.Equal(".osk", Path.GetExtension(path));
            using var archive = System.IO.Compression.ZipFile.OpenRead(path);
            Assert.Equal(
                new[] { "nested/cursor.png", "skin.ini" },
                archive.Entries.Select(entry => entry.FullName).Order().ToArray());
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Studio_cursor_preview_uses_dense_points_only_when_middle_exists()
    {
        var classic = SkinEditorPage.BuildCursorCompositionTrailPoints(smooth: false);
        var smooth = SkinEditorPage.BuildCursorCompositionTrailPoints(smooth: true);

        Assert.Equal(10, classic.Count);
        Assert.Equal(31, smooth.Count);
        Assert.Equal(classic[0], smooth[0]);
        Assert.Equal(classic[^1], smooth[^1]);
        Assert.True(smooth[1].X - smooth[0].X
                    < classic[1].X - classic[0].X);
    }

    [Fact]
    public void Extras_logical_elements_keep_resolutions_and_animation_frames_together()
    {
        var cursorFiles = new[] { "cursor.png", "cursor@2x.png", "cursortrail.png" };
        Assert.Equal(
            SkinExtraLogicalGrouping.Key("osu.cursor", "cursor.png", cursorFiles),
            SkinExtraLogicalGrouping.Key("osu.cursor", "cursor@2x.png", cursorFiles));

        var frames = new[] { "followpoint-0.png", "followpoint-1.png", "followpoint-2@2x.png" };
        Assert.All(
            frames,
            filename => Assert.Equal(
                "followpoint",
                SkinExtraLogicalGrouping.Key("osu.followpoints", filename, frames)));

        var judgements = new[] { "hit50.png", "hit100.png", "hit300.png" };
        Assert.Equal(
            "hit100",
            SkinExtraLogicalGrouping.Key("osu.hitbursts", "hit100.png", judgements));
    }

    [Fact]
    public void Followpoint_staging_restores_missing_timing_frames_as_transparent_pngs()
    {
        var completed = SkinFollowpointSequence.CompleteWithTransparentFrames(
        [
            new SkinExtraPackFile("followpoint-1.png", [1]),
            new SkinExtraPackFile("followpoint-3@2x.png", [3]),
        ]);

        Assert.Equal(4, completed.Count);
        var frameZero = Assert.Single(completed, file =>
            file.Filename == "followpoint-0.png");
        var frameTwo = Assert.Single(completed, file =>
            file.Filename == "followpoint-2.png");
        foreach (var frame in new[] { frameZero, frameTwo })
        {
            var bitmap = SkinImageTools.Decode(frame.Bytes);
            Assert.Equal(1, bitmap.PixelWidth);
            Assert.Equal(1, bitmap.PixelHeight);
            Assert.False(SkinImageTools.HasVisiblePixels(
                SkinImageTools.Pixels(bitmap, out _)));
        }
    }

    [Fact]
    public void Followpoint_selection_includes_hidden_transparent_manifest_frames()
    {
        var manifest = new SkinExtraPackManifest
        {
            Id = "followpoints",
            DisplayName = "Followpoints",
            FamilyId = "osu.followpoints",
            Area = "osu!",
            FamilyName = "Followpoints",
            Fingerprint = new string('a', 64),
            Files =
            [
                new SkinExtraManifestFile(
                    "followpoint-0.png",
                    "followpoint-0.png",
                    "followpoint-0.png",
                    "zero",
                    "zero",
                    "transparent"),
                new SkinExtraManifestFile(
                    "followpoint-1.png",
                    "followpoint-1.png",
                    "followpoint-1.png",
                    "one",
                    "visible"),
            ],
        };
        var selected = new HashSet<string>(
            ["followpoint-1.png"],
            StringComparer.OrdinalIgnoreCase);

        SkinFollowpointSequence.IncludeTransparentManifestFrames(
            manifest,
            selected);

        Assert.Contains("followpoint-0.png", selected);
    }

    [Fact]
    public void Logical_element_selection_replaces_only_stale_files_in_selected_elements()
    {
        var replaced = SkinExtraLogicalSelectionPlanner.FindReplacedCurrentFiles(
            "osu.followpoints",
            [
                "followpoint-0.png",
                "followpoint-1.png",
                "followpoint-2@2x.png",
                "hitcircle.png",
            ],
            [
                "followpoint-0.png",
                "followpoint-1.png",
            ]);

        Assert.Equal(["followpoint-2@2x.png"], replaced);
    }

    [Fact]
    public void Followpoint_replacement_has_no_frame_number_ceiling()
    {
        var veryHighFrame = "followpoint-" + new string('9', 100) + ".png";
        var replaced = SkinExtraLogicalSelectionPlanner.FindReplacedCurrentFiles(
            "osu.followpoints",
            [
                "followpoint-0.png",
                "followpoint-48.png",
                veryHighFrame,
                "hitcircle.png",
            ],
            ["followpoint-0.png"]);

        Assert.Equal(
            ["followpoint-48.png", veryHighFrame],
            replaced,
            StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Logical_element_selection_replaces_the_other_resolution_but_not_neighbours()
    {
        var replaced = SkinExtraLogicalSelectionPlanner.FindReplacedCurrentFiles(
            "osu.cursor",
            [
                "cursor.png",
                "cursor@2x.png",
                "cursortrail.png",
            ],
            ["cursor@2x.png"]);

        Assert.Equal(["cursor.png"], replaced);
    }

    [Fact]
    public void Current_skin_fallbacks_include_layers_missing_from_an_extras_pack()
    {
        var fallbacks = SkinExtraCurrentFallbackPlanner.FindMissingLayers(
            "osu.hitcircles",
            [
                "approachcircle.png",
                "approachcircle@2x.png",
                "hitcircle.png",
                "hitcircleoverlay.png",
            ],
            [
                "hitcircle.png",
                "hitcircleoverlay.png",
            ]);

        var fallback = Assert.Single(fallbacks);
        Assert.Equal("approachcircle", fallback.Key);
        Assert.Equal(
            ["approachcircle.png", "approachcircle@2x.png"],
            fallback.Filenames);
    }

    [Fact]
    public void Current_skin_fallbacks_never_inherit_cursor_middle()
    {
        var fallbacks = SkinExtraCurrentFallbackPlanner.FindMissingLayers(
            "osu.cursor",
            [
                "cursor.png",
                "cursortrail.png",
                "cursormiddle.png",
                "cursor-smoke.png",
            ],
            [
                "cursor.png",
                "cursortrail.png",
            ]);

        Assert.DoesNotContain(
            fallbacks,
            fallback => fallback.Key.Equals(
                "cursormiddle",
                StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            fallbacks,
            fallback => fallback.Key.Equals(
                "cursor-smoke",
                StringComparison.OrdinalIgnoreCase));
        Assert.True(SkinCursorMiddlePolicy.IsCursorMiddle("cursormiddle.png"));
        Assert.True(SkinCursorMiddlePolicy.IsCursorMiddle("cursormiddle@2x.png"));
    }

    [Fact]
    public void Smooth_trail_is_a_transparent_one_pixel_png()
    {
        var bytes = SkinCursorMiddlePolicy.CreateSmoothTrailPng();
        var image = SkinImageTools.Decode(bytes);
        var pixels = SkinImageTools.Pixels(image, out var stride);

        Assert.Equal(1, image.PixelWidth);
        Assert.Equal(1, image.PixelHeight);
        Assert.Equal(4, stride);
        Assert.Equal([0, 0, 0, 0], pixels);
        Assert.True(SkinImageTools.IsFullyTransparentImage(bytes));
        Assert.True(SkinExtrasPickerWindow.IsSmoothTrailPlaceholder(image));
        Assert.False(SkinExtrasPickerWindow.HasVisibleCursorMiddle(image));
    }

    [Fact]
    public void Fully_transparent_images_are_omitted_from_composed_previews()
    {
        var transparent = new SkinElementEntry(File("cursormiddle.png"))
        {
            HasVisiblePixels = false,
        };
        var visible = new SkinElementEntry(File("cursor.png"))
        {
            HasVisiblePixels = true,
        };
        var legacyOpaquePlaceholder = new SkinElementEntry(File("cursormiddle.png"))
        {
            HasVisiblePixels = true,
            PixelWidth = 1,
            PixelHeight = 1,
        };

        Assert.False(SkinEditorPage.ShouldRenderPreviewImage(transparent));
        Assert.False(SkinEditorPage.ShouldRenderPreviewImage(legacyOpaquePlaceholder));
        Assert.True(SkinEditorPage.ShouldRenderPreviewImage(visible));
        Assert.True(SkinEditorPage.ShouldRenderPreviewImage(null));
    }

    [Fact]
    public void One_pixel_cursor_middle_is_not_preview_artwork()
    {
        Assert.False(SkinCursorMiddlePolicy.HasRenderablePixels(
            "cursormiddle.png",
            1,
            1,
            [255, 255, 255, 255]));
        Assert.True(SkinCursorMiddlePolicy.HasRenderablePixels(
            "cursormiddle.png",
            2,
            1,
            [
                255, 255, 255, 255,
                255, 255, 255, 255,
            ]));
        Assert.True(SkinCursorMiddlePolicy.HasRenderablePixels(
            "cursor.png",
            1,
            1,
            [255, 255, 255, 255]));
    }

    [Fact]
    public void Cursor_policy_removes_every_middle_variant_and_only_readds_smooth_placeholder()
    {
        var current = new[]
        {
            File("cursor.png"),
            File("cursormiddle.png"),
            File("cursormiddle@2x.png"),
        };

        var classic = SkinCursorMiddlePolicy.BuildChanges(current, smoothTrail: false);
        Assert.Equal(2, classic.Count);
        Assert.All(classic, change => Assert.True(change.IsDeletion));

        var smooth = SkinCursorMiddlePolicy.BuildChanges(current, smoothTrail: true);
        Assert.Equal(3, smooth.Count);
        Assert.Contains(smooth, change =>
            change.Filename == "cursormiddle.png" && !change.IsDeletion);
        Assert.DoesNotContain(smooth, change =>
            change.Filename == "cursormiddle@2x.png" && !change.IsDeletion);
    }

    [Fact]
    public void Draft_projection_and_normalization_cancel_draft_only_stale_files()
    {
        var baseline = new[]
        {
            new LazerSkinFileInfo("followpoint-0.png", "baseline", 10),
        };
        var existingDraft = new[]
        {
            new SkinDraftChange("followpoint-2@2x.png", null, [2], "old staged frame"),
        };
        var effective = SkinDraftProjection.EffectiveFiles(baseline, existingDraft);

        Assert.Contains(effective, file => file.Filename == "followpoint-2@2x.png");
        var normalized = SkinDraftProjection.NormalizeAgainstBaseline(
            baseline,
            [
                new SkinDraftChange(
                    "followpoint-2@2x.png",
                    "",
                    [],
                    "remove stale staged frame",
                    SkinDraftOperation.Delete),
                new SkinDraftChange(
                    "followpoint-0.png",
                    "",
                    [9],
                    "replace baseline frame"),
            ]);

        var replacement = Assert.Single(normalized);
        Assert.Equal("followpoint-0.png", replacement.Filename);
        Assert.Equal("baseline", replacement.ExpectedHash);
    }

    [Fact]
    public void Cursor_family_plan_ignores_pack_supplied_cursor_middle()
    {
        var manifest = new SkinExtraPackManifest
        {
            Id = "cursor-pack",
            DisplayName = "Cursor",
            FamilyId = "osu.cursor",
            Area = "osu!",
            FamilyName = "Cursor",
            Fingerprint = new string('a', 64),
        };

        var plan = SkinExtraPackPlanner.BuildFamilyPlan(
            manifest,
            [],
            [
                new SkinExtraPackFile("cursor.png", [1]),
                new SkinExtraPackFile("cursormiddle.png", [2]),
                new SkinExtraPackFile("cursormiddle@2x.png", [3]),
            ],
            currentIni: null,
            replaceEntireFamily: false);

        Assert.Single(plan.Changes);
        Assert.Equal("cursor.png", plan.Changes[0].Filename);
    }

    [Fact]
    public void Extraction_never_adds_cursor_middle_to_new_cursor_packs()
    {
        var service = new SkinExtrasExtractionService();
        var source = new SkinExtractionSource
        {
            DisplayName = "Cursor",
            SourceLabel = "memory",
            Files =
            [
                new SkinExtractionFile("cursor.png", [1]),
                new SkinExtractionFile("cursortrail.png", [2]),
                new SkinExtractionFile("cursormiddle.png", [3]),
                new SkinExtractionFile("cursormiddle@2x.png", [4]),
            ],
        };

        var cursor = Assert.Single(service.Analyze(source));
        Assert.Equal("osu.cursor", cursor.Definition.Id);
        Assert.Equal(
            ["cursor.png", "cursortrail.png"],
            cursor.Files.Select(file => file.Filename)
                .OrderBy(filename => filename, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void Staging_selected_extras_targets_preserves_other_draft_changes()
    {
        var draft = new SkinDraftSession(Guid.NewGuid());
        draft.Stage("cursortrail.png", "trail", [1], "existing trail edit");

        draft.StageRange(
        [
            new SkinDraftChange("cursor.png", "cursor", [2], "selected Extras cursor"),
        ]);

        Assert.Equal(2, draft.Count);
        Assert.Contains(draft.Changes, change =>
            change.Filename == "cursortrail.png" && change.Bytes.SequenceEqual(new byte[] { 1 }));
        Assert.Contains(draft.Changes, change =>
            change.Filename == "cursor.png" && change.Bytes.SequenceEqual(new byte[] { 2 }));
    }

    [Fact]
    public void Legacy_slider_renderer_draws_a_transparent_bgra_slider_body()
    {
        var path = LegacySliderRenderer.SampleSCurve(20, 50, 100, 50, segments: 12);

        var bitmap = LegacySliderRenderer.Render(
            120,
            100,
            path,
            12,
            Color.FromRgb(255, 192, 0),
            sliderBorder: null,
            sliderTrackOverride: null);
        var pixels = new byte[120 * 100 * 4];
        bitmap.CopyPixels(pixels, 120 * 4, 0);

        Assert.Equal(120, bitmap.PixelWidth);
        Assert.Equal(100, bitmap.PixelHeight);
        Assert.Contains(pixels.Where((_, index) => index % 4 == 3), alpha => alpha > 0);
        Assert.Equal(0, pixels[3]);
    }

    [Fact]
    public void Legacy_slider_renderer_applies_slider_track_override()
    {
        var path = new[] { new System.Windows.Point(10, 20), new System.Windows.Point(50, 20) };
        var red = LegacySliderRenderer.Render(
            60, 40, path, 10, Colors.White, null, Color.FromRgb(255, 0, 0));
        var blue = LegacySliderRenderer.Render(
            60, 40, path, 10, Colors.White, null, Color.FromRgb(0, 0, 255));
        var redPixels = new byte[60 * 40 * 4];
        var bluePixels = new byte[60 * 40 * 4];
        red.CopyPixels(redPixels, 60 * 4, 0);
        blue.CopyPixels(bluePixels, 60 * 4, 0);

        var centre = (20 * 60 + 30) * 4;
        Assert.True(redPixels[centre + 2] > redPixels[centre]);
        Assert.True(bluePixels[centre] > bluePixels[centre + 2]);
    }

    [Fact]
    public void Legacy_slider_renderer_honours_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            LegacySliderRenderer.Render(
                880,
                505,
                LegacySliderRenderer.SampleSCurve(190, 310, 670, 180),
                50,
                Colors.White,
                sliderBorder: null,
                sliderTrackOverride: null,
                cancellationToken: cancellation.Token));
    }

    [Fact]
    public void Extras_naming_is_stable_windows_safe_and_collision_ready()
    {
        Assert.Equal("Skin_Name — Author_Test", SkinExtraNaming.PackName("Skin/Name", "Author:Test"));
        Assert.Equal("_CON", SkinExtraNaming.Sanitize("CON"));
        Assert.Equal(96, SkinExtraNaming.Sanitize(new string('x', 120)).Length);
    }

    [Fact]
    public void Mode_specific_extras_are_hidden_until_individually_enabled()
    {
        var defaults = new SkinExtraModeVisibility();
        Assert.False(defaults.AllowsArea("Catch"));
        Assert.False(defaults.AllowsArea("Taiko"));
        Assert.False(defaults.AllowsArea("Mania"));
        Assert.True(defaults.AllowsArea("osu!"));
        Assert.True(defaults.AllowsArea("Audio"));

        var maniaOnly = new SkinExtraModeVisibility(ShowMania: true);
        Assert.False(maniaOnly.AllowsArea("Catch"));
        Assert.False(maniaOnly.AllowsArea("Taiko"));
        Assert.True(maniaOnly.AllowsArea("Mania"));
        Assert.True(defaults.LazerUsedOnly);
        Assert.True(maniaOnly.LazerUsedOnly);
    }

    [Theory]
    [InlineData("menu-background.png", "interface.background", SkinExtraCompatibility.LazerUsed)]
    [InlineData("followpoint-0.png", "osu.followpoints", SkinExtraCompatibility.LazerUsed)]
    [InlineData("fruit-apple.png", "catch.fruits", SkinExtraCompatibility.LazerUsed)]
    [InlineData("taikohitnormal.png", "taiko.notes", SkinExtraCompatibility.LazerUsed)]
    [InlineData("mania-key1.png", "mania.keys", SkinExtraCompatibility.LazerUsed)]
    [InlineData("normal-hitnormal.wav", "audio.hitsounds.normal", SkinExtraCompatibility.LazerUsed)]
    [InlineData("default-0.png", "osu.number-font", SkinExtraCompatibility.LazerUsed)]
    [InlineData("selection-mod-doubletime.png", "interface.mod-icons", SkinExtraCompatibility.StableOnly)]
    [InlineData("inputoverlay-key.png", "interface.input-overlay", SkinExtraCompatibility.LazerUsed)]
    [InlineData("scorebar-bg.png", "interface.scorebar", SkinExtraCompatibility.LazerUsed)]
    [InlineData("scoreentry-7@2x.png", "interface.leaderboard", SkinExtraCompatibility.LazerUsed)]
    [InlineData("ranking-S-small.png", "interface.ranking", SkinExtraCompatibility.LazerUsed)]
    [InlineData("ranking-S.png", "interface.ranking", SkinExtraCompatibility.StableOnly)]
    [InlineData("combobreak.wav", "audio.combobreak", SkinExtraCompatibility.LazerUsed)]
    [InlineData("nightcore-kick.ogg", "audio.nightcore", SkinExtraCompatibility.LazerUsed)]
    [InlineData("spinnerbonus.wav", "audio.spinner", SkinExtraCompatibility.LazerUsed)]
    [InlineData("sectionpass.wav", "audio.sectionpass", SkinExtraCompatibility.StableOnly)]
    [InlineData("sectionfail.ogg", "audio.sectionfail", SkinExtraCompatibility.StableOnly)]
    [InlineData("applause-s.wav", "audio.applause", SkinExtraCompatibility.LazerUsed)]
    [InlineData("failsound.wav", "audio.failsound", SkinExtraCompatibility.LazerUsed)]
    [InlineData("seeya.wav", "audio.seeya", SkinExtraCompatibility.LazerUsed)]
    [InlineData("welcome.wav", "audio.welcome", SkinExtraCompatibility.LazerUsed)]
    [InlineData("mystery.png", "interface.menu", SkinExtraCompatibility.Unknown)]
    public void Lazer_compatibility_catalog_classifies_individual_assets(
        string filename,
        string familyId,
        SkinExtraCompatibility expected)
    {
        Assert.Equal(expected, SkinExtraLazerCompatibility.Classify(filename, familyId));
        Assert.Equal("2026.702.0", SkinExtraLazerCompatibility.AuditedOsuVersion);
        Assert.Equal(
            "b7774fe8d16a96690bef65b4f9562e3df393d5e4",
            SkinExtraLazerCompatibility.AuditedCommit);
    }

    [Fact]
    public void Interface_families_use_specific_user_facing_names()
    {
        var background = SkinExtraFamilyRegistry.ForFile("menu-background.jpg");
        var leaderboard = SkinExtraFamilyRegistry.ForFile("scoreentry-7@2x.png");

        Assert.NotNull(background);
        Assert.Equal("interface.background", background.Id);
        Assert.Equal("Background", background.Name);
        Assert.NotNull(leaderboard);
        Assert.Equal("interface.leaderboard", leaderboard.Id);
        Assert.Equal("Leaderboard rows & score digits", leaderboard.Name);
    }

    [Fact]
    public void Star_particles_and_result_judgements_have_dedicated_families()
    {
        Assert.Equal(
            "osu.star-particles",
            SkinExtraFamilyRegistry.ForFile("star2@2x.png")?.Id);
        Assert.Equal(
            "osu.hitbursts",
            SkinExtraFamilyRegistry.ForFile("hit100.png")?.Id);
        Assert.Equal(
            "osu.result-judgements",
            SkinExtraFamilyRegistry.ForFile("hit100k.png")?.Id);
        Assert.Equal(
            SkinExtraCompatibility.StableOnly,
            SkinExtraLazerCompatibility.Classify(
                "hit100k.png",
                "osu.result-judgements"));
    }

    [Fact]
    public void Greeting_and_result_audio_have_dedicated_families()
    {
        Assert.Equal("audio.seeya", SkinExtraFamilyRegistry.ForFile("seeya.wav")?.Id);
        Assert.Equal("audio.welcome", SkinExtraFamilyRegistry.ForFile("welcome.wav")?.Id);
        Assert.Equal("audio.applause", SkinExtraFamilyRegistry.ForFile("applause.mp3")?.Id);
        Assert.Equal("audio.applause", SkinExtraFamilyRegistry.ForFile("applause-s.mp3")?.Id);
        Assert.Equal("audio.failsound", SkinExtraFamilyRegistry.ForFile("failsound.wav")?.Id);
    }

    [Fact]
    public void Duplicate_detection_ignores_colour_tuple_spacing()
    {
        Assert.True(SkinExtraFingerprint.IniValuesEqual(
            "255, 255, 255",
            "255,255,255"));
        Assert.False(SkinExtraFingerprint.IniValuesEqual(
            "255,255,255",
            "255,255,254"));
        Assert.True(SkinExtraFingerprint.EquivalentPackContent(
            [],
            [new SkinExtraIniPatchEntry("Colours", "SliderBorder", "255, 255, 255")],
            [],
            [new SkinExtraIniPatchEntry("Colours", "SliderBorder", "255,255,255")]));
    }

    [Fact]
    public void Duplicate_detection_accepts_byte_identity_when_legacy_semantic_hashes_differ()
    {
        var byteHash = new string('a', 64);
        var current = new SkinExtraManifestFile(
            "inputoverlay-background.png",
            "inputoverlay-background.png",
            "inputoverlay-background",
            byteHash,
            new string('b', 64));
        var legacy = new SkinExtraManifestFile(
            "inputoverlay-background.png",
            "inputoverlay-background.png",
            "inputoverlay-background",
            byteHash,
            new string('c', 64));

        Assert.True(SkinExtraFingerprint.EquivalentPackContent(
            [current], [], [legacy], []));
    }

    [Fact]
    public void Duplicate_detection_matches_each_manifest_entry_only_once()
    {
        var first = new SkinExtraManifestFile(
            "cursor.png",
            "cursor.png",
            "cursor.png",
            new string('a', 64),
            new string('a', 64));
        var second = first with
        {
            ByteHash = new string('b', 64),
            SemanticHash = new string('b', 64),
        };

        Assert.False(SkinExtraFingerprint.EquivalentPackContent(
            [first, first],
            [],
            [first, second],
            []));
    }

    [Fact]
    public void Fully_transparent_images_share_one_semantic_identity()
    {
        var small = SkinExtraFingerprint.Describe(
            "star2.png",
            "star2.png",
            TransparentPng(1, 1, 255, 0, 0));
        var large = SkinExtraFingerprint.Describe(
            "star2.png",
            "star2.png",
            TransparentPng(8, 5, 0, 255, 255));

        Assert.Equal(small.SemanticHash, large.SemanticHash);
        Assert.Equal("transparent", small.SimilarityHash);
        Assert.Equal("transparent", large.SimilarityHash);
    }

    [Fact]
    public void Mixed_pack_views_have_independent_local_state_keys()
    {
        var source = new SkinExtraPackManifest
        {
            Id = "mixed-audio",
            DisplayName = "Original",
            FamilyId = "audio.other",
            Area = "Audio",
            FamilyName = "Other sounds",
            Fingerprint = new string('a', 64),
            Files =
            [
                ManifestFile("seeya.wav"),
                ManifestFile("welcome.wav"),
                ManifestFile("applause.mp3"),
                ManifestFile("pause-loop.mp3"),
            ],
        };
        SkinExtraPackManifest View(string familyId, string filename) => new()
        {
            Id = source.Id,
            DisplayName = source.DisplayName,
            FamilyId = familyId,
            Area = "Audio",
            FamilyName = familyId,
            Fingerprint = source.Fingerprint,
            Files = [ManifestFile(filename)],
        };

        var keys = new[]
        {
            SkinExtrasPickerWindow.PackStateKey(source, View("audio.seeya", "seeya.wav")),
            SkinExtrasPickerWindow.PackStateKey(source, View("audio.welcome", "welcome.wav")),
            SkinExtrasPickerWindow.PackStateKey(source, View("audio.applause", "applause.mp3")),
            SkinExtrasPickerWindow.PackStateKey(source, View("audio.gameplay", "pause-loop.mp3")),
        };

        Assert.Equal(keys.Length, keys.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(keys, key => Assert.StartsWith($"view:{source.Id}:", key));

        var rebuiltSource = new SkinExtraPackManifest
        {
            Id = source.Id,
            DisplayName = source.DisplayName,
            FamilyId = source.FamilyId,
            Area = source.Area,
            FamilyName = source.FamilyName,
            Fingerprint = new string('b', 64),
            Files = source.Files.ToList(),
        };
        Assert.Equal(
            keys[0],
            SkinExtrasPickerWindow.PackStateKey(
                rebuiltSource,
                View("audio.seeya", "seeya.wav")));
        Assert.NotEqual(
            SkinExtrasPickerWindow.LegacyPackStateKey(
                source,
                View("audio.seeya", "seeya.wav")),
            SkinExtrasPickerWindow.LegacyPackStateKey(
                rebuiltSource,
                View("audio.seeya", "seeya.wav")));
    }

    [Fact]
    public void Extras_watcher_ignores_internal_metadata_directory_and_children()
    {
        var metadata = Path.Combine(AppPaths.SkinExtrasDir, ".kumori");

        Assert.True(SkinExtrasPickerWindow.IsInternalLibraryPath(metadata));
        Assert.True(SkinExtrasPickerWindow.IsInternalLibraryPath(
            Path.Combine(metadata, "index-v1.json")));
        Assert.False(SkinExtrasPickerWindow.IsInternalLibraryPath(
            Path.Combine(AppPaths.SkinExtrasDir, "Audio", "pack.ogg")));
        Assert.False(SkinExtrasPickerWindow.IsInternalLibraryPath(
            metadata + "-backup"));
    }

    [Fact]
    public void Lazer_filter_builds_a_non_destructive_view_of_mixed_existing_pack()
    {
        var manifest = new SkinExtraPackManifest
        {
            Id = "mixed-menu",
            DisplayName = "Mixed menu",
            FamilyId = "interface.menu",
            Area = "Interface",
            FamilyName = "Menus & buttons",
            Fingerprint = new string('c', 64),
            Files =
            [
                ManifestFile("menu-background.png"),
                ManifestFile("button-left.png"),
                ManifestFile("unknown-custom.png"),
            ],
            IniPatch =
            [
                new SkinExtraIniPatchEntry("Colours", "MenuGlow", "255,0,255"),
            ],
        };

        var filtered = SkinExtraLazerCompatibility.FilterManifest(manifest);

        Assert.Single(filtered.Files);
        Assert.Equal("menu-background.png", filtered.Files[0].TargetFilename);
        Assert.Empty(filtered.IniPatch);
        Assert.Equal(manifest.Fingerprint, filtered.Fingerprint);
        Assert.Equal(3, manifest.Files.Count);
        Assert.Equal("Mixed compatibility", SkinExtraLazerCompatibility.CompatibilityBadge(manifest));
    }

    [Fact]
    public void Lazer_filter_removes_fully_stable_only_and_unknown_pack_content()
    {
        var stableManifest = new SkinExtraPackManifest
        {
            Id = "mods",
            DisplayName = "Mods",
            FamilyId = "interface.mod-icons",
            Area = "Interface",
            FamilyName = "Mod icons",
            Fingerprint = new string('e', 64),
            Files = [ManifestFile("selection-mod-doubletime.png")],
        };
        var unknownManifest = new SkinExtraPackManifest
        {
            Id = "unknown",
            DisplayName = "Unknown",
            FamilyId = "misc.other",
            Area = "Other",
            FamilyName = "Unclassified assets",
            Fingerprint = new string('f', 64),
            Files = [ManifestFile("custom-decoration.png")],
        };

        Assert.False(SkinExtraLazerCompatibility.HasLazerUsedContent(stableManifest));
        Assert.False(SkinExtraLazerCompatibility.HasLazerUsedContent(unknownManifest));
        Assert.Equal("Stable only", SkinExtraLazerCompatibility.CompatibilityBadge(stableManifest));
        Assert.Equal("Unverified", SkinExtraLazerCompatibility.CompatibilityBadge(unknownManifest));
        Assert.False(SkinExtraLazerCompatibility.IsIniPatchUsed(
            "osu.slider",
            new SkinExtraIniPatchEntry("General", "SliderBallFlip", "1")));
        Assert.False(SkinExtraLazerCompatibility.IsIniPatchUsed(
            "osu.comboburst",
            new SkinExtraIniPatchEntry("General", "ComboBurstRandom", "1")));
        Assert.False(SkinExtraLazerCompatibility.IsIniPatchUsed(
            "interface.menu",
            new SkinExtraIniPatchEntry("Colours", "MenuGlow", "255,0,255")));
    }

    [Fact]
    public void Lazer_filtered_family_plan_never_writes_or_deletes_stable_only_siblings()
    {
        var manifest = new SkinExtraPackManifest
        {
            Id = "mixed-menu",
            DisplayName = "Mixed menu",
            FamilyId = "interface.menu",
            Area = "Interface",
            FamilyName = "Menus & buttons",
            Fingerprint = new string('d', 64),
        };
        var current = new[]
        {
            File("menu-background.png"),
            File("button-left.png"),
            File("arrow-pause.png"),
        };

        var plan = SkinExtraPackPlanner.BuildFamilyPlan(
            manifest,
            current,
            [
                new SkinExtraPackFile("menu-background.png", [9]),
                new SkinExtraPackFile("button-left.png", [8]),
            ],
            currentIni: null,
            lazerUsedOnly: true);

        Assert.Single(plan.Changes);
        Assert.Equal("menu-background.png", plan.Changes[0].Filename);
        Assert.DoesNotContain("button-left.png", plan.OwnedCurrentFiles);
        Assert.DoesNotContain("arrow-pause.png", plan.OwnedCurrentFiles);
    }

    [Fact]
    public void Lazer_filtered_extraction_omits_stable_only_files_from_saved_manifest()
    {
        var extras = Path.Combine(Path.GetTempPath(), $"kumori-lazer-extras-{Guid.NewGuid():N}");
        try
        {
            var service = new SkinExtrasExtractionService();
            var source = service.BuildSource(
                "Mixed skin",
                "memory",
                [
                    new SkinExtractionFile("menu-background.png", [1, 2, 3]),
                    new SkinExtractionFile("button-left.png", [4, 5, 6]),
                ]);
            var family = Assert.Single(service.Analyze(source).Where(item =>
                item.Definition.Id == "interface.background"));

            var result = Assert.Single(service.Extract(
                source,
                [family],
                extras,
                lazerUsedOnly: true));
            var resultDirectory = Assert.IsType<string>(result.DirectoryPath);
            var manifest = SkinExtraManifestSerializer.TryRead(resultDirectory);

            Assert.NotNull(manifest);
            Assert.Single(manifest.Files);
            Assert.Equal("menu-background.png", manifest.Files[0].TargetFilename);
            Assert.False(System.IO.File.Exists(Path.Combine(resultDirectory, "button-left.png")));
        }
        finally
        {
            if (Directory.Exists(extras)) Directory.Delete(extras, recursive: true);
        }
    }

    [Fact]
    public void Scoped_ini_patch_targets_the_requested_repeated_mania_section()
    {
        var document = SkinIniDocument.ParseText(
            "[General]\nName: Test\n"
            + "[Mania]\nKeys: 4\nColumnStart: 100\n"
            + "[Mania]\nKeys: 7\nColumnStart: 200\n");

        document.ApplyPatch(
        [
            new SkinExtraIniPatchEntry("Mania", "ColumnStart", "222", ManiaKeys: 7),
            new SkinExtraIniPatchEntry("Mania", "HitPosition", "420", ManiaKeys: 4),
        ]);

        var mania = document.GetSections("Mania");
        Assert.Equal("100", mania[0].Values["ColumnStart"]);
        Assert.Equal("420", mania[0].Values["HitPosition"]);
        Assert.Equal("222", mania[1].Values["ColumnStart"]);
    }

    [Fact]
    public void Extractor_detects_hitsounds_combobreak_and_shared_number_font_once()
    {
        var files = new List<SkinExtractionFile>
        {
            new("skin.ini", Encoding.UTF8.GetBytes(
                "[General]\nName: Mix\nAuthor: Tester\n"
                + "[Fonts]\nHitCirclePrefix: default\nScorePrefix: score\nComboPrefix: score\n")),
            new("normal-hitnormal.wav", [1, 2, 3]),
            new("combobreak.wav", [4, 5, 6]),
        };
        for (var digit = 0; digit < 10; digit++)
        {
            files.Add(new SkinExtractionFile($"default-{digit}.png", [(byte)digit]));
            files.Add(new SkinExtractionFile($"score-{digit}.png", [(byte)(digit + 10)]));
        }
        var service = new SkinExtrasExtractionService();
        var source = service.BuildSource("fallback", "memory", files);

        var families = service.Analyze(source);

        Assert.Contains(families, family => family.Definition.Id == "audio.hitsounds.normal");
        Assert.Contains(families, family => family.Definition.Id == "audio.combobreak");
        var fonts = families.Where(family => family.Definition.Id == "osu.number-font").ToArray();
        Assert.Equal(2, fonts.Length);
        Assert.Contains(fonts, font => font.FontRoles.SequenceEqual(["Hitcircle"]));
        Assert.Contains(fonts, font => font.FontRoles.SequenceEqual(["Score", "Combo"]));
    }

    [Fact]
    public void Extractor_uses_last_repeated_combo_prefix()
    {
        var files = new List<SkinExtractionFile>
        {
            new("skin.ini", Encoding.UTF8.GetBytes(
                "[General]\nName: Annihilation-style\nAuthor: Tester\n"
                + "[Fonts]\nHitCirclePrefix: default\nScorePrefix: score\nComboPrefix: score\n"
                + "[Fonts]\nComboPrefix: default\n")),
        };
        for (var digit = 0; digit < 10; digit++)
        {
            files.Add(new SkinExtractionFile($"default-{digit}.png", [(byte)digit]));
            files.Add(new SkinExtractionFile($"score-{digit}.png", [(byte)(digit + 10)]));
        }
        var service = new SkinExtrasExtractionService();
        var source = service.BuildSource("fallback", "memory", files);

        var fonts = service.Analyze(source)
            .Where(family => family.Definition.Id == "osu.number-font")
            .ToArray();

        Assert.Equal(2, fonts.Length);
        Assert.Contains(fonts, font =>
            font.Variant == "default"
            && font.FontRoles.SequenceEqual(["Hitcircle", "Combo"]));
        Assert.Contains(fonts, font =>
            font.Variant == "score"
            && font.FontRoles.SequenceEqual(["Score"]));
    }

    [Fact]
    public void Reimport_repairs_existing_number_font_roles_without_creating_a_duplicate()
    {
        var extras = Path.Combine(
            Path.GetTempPath(),
            $"kumori-font-role-refresh-{Guid.NewGuid():N}");
        try
        {
            var service = new SkinExtrasExtractionService();
            var files = Enumerable.Range(0, 10)
                .SelectMany(digit => new[]
                {
                    new SkinExtractionFile($"default-{digit}.png", [(byte)digit]),
                    new SkinExtractionFile($"score-{digit}.png", [(byte)(digit + 10)]),
                })
                .ToList();
            var legacySource = service.BuildSource(
                "Annihilation-style",
                "memory",
                [
                    new SkinExtractionFile("skin.ini", Encoding.UTF8.GetBytes(
                        "[General]\nName: Annihilation-style\nAuthor: Tester\n"
                        + "[Fonts]\nHitCirclePrefix: default\nScorePrefix: score\nComboPrefix: score\n")),
                    .. files,
                ]);
            var legacyResults = service.Extract(
                legacySource,
                service.Analyze(legacySource).Where(family =>
                    family.Definition.Id == "osu.number-font"),
                extras);
            Assert.Equal(2, legacyResults.Count);

            var correctedSource = service.BuildSource(
                "Annihilation-style",
                "memory",
                [
                    new SkinExtractionFile("skin.ini", Encoding.UTF8.GetBytes(
                        "[General]\nName: Annihilation-style\nAuthor: Tester\n"
                        + "[Fonts]\nHitCirclePrefix: default\nScorePrefix: score\nComboPrefix: score\n"
                        + "[Fonts]\nComboPrefix: default\n")),
                    .. files,
                ]);
            var correctedResults = service.Extract(
                correctedSource,
                service.Analyze(correctedSource).Where(family =>
                    family.Definition.Id == "osu.number-font"),
                extras);

            Assert.Equal(2, correctedResults.Count);
            var packs = SkinExtraPackIndex.Scan(extras)
                .Where(pack => pack.Manifest.FamilyId == "osu.number-font")
                .ToArray();
            Assert.Equal(2, packs.Length);
            Assert.Contains(packs, pack =>
                pack.Manifest.Variant == "default"
                && pack.Manifest.FontRoles.SequenceEqual(["Hitcircle", "Combo"]));
            Assert.Contains(packs, pack =>
                pack.Manifest.Variant == "score"
                && pack.Manifest.FontRoles.SequenceEqual(["Score"]));
        }
        finally
        {
            if (Directory.Exists(extras))
                Directory.Delete(extras, recursive: true);
        }
    }

    [Fact]
    public void Number_font_imports_preserve_identical_combo_choices_from_different_skins()
    {
        var extras = Path.Combine(
            Path.GetTempPath(),
            $"kumori-combo-fonts-{Guid.NewGuid():N}");
        try
        {
            var service = new SkinExtrasExtractionService();
            var firstSource = ComboFontSource(service, "First combo skin");
            var secondSource = ComboFontSource(service, "Second combo skin");
            var firstFamily = Assert.Single(service.Analyze(firstSource).Where(family =>
                family.Definition.Id == "osu.number-font"
                && family.FontRoles.SequenceEqual(["Combo"])));
            var secondFamily = Assert.Single(service.Analyze(secondSource).Where(family =>
                family.Definition.Id == "osu.number-font"
                && family.FontRoles.SequenceEqual(["Combo"])));

            var first = Assert.Single(service.Extract(firstSource, [firstFamily], extras));
            var second = Assert.Single(service.Extract(secondSource, [secondFamily], extras));
            var repeated = Assert.Single(service.Extract(secondSource, [secondFamily], extras));

            Assert.Equal(SkinExtraExtractionStatus.Extracted, first.Status);
            Assert.Equal(SkinExtraExtractionStatus.Extracted, second.Status);
            Assert.Equal(SkinExtraExtractionStatus.ExactDuplicateSkipped, repeated.Status);
            Assert.NotEqual(first.DirectoryPath, second.DirectoryPath);
            Assert.Equal(
                2,
                SkinExtraPackIndex.Scan(extras).Count(pack =>
                    pack.Manifest.FamilyId == "osu.number-font"
                    && pack.Manifest.FontRoles.SequenceEqual(["Combo"])));
        }
        finally
        {
            if (Directory.Exists(extras))
                Directory.Delete(extras, recursive: true);
        }

        static SkinExtractionSource ComboFontSource(
            SkinExtrasExtractionService service,
            string name)
        {
            var files = new List<SkinExtractionFile>
            {
                new(
                    "skin.ini",
                    Encoding.UTF8.GetBytes(
                        $"[General]\nName: {name}\nAuthor: Tester\n"
                        + "[Fonts]\nComboPrefix: combo\n")),
            };
            for (var digit = 0; digit < 10; digit++)
                files.Add(new SkinExtractionFile(
                    $"combo-{digit}.png",
                    [(byte)(digit + 1)]));
            return service.BuildSource(name, "memory", files);
        }
    }

    [Fact]
    public void Shared_score_combo_font_is_listed_in_both_navigation_categories()
    {
        var shared = new SkinExtraPackManifest
        {
            Id = "shared-font",
            DisplayName = "Shared font",
            FamilyId = "osu.number-font",
            Area = "osu!",
            FamilyName = "Number fonts",
            Variant = "score",
            SourceSkin = "Skin",
            SourceAuthor = "Tester",
            Fingerprint = new string('a', 64),
            FontRoles = ["Score", "Combo"],
        };

        Assert.True(SkinExtrasPickerWindow.PackBelongsToNavigationFamily(
            shared,
            "osu.number-font.score-combo",
            "osu.number-font.score"));
        Assert.True(SkinExtrasPickerWindow.PackBelongsToNavigationFamily(
            shared,
            "osu.number-font.score-combo",
            "osu.number-font.combo"));
        Assert.False(SkinExtrasPickerWindow.PackBelongsToNavigationFamily(
            shared,
            "osu.number-font.score-combo",
            "osu.number-font.score-combo"));
        Assert.False(SkinExtrasPickerWindow.PackBelongsToNavigationFamily(
            shared,
            "osu.number-font.score-combo",
            "osu.number-font.hitcircle"));

        var otherSource = new SkinExtraPackManifest
        {
            Id = "other-shared-font",
            DisplayName = "Shared font",
            FamilyId = shared.FamilyId,
            Area = shared.Area,
            FamilyName = shared.FamilyName,
            Variant = shared.Variant,
            SourceSkin = "Other skin",
            SourceAuthor = shared.SourceAuthor,
            Fingerprint = shared.Fingerprint,
            FontRoles = shared.FontRoles.ToList(),
        };
        Assert.False(SkinExtrasPickerWindow.NumberFontCollapseScopeMatches(
            shared,
            otherSource));
    }

    [Fact]
    public void Combo_navigation_only_accepts_combo_only_number_fonts()
    {
        var combo = new SkinExtraPackManifest
        {
            Id = "combo-font",
            DisplayName = "Combo font",
            FamilyId = "osu.number-font",
            Area = "osu!",
            FamilyName = "Number fonts",
            Variant = "combo",
            Fingerprint = new string('c', 64),
            FontRoles = ["Combo"],
        };

        Assert.True(SkinExtrasPickerWindow.PackBelongsToNavigationFamily(
            combo,
            "osu.number-font.combo",
            "osu.number-font.combo"));
        Assert.False(SkinExtrasPickerWindow.PackBelongsToNavigationFamily(
            combo,
            "osu.number-font.combo",
            "osu.number-font.score"));
        Assert.False(SkinExtrasPickerWindow.PackBelongsToNavigationFamily(
            combo,
            "osu.number-font.combo",
            "osu.number-font.score-combo"));
    }

    [Fact]
    public void Extractor_separates_greeting_and_result_audio()
    {
        var service = new SkinExtrasExtractionService();
        var source = service.BuildSource(
            "Audio skin",
            "memory",
            [
                new SkinExtractionFile("seeya.wav", [1]),
                new SkinExtractionFile("welcome.wav", [2]),
                new SkinExtractionFile("applause.mp3", [3]),
                new SkinExtractionFile("failsound.wav", [4]),
                new SkinExtractionFile("pause-loop.mp3", [5]),
                new SkinExtractionFile("sectionpass.wav", [6]),
                new SkinExtractionFile("sectionfail.ogg", [7]),
            ]);

        var families = service.Analyze(source);

        Assert.Single(families.Where(family => family.Definition.Id == "audio.seeya"));
        Assert.Single(families.Where(family => family.Definition.Id == "audio.welcome"));
        Assert.Single(families.Where(family => family.Definition.Id == "audio.applause"));
        Assert.Single(families.Where(family => family.Definition.Id == "audio.failsound"));
        Assert.Single(families.Where(family => family.Definition.Id == "audio.gameplay"));
        Assert.Single(families.Where(family => family.Definition.Id == "audio.sectionpass"));
        Assert.Single(families.Where(family => family.Definition.Id == "audio.sectionfail"));
    }

    [Fact]
    public void Extractor_separates_slider_colours_and_stable_result_judgements()
    {
        var service = new SkinExtrasExtractionService();
        var source = service.BuildSource(
            "Skin",
            "memory",
            [
                new SkinExtractionFile(
                    "skin.ini",
                    Encoding.UTF8.GetBytes(
                        "[Colours]\nSliderBorder: 255,0,255\nSliderTrackOverride: 20,30,40\n")),
                new SkinExtractionFile("sliderb0.png", [1]),
                new SkinExtractionFile("hit100.png", [2]),
                new SkinExtractionFile("hit100k.png", [3]),
            ]);

        var families = service.Analyze(source);

        var sliderColours = Assert.Single(families.Where(family =>
            family.Definition.Id == "osu.slider-colours"));
        Assert.Empty(sliderColours.Files);
        Assert.Equal(2, sliderColours.IniPatch.Count);
        Assert.Single(families.Where(family => family.Definition.Id == "osu.hitbursts"));
        Assert.Single(families.Where(family =>
            family.Definition.Id == "osu.result-judgements"));
    }

    [Fact]
    public void Extractor_persists_slider_colours_as_their_own_pack_with_exact_values()
    {
        var extras = Path.Combine(
            Path.GetTempPath(),
            $"kumori-slider-colours-{Guid.NewGuid():N}");
        try
        {
            var service = new SkinExtrasExtractionService();
            var source = service.BuildSource(
                "Kumori Lazer",
                "memory",
                [
                    new SkinExtractionFile(
                        "skin.ini",
                        Encoding.UTF8.GetBytes(
                            "[Colours]\nSliderBorder: 100,180,200\n"
                            + "SliderTrackOverride: 18,18,18\n")),
                    new SkinExtractionFile("sliderb0.png", [1]),
                ]);

            var results = service.Extract(
                source,
                service.Analyze(source),
                extras,
                lazerUsedOnly: true);
            var manifests = results
                .Select(result => SkinExtraManifestSerializer.TryRead(result.DirectoryPath!))
                .Where(manifest => manifest is not null)
                .Cast<SkinExtraPackManifest>()
                .ToArray();

            var sliders = Assert.Single(manifests.Where(manifest =>
                manifest.FamilyId == "osu.slider"));
            Assert.Empty(sliders.IniPatch);
            var colours = Assert.Single(manifests.Where(manifest =>
                manifest.FamilyId == "osu.slider-colours"));
            Assert.Empty(colours.Files);
            Assert.Equal("Cyan + Black", colours.DisplayName);
            Assert.Contains(colours.IniPatch, entry =>
                entry.Key == "SliderBorder" && entry.Value == "100,180,200");
            Assert.Contains(colours.IniPatch, entry =>
                entry.Key == "SliderTrackOverride" && entry.Value == "18,18,18");
        }
        finally
        {
            if (Directory.Exists(extras)) Directory.Delete(extras, recursive: true);
        }
    }

    [Fact]
    public void Extractor_does_not_invent_slider_colours_when_skin_ini_has_none()
    {
        var service = new SkinExtrasExtractionService();
        var source = service.BuildSource(
            "Skin",
            "memory",
            [new SkinExtractionFile("sliderb0.png", [1])]);

        Assert.DoesNotContain(service.Analyze(source), family =>
            family.Definition.Id == "osu.slider-colours");
    }

    [Fact]
    public void Extractor_separates_followpoints_from_hit_circles()
    {
        var service = new SkinExtrasExtractionService();
        var source = service.BuildSource(
            "Skin",
            "memory",
            [
                new SkinExtractionFile("hitcircle.png", [1]),
                new SkinExtractionFile("hitcircleoverlay.png", [2]),
                new SkinExtractionFile("approachcircle.png", [3]),
                new SkinExtractionFile("followpoint-0.png", [4]),
                new SkinExtractionFile("followpoint-1.png", [5]),
            ]);

        var families = service.Analyze(source);
        var hitCircles = Assert.Single(families.Where(family =>
            family.Definition.Id == "osu.hitcircles"));
        var followpoints = Assert.Single(families.Where(family =>
            family.Definition.Id == "osu.followpoints"));

        Assert.Equal(3, hitCircles.Files.Count);
        Assert.DoesNotContain(hitCircles.Files, file =>
            file.Filename.StartsWith("followpoint", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, followpoints.Files.Count);
        Assert.All(followpoints.Files, file =>
            Assert.StartsWith("followpoint", file.Filename, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Extractor_preserves_transparent_followpoint_timing_frames()
    {
        var extras = Path.Combine(Path.GetTempPath(), $"kumori-extras-{Guid.NewGuid():N}");
        try
        {
            var service = new SkinExtrasExtractionService();
            var transparent = SkinImageTools.Encode(
                SkinImageTools.ToBitmap([0, 0, 0, 0], 1, 1, 4),
                "followpoint-0.png");
            var visible = SkinImageTools.Encode(
                SkinImageTools.ToBitmap([255, 255, 255, 255], 1, 1, 4),
                "followpoint-1.png");
            var source = service.BuildSource(
                "Skin",
                "memory",
                [
                    new SkinExtractionFile("followpoint-0.png", transparent),
                    new SkinExtractionFile("followpoint-1.png", visible),
                ]);
            var family = Assert.Single(service.Analyze(source).Where(item =>
                item.Definition.Id == "osu.followpoints"));

            var result = Assert.Single(service.Extract(source, [family], extras));
            var manifest = SkinExtraManifestSerializer.TryRead(result.DirectoryPath!);

            Assert.NotNull(manifest);
            Assert.Contains(manifest.Files, file => file.TargetFilename == "followpoint-0.png");
            Assert.Contains(manifest.Files, file => file.TargetFilename == "followpoint-1.png");
            Assert.True(System.IO.File.Exists(
                Path.Combine(result.DirectoryPath!, "followpoint-0.png")));
            Assert.True(SkinExtraPackValidator.Validate(
                new SkinExtraPackDescriptor(result.DirectoryPath!, manifest, false)).IsHealthy);
        }
        finally
        {
            if (Directory.Exists(extras)) Directory.Delete(extras, recursive: true);
        }
    }

    [Fact]
    public void Extractor_rejects_every_non_root_skin_asset()
    {
        var service = new SkinExtrasExtractionService();
        var source = service.BuildSource(
            "Skin",
            "memory",
            [
                new SkinExtractionFile("hitcircle.png", [1]),
                new SkinExtractionFile("Backups/Old/hitcircle.png", [2]),
                new SkinExtractionFile("Stable files/hitcircleoverlay.png", [3]),
                new SkinExtractionFile("Extras/Recolours/Blue/hitcircleoverlay.png", [4]),
                new SkinExtractionFile("approachcircle.png", [5]),
            ]);

        var hitCircles = Assert.Single(service.Analyze(source).Where(family =>
            family.Definition.Id == "osu.hitcircles"));

        Assert.Equal(2, hitCircles.Files.Count);
        Assert.Contains(hitCircles.Files, file =>
            file.Filename == "hitcircle.png"
            && file.Bytes.SequenceEqual(new byte[] { 1 }));
        Assert.Contains(hitCircles.Files, file => file.Filename == "approachcircle.png");
        Assert.DoesNotContain(source.Files, file =>
            file.Filename.Contains('/') || file.Filename.Contains('\\'));
    }

    [Fact]
    public void Hit_circle_and_followpoint_plans_do_not_delete_each_others_assets()
    {
        var current = new[]
        {
            File("hitcircle.png"),
            File("approachcircle.png"),
            File("followpoint-0.png"),
            File("followpoint-1.png"),
        };
        var hitCircleManifest = new SkinExtraPackManifest
        {
            Id = "hitcircles",
            DisplayName = "Hit circles",
            FamilyId = "osu.hitcircles",
            Area = "osu!",
            FamilyName = "Hit circles",
            Fingerprint = new string('a', 64),
        };
        var followpointManifest = new SkinExtraPackManifest
        {
            Id = "followpoints",
            DisplayName = "Followpoints",
            FamilyId = "osu.followpoints",
            Area = "osu!",
            FamilyName = "Followpoints",
            Fingerprint = new string('b', 64),
        };

        var hitCirclePlan = SkinExtraPackPlanner.BuildFamilyPlan(
            hitCircleManifest,
            current,
            [new SkinExtraPackFile("hitcircle.png", [9])],
            currentIni: null);
        var followpointPlan = SkinExtraPackPlanner.BuildFamilyPlan(
            followpointManifest,
            current,
            [new SkinExtraPackFile("followpoint-0.png", [8])],
            currentIni: null);

        Assert.DoesNotContain(hitCirclePlan.Changes, change =>
            change.Filename.StartsWith("followpoint", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(followpointPlan.Changes, change =>
            change.Filename.StartsWith("hitcircle", StringComparison.OrdinalIgnoreCase)
            || change.Filename.StartsWith("approachcircle", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(followpointPlan.Changes, change =>
            change.Filename == "followpoint-1.png" && change.IsDeletion);
    }

    [Fact]
    public void Exact_family_duplicate_is_skipped_even_when_extracted_twice()
    {
        var extras = Path.Combine(Path.GetTempPath(), $"kumori-extras-{Guid.NewGuid():N}");
        try
        {
            var service = new SkinExtrasExtractionService();
            var source = service.BuildSource(
                "Skin",
                "memory",
                [new SkinExtractionFile("combobreak.wav", [1, 2, 3, 4])]);
            var family = Assert.Single(service.Analyze(source));

            var first = Assert.Single(service.Extract(source, [family], extras));
            var second = Assert.Single(service.Extract(source, [family], extras));

            Assert.Equal(SkinExtraExtractionStatus.Extracted, first.Status);
            Assert.Equal(SkinExtraExtractionStatus.ExactDuplicateSkipped, second.Status);
            Assert.Equal(first.DirectoryPath, second.DirectoryPath);
        }
        finally
        {
            if (Directory.Exists(extras)) Directory.Delete(extras, recursive: true);
        }
    }

    [Fact]
    public void Family_plan_does_not_delete_neighbouring_category_assets()
    {
        var manifest = new SkinExtraPackManifest
        {
            Id = "cursor-pack",
            DisplayName = "Cursor pack",
            FamilyId = "osu.cursor",
            Area = "osu!",
            FamilyName = "Cursor",
            Fingerprint = new string('f', 64),
        };
        var current = new[]
        {
            new LazerSkinFileInfo("cursor.png", new string('a', 64), 1),
            new LazerSkinFileInfo("cursortrail.png", new string('b', 64), 1),
            new LazerSkinFileInfo("menu-background.png", new string('c', 64), 1),
        };

        var plan = SkinExtraPackPlanner.BuildFamilyPlan(
            manifest,
            current,
            [new SkinExtraPackFile("cursor.png", [9])],
            currentIni: null);

        Assert.Contains(plan.Changes, change => change.Filename == "cursortrail.png" && change.IsDeletion);
        Assert.DoesNotContain(plan.Changes, change => change.Filename == "menu-background.png");
    }

    [Fact]
    public void Selected_file_plan_preserves_unchecked_files_in_the_same_family()
    {
        var manifest = new SkinExtraPackManifest
        {
            Id = "cursor-pack",
            DisplayName = "Cursor pack",
            FamilyId = "osu.cursor",
            Area = "osu!",
            FamilyName = "Cursor",
            Fingerprint = new string('f', 64),
        };
        var current = new[]
        {
            new LazerSkinFileInfo("cursor.png", new string('a', 64), 1),
            new LazerSkinFileInfo("cursortrail.png", new string('b', 64), 1),
        };

        var plan = SkinExtraPackPlanner.BuildFamilyPlan(
            manifest,
            current,
            [new SkinExtraPackFile("cursor.png", [9])],
            currentIni: null,
            replaceEntireFamily: false);

        var change = Assert.Single(plan.Changes);
        Assert.Equal("cursor.png", change.Filename);
        Assert.False(change.IsDeletion);
        Assert.DoesNotContain(plan.Changes, item => item.Filename == "cursortrail.png");
    }

    [Fact]
    public void Selected_logical_element_removes_stale_resolution_but_preserves_other_elements()
    {
        var manifest = new SkinExtraPackManifest
        {
            Id = "cursor-pack",
            DisplayName = "Cursor pack",
            FamilyId = "osu.cursor",
            Area = "osu!",
            FamilyName = "Cursor",
            Fingerprint = new string('f', 64),
        };
        var current = new[]
        {
            new LazerSkinFileInfo("cursor.png", new string('a', 64), 1),
            new LazerSkinFileInfo("cursor@2x.png", new string('b', 64), 1),
            new LazerSkinFileInfo("cursortrail@2x.png", new string('c', 64), 1),
        };

        var plan = SkinExtraPackPlanner.BuildFamilyPlan(
            manifest,
            current,
            [new SkinExtraPackFile("cursor.png", [9])],
            currentIni: null,
            replaceEntireFamily: false,
            replaceSelectedLogicalElements: true);

        Assert.Contains(plan.Changes, change =>
            change.Filename == "cursor@2x.png" && change.IsDeletion);
        Assert.Contains(plan.Changes, change =>
            change.Filename == "cursor.png" && !change.IsDeletion);
        Assert.DoesNotContain(plan.Changes, change =>
            change.Filename == "cursortrail@2x.png");
    }

    [Fact]
    public void Extras_ini_information_lists_only_values_that_will_change()
    {
        var manifest = new SkinExtraPackManifest
        {
            Id = "slider-pack",
            DisplayName = "Slider pack",
            FamilyId = "osu.slider",
            Area = "osu!",
            FamilyName = "Sliders",
            Fingerprint = new string('e', 64),
            IniPatch =
            [
                new SkinExtraIniPatchEntry("General", "AllowSliderBallTint", "1"),
                new SkinExtraIniPatchEntry("Colours", "SliderBorder", "255,0,255"),
            ],
        };
        var current = SkinIniDocument.ParseText(
            "[General]\nAllowSliderBallTint: 1\n\n[Colours]\nSliderBorder: 255,255,255\n");

        var description = SkinExtrasPickerWindow.DescribeIniPatchChanges(manifest, current);

        Assert.DoesNotContain("AllowSliderBallTint", description);
        Assert.Contains(
            "[Colours] SliderBorder: 255,255,255 → 255,0,255",
            description);
    }

    [Fact]
    public void Number_font_plan_preserves_source_names_and_removes_replaced_prefix()
    {
        var manifest = new SkinExtraPackManifest
        {
            Id = "font-pack",
            DisplayName = "Font pack",
            FamilyId = "osu.number-font",
            Area = "osu!",
            FamilyName = "Number fonts",
            Fingerprint = new string('a', 64),
            FontRoles = ["Hitcircle"],
            IniPatch =
            [
                new SkinExtraIniPatchEntry("Fonts", "HitCirclePrefix", "default"),
                new SkinExtraIniPatchEntry("Fonts", "HitCircleOverlap", "-2"),
            ],
        };
        var ini = SkinIniDocument.ParseText(
            "[Fonts]\nHitCirclePrefix: kumori-font-old\nScorePrefix: score\n");
        var current = new[]
        {
            new LazerSkinFileInfo("kumori-font-old-0.png", new string('b', 64), 1),
            new LazerSkinFileInfo("score-0.png", new string('c', 64), 1),
        };

        var plan = SkinExtraPackPlanner.BuildFamilyPlan(
            manifest,
            current,
            [new SkinExtraPackFile("default-0.png", [1])],
            ini);

        Assert.Contains(plan.Changes, change => change.Filename == "kumori-font-old-0.png" && change.IsDeletion);
        Assert.Contains(plan.Changes, change => change.Filename == "default-0.png");
        Assert.DoesNotContain(plan.Changes, change =>
            change.Filename.StartsWith("kumori-font-", StringComparison.OrdinalIgnoreCase)
            && !change.IsDeletion);
        Assert.DoesNotContain(plan.Changes, change => change.Filename == "score-0.png");
        Assert.Contains(plan.IniPatch, entry =>
            entry.Key == "HitCirclePrefix" && entry.Value == "default");
    }

    [Fact]
    public void Number_font_plan_preserves_one_shared_source_and_its_prefixes()
    {
        var manifest = new SkinExtraPackManifest
        {
            Id = "shared-font-pack",
            DisplayName = "Shared font pack",
            FamilyId = "osu.number-font",
            Area = "osu!",
            FamilyName = "Number fonts",
            Fingerprint = new string('a', 64),
            FontRoles = ["Hitcircle", "Score", "Combo"],
            IniPatch =
            [
                new SkinExtraIniPatchEntry("Fonts", "HitCirclePrefix", "numbers"),
                new SkinExtraIniPatchEntry("Fonts", "ScorePrefix", "numbers"),
                new SkinExtraIniPatchEntry("Fonts", "ComboPrefix", "numbers"),
            ],
        };

        var plan = SkinExtraPackPlanner.BuildFamilyPlan(
            manifest,
            [],
            [new SkinExtraPackFile("numbers-7@2x.png", [7])],
            SkinIniDocument.ParseText("[Fonts]\nHitCirclePrefix: default\nScorePrefix: score\nComboPrefix: score\n"));

        Assert.Equal("numbers-7@2x.png", Assert.Single(plan.Changes).Filename);
        Assert.Contains(plan.IniPatch, entry =>
            entry.Key == "HitCirclePrefix" && entry.Value == "numbers");
        Assert.Contains(plan.IniPatch, entry =>
            entry.Key == "ScorePrefix" && entry.Value == "numbers");
        Assert.Contains(plan.IniPatch, entry =>
            entry.Key == "ComboPrefix" && entry.Value == "numbers");
    }

    [Fact]
    public void Number_font_plan_preserves_custom_score_prefix_and_filename()
    {
        var manifest = new SkinExtraPackManifest
        {
            Id = "score-font-pack",
            DisplayName = "Score font pack",
            FamilyId = "osu.number-font",
            Area = "osu!",
            FamilyName = "Number fonts",
            Fingerprint = new string('b', 64),
            FontRoles = ["Score"],
            IniPatch =
            [
                new SkinExtraIniPatchEntry("Fonts", "ScorePrefix", "points"),
            ],
        };

        var plan = SkinExtraPackPlanner.BuildFamilyPlan(
            manifest,
            [],
            [new SkinExtraPackFile("points-0.png", [0])],
            SkinIniDocument.ParseText("[Fonts]\nScorePrefix: score\n"));

        Assert.Equal("points-0.png", Assert.Single(plan.Changes).Filename);
        Assert.Equal(
            "points",
            Assert.Single(plan.IniPatch, entry => entry.Key == "ScorePrefix").Value);
    }

    [Fact]
    public void Number_font_plan_keeps_shared_current_prefix_used_by_an_untouched_role()
    {
        var manifest = new SkinExtraPackManifest
        {
            Id = "score-font-pack",
            DisplayName = "Score font pack",
            FamilyId = "osu.number-font",
            Area = "osu!",
            FamilyName = "Number fonts",
            Fingerprint = new string('c', 64),
            FontRoles = ["Score"],
            IniPatch =
            [
                new SkinExtraIniPatchEntry("Fonts", "ScorePrefix", "points"),
            ],
        };
        var current = new[]
        {
            new LazerSkinFileInfo("shared-0.png", new string('d', 64), 1),
        };

        var plan = SkinExtraPackPlanner.BuildFamilyPlan(
            manifest,
            current,
            [new SkinExtraPackFile("points-0.png", [0])],
            SkinIniDocument.ParseText(
                "[Fonts]\nHitCirclePrefix: default\nScorePrefix: shared\nComboPrefix: shared\n"));

        Assert.DoesNotContain(plan.Changes, change =>
            change.Filename == "shared-0.png" && change.IsDeletion);
        Assert.Contains(plan.Changes, change => change.Filename == "points-0.png");
    }

    private static LazerSkinFileInfo File(string filename) =>
        new(filename, new string('a', 64), 10);

    private static SkinExtraManifestFile ManifestFile(string filename) =>
        new(filename, filename, filename, new string('a', 64), new string('b', 64));

    private static byte[] TransparentPng(
        int width,
        int height,
        byte red,
        byte green,
        byte blue)
    {
        var pixels = new byte[width * height * 4];
        for (var index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = blue;
            pixels[index + 1] = green;
            pixels[index + 2] = red;
        }
        var bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
