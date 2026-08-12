using Kumori.Skins;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Kumori.Skins.Tests;

public sealed class SkinDraftExtrasServiceTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        $"kumori-skin-extras-{Guid.NewGuid():N}");

    [Fact]
    public void Healthy_pack_replaces_its_family_in_one_undoable_revision()
    {
        var workspace = new SkinDraftWorkspaceService(Path.Combine(root, "workspace"));
        var draft = workspace.Create("Draft", "Kumori");
        draft = workspace.StageFile(
            draft.DraftId,
            "cursortrail.png",
            Png(new Rgba32(1, 2, 3, 255)),
            null,
            "old trail");
        var packDirectory = Path.Combine(root, "pack");
        Directory.CreateDirectory(packDirectory);
        var bytes = Png(new Rgba32(200, 100, 50, 255));
        File.WriteAllBytes(Path.Combine(packDirectory, "cursor.png"), bytes);
        var described = SkinExtraFingerprint.Describe(
            "cursor.png",
            "cursor.png",
            bytes);
        var manifest = new SkinExtraPackManifest
        {
            Id = "cursor-test",
            DisplayName = "Cursor test",
            FamilyId = "osu.cursor",
            Area = "osu!",
            FamilyName = "Cursor",
            Fingerprint = SkinExtraFingerprint.ForPack("osu.cursor", [described]),
            Files = [described],
        };
        var historyCount = draft.History.Count;

        draft = new SkinDraftExtrasService(workspace).StagePack(
            draft.DraftId,
            new SkinExtraPackDescriptor(packDirectory, manifest, false));

        Assert.Equal(historyCount + 1, draft.History.Count);
        Assert.Contains(
            draft.Changes,
            change => change.Filename == "cursor.png"
                      && change.Kind == SkinDraftChangeKind.Upsert);
        Assert.Contains(
            draft.Changes,
            change => change.Filename == "cursortrail.png"
                      && change.Kind == SkinDraftChangeKind.Delete);
        var effective = new SkinPackageService(workspace).Materialize(draft.DraftId);
        Assert.Contains("cursor.png", effective.Keys);
        Assert.DoesNotContain("cursortrail.png", effective.Keys);
    }

    [Fact]
    public void Partial_selection_replaces_only_the_selected_logical_element()
    {
        var workspace = new SkinDraftWorkspaceService(Path.Combine(root, "partial"));
        var draft = workspace.Create("Draft", "Kumori");
        draft = workspace.StageFile(
            draft.DraftId,
            "cursor@2x.png",
            Png(new Rgba32(1, 2, 3, 255)),
            null,
            "old cursor");
        draft = workspace.StageFile(
            draft.DraftId,
            "cursortrail.png",
            Png(new Rgba32(4, 5, 6, 255)),
            null,
            "old trail");

        var packDirectory = Path.Combine(root, "partial-pack");
        Directory.CreateDirectory(packDirectory);
        var cursorBytes = Png(new Rgba32(200, 100, 50, 255));
        var trailBytes = Png(new Rgba32(50, 100, 200, 255));
        File.WriteAllBytes(Path.Combine(packDirectory, "cursor.png"), cursorBytes);
        File.WriteAllBytes(Path.Combine(packDirectory, "cursortrail.png"), trailBytes);
        var cursor = SkinExtraFingerprint.Describe(
            "cursor.png",
            "cursor.png",
            cursorBytes);
        var trail = SkinExtraFingerprint.Describe(
            "cursortrail.png",
            "cursortrail.png",
            trailBytes);
        var patch = new SkinExtraIniPatchEntry(
            "General",
            "CursorExpand",
            "0");
        var manifest = new SkinExtraPackManifest
        {
            Id = "cursor-partial",
            DisplayName = "Cursor partial",
            FamilyId = "osu.cursor",
            Area = "osu!",
            FamilyName = "Cursor",
            Fingerprint = SkinExtraFingerprint.ForPack(
                "osu.cursor",
                [cursor, trail],
                [patch]),
            Files = [cursor, trail],
            IniPatch = [patch],
        };

        draft = new SkinDraftExtrasService(workspace).StageSelection(
            draft.DraftId,
            new SkinExtraPackDescriptor(packDirectory, manifest, false),
            new SkinDraftExtrasSelection(
                ["cursor.png"],
                [],
                ReplaceEntireFamily: false));

        var effective = new SkinPackageService(workspace).Materialize(draft.DraftId);
        Assert.Equal(cursorBytes, effective["cursor.png"]);
        Assert.DoesNotContain("cursor@2x.png", effective.Keys);
        Assert.NotEqual(trailBytes, effective["cursortrail.png"]);
        Assert.DoesNotContain(
            "CursorExpand: 0",
            System.Text.Encoding.UTF8.GetString(effective["skin.ini"]));
    }

    [Fact]
    public void Selection_rejects_undeclared_pack_files()
    {
        var workspace = new SkinDraftWorkspaceService(Path.Combine(root, "invalid"));
        var draft = workspace.Create("Draft", "Kumori");
        var packDirectory = Path.Combine(root, "invalid-pack");
        Directory.CreateDirectory(packDirectory);
        var bytes = Png(new Rgba32(1, 2, 3, 255));
        File.WriteAllBytes(Path.Combine(packDirectory, "cursor.png"), bytes);
        var cursor = SkinExtraFingerprint.Describe(
            "cursor.png",
            "cursor.png",
            bytes);
        var manifest = new SkinExtraPackManifest
        {
            Id = "cursor-invalid",
            DisplayName = "Cursor invalid",
            FamilyId = "osu.cursor",
            Area = "osu!",
            FamilyName = "Cursor",
            Fingerprint = SkinExtraFingerprint.ForPack("osu.cursor", [cursor]),
            Files = [cursor],
        };

        Assert.Throws<InvalidDataException>(() =>
            new SkinDraftExtrasService(workspace).StageSelection(
                draft.DraftId,
                new SkinExtraPackDescriptor(packDirectory, manifest, false),
                new SkinDraftExtrasSelection(
                    ["not-declared.png"],
                    [],
                    ReplaceEntireFamily: false)));
    }

    [Fact]
    public void Comparison_reports_file_and_setting_impact_before_apply()
    {
        var workspace = new SkinDraftWorkspaceService(Path.Combine(root, "compare"));
        var draft = workspace.Create("Draft", "Kumori");
        var same = Png(new Rgba32(10, 20, 30, 255));
        draft = workspace.StageFile(
            draft.DraftId,
            "cursor.png",
            same,
            null,
            "same cursor");
        draft = workspace.StageFile(
            draft.DraftId,
            "cursortrail.png",
            Png(new Rgba32(40, 50, 60, 255)),
            null,
            "old trail");
        draft = workspace.StageFile(
            draft.DraftId,
            "cursormiddle.png",
            Png(new Rgba32(70, 80, 90, 255)),
            null,
            "old middle");

        var replacement = Png(new Rgba32(100, 110, 120, 255));
        var added = Png(new Rgba32(130, 140, 150, 255));
        var cursor = SkinExtraFingerprint.Describe(
            "cursor.png",
            "cursor.png",
            same);
        var trail = SkinExtraFingerprint.Describe(
            "cursortrail.png",
            "cursortrail.png",
            replacement);
        var ripple = SkinExtraFingerprint.Describe(
            "cursor-ripple.png",
            "cursor-ripple.png",
            added);
        var patch = new SkinExtraIniPatchEntry(
            "General",
            "CursorExpand",
            "0");
        var manifest = new SkinExtraPackManifest
        {
            Id = "cursor-compare",
            DisplayName = "Cursor compare",
            FamilyId = "osu.cursor",
            Area = "osu!",
            FamilyName = "Cursor",
            Fingerprint = SkinExtraFingerprint.ForPack(
                "osu.cursor",
                [cursor, trail, ripple],
                [patch]),
            Files = [cursor, trail, ripple],
            IniPatch = [patch],
        };
        var selection = new SkinDraftExtrasSelection(
            ["cursor.png", "cursortrail.png", "cursor-ripple.png"],
            [patch],
            ReplaceEntireFamily: true);

        var result = new SkinDraftExtrasComparisonService(workspace).Compare(
            draft.DraftId,
            new SkinExtraPackDescriptor(
                Path.Combine(root, "compare-pack"),
                manifest,
                false),
            selection);

        Assert.Equal(1, result.AddedFiles);
        Assert.Equal(1, result.ReplacedFiles);
        Assert.Equal(1, result.IdenticalFiles);
        Assert.Equal(1, result.RemovedFiles);
        Assert.Equal(1, result.ChangedSettings);
        Assert.Equal(0, result.IdenticalSettings);
    }

    private static byte[] Png(Rgba32 pixel)
    {
        using var image = new Image<Rgba32>(1, 1, pixel);
        using var stream = new MemoryStream();
        image.SaveAsPng(stream);
        return stream.ToArray();
    }

    public void Dispose()
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
    }
}
