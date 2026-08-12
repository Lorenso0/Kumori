using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using Kumori.App.Skins;
using Kumori.Skins;
using Xunit;

namespace Kumori.App.Tests;

public sealed class HybridSkinStudioTests
{
    [Fact]
    public void Shared_element_catalog_maps_real_gameplay_scenes()
    {
        Assert.Equal(900, SkinStudioPreviewScenes.TimeMilliseconds(SkinStudioPreviewScene.Circles));
        Assert.Equal(2_550, SkinStudioPreviewScenes.TimeMilliseconds(SkinStudioPreviewScene.Sliders));
        Assert.Equal(2_900, SkinStudioPreviewScenes.TimeMilliseconds(SkinStudioPreviewScene.Hud));
        Assert.Equal(5_100, SkinStudioPreviewScenes.TimeMilliseconds(SkinStudioPreviewScene.Cursor));
        Assert.Equal(7_900, SkinStudioPreviewScenes.TimeMilliseconds(SkinStudioPreviewScene.Spinner));
        Assert.Equal(11_050, SkinStudioPreviewScenes.TimeMilliseconds(SkinStudioPreviewScene.Judgements));
        Assert.Equal(6_500, SkinStudioPreviewScenes.TimeMilliseconds(SkinStudioPreviewScene.Followpoints));
        Assert.Equal(
            SkinStudioPreviewScene.Spinner,
            SkinStudioElementCatalog.Find("spinner-circle")?.PreviewScene);
        Assert.Null(SkinStudioElementCatalog.Find("ranking-panel")?.PreviewScene);
        Assert.True(SkinStudioElementCatalog.Categories.Sum(category => category.Elements.Count) >= 130);
        Assert.Equal(
            ["Hit objects", "Sliders", "Cursor", "Judgements", "HUD & interface", "Numbers", "Spinner", "Catch", "Taiko", "Mania", "Modes & other"],
            SkinStudioElementCatalog.LegacySidebarCategories.Select(category => category.Title));
        Assert.True(
            (int)SkinStudioRendererColourTarget.ElementTint
            > (int)SkinStudioRendererColourTarget.SliderOuter);
        Assert.True(
            (int)SkinStudioRendererCommandKind.SetSmoothTrail
            > (int)SkinStudioRendererCommandKind.PollEvent);
    }

    [Fact]
    public void Gameplay_overview_uses_the_skin_combo_colour_count()
    {
        var ini = SkinIniDocument.ParseText(
            "[Colours]\r\n"
            + "Combo1: 255,0,0\r\n"
            + "Combo2: 0,255,0\r\n"
            + "Combo3: 0,0,255\r\n"
            + "Combo4: 255,255,0\r\n"
            + "Combo5: 255,0,255\r\n");

        Assert.Equal(5, SkinStudioPreviewScenes.ComboColourCount(ini));
        Assert.Equal(4, SkinStudioPreviewScenes.ComboColourCount(
            SkinIniDocument.ParseText("[General]\r\nName: Default palette\r\n")));
    }

    [Theory]
    [InlineData("osu.cursor", "cursor", SkinStudioPreviewScene.Cursor)]
    [InlineData("osu.hitcircles", "hitcircleoverlay", SkinStudioPreviewScene.Circles)]
    [InlineData("osu.followpoints", "unknown", SkinStudioPreviewScene.Followpoints)]
    [InlineData("osu.spinner", "unknown", SkinStudioPreviewScene.Spinner)]
    [InlineData("interface.scorebar", "unknown", SkinStudioPreviewScene.Hud)]
    public void Extras_families_choose_a_stationary_lazer_inspection_scene(
        string family,
        string component,
        SkinStudioPreviewScene expected)
    {
        Assert.Equal(
            expected,
            SkinStudioExtrasPreview.SceneFor(family, [component]));
    }

    [Fact]
    public void Renderer_contract_contains_no_player_write_capability()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"kumori-renderer-contract-{Guid.NewGuid():N}");
        try
        {
            var session = Guid.NewGuid();
            var contract = new SkinStudioRendererLaunchContract
            {
                WorkspacePath = workspace,
                DraftId = Guid.NewGuid(),
                DraftRevision = 4,
                SessionId = session,
                CommandPipeName = $"kumori-skin-renderer-{session:N}",
            }.Normalize();
            var json = JsonSerializer.Serialize(contract, SkinStudioLaunchContract.JsonOptions);
            Assert.DoesNotContain("player_root", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("live_sync", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("reload_pipe", json, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(Path.GetFullPath(workspace), contract.WorkspacePath);
        }
        finally
        {
            try { Directory.Delete(workspace, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Renderer_protocol_round_trips_and_rejects_oversized_messages()
    {
        var request = new SkinStudioRendererRequest
        {
            RequestId = Guid.NewGuid(),
            Command = SkinStudioRendererCommandKind.Seek,
            Scene = SkinStudioPreviewScene.Spinner,
            Component = "osu.spinner",
            Components = ["spinner-circle", "spinner-top"],
        };
        using var stream = new MemoryStream();
        await SkinStudioRendererPipeProtocol.WriteAsync(stream, request);
        stream.Position = 0;
        var restored = await SkinStudioRendererPipeProtocol
            .ReadAsync<SkinStudioRendererRequest>(stream);
        Assert.NotNull(restored);
        Assert.Equal(request.RequestId, restored.RequestId);
        Assert.Equal(request.Command, restored.Command);
        Assert.Equal(request.Scene, restored.Scene);
        Assert.Equal(request.Component, restored.Component);
        Assert.Equal(request.Components, restored.Components);

        var semanticTarget = SkinStudioSemanticPreviewCatalog.Resolve(
            "default-7",
            "osu.number-font");
        using var semanticStream = new MemoryStream();
        var semanticRequest = new SkinStudioRendererRequest
        {
            Command = SkinStudioRendererCommandKind.SelectPreviewTarget,
            PreviewTargetId = semanticTarget.Id,
            FamilyId = semanticTarget.FamilyId,
            Component = semanticTarget.ComponentName,
            Ruleset = semanticTarget.Ruleset,
            ManiaKeyCount = semanticTarget.ManiaKeyCount,
        };
        await SkinStudioRendererPipeProtocol.WriteAsync(
            semanticStream,
            semanticRequest);
        semanticStream.Position = 0;
        Assert.Equal(
            semanticRequest,
            await SkinStudioRendererPipeProtocol
                .ReadAsync<SkinStudioRendererRequest>(semanticStream));

        using var motionStream = new MemoryStream();
        var motion = new SkinStudioRendererRequest
        {
            Command = SkinStudioRendererCommandKind.SetAutoMotion,
            Active = true,
        };
        await SkinStudioRendererPipeProtocol.WriteAsync(motionStream, motion);
        motionStream.Position = 0;
        Assert.Equal(
            motion,
            await SkinStudioRendererPipeProtocol.ReadAsync<SkinStudioRendererRequest>(motionStream));

        using var smoothTrailStream = new MemoryStream();
        var smoothTrail = new SkinStudioRendererRequest
        {
            Command = SkinStudioRendererCommandKind.SetSmoothTrail,
            Active = true,
        };
        await SkinStudioRendererPipeProtocol.WriteAsync(
            smoothTrailStream,
            smoothTrail);
        smoothTrailStream.Position = 0;
        Assert.Equal(
            smoothTrail,
            await SkinStudioRendererPipeProtocol
                .ReadAsync<SkinStudioRendererRequest>(smoothTrailStream));

        using var liveColourStream = new MemoryStream();
        var liveColour = new SkinStudioRendererRequest
        {
            Command = SkinStudioRendererCommandKind.SetPreviewColour,
            ColourTarget = SkinStudioRendererColourTarget.SliderOuter,
            ColourRed = 96,
            ColourGreen = 78,
            ColourBlue = 78,
        };
        await SkinStudioRendererPipeProtocol.WriteAsync(liveColourStream, liveColour);
        liveColourStream.Position = 0;
        Assert.Equal(
            liveColour,
            await SkinStudioRendererPipeProtocol
                .ReadAsync<SkinStudioRendererRequest>(liveColourStream));

        using var tintStream = new MemoryStream();
        var tint = new SkinStudioRendererRequest
        {
            Command = SkinStudioRendererCommandKind.SetPreviewColour,
            ColourTarget = SkinStudioRendererColourTarget.ElementTint,
            Component = "cursortrail",
            ColourRed = 255,
            ColourGreen = 102,
            ColourBlue = 170,
        };
        await SkinStudioRendererPipeProtocol.WriteAsync(tintStream, tint);
        tintStream.Position = 0;
        Assert.Equal(
            tint,
            await SkinStudioRendererPipeProtocol
                .ReadAsync<SkinStudioRendererRequest>(tintStream));

        using var previewScaleStream = new MemoryStream();
        var previewScale = new SkinStudioRendererRequest
        {
            Command = SkinStudioRendererCommandKind.SetPreviewScale,
            CursorScale = 1.25,
            ObjectScale = 0.9,
        };
        await SkinStudioRendererPipeProtocol.WriteAsync(
            previewScaleStream,
            previewScale);
        previewScaleStream.Position = 0;
        Assert.Equal(
            previewScale,
            await SkinStudioRendererPipeProtocol
                .ReadAsync<SkinStudioRendererRequest>(previewScaleStream));

        using var colourEventStream = new MemoryStream();
        var colourEvent = new SkinStudioRendererResponse
        {
            RequestId = Guid.NewGuid(),
            Accepted = true,
            Message = "Colour edit requested.",
            Event = SkinStudioRendererEventKind.ColourEditRequested,
            ColourTarget = SkinStudioRendererColourTarget.SliderInner,
            ColourRed = 12,
            ColourGreen = 34,
            ColourBlue = 56,
            AnchorX = 0.72,
            AnchorY = 0.41,
            AvoidLeft = 0.35,
            AvoidTop = 0.38,
            AvoidRight = 0.82,
            AvoidBottom = 0.74,
        };
        await SkinStudioRendererPipeProtocol.WriteAsync(colourEventStream, colourEvent);
        colourEventStream.Position = 0;
        Assert.Equal(
            colourEvent,
            await SkinStudioRendererPipeProtocol
                .ReadAsync<SkinStudioRendererResponse>(colourEventStream));

        using var pollStream = new MemoryStream();
        var poll = new SkinStudioRendererRequest
        {
            Command = SkinStudioRendererCommandKind.PollEvent,
        };
        await SkinStudioRendererPipeProtocol.WriteAsync(pollStream, poll);
        pollStream.Position = 0;
        Assert.Equal(
            poll,
            await SkinStudioRendererPipeProtocol
                .ReadAsync<SkinStudioRendererRequest>(pollStream));

        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(
            header,
            SkinStudioRendererPipeProtocol.MaximumMessageBytes + 1);
        using var invalid = new MemoryStream(header);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            SkinStudioRendererPipeProtocol.ReadAsync<SkinStudioRendererRequest>(invalid));
    }

    [Fact]
    public void Every_recognised_family_and_editor_element_has_a_semantic_preview()
    {
        Assert.All(
            SkinStudioSemanticPreviewCatalog.FamilyDescriptors,
            descriptor => Assert.False(descriptor.IsRaw));
        Assert.Equal(
            SkinExtraFamilyRegistry.All.Count - 1,
            SkinStudioSemanticPreviewCatalog.FamilyDescriptors.Count);
        Assert.All(
            SkinStudioElementCatalog.Categories.SelectMany(category => category.Elements),
            element => Assert.False(element.SemanticPreview.IsRaw));
        Assert.Contains(
            SkinStudioElementCatalog.Categories,
            category => category.Title == "Catch");
        Assert.Contains(
            SkinStudioElementCatalog.Categories,
            category => category.Title == "Taiko");
        Assert.Contains(
            SkinStudioElementCatalog.Categories,
            category => category.Title == "Mania");
    }

    [Fact]
    public void Hitcircle_numbers_and_hitsound_loops_use_logical_contexts()
    {
        var numbers = SkinStudioSemanticPreviewCatalog.Resolve(
            "default-0",
            "osu.number-font");
        Assert.Equal(
            SkinStudioSemanticPreviewKind.HitCircleNumbers,
            numbers.Kind);
        Assert.Equal(SkinStudioPreviewScene.Circles, numbers.Scene);
        Assert.Equal(10, SkinStudioSemanticPreviewCatalog.HitCircleNumberPreviewCount);

        var hitsounds = SkinStudioSemanticPreviewCatalog.Resolve(
            "soft-hitnormal",
            "audio.hitsounds.soft");
        var plan = SkinStudioSemanticAudioPlan.Build(hitsounds);
        Assert.Equal(500, plan.IntervalMilliseconds);
        Assert.Equal(
            ["soft-hitnormal", "soft-hitwhistle", "soft-hitfinish", "soft-hitclap"],
            plan.Components);
        Assert.Equal(
            ["soft-hitnormal", "soft-hitclap"],
            SkinStudioSemanticAudioPlan.LayeredComponents(
                "soft-hitclap",
                layered: true));
        Assert.Equal(
            ["soft-hitclap"],
            SkinStudioSemanticAudioPlan.LayeredComponents(
                "soft-hitclap",
                layered: false));
    }

    [Theory]
    [InlineData("fruit-pear", "catch.fruits", SkinStudioRuleset.Catch, SkinStudioSemanticPreviewKind.Catch)]
    [InlineData("taikohitcircle", "taiko.notes", SkinStudioRuleset.Taiko, SkinStudioSemanticPreviewKind.Taiko)]
    [InlineData("mania-note", "mania.notes", SkinStudioRuleset.Mania, SkinStudioSemanticPreviewKind.Mania)]
    [InlineData("followpoint", "osu.followpoints", SkinStudioRuleset.Osu, SkinStudioSemanticPreviewKind.FollowPoints)]
    public void Mode_families_resolve_to_their_semantic_native_context(
        string component,
        string family,
        SkinStudioRuleset ruleset,
        SkinStudioSemanticPreviewKind kind)
    {
        var descriptor = SkinStudioSemanticPreviewCatalog.Resolve(
            component,
            family);
        Assert.Equal(ruleset, descriptor.Ruleset);
        Assert.Equal(kind, descriptor.Kind);
        Assert.False(descriptor.IsRaw);
    }

    [Fact]
    public async Task Renderer_client_rejects_an_uncorrelated_response()
    {
        var pipeName = $"kumori-skin-renderer-{Guid.NewGuid():N}";
        var server = Task.Run(async () =>
        {
            using var pipe = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.WaitForConnectionAsync();
            _ = await SkinStudioRendererPipeProtocol.ReadAsync<SkinStudioRendererRequest>(pipe);
            await SkinStudioRendererPipeProtocol.WriteAsync(pipe, new SkinStudioRendererResponse
            {
                RequestId = Guid.NewGuid(),
                Accepted = true,
                Message = "wrong request",
            });
        });
        var client = new SkinStudioRendererPipeClient(pipeName);
        await Assert.ThrowsAsync<InvalidDataException>(() => client.SendAsync(new SkinStudioRendererRequest
        {
            Command = SkinStudioRendererCommandKind.Pause,
        }));
        await server;
    }

    [Fact]
    public void Workspace_controller_uses_atomic_revisions_for_sidebar_edits()
    {
        var root = Path.Combine(Path.GetTempPath(), $"kumori-hybrid-controller-{Guid.NewGuid():N}");
        var replacement = Path.Combine(root, "replacement.png");
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllBytes(replacement, Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAFgwJ/lb27WQAAAABJRU5ErkJggg=="));
            var controller = new SkinStudioWorkspaceController(Path.Combine(root, "workspace"));
            controller.Initialize();
            var initialRevision = controller.CurrentRevision;
            var stateChanges = 0;
            controller.StateChanged += (_, _) => stateChanges++;

            controller.Select("hitcircle");
            Assert.Equal(0, stateChanges);
            controller.ReplaceSelected(replacement);
            Assert.Equal(1, stateChanges);
            Assert.True(controller.CurrentRevision > initialRevision);
            Assert.Single(controller.SelectedFamily);
            Assert.True(controller.CurrentDraft.CanUndo);

            var replacementRevision = controller.CurrentRevision;
            controller.DeleteSelected();
            Assert.True(controller.CurrentRevision > replacementRevision);
            Assert.Empty(controller.SelectedFamily);

            controller.Undo();
            Assert.Single(controller.SelectedFamily);
            controller.Redo();
            Assert.Empty(controller.SelectedFamily);

            controller.SaveSkinIni("[General]\r\nName: Hybrid test\r\nAuthor: Kumori\r\nVersion: 2.7\r\n");
            Assert.Contains("Hybrid test", controller.ReadSkinIni(), StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Workspace_controller_manages_drafts_and_individual_changes_recoverably()
    {
        var root = Path.Combine(Path.GetTempPath(), $"kumori-hybrid-drafts-{Guid.NewGuid():N}");
        try
        {
            var controller = new SkinStudioWorkspaceController(root);
            controller.Initialize();
            var originalId = controller.CurrentDraft.DraftId;
            controller.DuplicateCurrent();
            Assert.NotEqual(originalId, controller.CurrentDraft.DraftId);
            Assert.EndsWith(" Copy", controller.CurrentDraft.Name, StringComparison.Ordinal);

            controller.RenameCurrent("Managed draft", "Kumori test");
            Assert.Equal("Managed draft", controller.CurrentDraft.Name);
            Assert.Equal("Kumori test", controller.CurrentDraft.Creator);

            controller.SaveSkinIni("[General]\r\nName: Changed\r\nAuthor: Kumori test\r\nVersion: 2.7\r\n");
            var changedFilename = Assert.Single(controller.CurrentDraft.Changes).Filename;
            controller.DiscardChange(changedFilename);
            Assert.Empty(controller.CurrentDraft.Changes);

            var deletedId = controller.CurrentDraft.DraftId;
            controller.DeleteCurrentRecoverably();
            Assert.DoesNotContain(controller.Drafts, draft => draft.DraftId == deletedId);
            controller.RestoreLatestDeleted();
            Assert.Equal(deletedId, controller.CurrentDraft.DraftId);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Extras_renderer_preview_draft_is_hidden_from_the_studio_draft_picker()
    {
        var root = Path.Combine(Path.GetTempPath(), $"kumori-hybrid-preview-{Guid.NewGuid():N}");
        try
        {
            var studio = new SkinStudioWorkspaceController(root);
            studio.Initialize();
            var visibleId = studio.CurrentDraft.DraftId;
            var preview = new SkinStudioWorkspaceController(root);
            preview.InitializeExtrasPreview();

            Assert.NotEqual(visibleId, preview.CurrentDraft.DraftId);
            Assert.DoesNotContain(studio.Drafts, draft =>
                draft.DraftId == preview.CurrentDraft.DraftId);
            Assert.Contains(studio.Drafts, draft => draft.DraftId == visibleId);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Sidebar_distinguishes_real_lazer_assets_from_legacy_slider_files()
    {
        var root = Path.Combine(Path.GetTempPath(), $"kumori-hybrid-elements-{Guid.NewGuid():N}");
        try
        {
            var controller = new SkinStudioWorkspaceController(root);
            controller.Initialize();
            controller.SaveSkinIni("[General]\r\nName: Element test\r\nVersion: 2.7\r\n");
            var files = Path.Combine(root, "files");
            Directory.CreateDirectory(files);
            var reverse = Path.Combine(files, "reversearrow.png");
            var legacy = Path.Combine(files, "sliderpoint10.png");
            File.WriteAllBytes(reverse, [1]);
            File.WriteAllBytes(legacy, [2]);
            controller.ImportFiles([reverse, legacy]);

            Assert.True(controller.IsUsedByLazer("reversearrow"));
            Assert.False(controller.IsUsedByLazer("sliderpoint10"));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("wrong-prefix")]
    [InlineData("kumori-skin-renderer-has spaces")]
    public void Renderer_pipe_name_validation_fails_closed(string pipeName)
    {
        Assert.Throws<InvalidDataException>(() =>
            SkinStudioRendererLaunchContract.ValidatePipeName(pipeName));
    }
}
