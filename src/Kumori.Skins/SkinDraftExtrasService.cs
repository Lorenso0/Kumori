namespace Kumori.Skins;

public sealed record SkinDraftExtrasSelection(
    IReadOnlyCollection<string> TargetFilenames,
    IReadOnlyCollection<SkinExtraIniPatchEntry> IniPatch,
    bool ReplaceEntireFamily,
    IReadOnlyDictionary<string, SkinImageTransform>? ImageTransforms = null);

public sealed class SkinDraftExtrasService
{
    private readonly SkinDraftWorkspaceService workspace;
    private readonly SkinPackageService packages;

    public SkinDraftExtrasService(SkinDraftWorkspaceService workspace)
    {
        this.workspace = workspace;
        packages = new SkinPackageService(workspace);
    }

    public SkinDraftManifest StagePack(
        Guid draftId,
        SkinExtraPackDescriptor pack,
        bool verifyContent = true)
    {
        return StageSelection(
            draftId,
            pack,
            new SkinDraftExtrasSelection(
                pack.Manifest.Files
                    .Select(file => file.TargetFilename)
                    .ToArray(),
                pack.Manifest.IniPatch,
                ReplaceEntireFamily: true),
            verifyContent);
    }

    public SkinDraftManifest StageSelection(
        Guid draftId,
        SkinExtraPackDescriptor pack,
        SkinDraftExtrasSelection selection,
        bool verifyContent = true)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(selection);
        var health = SkinExtraPackValidator.Validate(pack, verifyContent);
        if (!health.IsHealthy)
        {
            throw new InvalidDataException(
                $"Extras pack is unhealthy: {health.Errors} error(s), {health.Warnings} warning(s).");
        }
        var family = SkinExtraFamilyRegistry.ById(pack.Manifest.FamilyId)
                     ?? throw new InvalidDataException(
                         $"Unknown Extras family '{pack.Manifest.FamilyId}'.");
        var declaredByTarget = pack.Manifest.Files.ToDictionary(
            file => SkinDraftWorkspaceService.NormalizeSkinFilename(
                file.TargetFilename),
            StringComparer.OrdinalIgnoreCase);
        var selectedTargets = selection.TargetFilenames
            .Select(SkinDraftWorkspaceService.NormalizeSkinFilename)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!selectedTargets.IsSubsetOf(declaredByTarget.Keys))
        {
            throw new InvalidDataException(
                "Extras selection contains a file not declared by the pack.");
        }
        var selectedPatch = selection.IniPatch.ToHashSet();
        if (!selectedPatch.IsSubsetOf(pack.Manifest.IniPatch))
        {
            throw new InvalidDataException(
                "Extras selection contains a skin.ini setting not declared by the pack.");
        }
        if (selectedTargets.Count == 0 && selectedPatch.Count == 0)
            throw new InvalidDataException("Select at least one Extras file or setting.");

        var effective = packages.Materialize(draftId);
        var selectedComponents = selectedTargets
            .Select(SkinDraftAssetService.ComponentName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var imageTransforms = selection.ImageTransforms
                              ?? new Dictionary<string, SkinImageTransform>(
                                  StringComparer.OrdinalIgnoreCase);
        if (imageTransforms.Keys.Any(component =>
                !selectedComponents.Contains(component)))
        {
            throw new InvalidDataException(
                "Extras recolouring contains an element outside the file selection.");
        }
        var mutations = new List<SkinDraftFileMutation>();
        var transformer = new SkinImageTransformService();

        foreach (var (filename, bytes) in effective)
        {
            if (filename.Equals("skin.ini", StringComparison.OrdinalIgnoreCase)
                || !family.Matches(filename)
                || selectedTargets.Contains(filename)
                || (!selection.ReplaceEntireFamily
                    && !selectedComponents.Contains(
                        SkinDraftAssetService.ComponentName(filename))))
            {
                continue;
            }
            mutations.Add(new SkinDraftFileMutation(
                filename,
                SkinDraftChangeKind.Delete,
                null,
                SkinDraftWorkspaceService.Hash(bytes),
                $"Remove {filename} for Extras/{pack.Manifest.DisplayName}"));
        }

        foreach (var filename in selectedTargets)
        {
            var declared = declaredByTarget[filename];
            var path = containedPackPath(pack.DirectoryPath, filename);
            var bytes = File.ReadAllBytes(path);
            var component = SkinDraftAssetService.ComponentName(filename);
            if (imageTransforms.TryGetValue(component, out var transform))
            {
                if (!SkinMediaTypes.IsImage(filename))
                {
                    throw new InvalidDataException(
                        $"Extras element '{component}' is not an image and cannot be recoloured.");
                }
                bytes = transformer.Apply(bytes, filename, transform);
            }
            effective.TryGetValue(filename, out var current);
            mutations.Add(new SkinDraftFileMutation(
                filename,
                SkinDraftChangeKind.Upsert,
                bytes,
                current is null ? null : SkinDraftWorkspaceService.Hash(current),
                $"Apply {filename} from Extras/{pack.Manifest.DisplayName}"));
        }

        if (selectedPatch.Count > 0)
        {
            var currentIni = effective["skin.ini"];
            var ini = SkinIniDocument.Parse(currentIni);
            ini.ApplyPatch(selectedPatch);
            mutations.Add(new SkinDraftFileMutation(
                "skin.ini",
                SkinDraftChangeKind.Upsert,
                ini.ToBytes(),
                SkinDraftWorkspaceService.Hash(currentIni),
                $"Apply skin.ini from Extras/{pack.Manifest.DisplayName}"));
        }

        return workspace.StageBatch(
            draftId,
            mutations,
            selection.ReplaceEntireFamily
                ? $"Apply complete Extras family {pack.Manifest.DisplayName}"
                : $"Apply Extras selection {pack.Manifest.DisplayName}");
    }

    private static string containedPackPath(string directory, string filename)
    {
        var root = Path.GetFullPath(directory);
        var candidate = Path.GetFullPath(Path.Combine(
            root,
            filename.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = Path.TrimEndingDirectorySeparator(root)
                     + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Extras pack path escaped its directory.");
        return candidate;
    }
}
