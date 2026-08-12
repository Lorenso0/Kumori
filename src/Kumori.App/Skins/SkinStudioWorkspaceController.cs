using System.IO;

namespace Kumori.App.Skins;

internal sealed class SkinStudioWorkspaceController
{
    private const string extras_preview_draft_name = "__Kumori Extras Renderer Preview";
    private static readonly HashSet<string> legacy_only_components = new(
        ["sliderpoint10", "sliderpoint30"],
        StringComparer.OrdinalIgnoreCase);
    private readonly SkinDraftWorkspaceService workspace;
    private readonly SkinDraftAssetService assets;
    private readonly SkinPackageService packages;
    private SkinDraftAssetFamilySnapshot? clipboard;
    private Guid cachedDraftId;
    private long cachedRevision = -1;
    private IReadOnlyDictionary<string, byte[]>? cachedFiles;
    private IReadOnlyList<SkinDraftAsset>? cachedAssets;

    public SkinStudioWorkspaceController(string workspacePath)
    {
        workspace = new SkinDraftWorkspaceService(workspacePath);
        assets = new SkinDraftAssetService(workspace);
        packages = new SkinPackageService(workspace);
    }

    public event EventHandler? StateChanged;

    public SkinDraftManifest CurrentDraft { get; private set; } = null!;
    public string? SelectedComponent { get; private set; }
    public string WorkspacePath => workspace.RootPath;
    public IReadOnlyList<SkinDraftManifest> Drafts => workspace.List()
        .Where(draft => !draft.Name.Equals(
            extras_preview_draft_name,
            StringComparison.Ordinal))
        .ToArray();
    public IReadOnlyList<SkinStudioElementCategory> Categories =>
        SkinStudioElementCatalog.LegacySidebarCategories;

    public long CurrentRevision => CurrentDraft
        .History[CurrentDraft.HistoryIndex].Revision;

    public IReadOnlyList<SkinDraftAsset> SelectedFamily =>
        string.IsNullOrWhiteSpace(SelectedComponent)
            ? []
            : currentAssets().Where(asset => asset.ComponentName.Equals(
                SelectedComponent,
                StringComparison.OrdinalIgnoreCase)).ToArray();
    public bool HasClipboard => clipboard is not null;

    public void Initialize(Guid? requestedDraftId = null)
    {
        Directory.CreateDirectory(workspace.RootPath);
        CurrentDraft = requestedDraftId is { } requested
            ? workspace.Load(requested)
            : Drafts.FirstOrDefault()
              ?? workspace.Create("New Kumori Skin", "Kumori");
        SelectedComponent = null;
        raiseStateChanged();
    }

    public void OpenDraft(Guid draftId)
    {
        CurrentDraft = workspace.Load(draftId);
        SelectedComponent = null;
        raiseStateChanged();
    }

    public SkinDraftManifest CreateBlank(string name = "New Kumori Skin", string creator = "Kumori")
    {
        CurrentDraft = workspace.Create(name, creator);
        SelectedComponent = null;
        raiseStateChanged();
        return CurrentDraft;
    }

    public void DuplicateCurrent()
    {
        CurrentDraft = workspace.Duplicate(
            CurrentDraft.DraftId,
            CurrentDraft.Name + " Copy");
        SelectedComponent = null;
        raiseStateChanged();
    }

    public void RenameCurrent(string name, string creator)
    {
        CurrentDraft = workspace.UpdateIdentity(CurrentDraft.DraftId, name, creator);
        raiseStateChanged();
    }

    public void DeleteCurrentRecoverably()
    {
        workspace.DeleteRecoverably(CurrentDraft.DraftId);
        CurrentDraft = Drafts.FirstOrDefault()
                       ?? workspace.Create("New Kumori Skin", "Kumori");
        SelectedComponent = null;
        raiseStateChanged();
    }

    public void RestoreLatestDeleted()
    {
        var deleted = workspace.ListDeleted().OrderByDescending(item => item.DeletedAt).FirstOrDefault()
                      ?? throw new InvalidOperationException("There is no deleted draft to restore.");
        CurrentDraft = workspace.RestoreDeleted(deleted.TrashName);
        SelectedComponent = null;
        raiseStateChanged();
    }

    public SkinDraftManifest ImportSkin(string sourcePath)
        => ImportSkin(sourcePath, Path.GetFileNameWithoutExtension(sourcePath), "Kumori");

    public SkinDraftManifest ImportSkin(
        string sourcePath,
        string name,
        string creator,
        Guid? sourceLazerSkinId = null)
    {
        var source = Path.GetFullPath(sourcePath);
        SkinPackageService.ValidatePackage(source);
        CurrentDraft = workspace.Create(
            name,
            creator,
            source,
            SkinPackageService.Fingerprint(source),
            sourceLazerSkinId: sourceLazerSkinId);
        SelectedComponent = null;
        raiseStateChanged();
        return CurrentDraft;
    }

    public void Select(string componentName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(componentName);
        SelectedComponent = SkinDraftAssetService.ComponentName(
            componentName.Trim() + ".png");
    }

    public IReadOnlyDictionary<string, byte[]> Materialize() =>
        currentFiles();

    public bool IsSupplied(string componentName) =>
        currentAssets().Any(asset => asset.ComponentName.Equals(
            componentName,
            StringComparison.OrdinalIgnoreCase));

    public bool IsUsedByLazer(string componentName) =>
        !legacy_only_components.Contains(componentName)
        && currentAssets().Where(asset => asset.ComponentName.Equals(
            componentName,
            StringComparison.OrdinalIgnoreCase)).Any(asset =>
        {
            var familyId = SkinExtraFamilyRegistry.ForFile(asset.Filename)?.Id;
            return SkinExtraLazerCompatibility.IsLazerUsed(asset.Filename, familyId);
        });

    public void ReplaceSelected(string sourcePath)
    {
        var component = requireSelection();
        var source = Path.GetFullPath(sourcePath);
        if (!File.Exists(source))
            throw new FileNotFoundException("The replacement asset was not found.", source);
        var filename = assets.ResolveReplacementFilename(
            CurrentDraft.DraftId,
            component,
            source);
        var materialized = currentFiles();
        var expected = materialized.TryGetValue(filename, out var current)
            ? SkinDraftWorkspaceService.Hash(current)
            : null;
        CurrentDraft = workspace.StageFile(
            CurrentDraft.DraftId,
            filename,
            File.ReadAllBytes(source),
            expected,
            $"Replace {component}");
        raiseStateChanged();
    }

    public void ImportFiles(IEnumerable<string> sourcePaths)
    {
        CurrentDraft = assets.ImportFiles(CurrentDraft.DraftId, sourcePaths);
        raiseStateChanged();
    }

    public void DeleteSelected()
    {
        CurrentDraft = assets.DeleteFamily(CurrentDraft.DraftId, requireSelection());
        raiseStateChanged();
    }

    public void ResetSelected()
    {
        CurrentDraft = assets.ResetFamily(CurrentDraft.DraftId, requireSelection());
        raiseStateChanged();
    }

    public void CopySelected()
    {
        clipboard = assets.CopyFamily(CurrentDraft.DraftId, requireSelection());
        raiseStateChanged();
    }

    public void PasteSelected()
    {
        if (clipboard is null)
            throw new InvalidOperationException("Copy an element family first.");
        CurrentDraft = assets.PasteFamily(
            CurrentDraft.DraftId,
            requireSelection(),
            clipboard);
        raiseStateChanged();
    }

    public void TransformSelected(SkinImageTransformMode mode, SkinRgb colour)
        => TransformSelected(mode, colour, SkinImageTransformScope.FullFamily);

    public void TransformSelected(
        SkinImageTransformMode mode,
        SkinRgb colour,
        SkinImageTransformScope scope,
        int? animationFrame = null)
    {
        CurrentDraft = assets.TransformImageFamily(
            CurrentDraft.DraftId,
            requireSelection(),
            new SkinImageTransform(mode, colour),
            scope,
            animationFrame);
        raiseStateChanged();
    }

    public void NormalizeSelectedAudio()
    {
        CurrentDraft = assets.NormalizeAudioFamily(
            CurrentDraft.DraftId,
            requireSelection());
        raiseStateChanged();
    }

    public string ReadSkinIni()
    {
        var files = currentFiles();
        return files.TryGetValue("skin.ini", out var bytes)
            ? SkinIniDocument.Parse(bytes).ToText()
            : "[General]\r\nName: Kumori draft\r\nAuthor: Kumori\r\nVersion: 2.7\r\n";
    }

    public void SaveSkinIni(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var files = currentFiles();
        var expected = files.TryGetValue("skin.ini", out var current)
            ? SkinDraftWorkspaceService.Hash(current)
            : null;
        var encoded = current is null
            ? System.Text.Encoding.UTF8.GetBytes(text)
            : SkinIniDocument.Parse(current).WithText(text).ToBytes();
        CurrentDraft = workspace.StageFile(
            CurrentDraft.DraftId,
            "skin.ini",
            encoded,
            expected,
            "Edit skin.ini");
        raiseStateChanged();
    }

    public void Undo()
    {
        CurrentDraft = workspace.Undo(CurrentDraft.DraftId);
        raiseStateChanged();
    }

    public void Redo()
    {
        CurrentDraft = workspace.Redo(CurrentDraft.DraftId);
        raiseStateChanged();
    }

    public void DiscardAll()
    {
        CurrentDraft = workspace.DiscardAll(CurrentDraft.DraftId);
        raiseStateChanged();
    }

    public void DiscardChange(string filename)
    {
        CurrentDraft = workspace.Unstage(CurrentDraft.DraftId, filename);
        raiseStateChanged();
    }

    public string Export(string destination) =>
        packages.Export(CurrentDraft.DraftId, destination);

    public IReadOnlyList<string> ExportSelected(string destinationDirectory) =>
        assets.ExportFamily(CurrentDraft.DraftId, requireSelection(), destinationDirectory);

    public SkinDraftBackup CreateBackup(string reason) =>
        new SkinDraftBackupService(workspace).Create(CurrentDraft.DraftId, reason);

    public void ApplyExtrasPack(string directory, SkinExtraPackManifest manifest)
    {
        CreateBackup($"Before applying Extras/{manifest.DisplayName}");
        CurrentDraft = new SkinDraftExtrasService(workspace).StagePack(
            CurrentDraft.DraftId,
            new SkinExtraPackDescriptor(directory, manifest, false));
        raiseStateChanged();
    }

    public void ApplyExtrasSelection(SkinExtrasSelectionResult selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        CreateBackup($"Before applying Extras/{selection.Manifest.DisplayName}");
        var descriptor = new SkinExtraPackDescriptor(
            selection.PackDirectory,
            selection.Manifest,
            false);
        CurrentDraft = new SkinDraftExtrasService(workspace).StageSelection(
            CurrentDraft.DraftId,
            descriptor,
            new SkinDraftExtrasSelection(
                selection.Manifest.Files.Select(file => file.TargetFilename).ToArray(),
                selection.Manifest.IniPatch,
                selection.ReplaceEntireFamily));
        if (SkinCursorMiddlePolicy.IsCursorFamily(selection.Manifest.FamilyId))
            applyCursorMiddlePolicy(selection.SmoothTrail);
        if (selection.ElementTints is { Count: > 0 })
            applyExtrasElementTints(selection);
        raiseStateChanged();
    }

    public void InitializeExtrasPreview() => InitializeExtrasPreview(
        new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["skin.ini"] = System.Text.Encoding.UTF8.GetBytes(
                "[General]\r\nName: Kumori Extras preview\r\nAuthor: Kumori\r\nVersion: 2.7\r\n"),
        });

    public void InitializeExtrasPreview(IReadOnlyDictionary<string, byte[]> baseFiles)
    {
        ArgumentNullException.ThrowIfNull(baseFiles);
        Directory.CreateDirectory(workspace.RootPath);
        var existing = workspace.List().FirstOrDefault(draft =>
            draft.Name.Equals(extras_preview_draft_name, StringComparison.Ordinal));
        if (existing is not null)
            workspace.DeleteRecoverably(existing.DraftId);
        var temporaryDirectory = Path.Combine(workspace.RootPath, "temporary");
        Directory.CreateDirectory(temporaryDirectory);
        var temporaryPackage = Path.Combine(
            temporaryDirectory,
            $"extras-preview-base-{Guid.NewGuid():N}.osk");
        try
        {
            packages.Export(baseFiles, temporaryPackage);
            CurrentDraft = workspace.Create(
                extras_preview_draft_name,
                "Kumori",
                temporaryPackage,
                trackOrigin: false);
        }
        finally
        {
            try { if (File.Exists(temporaryPackage)) File.Delete(temporaryPackage); } catch { }
        }
        SelectedComponent = null;
        raiseStateChanged();
    }

    public void PrepareExtrasPreview(
        SkinExtraPackDescriptor pack,
        bool smoothTrail = false)
    {
        ArgumentNullException.ThrowIfNull(pack);
        if (!CurrentDraft.Name.Equals(extras_preview_draft_name, StringComparison.Ordinal))
            throw new InvalidOperationException("The Extras preview workspace was not initialized.");
        if (CurrentDraft.Changes.Count > 0)
            CurrentDraft = workspace.DiscardAll(CurrentDraft.DraftId);
        CurrentDraft = new SkinDraftExtrasService(workspace).StagePack(
            CurrentDraft.DraftId,
            pack,
            verifyContent: false);
        if (SkinCursorMiddlePolicy.IsCursorFamily(pack.Manifest.FamilyId))
            applyCursorMiddlePolicy(smoothTrail);
        raiseStateChanged();
    }

    private void applyCursorMiddlePolicy(bool smoothTrail)
    {
        var effective = packages.Materialize(CurrentDraft.DraftId);
        var mutations = effective
            .Where(file => SkinCursorMiddlePolicy.IsCursorMiddle(file.Key))
            .Where(file => !smoothTrail
                           || !file.Key.Equals(
                               SkinCursorMiddlePolicy.CanonicalFilename,
                               StringComparison.OrdinalIgnoreCase))
            .Select(file => new SkinDraftFileMutation(
                file.Key,
                SkinDraftChangeKind.Delete,
                null,
                SkinDraftWorkspaceService.Hash(file.Value),
                "Remove cursor-middle for the selected trail mode"))
            .ToList();
        if (smoothTrail)
        {
            effective.TryGetValue(
                SkinCursorMiddlePolicy.CanonicalFilename,
                out var current);
            mutations.Add(new SkinDraftFileMutation(
                SkinCursorMiddlePolicy.CanonicalFilename,
                SkinDraftChangeKind.Upsert,
                SkinCursorMiddlePolicy.CreateSmoothTrailPng(),
                current is null ? null : SkinDraftWorkspaceService.Hash(current),
                "Enable Smooth Trail for the cursor preview"));
        }
        if (mutations.Count > 0)
        {
            CurrentDraft = workspace.StageBatch(
                CurrentDraft.DraftId,
                mutations,
                smoothTrail
                    ? "Enable Smooth Trail"
                    : "Use disjoint cursor trail");
        }
    }

    private void applyExtrasElementTints(SkinExtrasSelectionResult selection)
    {
        var elementTints = selection.ElementTints;
        if (elementTints is null || elementTints.Count == 0)
            return;
        var effective = packages.Materialize(CurrentDraft.DraftId);
        var family = SkinExtraFamilyRegistry.ById(selection.Manifest.FamilyId);
        var familyFilenames = effective.Keys
            .Where(filename => family?.Matches(filename) == true)
            .ToArray();
        var transformer = new SkinImageTransformService();
        var mutations = new List<SkinDraftFileMutation>();
        foreach (var filename in familyFilenames.Where(
                     SkinElementCategorizer.IsImage))
        {
            var key = SkinExtraLogicalGrouping.Key(
                selection.Manifest.FamilyId,
                filename,
                familyFilenames);
            if (!elementTints.TryGetValue(key, out var tint)
                || !effective.TryGetValue(filename, out var current))
            {
                continue;
            }
            var transformed = transformer.Apply(
                current,
                filename,
                new SkinImageTransform(
                    SkinImageTransformMode.MultiplicativeTint,
                    tint));
            mutations.Add(new SkinDraftFileMutation(
                filename,
                SkinDraftChangeKind.Upsert,
                transformed,
                SkinDraftWorkspaceService.Hash(current),
                $"Tint {key} from Extras/{selection.Manifest.DisplayName}"));
        }
        if (mutations.Count > 0)
        {
            CurrentDraft = workspace.StageBatch(
                CurrentDraft.DraftId,
                mutations,
                $"Tint Extras elements for {selection.Manifest.DisplayName}");
        }
    }

    private string requireSelection() =>
        string.IsNullOrWhiteSpace(SelectedComponent)
            ? throw new InvalidOperationException("Choose an element first.")
            : SelectedComponent;

    private IReadOnlyDictionary<string, byte[]> currentFiles()
    {
        ensureCurrentCache();
        return cachedFiles!;
    }

    private IReadOnlyList<SkinDraftAsset> currentAssets()
    {
        ensureCurrentCache();
        return cachedAssets!;
    }

    private void ensureCurrentCache()
    {
        var revision = CurrentRevision;
        if (cachedFiles is not null
            && cachedAssets is not null
            && cachedDraftId == CurrentDraft.DraftId
            && cachedRevision == revision)
        {
            return;
        }

        cachedDraftId = CurrentDraft.DraftId;
        cachedRevision = revision;
        cachedFiles = packages.Materialize(cachedDraftId);
        cachedAssets = SkinDraftAssetService.List(cachedFiles);
    }

    private void raiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);
}
