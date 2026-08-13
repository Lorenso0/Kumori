namespace Kumori.Skins;

public sealed record SkinDraftAsset(
    string Filename,
    string ComponentName,
    string Extension,
    bool IsImage,
    bool IsAudio,
    bool IsTwoX,
    int? AnimationFrame,
    long SizeBytes,
    string ContentHash);

public sealed record SkinDraftAssetClipboardFile(
    string FilenameSuffix,
    string Extension,
    byte[] Contents);

public sealed record SkinDraftAssetFamilySnapshot(
    string SourceComponentName,
    IReadOnlyList<SkinDraftAssetClipboardFile> Files);

public enum SkinImageTransformScope
{
    FullFamily,
    OneXVariants,
    TwoXVariants,
    PrimaryPair,
    AnimationFramePair,
}

/// <summary>
/// File-family operations shared by the WPF and lazer-native editors.
/// A family groups animation frames and @2x variants under one osu skin
/// component name while retaining every original filename.
/// </summary>
public sealed class SkinDraftAssetService
{
    private static readonly HashSet<string> imageExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg" };
    private static readonly HashSet<string> audioExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".wav", ".mp3", ".ogg" };

    private readonly SkinDraftWorkspaceService workspace;
    private readonly SkinPackageService packages;

    public SkinDraftAssetService(SkinDraftWorkspaceService workspace)
    {
        this.workspace = workspace;
        packages = new SkinPackageService(workspace);
    }

    public IReadOnlyList<SkinDraftAsset> List(Guid draftId)
    {
        return List(packages.Materialize(draftId));
    }

    public static IReadOnlyList<SkinDraftAsset> List(
        IReadOnlyDictionary<string, byte[]> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        return files
            .Select(pair =>
            {
                var extension = Path.GetExtension(pair.Key).ToLowerInvariant();
                var component = ComponentName(pair.Key);
                return new SkinDraftAsset(
                    pair.Key,
                    component,
                    extension,
                    imageExtensions.Contains(extension),
                    audioExtensions.Contains(extension),
                    pair.Key.Contains("@2x", StringComparison.OrdinalIgnoreCase),
                    AnimationFrame(pair.Key),
                    pair.Value.LongLength,
                    SkinDraftWorkspaceService.Hash(pair.Value));
            })
            .Where(asset => asset.IsImage || asset.IsAudio)
            .OrderBy(asset => asset.ComponentName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(asset => asset.IsTwoX)
            .ThenBy(asset => asset.AnimationFrame)
            .ThenBy(asset => asset.Filename, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<SkinDraftAsset> Family(Guid draftId, string componentName)
    {
        var normalized = NormalizeComponent(componentName);
        return List(draftId)
            .Where(asset => asset.ComponentName.Equals(
                normalized,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    public string ResolveReplacementFilename(
        Guid draftId,
        string componentName,
        string sourceFilename)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFilename);
        var normalizedComponent = NormalizeComponent(componentName);
        var extension = Path.GetExtension(sourceFilename).ToLowerInvariant();
        if (!imageExtensions.Contains(extension) && !audioExtensions.Contains(extension))
            throw new InvalidDataException($"Unsupported replacement extension '{extension}'.");

        var family = Family(draftId, normalizedComponent);
        if (family.Count == 0)
            return normalizedComponent + extension;
        var compatible = family
            .Where(asset => asset.Extension.Equals(extension, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (compatible.Length == 0)
        {
            throw new InvalidDataException(
                $"Choose a {family[0].Extension} file to replace this family without creating conflicting formats.");
        }

        var sourceFrame = AnimationFrame(sourceFilename);
        var sourceIsTwoX = Path.GetFileNameWithoutExtension(sourceFilename)
            .Contains("@2x", StringComparison.OrdinalIgnoreCase);
        var exactVariant = compatible.FirstOrDefault(asset =>
            asset.AnimationFrame == sourceFrame
            && asset.IsTwoX == sourceIsTwoX);
        if (exactVariant is not null)
            return exactVariant.Filename;

        if (sourceFrame is int frame)
            return $"{normalizedComponent}-{frame}{(sourceIsTwoX ? "@2x" : "")}{extension}";
        if (sourceIsTwoX)
            return $"{normalizedComponent}@2x{extension}";

        return compatible
            .OrderBy(asset => asset.AnimationFrame.HasValue)
            .ThenBy(asset => asset.AnimationFrame)
            .ThenBy(asset => asset.IsTwoX)
            .ThenBy(asset => asset.Filename, StringComparer.OrdinalIgnoreCase)
            .First()
            .Filename;
    }

    public static string VariantSummary(IReadOnlyList<SkinDraftAsset> family)
    {
        ArgumentNullException.ThrowIfNull(family);
        if (family.Count == 0)
            return "fallback only";
        var frames = family
            .Where(asset => asset.AnimationFrame is not null)
            .Select(asset => asset.AnimationFrame!.Value)
            .Distinct()
            .Order()
            .ToArray();
        var resolutions = new List<string>();
        if (family.Any(asset => !asset.IsTwoX))
            resolutions.Add("1×");
        if (family.Any(asset => asset.IsTwoX))
            resolutions.Add("2×");
        var frameText = frames.Length == 0
            ? "static"
            : frames.Length == 1
                ? $"frame {frames[0]}"
                : $"{frames.Length} frames ({frames[0]}–{frames[^1]})";
        return $"{family.Count} file(s) · {string.Join(" + ", resolutions)} · {frameText}";
    }

    public SkinDraftManifest DeleteFamily(Guid draftId, string componentName)
    {
        var family = Family(draftId, componentName);
        if (family.Count == 0)
            return workspace.Load(draftId);
        return workspace.StageDeleteMany(
            draftId,
            family.Select(asset => (asset.Filename, (string?)asset.ContentHash)),
            $"Delete {NormalizeComponent(componentName)} family");
    }

    public SkinDraftManifest DeleteAnimationFrame(
        Guid draftId,
        string componentName,
        int animationFrame)
    {
        if (animationFrame < 0)
            throw new ArgumentOutOfRangeException(nameof(animationFrame));
        var normalized = NormalizeComponent(componentName);
        var frame = Family(draftId, normalized)
            .Where(asset => asset.AnimationFrame == animationFrame)
            .ToArray();
        if (frame.Length == 0)
        {
            throw new InvalidOperationException(
                $"{normalized} has no animation frame {animationFrame}.");
        }
        return workspace.StageDeleteMany(
            draftId,
            frame.Select(asset => (asset.Filename, (string?)asset.ContentHash)),
            $"Delete {normalized} animation frame {animationFrame}");
    }

    public SkinDraftManifest InsertAnimationFrame(
        Guid draftId,
        string componentName,
        int sourceFrame,
        int insertionFrame)
    {
        if (sourceFrame < 0)
            throw new ArgumentOutOfRangeException(nameof(sourceFrame));
        if (insertionFrame < 0)
            throw new ArgumentOutOfRangeException(nameof(insertionFrame));
        var normalized = NormalizeComponent(componentName);
        var frames = Family(draftId, normalized)
            .Where(asset => asset.AnimationFrame is not null)
            .ToArray();
        var source = frames
            .Where(asset => asset.AnimationFrame == sourceFrame)
            .ToArray();
        if (source.Length == 0)
        {
            throw new InvalidOperationException(
                $"{normalized} has no animation frame {sourceFrame}.");
        }
        var maximum = frames.Max(asset => asset.AnimationFrame!.Value);
        if (insertionFrame > maximum + 1)
        {
            throw new InvalidDataException(
                $"Insert position must be between 0 and {maximum + 1}.");
        }
        var materialized = packages.Materialize(draftId);
        var output = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in frames)
        {
            var targetFrame = asset.AnimationFrame!.Value >= insertionFrame
                ? asset.AnimationFrame.Value + 1
                : asset.AnimationFrame.Value;
            output.Add(
                animationFilename(normalized, targetFrame, asset.IsTwoX, asset.Extension),
                materialized[asset.Filename]);
        }
        foreach (var asset in source)
        {
            output.Add(
                animationFilename(normalized, insertionFrame, asset.IsTwoX, asset.Extension),
                materialized[asset.Filename]);
        }
        return stageAnimationReplacement(
            draftId,
            frames,
            output,
            $"Insert {normalized} animation frame {insertionFrame} from frame {sourceFrame}");
    }

    public SkinDraftManifest MoveAnimationFrame(
        Guid draftId,
        string componentName,
        int sourceFrame,
        int targetFrame)
    {
        if (sourceFrame < 0)
            throw new ArgumentOutOfRangeException(nameof(sourceFrame));
        if (targetFrame < 0)
            throw new ArgumentOutOfRangeException(nameof(targetFrame));
        var normalized = NormalizeComponent(componentName);
        var frames = Family(draftId, normalized)
            .Where(asset => asset.AnimationFrame is not null)
            .ToArray();
        if (!frames.Any(asset => asset.AnimationFrame == sourceFrame))
        {
            throw new InvalidOperationException(
                $"{normalized} has no animation frame {sourceFrame}.");
        }
        if (!frames.Any(asset => asset.AnimationFrame == targetFrame))
        {
            throw new InvalidOperationException(
                $"{normalized} has no animation frame {targetFrame}.");
        }
        if (sourceFrame == targetFrame)
            return workspace.Load(draftId);

        var materialized = packages.Materialize(draftId);
        var output = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in frames)
        {
            var current = asset.AnimationFrame!.Value;
            var target = current;
            if (current == sourceFrame)
                target = targetFrame;
            else if (sourceFrame < targetFrame && current > sourceFrame && current <= targetFrame)
                target = current - 1;
            else if (sourceFrame > targetFrame && current >= targetFrame && current < sourceFrame)
                target = current + 1;
            output.Add(
                animationFilename(normalized, target, asset.IsTwoX, asset.Extension),
                materialized[asset.Filename]);
        }
        return stageAnimationReplacement(
            draftId,
            frames,
            output,
            $"Move {normalized} animation frame {sourceFrame} to {targetFrame}");
    }

    public SkinDraftManifest NormalizeAudioFamily(
        Guid draftId,
        string componentName)
    {
        var normalized = NormalizeComponent(componentName);
        var family = Family(draftId, normalized)
            .Where(asset => asset.IsAudio)
            .ToArray();
        if (family.Length == 0)
        {
            throw new InvalidOperationException(
                $"{normalized} has no draft-supplied audio files.");
        }
        var materialized = packages.Materialize(draftId);
        var transformer = new SkinAudioTransformService();
        var output = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in family)
        {
            var filename = SkinDraftWorkspaceService.NormalizeSkinFilename(
                Path.ChangeExtension(asset.Filename, ".wav"));
            if (!output.TryAdd(
                    filename,
                    transformer.NormalizeToPcmWav(materialized[asset.Filename]).PcmWav))
            {
                throw new InvalidDataException(
                    $"Multiple audio variants map to '{filename}'. Remove the conflicting formats first.");
            }
        }
        var touched = family
            .Select(asset => asset.Filename)
            .Concat(output.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var mutations = touched.Select(filename =>
        {
            var expected = materialized.TryGetValue(filename, out var current)
                ? SkinDraftWorkspaceService.Hash(current)
                : null;
            return output.TryGetValue(filename, out var replacement)
                ? new SkinDraftFileMutation(
                    filename,
                    SkinDraftChangeKind.Upsert,
                    replacement,
                    expected,
                    $"Normalize {normalized} audio")
                : new SkinDraftFileMutation(
                    filename,
                    SkinDraftChangeKind.Delete,
                    null,
                    expected,
                    $"Normalize {normalized} audio");
        });
        return workspace.StageBatch(
            draftId,
            mutations,
            $"Normalize {normalized} audio to PCM WAV");
    }

    public SkinDraftManifest ImportFiles(
        Guid draftId,
        IEnumerable<string> sourcePaths)
    {
        ArgumentNullException.ThrowIfNull(sourcePaths);
        var sources = sourcePaths
            .Select(Path.GetFullPath)
            .ToArray();
        if (sources.Length == 0)
            throw new InvalidDataException("Choose at least one skin asset file.");
        var materialized = packages.Materialize(draftId);
        var filenames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mutations = new List<SkinDraftFileMutation>(sources.Length);
        foreach (var source in sources)
        {
            if (!File.Exists(source))
                throw new FileNotFoundException("Skin asset file was not found.", source);
            var extension = Path.GetExtension(source).ToLowerInvariant();
            if (!imageExtensions.Contains(extension) && !audioExtensions.Contains(extension))
            {
                throw new InvalidDataException(
                    $"Unsupported skin asset extension '{extension}' in '{Path.GetFileName(source)}'.");
            }
            var filename = SkinDraftWorkspaceService.NormalizeSkinFilename(
                Path.GetFileName(source));
            if (!filenames.Add(filename))
            {
                throw new InvalidDataException(
                    $"Multiple selected files map to '{filename}'. Rename one before importing.");
            }
            var expected = materialized.TryGetValue(filename, out var current)
                ? SkinDraftWorkspaceService.Hash(current)
                : null;
            mutations.Add(new SkinDraftFileMutation(
                filename,
                SkinDraftChangeKind.Upsert,
                File.ReadAllBytes(source),
                expected,
                $"Import {filename}"));
        }
        return workspace.StageBatch(
            draftId,
            mutations,
            $"Import {mutations.Count} skin asset file(s)");
    }

    public SkinDraftManifest ResetFamily(Guid draftId, string componentName)
    {
        var normalized = NormalizeComponent(componentName);
        var manifest = workspace.Load(draftId);
        var filenames = manifest.Changes
            .Where(change => ComponentName(change.Filename).Equals(
                normalized,
                StringComparison.OrdinalIgnoreCase))
            .Select(change => change.Filename)
            .ToArray();
        return workspace.UnstageMany(
            draftId,
            filenames,
            $"Reset {normalized} family");
    }

    public SkinDraftManifest ResetFamilies(
        Guid draftId,
        IEnumerable<string> componentNames,
        string description)
    {
        ArgumentNullException.ThrowIfNull(componentNames);
        var normalized = componentNames
            .Select(NormalizeComponent)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (normalized.Count == 0)
            return workspace.Load(draftId);
        var manifest = workspace.Load(draftId);
        var filenames = manifest.Changes
            .Where(change => normalized.Contains(ComponentName(change.Filename)))
            .Select(change => change.Filename)
            .ToArray();
        return workspace.UnstageMany(draftId, filenames, description);
    }

    public SkinDraftAssetFamilySnapshot CopyFamily(
        Guid draftId,
        string componentName)
    {
        var normalized = NormalizeComponent(componentName);
        var family = Family(draftId, normalized);
        if (family.Count == 0)
        {
            throw new InvalidOperationException(
                $"{normalized} has no draft-supplied files to copy.");
        }
        var materialized = packages.Materialize(draftId);
        var files = family.Select(asset =>
        {
            var stem = Path.ChangeExtension(asset.Filename, null)
                .Replace('\\', '/');
            if (!stem.StartsWith(normalized, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Asset '{asset.Filename}' does not belong to '{normalized}'.");
            }
            return new SkinDraftAssetClipboardFile(
                stem[normalized.Length..],
                asset.Extension,
                materialized[asset.Filename].ToArray());
        }).ToArray();
        return new SkinDraftAssetFamilySnapshot(normalized, files);
    }

    public SkinDraftManifest PasteFamily(
        Guid draftId,
        string targetComponentName,
        SkinDraftAssetFamilySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Files.Count == 0)
            throw new InvalidDataException("The copied asset family is empty.");
        var target = NormalizeComponent(targetComponentName);
        var materialized = packages.Materialize(draftId);
        var existing = Family(draftId, target);
        var mutations = existing.Select(asset => new SkinDraftFileMutation(
                asset.Filename,
                SkinDraftChangeKind.Delete,
                null,
                asset.ContentHash,
                $"Replace {target} family"))
            .ToList();
        var mapped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in snapshot.Files)
        {
            if (!imageExtensions.Contains(file.Extension)
                && !audioExtensions.Contains(file.Extension))
            {
                throw new InvalidDataException(
                    $"Unsupported clipboard extension '{file.Extension}'.");
            }
            var filename = SkinDraftWorkspaceService.NormalizeSkinFilename(
                target + file.FilenameSuffix + file.Extension);
            if (!mapped.Add(filename))
                throw new InvalidDataException("The copied family contains duplicate variants.");
            var expected = materialized.TryGetValue(filename, out var current)
                ? SkinDraftWorkspaceService.Hash(current)
                : null;
            mutations.Add(new SkinDraftFileMutation(
                filename,
                SkinDraftChangeKind.Upsert,
                file.Contents.ToArray(),
                expected,
                $"Paste {snapshot.SourceComponentName} into {target}"));
        }
        return workspace.StageBatch(
            draftId,
            mutations,
            $"Paste {snapshot.SourceComponentName} into {target}");
    }

    public IReadOnlyList<string> ExportFamily(
        Guid draftId,
        string componentName,
        string destinationDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        var files = packages.Materialize(draftId);
        var family = Family(draftId, componentName);
        Directory.CreateDirectory(destinationDirectory);
        var destinationRoot = Path.GetFullPath(destinationDirectory);
        var rootPrefix = Path.TrimEndingDirectorySeparator(destinationRoot)
                         + Path.DirectorySeparatorChar;
        var written = new List<string>();
        foreach (var asset in family)
        {
            var destination = Path.GetFullPath(Path.Combine(
                destinationRoot,
                asset.Filename.Replace('/', Path.DirectorySeparatorChar)));
            if (!destination.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Asset export escaped its destination.");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.WriteAllBytes(destination, files[asset.Filename]);
            written.Add(destination);
        }
        return written;
    }

    public SkinDraftManifest TransformImageFamily(
        Guid draftId,
        string componentName,
        SkinImageTransform transform,
        SkinImageTransformScope scope = SkinImageTransformScope.FullFamily,
        int? animationFrame = null)
    {
        var materialized = packages.Materialize(draftId);
        var normalizedComponent = NormalizeComponent(componentName);
        var family = List(materialized)
            .Where(asset => asset.ComponentName.Equals(
                normalizedComponent,
                StringComparison.OrdinalIgnoreCase))
            .Where(asset => asset.IsImage)
            .ToArray();
        if (family.Length == 0)
            throw new InvalidOperationException(
                $"{NormalizeComponent(componentName)} has no draft-supplied image files.");
        family = SelectTransformScope(family, scope, animationFrame).ToArray();
        if (family.Length == 0)
        {
            throw new InvalidOperationException(
                $"{NormalizeComponent(componentName)} has no image files in the {scope} scope.");
        }
        var transformer = new SkinImageTransformService();
        return workspace.StageFileMany(
            draftId,
            family.Select(asset => (
                asset.Filename,
                transformer.Apply(
                    materialized[asset.Filename],
                    asset.Filename,
                    transform),
                (string?)asset.ContentHash)),
            $"Transform {NormalizeComponent(componentName)} ({scopeDescription(scope, animationFrame)})");
    }

    internal static IReadOnlyList<SkinDraftAsset> SelectTransformScope(
        IReadOnlyList<SkinDraftAsset> family,
        SkinImageTransformScope scope,
        int? animationFrame = null)
    {
        ArgumentNullException.ThrowIfNull(family);
        return scope switch
        {
            SkinImageTransformScope.FullFamily => family.ToArray(),
            SkinImageTransformScope.OneXVariants =>
                family.Where(asset => !asset.IsTwoX).ToArray(),
            SkinImageTransformScope.TwoXVariants =>
                family.Where(asset => asset.IsTwoX).ToArray(),
            SkinImageTransformScope.PrimaryPair => family
                .GroupBy(asset => asset.IsTwoX)
                .SelectMany(group =>
                {
                    var staticAssets = group
                        .Where(asset => asset.AnimationFrame is null)
                        .ToArray();
                    if (staticAssets.Length > 0)
                        return staticAssets;
                    var firstFrame = group
                        .Where(asset => asset.AnimationFrame is not null)
                        .Min(asset => asset.AnimationFrame);
                    return group.Where(asset =>
                        asset.AnimationFrame == firstFrame);
                })
                .ToArray(),
            SkinImageTransformScope.AnimationFramePair when animationFrame is int frame =>
                family.Where(asset => asset.AnimationFrame == frame).ToArray(),
            SkinImageTransformScope.AnimationFramePair =>
                throw new ArgumentException(
                    "An animation frame is required for the animation-frame scope.",
                    nameof(animationFrame)),
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, null),
        };
    }

    private static string scopeDescription(
        SkinImageTransformScope scope,
        int? animationFrame) =>
        scope == SkinImageTransformScope.AnimationFramePair
            ? $"animation frame {animationFrame}"
            : scope.ToString();

    private SkinDraftManifest stageAnimationReplacement(
        Guid draftId,
        IReadOnlyList<SkinDraftAsset> existingFrames,
        IReadOnlyDictionary<string, byte[]> output,
        string description)
    {
        var materialized = packages.Materialize(draftId);
        var touched = existingFrames
            .Select(asset => asset.Filename)
            .Concat(output.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var mutations = new List<SkinDraftFileMutation>(touched.Length);
        foreach (var filename in touched)
        {
            var current = materialized.TryGetValue(filename, out var bytes)
                ? bytes
                : null;
            var expected = current is null
                ? null
                : SkinDraftWorkspaceService.Hash(current);
            if (output.TryGetValue(filename, out var replacement))
            {
                mutations.Add(new SkinDraftFileMutation(
                    filename,
                    SkinDraftChangeKind.Upsert,
                    replacement,
                    expected,
                    description));
            }
            else
            {
                mutations.Add(new SkinDraftFileMutation(
                    filename,
                    SkinDraftChangeKind.Delete,
                    null,
                    expected,
                    description));
            }
        }
        return workspace.StageBatch(draftId, mutations, description);
    }

    private static string animationFilename(
        string componentName,
        int frame,
        bool isTwoX,
        string extension) =>
        $"{componentName}-{frame}{(isTwoX ? "@2x" : "")}{extension}";

    public static string ComponentName(string filename)
    {
        var normalized = Path.ChangeExtension(
                SkinDraftWorkspaceService.NormalizeSkinFilename(filename),
                null)
            .Replace("@2x", "", StringComparison.OrdinalIgnoreCase);
        if (isNumberFontGlyph(normalized))
            return normalized;
        var dash = normalized.LastIndexOf('-');
        if (dash >= 0 && int.TryParse(normalized[(dash + 1)..], out _))
            normalized = normalized[..dash];
        return normalized;
    }

    public static int? AnimationFrame(string filename)
    {
        var stem = Path.ChangeExtension(
                SkinDraftWorkspaceService.NormalizeSkinFilename(filename),
                null)
            .Replace("@2x", "", StringComparison.OrdinalIgnoreCase);
        if (isNumberFontGlyph(stem))
            return null;
        var dash = stem.LastIndexOf('-');
        return dash >= 0 && int.TryParse(stem[(dash + 1)..], out var frame)
            ? frame
            : null;
    }

    private static bool isNumberFontGlyph(string stem)
    {
        var filename = Path.GetFileName(stem);
        var dash = filename.LastIndexOf('-');
        if (dash <= 0 || dash == filename.Length - 1)
            return false;
        var prefix = filename[..dash];
        var suffix = filename[(dash + 1)..];
        return (prefix.Equals("default", StringComparison.OrdinalIgnoreCase)
                || prefix.Equals("score", StringComparison.OrdinalIgnoreCase)
                || prefix.Equals("combo", StringComparison.OrdinalIgnoreCase)
                || prefix.Equals("scoreentry", StringComparison.OrdinalIgnoreCase))
               && (suffix.Length == 1 && char.IsDigit(suffix[0])
                   || suffix.Equals("comma", StringComparison.OrdinalIgnoreCase)
                   || suffix.Equals("dot", StringComparison.OrdinalIgnoreCase)
                   || suffix.Equals("percent", StringComparison.OrdinalIgnoreCase)
                   || suffix.Equals("x", StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeComponent(string componentName) =>
        ComponentName(componentName + ".png");
}
