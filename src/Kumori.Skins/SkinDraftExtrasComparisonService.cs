namespace Kumori.Skins;

public sealed record SkinDraftExtrasComparison(
    int AddedFiles,
    int ReplacedFiles,
    int IdenticalFiles,
    int RemovedFiles,
    int ChangedSettings,
    int IdenticalSettings)
{
    public string Summary =>
        $"{AddedFiles} add · {ReplacedFiles} replace · {IdenticalFiles} identical"
        + $" · {RemovedFiles} remove · {ChangedSettings} setting change"
        + $" · {IdenticalSettings} setting unchanged";
}

public sealed class SkinDraftExtrasComparisonService
{
    private readonly SkinPackageService packages;

    public SkinDraftExtrasComparisonService(SkinDraftWorkspaceService workspace)
    {
        packages = new SkinPackageService(workspace);
    }

    public SkinDraftExtrasComparison Compare(
        Guid draftId,
        SkinExtraPackDescriptor pack,
        SkinDraftExtrasSelection selection)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(selection);
        var family = SkinExtraFamilyRegistry.ById(pack.Manifest.FamilyId)
                     ?? throw new InvalidDataException(
                         $"Unknown Extras family '{pack.Manifest.FamilyId}'.");
        var declared = pack.Manifest.Files.ToDictionary(
            file => SkinDraftWorkspaceService.NormalizeSkinFilename(
                file.TargetFilename),
            StringComparer.OrdinalIgnoreCase);
        var targets = selection.TargetFilenames
            .Select(SkinDraftWorkspaceService.NormalizeSkinFilename)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!targets.IsSubsetOf(declared.Keys))
        {
            throw new InvalidDataException(
                "Extras comparison contains a file not declared by the pack.");
        }
        var settings = selection.IniPatch.ToHashSet();
        if (!settings.IsSubsetOf(pack.Manifest.IniPatch))
        {
            throw new InvalidDataException(
                "Extras comparison contains a setting not declared by the pack.");
        }

        var effective = packages.Materialize(draftId);
        var imageTransforms = selection.ImageTransforms
                              ?? new Dictionary<string, SkinImageTransform>(
                                  StringComparer.OrdinalIgnoreCase);
        var selectedComponents = targets
            .Select(SkinDraftAssetService.ComponentName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (imageTransforms.Keys.Any(component =>
                !selectedComponents.Contains(component)))
        {
            throw new InvalidDataException(
                "Extras recolouring contains an element outside the file selection.");
        }
        var transformer = new SkinImageTransformService();
        var added = 0;
        var replaced = 0;
        var identical = 0;
        foreach (var target in targets)
        {
            if (!effective.TryGetValue(target, out var current))
            {
                added++;
                continue;
            }
            var comparisonHash = declared[target].ByteHash;
            var component = SkinDraftAssetService.ComponentName(target);
            if (imageTransforms.TryGetValue(component, out var transform))
            {
                var path = containedPackPath(pack.DirectoryPath, target);
                comparisonHash = SkinDraftWorkspaceService.Hash(
                    transformer.Apply(
                        File.ReadAllBytes(path),
                        target,
                        transform));
            }
            if (SkinDraftWorkspaceService.Hash(current).Equals(
                    comparisonHash,
                    StringComparison.OrdinalIgnoreCase))
            {
                identical++;
            }
            else
            {
                replaced++;
            }
        }

        var removed = effective.Keys.Count(filename =>
            !filename.Equals("skin.ini", StringComparison.OrdinalIgnoreCase)
            && family.Matches(filename)
            && !targets.Contains(filename)
            && (selection.ReplaceEntireFamily
                || selectedComponents.Contains(
                    SkinDraftAssetService.ComponentName(filename))));

        var changedSettings = 0;
        var identicalSettings = 0;
        var ini = effective.TryGetValue("skin.ini", out var iniBytes)
            ? SkinIniDocument.Parse(iniBytes)
            : SkinIniDocument.Parse(
                System.Text.Encoding.UTF8.GetBytes("[General]\n"));
        foreach (var setting in settings)
        {
            var current = setting.ManiaKeys is null
                ? ini.GetValue(setting.Section, setting.Key)
                : ini.GetSections("Mania")
                    .FirstOrDefault(section =>
                        section.ManiaKeys == setting.ManiaKeys)?
                    .Values.GetValueOrDefault(setting.Key);
            if (string.Equals(
                    current?.Trim(),
                    setting.Value?.Trim(),
                    StringComparison.Ordinal))
            {
                identicalSettings++;
            }
            else
            {
                changedSettings++;
            }
        }

        return new SkinDraftExtrasComparison(
            added,
            replaced,
            identical,
            removed,
            changedSettings,
            identicalSettings);
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
