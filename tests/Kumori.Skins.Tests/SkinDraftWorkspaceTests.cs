using System.IO.Compression;
using Kumori.Skins;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Kumori.Skins.Tests;

public sealed class SkinDraftWorkspaceTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        $"kumori-skin-workspace-{Guid.NewGuid():N}");

    [Fact]
    public void Contract_rejects_workspace_overlapping_player_root()
    {
        var contract = new SkinStudioLaunchContract
        {
            WorkspacePath = Path.Combine(root, "osu", "studio"),
            PlayerRoot = Path.Combine(root, "osu"),
        };

        Assert.Throws<InvalidDataException>(contract.Normalize);
    }

    [Fact]
    public void Normal_write_boundary_rejects_every_root_that_can_reach_player_data()
    {
        var player = Path.Combine(root, "osu");
        var workspace = Path.Combine(root, "kumori", "studio");
        var extras = Path.Combine(root, "kumori", "extras");

        SkinStudioWriteBoundary.AssertNormalRootsAreIsolated(
            player,
            workspace,
            extras);
        Assert.True(SkinStudioWriteBoundary.IsNormalWriteAllowed(
            player,
            Path.Combine(workspace, "drafts", "manifest.json")));
        Assert.False(SkinStudioWriteBoundary.IsNormalWriteAllowed(
            player,
            Path.Combine(player, "client.realm")));
        Assert.False(SkinStudioWriteBoundary.IsNormalWriteAllowed(
            player,
            Path.Combine(player, "exports", "skin.osk")));
        Assert.True(SkinStudioWriteBoundary.IsNormalWriteAllowed(
            player,
            Path.Combine(root, "user-exports", "skin.osk")));
        Assert.Throws<InvalidDataException>(() =>
            SkinStudioWriteBoundary.AssertNormalRootsAreIsolated(
                player,
                Path.Combine(player, "studio")));
        Assert.Throws<InvalidDataException>(() =>
            SkinStudioWriteBoundary.AssertNormalRootsAreIsolated(
                Path.Combine(player, "files"),
                player));
    }

    [Fact]
    public void Draft_history_recovers_staged_files_and_branches_after_undo()
    {
        var service = new SkinDraftWorkspaceService(root);
        var draft = service.Create("Test", "Kumori");
        draft = service.StageFile(draft.DraftId, "cursor.png", [1, 2, 3], null, "Cursor");
        draft = service.StageDelete(
            draft.DraftId,
            "cursortrail.png",
            new string('a', 64),
            "Trail");

        Assert.Equal(2, draft.Changes.Count);
        Assert.True(draft.CanUndo);

        draft = service.Undo(draft.DraftId);
        Assert.Single(draft.Changes);
        Assert.True(draft.CanRedo);

        draft = service.StageFile(draft.DraftId, "skin.ini", [4, 5], null, "INI");
        Assert.False(draft.CanRedo);
        Assert.Equal(2, draft.Changes.Count);

        var recovered = new SkinDraftWorkspaceService(root).Load(draft.DraftId);
        Assert.Equal(draft.DraftId, recovered.DraftId);
        Assert.Equal(draft.HistoryIndex, recovered.HistoryIndex);
        Assert.Equal(
            draft.Changes.Select(change => change.Filename),
            recovered.Changes.Select(change => change.Filename));
        var cursor = Assert.Single(recovered.Changes, change => change.Filename == "cursor.png");
        Assert.Equal([1, 2, 3], service.ReadObject(draft.DraftId, cursor.ContentHash!));
    }

    [Theory]
    [InlineData("../escape.png")]
    [InlineData("folder/../../escape.png")]
    [InlineData("/")]
    public void Draft_rejects_unsafe_filenames(string filename)
    {
        Assert.ThrowsAny<Exception>(
            () => SkinDraftWorkspaceService.NormalizeSkinFilename(filename));
    }

    [Fact]
    public void Export_applies_changes_without_mutating_source()
    {
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "source.osk");
        using (var archive = ZipFile.Open(source, ZipArchiveMode.Create))
        {
            Write(archive, "skin.ini", "[General]\nName: Source\n"u8.ToArray());
            Write(archive, "cursor.png", [1]);
        }
        var sourceHash = SkinPackageService.Fingerprint(source);
        var workspaceRoot = Path.Combine(root, "workspace");
        var service = new SkinDraftWorkspaceService(workspaceRoot);
        var draft = service.Create(
            "Draft",
            "Kumori",
            source,
            sourceHash);
        service.StageFile(draft.DraftId, "cursor.png", [9, 8, 7], null, "Replace");
        var destination = Path.Combine(root, "export.osk");

        new SkinPackageService(service).Export(draft.DraftId, destination);

        Assert.Equal(sourceHash, SkinPackageService.Fingerprint(source));
        using var exported = ZipFile.OpenRead(destination);
        var cursor = exported.GetEntry("cursor.png")!;
        using var stream = cursor.Open();
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        Assert.Equal([9, 8, 7], bytes.ToArray());
    }

    [Fact]
    public void Export_accepts_an_already_materialized_snapshot()
    {
        Directory.CreateDirectory(root);
        var destination = Path.Combine(root, "snapshot-export.osk");
        var files = new Dictionary<string, byte[]>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["skin.ini"] = "[General]\nName: Snapshot\n"u8.ToArray(),
            ["cursor.png"] = [3, 2, 1],
        };

        var service = new SkinDraftWorkspaceService(
            Path.Combine(root, "workspace"));
        new SkinPackageService(service).Export(files, destination);

        using var exported = ZipFile.OpenRead(destination);
        Assert.Equal(2, exported.Entries.Count);
        using var cursor = exported.GetEntry("cursor.png")!.Open();
        using var bytes = new MemoryStream();
        cursor.CopyTo(bytes);
        Assert.Equal([3, 2, 1], bytes.ToArray());
    }

    [Fact]
    public void Installed_lazer_identity_survives_draft_persistence_and_duplication()
    {
        var service = new SkinDraftWorkspaceService(root);
        var skinId = Guid.NewGuid();

        var draft = service.Create(
            "Installed skin",
            "Creator",
            sourceLazerSkinId: skinId);
        var loaded = service.Load(draft.DraftId);
        var duplicate = service.Duplicate(draft.DraftId);

        Assert.Equal(skinId, loaded.SourceLazerSkinId);
        Assert.Equal(skinId, duplicate.SourceLazerSkinId);
    }

    [Fact]
    public void Imported_source_is_snapshotted_and_external_changes_are_reported()
    {
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "source.osk");
        using (var archive = ZipFile.Open(source, ZipArchiveMode.Create))
        {
            Write(archive, "skin.ini", "[General]\nName: Source\n"u8.ToArray());
            Write(archive, "hitcircle.png", [1, 2, 3]);
        }
        var service = new SkinDraftWorkspaceService(Path.Combine(root, "workspace"));
        var draft = service.Create("Draft", "Kumori", source);
        var snapshotHash = SkinPackageService.Fingerprint(draft.SourcePath!);

        using (var archive = ZipFile.Open(source, ZipArchiveMode.Update))
            Write(archive, "cursor.png", [9]);

        var check = service.CheckSource(draft.DraftId);
        Assert.Equal(SkinDraftSourceState.Changed, check.State);
        Assert.Equal(snapshotHash, SkinPackageService.Fingerprint(draft.SourcePath!));
        Assert.NotEqual(Path.GetFullPath(source), draft.SourcePath);
    }

    [Fact]
    public void Draft_can_be_renamed_and_all_changes_discarded_with_undo()
    {
        var service = new SkinDraftWorkspaceService(root);
        var draft = service.Create("Original", "Creator");
        draft = service.StageFile(draft.DraftId, "cursor.png", [1, 2, 3], null, "Cursor");

        draft = service.Rename(draft.DraftId, "Renamed", "New creator");
        Assert.Equal("Renamed", draft.Name);
        Assert.Equal("New creator", draft.Creator);

        draft = service.DiscardAll(draft.DraftId);
        Assert.Empty(draft.Changes);
        Assert.True(draft.CanUndo);

        draft = service.Undo(draft.DraftId);
        Assert.Single(draft.Changes);
    }

    [Fact]
    public void Identity_update_journals_skin_ini_without_losing_unknown_content()
    {
        var service = new SkinDraftWorkspaceService(root);
        var draft = service.Create("Original", "Creator");
        var original =
            "// keep me\r\n[General]\r\nName: Original\r\nAuthor: Creator\r\nUnknownThing: yes\r\n"u8.ToArray();
        draft = service.StageFile(draft.DraftId, "skin.ini", original, null, "ini");

        draft = service.UpdateIdentity(draft.DraftId, "Renamed", "New creator");

        Assert.Equal("Renamed", draft.Name);
        Assert.Equal("New creator", draft.Creator);
        var bytes = new SkinPackageService(service).Materialize(draft.DraftId)["skin.ini"];
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        Assert.Contains("// keep me\r\n", text);
        Assert.Contains("UnknownThing: yes\r\n", text);
        Assert.Contains("Name: Renamed\r\n", text);
        Assert.Contains("Author: New creator\r\n", text);
        Assert.Equal(
            "Update skin identity",
            Assert.Single(draft.Changes, change => change.Filename == "skin.ini").Description);
    }

    [Fact]
    public void Duplicate_is_an_independent_clean_snapshot_of_effective_files()
    {
        var service = new SkinDraftWorkspaceService(root);
        var source = service.Create("Source", "Kumori");
        source = service.StageFile(
            source.DraftId,
            "cursor.png",
            [9, 8, 7],
            null,
            "Cursor");

        var duplicate = service.Duplicate(source.DraftId, "Duplicate");

        Assert.NotEqual(source.DraftId, duplicate.DraftId);
        Assert.Equal("Duplicate", duplicate.Name);
        Assert.Null(duplicate.OriginPath);
        Assert.Empty(duplicate.Changes);
        Assert.True(File.Exists(duplicate.SourcePath));
        var materialized = new SkinPackageService(service).Materialize(duplicate.DraftId);
        Assert.Equal([9, 8, 7], materialized["cursor.png"]);

        service.StageFile(
            source.DraftId,
            "cursor.png",
            [1],
            source.Changes.Single().ContentHash,
            "Change original");
        Assert.Equal(
            [9, 8, 7],
            new SkinPackageService(service).Materialize(duplicate.DraftId)["cursor.png"]);
    }

    [Fact]
    public void Delete_is_recoverable_and_restore_rejects_unsafe_identifier()
    {
        var service = new SkinDraftWorkspaceService(root);
        var draft = service.Create("Recover me", "Kumori");
        var deleted = service.DeleteRecoverably(draft.DraftId);

        Assert.Empty(service.List());
        Assert.Equal(
            deleted.TrashName,
            Assert.Single(service.ListDeleted()).TrashName);
        Assert.Throws<InvalidDataException>(
            () => service.RestoreDeleted("../outside"));

        var restored = service.RestoreDeleted(deleted.TrashName);
        Assert.Equal(draft.DraftId, restored.DraftId);
        Assert.Equal("Recover me", restored.Name);
        Assert.Empty(service.ListDeleted());
    }

    [Fact]
    public void Interrupted_manifest_save_is_isolated_and_recoverable()
    {
        var service = new SkinDraftWorkspaceService(root);
        var draft = service.Create("Recover pending", "Kumori");
        var directory = Path.Combine(root, "drafts", draft.DraftId.ToString("N"));
        var manifest = Path.Combine(directory, "manifest.json");
        File.Copy(manifest, manifest + ".new");
        File.WriteAllText(manifest, "{ interrupted");

        Assert.Empty(service.List());
        var candidate = Assert.Single(service.ListRecoveryCandidates());
        Assert.False(candidate.ManifestValid);
        Assert.True(candidate.PendingManifestValid);
        Assert.Equal(draft.DraftId, candidate.DraftId);

        var recovered = service.RecoverPendingManifest(candidate.DirectoryName);

        Assert.Equal(draft.DraftId, recovered.DraftId);
        Assert.Equal("Recover pending", Assert.Single(service.List()).Name);
        Assert.Empty(service.ListRecoveryCandidates());
        Assert.Single(Directory.EnumerateFiles(
            Path.Combine(directory, "recovery-backups")));
    }

    [Fact]
    public void Draft_recovery_rejects_unsafe_or_mismatched_identifiers()
    {
        var service = new SkinDraftWorkspaceService(root);
        Assert.Throws<InvalidDataException>(() =>
            service.RecoverPendingManifest("../outside"));
        var draft = service.Create("Mismatch", "Kumori");
        var directory = Path.Combine(root, "drafts", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.Copy(
            Path.Combine(
                root,
                "drafts",
                draft.DraftId.ToString("N"),
                "manifest.json"),
            Path.Combine(directory, "manifest.json.new"));

        Assert.Throws<InvalidDataException>(() =>
            service.RecoverPendingManifest(Path.GetFileName(directory)));
    }

    [Fact]
    public void Asset_family_operations_group_frames_and_resolution_variants_atomically()
    {
        var service = new SkinDraftWorkspaceService(root);
        var draft = service.Create("Assets", "Kumori");
        draft = service.StageFile(draft.DraftId, "cursor.png", [1], null, "base");
        draft = service.StageFile(draft.DraftId, "cursor@2x.png", [2], null, "2x");
        draft = service.StageFile(draft.DraftId, "cursor-0.png", [3], null, "frame");
        draft = service.StageFile(draft.DraftId, "hitcircle.png", [4], null, "other");
        var assets = new SkinDraftAssetService(service);

        var family = assets.Family(draft.DraftId, "cursor");

        Assert.Equal(3, family.Count);
        Assert.Contains(family, asset => asset.IsTwoX);
        Assert.Contains(family, asset => asset.AnimationFrame == 0);

        var historyCount = draft.History.Count;
        draft = assets.DeleteFamily(draft.DraftId, "cursor");
        Assert.Equal(historyCount + 1, draft.History.Count);
        Assert.Equal(
            3,
            draft.Changes.Count(change => change.Kind == SkinDraftChangeKind.Delete));
        Assert.Single(draft.Changes, change => change.Filename == "hitcircle.png");

        draft = assets.ResetFamily(draft.DraftId, "cursor");
        Assert.Single(draft.Changes);
        Assert.Equal("hitcircle.png", draft.Changes[0].Filename);
    }

    [Fact]
    public void Asset_family_export_preserves_exact_effective_bytes()
    {
        var service = new SkinDraftWorkspaceService(root);
        var draft = service.Create("Assets", "Kumori");
        service.StageFile(draft.DraftId, "nested/sliderb-0.png", [7, 8, 9], null, "frame");
        var assets = new SkinDraftAssetService(service);
        var destination = Path.Combine(root, "exported");

        var written = assets.ExportFamily(draft.DraftId, "nested/sliderb", destination);

        var path = Assert.Single(written);
        Assert.Equal([7, 8, 9], File.ReadAllBytes(path));
        Assert.StartsWith(
            Path.GetFullPath(destination) + Path.DirectorySeparatorChar,
            Path.GetFullPath(path),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Image_family_transform_updates_all_variants_in_one_revision()
    {
        var service = new SkinDraftWorkspaceService(root);
        var draft = service.Create("Images", "Kumori");
        var source = Png(new Rgba32(10, 20, 30, 200));
        draft = service.StageFile(draft.DraftId, "cursor.png", source, null, "base");
        draft = service.StageFile(draft.DraftId, "cursor@2x.png", source, null, "2x");
        var historyCount = draft.History.Count;

        draft = new SkinDraftAssetService(service).TransformImageFamily(
            draft.DraftId,
            "cursor",
            new SkinImageTransform(
                SkinImageTransformMode.Colorize,
                new SkinRgb(220, 110, 55)));

        Assert.Equal(historyCount + 1, draft.History.Count);
        var files = new SkinPackageService(service).Materialize(draft.DraftId);
        foreach (var filename in new[] { "cursor.png", "cursor@2x.png" })
        {
            using var image = Image.Load<Rgba32>(files[filename]);
            Assert.Equal(new Rgba32(220, 110, 55, 200), image[0, 0]);
        }
    }

    [Fact]
    public void Image_transform_primary_pair_leaves_later_animation_frames_untouched()
    {
        var service = new SkinDraftWorkspaceService(root);
        var draft = service.Create("Scoped images", "Kumori");
        var first = Png(new Rgba32(10, 20, 30, 255));
        var later = Png(new Rgba32(40, 50, 60, 255));
        foreach (var filename in new[]
                 {
                     "cursor-0.png",
                     "cursor-0@2x.png",
                 })
            draft = service.StageFile(draft.DraftId, filename, first, null, "first");
        foreach (var filename in new[]
                 {
                     "cursor-1.png",
                     "cursor-1@2x.png",
                 })
            draft = service.StageFile(draft.DraftId, filename, later, null, "later");

        draft = new SkinDraftAssetService(service).TransformImageFamily(
            draft.DraftId,
            "cursor",
            new SkinImageTransform(
                SkinImageTransformMode.Colorize,
                new SkinRgb(200, 100, 50)),
            SkinImageTransformScope.PrimaryPair);

        var files = new SkinPackageService(service).Materialize(draft.DraftId);
        foreach (var filename in new[] { "cursor-0.png", "cursor-0@2x.png" })
        {
            using var image = Image.Load<Rgba32>(files[filename]);
            Assert.Equal(new Rgba32(200, 100, 50, 255), image[0, 0]);
        }
        foreach (var filename in new[] { "cursor-1.png", "cursor-1@2x.png" })
        {
            using var image = Image.Load<Rgba32>(files[filename]);
            Assert.Equal(new Rgba32(40, 50, 60, 255), image[0, 0]);
        }
    }

    [Fact]
    public void Image_transform_animation_frame_pair_updates_only_chosen_frame()
    {
        var service = new SkinDraftWorkspaceService(root);
        var draft = service.Create("Frame scope", "Kumori");
        var unchanged = Png(new Rgba32(10, 20, 30, 255));
        var selected = Png(new Rgba32(40, 50, 60, 255));
        foreach (var filename in new[] { "cursor-0.png", "cursor-0@2x.png" })
            draft = service.StageFile(draft.DraftId, filename, unchanged, null, "frame 0");
        foreach (var filename in new[] { "cursor-1.png", "cursor-1@2x.png" })
            draft = service.StageFile(draft.DraftId, filename, selected, null, "frame 1");

        draft = new SkinDraftAssetService(service).TransformImageFamily(
            draft.DraftId,
            "cursor",
            new SkinImageTransform(
                SkinImageTransformMode.Colorize,
                new SkinRgb(200, 100, 50)),
            SkinImageTransformScope.AnimationFramePair,
            1);

        var files = new SkinPackageService(service).Materialize(draft.DraftId);
        foreach (var filename in new[] { "cursor-0.png", "cursor-0@2x.png" })
        {
            using var image = Image.Load<Rgba32>(files[filename]);
            Assert.Equal(new Rgba32(10, 20, 30, 255), image[0, 0]);
        }
        foreach (var filename in new[] { "cursor-1.png", "cursor-1@2x.png" })
        {
            using var image = Image.Load<Rgba32>(files[filename]);
            Assert.Equal(new Rgba32(200, 100, 50, 255), image[0, 0]);
        }
    }

    [Fact]
    public void Image_transform_animation_frame_scope_requires_a_frame()
    {
        var service = new SkinDraftWorkspaceService(root);
        var draft = service.Create("Frame required", "Kumori");
        draft = service.StageFile(
            draft.DraftId,
            "cursor-0.png",
            Png(new Rgba32(10, 20, 30, 255)),
            null,
            "frame");

        var exception = Assert.Throws<ArgumentException>(() =>
            new SkinDraftAssetService(service).TransformImageFamily(
                draft.DraftId,
                "cursor",
                new SkinImageTransform(
                    SkinImageTransformMode.Colorize,
                    new SkinRgb(200, 100, 50)),
                SkinImageTransformScope.AnimationFramePair));

        Assert.Contains("animation frame", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Replacement_filename_preserves_resolution_and_animation_variant()
    {
        var service = new SkinDraftWorkspaceService(root);
        var draft = service.Create("Variants", "Kumori");
        foreach (var filename in new[]
                 {
                     "cursor-0.png",
                     "cursor-0@2x.png",
                     "cursor-1.png",
                     "cursor-1@2x.png",
                 })
            draft = service.StageFile(draft.DraftId, filename, [1], null, filename);
        var assets = new SkinDraftAssetService(service);

        Assert.Equal(
            "cursor-1@2x.png",
            assets.ResolveReplacementFilename(
                draft.DraftId,
                "cursor",
                "replacement-1@2x.png"));
        Assert.Equal(
            "cursor-0.png",
            assets.ResolveReplacementFilename(
                draft.DraftId,
                "cursor",
                "arbitrary.png"));
    }

    [Fact]
    public void Variant_summary_reports_resolution_and_frame_coverage()
    {
        var service = new SkinDraftWorkspaceService(root);
        var draft = service.Create("Summary", "Kumori");
        foreach (var filename in new[]
                 {
                     "cursor-0.png",
                     "cursor-0@2x.png",
                     "cursor-1.png",
                     "cursor-1@2x.png",
                 })
            draft = service.StageFile(draft.DraftId, filename, [1], null, filename);

        var summary = SkinDraftAssetService.VariantSummary(
            new SkinDraftAssetService(service).Family(draft.DraftId, "cursor"));

        Assert.Equal("4 file(s) · 1× + 2× · 2 frames (0–1)", summary);
    }

    [Fact]
    public void Multi_file_asset_import_is_one_atomic_revision()
    {
        var service = new SkinDraftWorkspaceService(root);
        var draft = service.Create("Batch import", "Kumori");
        var input = Path.Combine(root, "input");
        Directory.CreateDirectory(input);
        var cursor = Path.Combine(input, "cursor.png");
        var hit = Path.Combine(input, "normal-hitnormal.wav");
        File.WriteAllBytes(cursor, [1, 2, 3]);
        File.WriteAllBytes(hit, [4, 5]);
        var historyCount = draft.History.Count;

        draft = new SkinDraftAssetService(service).ImportFiles(
            draft.DraftId,
            [cursor, hit]);

        Assert.Equal(historyCount + 1, draft.History.Count);
        var files = new SkinPackageService(service).Materialize(draft.DraftId);
        Assert.Equal([1, 2, 3], files["cursor.png"]);
        Assert.Equal([4, 5], files["normal-hitnormal.wav"]);
    }

    [Fact]
    public void Multi_file_asset_import_rejects_duplicate_names_and_unsupported_files()
    {
        var service = new SkinDraftWorkspaceService(root);
        var draft = service.Create("Batch validation", "Kumori");
        var firstDirectory = Path.Combine(root, "one");
        var secondDirectory = Path.Combine(root, "two");
        Directory.CreateDirectory(firstDirectory);
        Directory.CreateDirectory(secondDirectory);
        var first = Path.Combine(firstDirectory, "cursor.png");
        var duplicate = Path.Combine(secondDirectory, "CURSOR.PNG");
        var unsupported = Path.Combine(firstDirectory, "readme.txt");
        File.WriteAllBytes(first, [1]);
        File.WriteAllBytes(duplicate, [2]);
        File.WriteAllText(unsupported, "no");
        var assets = new SkinDraftAssetService(service);

        Assert.Throws<InvalidDataException>(() =>
            assets.ImportFiles(draft.DraftId, [first, duplicate]));
        Assert.Throws<InvalidDataException>(() =>
            assets.ImportFiles(draft.DraftId, [unsupported]));
        Assert.Empty(service.Load(draft.DraftId).Changes);
    }

    [Fact]
    public void Delete_animation_frame_removes_both_resolutions_in_one_revision()
    {
        var service = new SkinDraftWorkspaceService(root);
        var draft = service.Create("Frame delete", "Kumori");
        foreach (var filename in new[]
                 {
                     "cursor-0.png",
                     "cursor-0@2x.png",
                     "cursor-1.png",
                     "cursor-1@2x.png",
                 })
            draft = service.StageFile(draft.DraftId, filename, [1], null, filename);
        var historyCount = draft.History.Count;

        draft = new SkinDraftAssetService(service).DeleteAnimationFrame(
            draft.DraftId,
            "cursor",
            1);

        Assert.Equal(historyCount + 1, draft.History.Count);
        var files = new SkinPackageService(service).Materialize(draft.DraftId);
        Assert.Contains("cursor-0.png", files.Keys);
        Assert.Contains("cursor-0@2x.png", files.Keys);
        Assert.DoesNotContain("cursor-1.png", files.Keys);
        Assert.DoesNotContain("cursor-1@2x.png", files.Keys);
    }

    [Fact]
    public void Insert_animation_frame_duplicates_both_resolutions_and_shifts_later_frames()
    {
        var service = new SkinDraftWorkspaceService(root);
        var draft = service.Create("Frame insert", "Kumori");
        draft = service.StageFile(draft.DraftId, "cursor-0.png", [10], null, "0");
        draft = service.StageFile(draft.DraftId, "cursor-0@2x.png", [20], null, "0x2");
        draft = service.StageFile(draft.DraftId, "cursor-1.png", [11], null, "1");
        draft = service.StageFile(draft.DraftId, "cursor-1@2x.png", [21], null, "1x2");
        var historyCount = draft.History.Count;

        draft = new SkinDraftAssetService(service).InsertAnimationFrame(
            draft.DraftId,
            "cursor",
            0,
            1);

        Assert.Equal(historyCount + 1, draft.History.Count);
        var files = new SkinPackageService(service).Materialize(draft.DraftId);
        Assert.Equal([10], files["cursor-0.png"]);
        Assert.Equal([20], files["cursor-0@2x.png"]);
        Assert.Equal([10], files["cursor-1.png"]);
        Assert.Equal([20], files["cursor-1@2x.png"]);
        Assert.Equal([11], files["cursor-2.png"]);
        Assert.Equal([21], files["cursor-2@2x.png"]);
    }

    [Fact]
    public void Move_animation_frame_reorders_every_resolution_in_one_revision()
    {
        var service = new SkinDraftWorkspaceService(root);
        var draft = service.Create("Frame move", "Kumori");
        foreach (var frame in Enumerable.Range(0, 3))
        {
            draft = service.StageFile(
                draft.DraftId,
                $"cursor-{frame}.png",
                [(byte)(10 + frame)],
                null,
                frame.ToString());
            draft = service.StageFile(
                draft.DraftId,
                $"cursor-{frame}@2x.png",
                [(byte)(20 + frame)],
                null,
                $"{frame}x2");
        }
        var historyCount = draft.History.Count;

        draft = new SkinDraftAssetService(service).MoveAnimationFrame(
            draft.DraftId,
            "cursor",
            0,
            2);

        Assert.Equal(historyCount + 1, draft.History.Count);
        var files = new SkinPackageService(service).Materialize(draft.DraftId);
        Assert.Equal([11], files["cursor-0.png"]);
        Assert.Equal([21], files["cursor-0@2x.png"]);
        Assert.Equal([12], files["cursor-1.png"]);
        Assert.Equal([22], files["cursor-1@2x.png"]);
        Assert.Equal([10], files["cursor-2.png"]);
        Assert.Equal([20], files["cursor-2@2x.png"]);
    }

    [Fact]
    public void Normalize_audio_family_is_one_revision_and_preserves_variants()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var service = new SkinDraftWorkspaceService(root);
        var draft = service.Create("Audio normalize", "Kumori");
        draft = service.StageFile(
            draft.DraftId,
            "normal-hitnormal.wav",
            PcmWav(0.1),
            null,
            "1x");
        draft = service.StageFile(
            draft.DraftId,
            "normal-hitnormal@2x.wav",
            PcmWav(0.2),
            null,
            "2x");
        var historyCount = draft.History.Count;

        draft = new SkinDraftAssetService(service).NormalizeAudioFamily(
            draft.DraftId,
            "normal-hitnormal");

        Assert.Equal(historyCount + 1, draft.History.Count);
        var files = new SkinPackageService(service).Materialize(draft.DraftId);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(
            files["normal-hitnormal.wav"],
            0,
            4));
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(
            files["normal-hitnormal@2x.wav"],
            0,
            4));
        var analyzer = new SkinAudioTransformService();
        Assert.InRange(
            analyzer.Analyze(files["normal-hitnormal.wav"]).Peak,
            0.94f,
            0.96f);
        Assert.InRange(
            analyzer.Analyze(files["normal-hitnormal@2x.wav"]).Peak,
            0.94f,
            0.96f);
    }

    [Fact]
    public void Asset_family_clipboard_maps_all_variants_in_one_revision()
    {
        var service = new SkinDraftWorkspaceService(root);
        var draft = service.Create("Clipboard", "Kumori");
        draft = service.StageFile(draft.DraftId, "cursor.png", [1], null, "base");
        draft = service.StageFile(draft.DraftId, "cursor-0@2x.png", [2], null, "frame");
        draft = service.StageFile(draft.DraftId, "hitcircle.png", [9], null, "target");
        var assets = new SkinDraftAssetService(service);
        var snapshot = assets.CopyFamily(draft.DraftId, "cursor");
        var historyCount = draft.History.Count;

        draft = assets.PasteFamily(draft.DraftId, "hitcircle", snapshot);

        Assert.Equal(historyCount + 1, draft.History.Count);
        var files = new SkinPackageService(service).Materialize(draft.DraftId);
        Assert.Equal([1], files["hitcircle.png"]);
        Assert.Equal([2], files["hitcircle-0@2x.png"]);
        Assert.Equal([1], files["cursor.png"]);
        Assert.Equal([2], files["cursor-0@2x.png"]);
    }

    [Fact]
    public void Reset_families_removes_category_changes_in_one_revision()
    {
        var service = new SkinDraftWorkspaceService(root);
        var draft = service.Create("Reset", "Kumori");
        draft = service.StageFile(draft.DraftId, "cursor.png", [1], null, "cursor");
        draft = service.StageFile(draft.DraftId, "cursortrail.png", [2], null, "trail");
        draft = service.StageFile(draft.DraftId, "hitcircle.png", [3], null, "hit");
        var assets = new SkinDraftAssetService(service);
        var historyCount = draft.History.Count;

        draft = assets.ResetFamilies(
            draft.DraftId,
            ["cursor", "cursortrail"],
            "Reset cursor category");

        Assert.Equal(historyCount + 1, draft.History.Count);
        var change = Assert.Single(draft.Changes);
        Assert.Equal("hitcircle.png", change.Filename);
    }

    private static void Write(ZipArchive archive, string name, byte[] bytes)
    {
        using var stream = archive.CreateEntry(name).Open();
        stream.Write(bytes);
    }

    private static byte[] Png(Rgba32 pixel)
    {
        using var image = new Image<Rgba32>(1, 1, pixel);
        using var stream = new MemoryStream();
        image.SaveAsPng(stream);
        return stream.ToArray();
    }

    private static byte[] PcmWav(double amplitude)
    {
        const int sampleRate = 8_000;
        const int frames = 800;
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        var dataBytes = frames * sizeof(short);
        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * sizeof(short));
        writer.Write((short)sizeof(short));
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataBytes);
        for (var frame = 0; frame < frames; frame++)
        {
            writer.Write((short)Math.Round(
                Math.Sin(frame * Math.PI * 2 * 440 / sampleRate)
                * amplitude
                * short.MaxValue));
        }
        return output.ToArray();
    }

    public void Dispose()
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
    }
}
