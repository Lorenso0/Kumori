using System.ComponentModel;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Kumori.Tracking;
using Color = System.Windows.Media.Color;

namespace Kumori.App.Skins;

public enum SkinRecolorMode
{
    Colorize,
    Tint,
    HueSaturation,
}

internal enum LegacySpinnerPreviewStyle
{
    Default,
    Old,
    New,
}

internal static class LegacySpinnerPreview
{
    public static LegacySpinnerPreviewStyle Resolve(bool hasBackground, bool hasTop) =>
        hasBackground
            ? LegacySpinnerPreviewStyle.Old
            : hasTop
                ? LegacySpinnerPreviewStyle.New
                : LegacySpinnerPreviewStyle.Default;
}

/// <summary>
/// The in-memory write set for one skin-editing session.  Keeping the bytes and
/// the source hash together makes previewing non-destructive and gives the
/// Realm writer an optimistic-concurrency check when the draft is applied.
/// </summary>
public sealed class SkinDraftSession
{
    private readonly Dictionary<string, SkinDraftChange> changes =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Dictionary<string, SkinDraftChange>> history = [];
    private int historyIndex;

    public SkinDraftSession(Guid skinId)
    {
        SkinId = skinId;
        history.Add(new Dictionary<string, SkinDraftChange>(changes, StringComparer.OrdinalIgnoreCase));
    }

    public Guid SkinId { get; }
    public IReadOnlyCollection<SkinDraftChange> Changes => changes.Values;
    public int Count => changes.Count;
    public bool CanUndo => historyIndex > 0;
    public bool CanRedo => historyIndex < history.Count - 1;
    public long Revision { get; private set; }

    public void Stage(string filename, string? expectedHash, byte[] bytes, string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filename);
        ArgumentNullException.ThrowIfNull(bytes);
        changes[filename] = new SkinDraftChange(filename, expectedHash, bytes.ToArray(), description);
        RecordHistory();
    }

    public void StageDeletion(string filename, string expectedHash, string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filename);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedHash);
        changes[filename] = new SkinDraftChange(
            filename,
            expectedHash,
            [],
            description,
            SkinDraftOperation.Delete);
        RecordHistory();
    }

    public void StageRange(IEnumerable<SkinDraftChange> entries)
    {
        foreach (var entry in entries)
            changes[entry.Filename] = entry with { Bytes = entry.Bytes.ToArray() };
        RecordHistory();
    }

    public void ReplaceWhere(
        Func<SkinDraftChange, bool> predicate,
        IEnumerable<SkinDraftChange> entries)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        foreach (var filename in changes.Values.Where(predicate)
                     .Select(change => change.Filename)
                     .ToArray())
            changes.Remove(filename);
        foreach (var entry in entries)
            changes[entry.Filename] = entry with { Bytes = entry.Bytes.ToArray() };
        RecordHistory();
    }

    public bool Remove(string filename)
    {
        if (!changes.Remove(filename)) return false;
        RecordHistory();
        return true;
    }

    public bool Undo()
    {
        if (!CanUndo) return false;
        Restore(--historyIndex);
        Revision++;
        return true;
    }

    public bool Redo()
    {
        if (!CanRedo) return false;
        Restore(++historyIndex);
        Revision++;
        return true;
    }

    /// <summary>Used after a successful Realm apply; disk is now the baseline.</summary>
    public void AcceptApplied()
    {
        changes.Clear();
        history.Clear();
        history.Add(new Dictionary<string, SkinDraftChange>(changes, StringComparer.OrdinalIgnoreCase));
        historyIndex = 0;
        Revision++;
    }

    /// <summary>
    /// Removes only the files that reached disk after a partial apply. Remaining
    /// entries stay available for retry, but the old undo timeline is no longer
    /// valid once any source file has changed externally.
    /// </summary>
    public void AcceptCommitted(IEnumerable<string> filenames)
    {
        foreach (var filename in filenames)
            changes.Remove(filename);
        history.Clear();
        history.Add(CloneChanges());
        historyIndex = 0;
        Revision++;
    }

    private void RecordHistory()
    {
        if (historyIndex < history.Count - 1)
            history.RemoveRange(historyIndex + 1, history.Count - historyIndex - 1);
        history.Add(CloneChanges());
        historyIndex = history.Count - 1;
        Revision++;
    }

    private void Restore(int index)
    {
        changes.Clear();
        foreach (var entry in history[index])
            changes[entry.Key] = entry.Value;
    }

    private Dictionary<string, SkinDraftChange> CloneChanges() =>
        new(changes, StringComparer.OrdinalIgnoreCase);
}

public sealed record SkinDraftChange(
    string Filename,
    string? ExpectedHash,
    byte[] Bytes,
    string Description,
    SkinDraftOperation Operation = SkinDraftOperation.Upsert)
{
    public bool IsDeletion => Operation == SkinDraftOperation.Delete;
}

public enum SkinDraftOperation
{
    Upsert,
    Delete,
}

internal static class SkinFileReplacementPlanner
{
    public static SkinDraftChange Build(
        LazerSkinFileInfo target,
        string sourcePath,
        byte[] sourceBytes)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentNullException.ThrowIfNull(sourceBytes);

        return new SkinDraftChange(
            target.Filename,
            target.Hash,
            sourceBytes,
            $"{target.Filename} (replacement from {Path.GetFileName(sourcePath)})");
    }
}

internal static class SkinDraftProjection
{
    public static IReadOnlyList<LazerSkinFileInfo> EffectiveFiles(
        IEnumerable<LazerSkinFileInfo> baselineFiles,
        IEnumerable<SkinDraftChange> draftChanges)
    {
        var files = baselineFiles.ToDictionary(
            file => file.Filename,
            StringComparer.OrdinalIgnoreCase);
        foreach (var change in draftChanges)
        {
            if (change.IsDeletion)
            {
                files.Remove(change.Filename);
                continue;
            }

            files[change.Filename] = new LazerSkinFileInfo(
                change.Filename.Replace('\\', '/'),
                change.ExpectedHash ?? "",
                change.Bytes.LongLength);
        }

        return files.Values
            .OrderBy(file => file.Filename, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static IReadOnlyList<SkinDraftChange> NormalizeAgainstBaseline(
        IEnumerable<LazerSkinFileInfo> baselineFiles,
        IEnumerable<SkinDraftChange> proposedChanges)
    {
        var baseline = baselineFiles.ToDictionary(
            file => file.Filename,
            StringComparer.OrdinalIgnoreCase);
        var proposed = new Dictionary<string, SkinDraftChange>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var change in proposedChanges)
            proposed[change.Filename.Replace('\\', '/')] = change;

        var normalized = new List<SkinDraftChange>();
        foreach (var (filename, change) in proposed)
        {
            baseline.TryGetValue(filename, out var original);
            if (change.IsDeletion)
            {
                // A file introduced only by this draft is removed by clearing
                // its upsert. There is no Realm object on disk to delete.
                if (original is null)
                    continue;
                normalized.Add(change with
                {
                    Filename = filename,
                    ExpectedHash = original.Hash,
                    Bytes = [],
                });
                continue;
            }

            normalized.Add(change with
            {
                Filename = filename,
                ExpectedHash = original?.Hash,
                Bytes = change.Bytes.ToArray(),
            });
        }

        return normalized
            .OrderBy(change => change.Filename, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}

internal static class SkinEditorCatalogProjection
{
    public static LazerSkinInfo ApplyBatch(
        LazerSkinInfo skin,
        IReadOnlyList<LazerSkinBatchMutation> mutations,
        LazerSkinBatchWriteResult result)
    {
        ArgumentNullException.ThrowIfNull(skin);
        ArgumentNullException.ThrowIfNull(mutations);
        ArgumentNullException.ThrowIfNull(result);
        if (!result.Succeeded || result.Results.Count != mutations.Count)
            throw new InvalidOperationException("Only a complete successful skin batch can update the Studio view.");

        var files = skin.Files.ToDictionary(
            file => file.Filename,
            StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < mutations.Count; index++)
        {
            var mutation = mutations[index];
            if (mutation.IsDeletion)
            {
                files.Remove(mutation.Filename);
                continue;
            }

            var filename = mutation.Filename.Replace('\\', '/');
            files[filename] = new LazerSkinFileInfo(
                filename,
                result.Results[index].Hash,
                mutation.Bytes.LongLength);
        }

        return skin with
        {
            Files = files.Values
                .OrderBy(file => file.Filename, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
        };
    }
}

public sealed record SkinElementSemanticGroup(string Name, IReadOnlyList<string> Categories);

public static class SkinElementSemanticGroups
{
    public static readonly IReadOnlyList<SkinElementSemanticGroup> All =
    [
        new("Hit objects", ["Hitcircles"]),
        new("Sliders", ["Sliders"]),
        new("Cursor", ["Cursor"]),
        new("Judgements", ["Judgements"]),
        new("HUD & interface", ["Scorebar", "Interface"]),
        new("Numbers", ["Numbers"]),
        new("Spinner", ["Spinner"]),
        new("Modes & other", ["Catch", "Taiko", "Mania", "Skin Previews", "Sounds", "Other"]),
    ];

    public static SkinElementSemanticGroup ForCategory(string category) =>
        All.FirstOrDefault(group => group.Categories.Contains(category, StringComparer.OrdinalIgnoreCase))
        ?? All[^1];

    public static string UsageForFilename(string filename)
    {
        var category = SkinElementCategorizer.CategoryFor(filename);
        return ForCategory(category).Name switch
        {
            "Hit objects" => "Used by hit circles, approach circles, and the combo preview.",
            "Sliders" => "Used by the slider scene and linked slider colours in skin.ini.",
            "Cursor" => "Used by the cursor scene and cursor behaviour settings in skin.ini.",
            "HUD & interface" => "Used by HUD/interface scenes and linked interface colours in skin.ini.",
            _ => $"Gameplay group: {ForCategory(category).Name}.",
        };
    }
}

public sealed class SkinElementCategory
{
    public required string Name { get; init; }
    public bool IsSubfolder { get; init; }
    public List<SkinElementEntry> Files { get; } = new();
}

public sealed class SkinElementEntry : INotifyPropertyChanged
{
    private BitmapSource? thumbnail;
    private readonly List<SkinElementEntry> resolutionVariants = [];
    private bool? hasVisiblePixels;

    public SkinElementEntry(LazerSkinFileInfo file)
    {
        File = file;
        Mode = SkinElementCategorizer.DefaultModeFor(file.Filename);
    }

    public LazerSkinFileInfo File { get; private set; }
    public string Filename => File.Filename;
    public string Hash => File.Hash;
    public bool IsImage => SkinElementCategorizer.IsImage(Filename);
    public bool IsAudio => SkinElementCategorizer.IsAudio(Filename);
    public bool IsHighResolution => SkinElementCategorizer.IsHighResolution(Filename);
    public IReadOnlyList<SkinElementEntry> ResolutionVariants => resolutionVariants;
    public IEnumerable<SkinElementEntry> PhysicalEntries => [this, .. resolutionVariants];
    public bool HasPairedResolution => resolutionVariants.Count > 0;
    public string ResolutionVariantLabel => HasPairedResolution ? "1× + 2× · edits both" : "";
    public long TotalSizeBytes => PhysicalEntries.Sum(entry => entry.File.SizeBytes);
    public bool? HasVisiblePixels
    {
        get => hasVisiblePixels;
        set
        {
            hasVisiblePixels = value;
            OnPropertyChanged(nameof(HasVisiblePixels));
            OnPropertyChanged(nameof(IsLogicallyEmpty));
        }
    }
    public bool IsLogicallyEmpty =>
        IsImage
        && PhysicalEntries.All(entry => entry.HasVisiblePixels == false);
    public SkinRecolorMode Mode { get; set; }
    public Color? TintColor { get; set; }
    public double HueShiftDegrees { get; set; }
    public double SaturationMultiplier { get; set; } = 1;
    public double LightnessMultiplier { get; set; } = 1;
    public byte[]? OriginalBytes { get; set; }
    public byte[]? OriginalPixels { get; set; }
    public int PixelWidth { get; set; }
    public int PixelHeight { get; set; }
    public int Stride { get; set; }

    public BitmapSource? Thumbnail
    {
        get => thumbnail;
        set
        {
            thumbnail = value;
            OnPropertyChanged(nameof(Thumbnail));
        }
    }

    public bool HasEdits => Mode == SkinRecolorMode.HueSaturation
        ? HueShiftDegrees != 0
          || Math.Abs(SaturationMultiplier - 1) > 0.0001
          || Math.Abs(LightnessMultiplier - 1) > 0.0001
        : TintColor is not null;

    public string EditState => HasEdits ? "Edited" : "";

    public void Reset()
    {
        foreach (var entry in PhysicalEntries)
            entry.ResetOwnState();
        RaiseStateChanged();
    }

    public void AddResolutionVariant(SkinElementEntry entry)
    {
        resolutionVariants.Add(entry);
        OnPropertyChanged(nameof(ResolutionVariants));
        OnPropertyChanged(nameof(HasPairedResolution));
        OnPropertyChanged(nameof(ResolutionVariantLabel));
        OnPropertyChanged(nameof(TotalSizeBytes));
        OnPropertyChanged(nameof(IsLogicallyEmpty));
    }

    public void SynchronizeEditsToVariants()
    {
        foreach (var variant in resolutionVariants)
        {
            variant.Mode = Mode;
            variant.TintColor = TintColor;
            variant.HueShiftDegrees = HueShiftDegrees;
            variant.SaturationMultiplier = SaturationMultiplier;
            variant.LightnessMultiplier = LightnessMultiplier;
            variant.RaiseStateChanged();
        }
    }

    public void ReplaceFile(LazerSkinFileInfo file)
    {
        File = file;
        OnPropertyChanged(nameof(Hash));
        OnPropertyChanged(nameof(File));
    }

    public void RaiseStateChanged()
    {
        OnPropertyChanged(nameof(HasEdits));
        OnPropertyChanged(nameof(EditState));
    }

    private void ResetOwnState()
    {
        TintColor = null;
        HueShiftDegrees = 0;
        SaturationMultiplier = 1;
        LightnessMultiplier = 1;
        RaiseStateChanged();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string property) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}

public static class SkinElementCategorizer
{
    public static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg"];
    public static readonly string[] AudioExtensions = [".mp3", ".wav", ".ogg"];

    private static readonly string[] orderedCategories =
    [
        "Cursor", "Hitcircles", "Numbers", "Judgements", "Sliders", "Spinner",
        "Scorebar", "Interface", "Catch", "Taiko", "Mania", "Skin Previews",
        "Sounds", "Other",
    ];

    public static IReadOnlyList<string> ExtraCategories => orderedCategories;

    public static bool IsImage(string filename) =>
        ImageExtensions.Contains(Path.GetExtension(filename), StringComparer.OrdinalIgnoreCase);

    public static bool IsAudio(string filename) =>
        AudioExtensions.Contains(Path.GetExtension(filename), StringComparer.OrdinalIgnoreCase);

    public static bool IsHighResolution(string filename) =>
        Path.GetFileNameWithoutExtension(filename)
            .EndsWith("@2x", StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<SkinElementCategory> Categorize(IEnumerable<LazerSkinFileInfo> files)
    {
        var roots = orderedCategories.ToDictionary(
            name => name,
            name => new SkinElementCategory { Name = name },
            StringComparer.OrdinalIgnoreCase);
        var folders = new Dictionary<string, SkinElementCategory>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files.Where(file =>
                     !file.Filename.Equals("skin.ini", StringComparison.OrdinalIgnoreCase)))
        {
            var normalized = file.Filename.Replace('\\', '/');
            var slash = normalized.IndexOf('/');
            SkinElementCategory category;
            if (slash >= 0)
            {
                var folder = normalized[..slash];
                if (!folders.TryGetValue(folder, out category!))
                {
                    category = new SkinElementCategory { Name = folder, IsSubfolder = true };
                    folders[folder] = category;
                }
            }
            else
            {
                category = roots[CategoryFor(normalized)];
            }

            category.Files.Add(new SkinElementEntry(file));
        }

        foreach (var category in roots.Values.Concat(folders.Values))
        {
            var logicalEntries = category.Files
                .GroupBy(entry => ResolutionKey(entry.Filename), StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var entries = group.ToArray();
                    var primary = entries.FirstOrDefault(entry => entry.IsHighResolution)
                        ?? entries[0];
                    foreach (var variant in entries.Where(entry => !ReferenceEquals(entry, primary)))
                        primary.AddResolutionVariant(variant);
                    return primary;
                })
                .ToList();
            logicalEntries.Sort((left, right) =>
                StringComparer.OrdinalIgnoreCase.Compare(left.Filename, right.Filename));
            category.Files.Clear();
            category.Files.AddRange(logicalEntries);
        }

        return orderedCategories.Select(name => roots[name])
            .Where(category => category.Files.Count > 0)
            .Concat(folders.Values.OrderBy(category => category.Name, StringComparer.OrdinalIgnoreCase))
            .ToArray();
    }

    public static SkinRecolorMode DefaultModeFor(string filename)
    {
        var stem = Stem(filename);
        return stem.Contains("preview") || stem.Contains("banner") || stem.Contains("thumbnail")
            ? SkinRecolorMode.HueSaturation
            : SkinRecolorMode.Colorize;
    }

    internal static string CategoryFor(string filename)
    {
        var stem = Stem(filename);
        if (IsAudio(filename)) return "Sounds";
        if (!IsImage(filename)) return "Other";
        if (stem.Contains("preview") || stem.Contains("banner") || stem.Contains("thumbnail"))
            return "Skin Previews";
        if (stem.StartsWith("cursor")) return "Cursor";
        if (stem.StartsWith("fruit") || stem == "comboburst-fruits") return "Catch";
        if (stem.StartsWith("taiko") || stem.StartsWith("pippidon")) return "Taiko";
        if (stem.StartsWith("mania") || stem.StartsWith("stage-") || stem.StartsWith("key")
            || stem.StartsWith("note") || stem is "lightingn" or "lightingl" or "comboburst-mania")
            return "Mania";
        if (stem.StartsWith("hitcircle") || stem.StartsWith("approachcircle")
            || stem.StartsWith("followpoint")) return "Hitcircles";
        if (stem.StartsWith("scorebar")) return "Scorebar";
        if (stem.StartsWith("default-") || stem.StartsWith("score-")
            || stem.StartsWith("combo-") || stem.StartsWith("scoreentry")) return "Numbers";
        if (stem.StartsWith("hit0") || stem.StartsWith("hit50") || stem.StartsWith("hit100")
            || stem.StartsWith("hit300") || stem.StartsWith("particle")
            || stem.StartsWith("lighting")) return "Judgements";
        if (stem.StartsWith("slider") || stem.StartsWith("reversearrow")) return "Sliders";
        if (stem.StartsWith("spinner")) return "Spinner";
        if (stem.StartsWith("menu") || stem.StartsWith("button") || stem.StartsWith("star")
            || stem.StartsWith("ranking") || stem.StartsWith("pause") || stem.StartsWith("fail")
            || stem.StartsWith("selection") || stem.StartsWith("mode-") || stem.StartsWith("count")
            || stem is "go" or "ready" || stem.StartsWith("play") || stem.StartsWith("inputoverlay")
            || stem.StartsWith("arrow") || stem.StartsWith("section-") || stem.StartsWith("multi-")
            || stem.StartsWith("songselect") || stem.StartsWith("comboburst")
            || stem.StartsWith("masking-")) return "Interface";
        return "Other";
    }

    private static string Stem(string filename)
    {
        var stem = Path.GetFileNameWithoutExtension(filename).ToLowerInvariant();
        if (stem.EndsWith("@2x", StringComparison.Ordinal))
            stem = stem[..^3];
        // A trailing number is the glyph identity for number fonts, not an
        // animation frame. Stripping it turned default-0/score-0/combo-0 into
        // unrelated "Other" assets and left only punctuation in Numbers.
        if (stem.StartsWith("default-", StringComparison.Ordinal)
            || stem.StartsWith("score-", StringComparison.Ordinal)
            || stem.StartsWith("combo-", StringComparison.Ordinal)
            || stem.StartsWith("scoreentry-", StringComparison.Ordinal))
            return stem;
        var animationSuffix = stem.LastIndexOf('-');
        if (animationSuffix > 0
            && int.TryParse(stem[(animationSuffix + 1)..], out _))
            stem = stem[..animationSuffix];
        return stem;
    }

    private static string ResolutionKey(string filename)
    {
        var normalized = filename.Replace('\\', '/');
        var extension = Path.GetExtension(normalized);
        var stem = normalized[..^extension.Length];
        if (stem.EndsWith("@2x", StringComparison.OrdinalIgnoreCase))
            stem = stem[..^3];
        return stem + extension;
    }
}

public sealed record SkinExtraPackFile(string Filename, byte[] Bytes);

public static class SkinExtrasCatalog
{
    public static readonly IReadOnlyList<string> CursorCollections =
    [
        "Cursors with cursortrail",
        "Cursors with long cursortrail",
        "Cursors without cursortrail",
    ];
}

public static class SkinExtraPackPlanner
{
    public static IReadOnlyList<SkinDraftChange> BuildChanges(
        string category,
        IEnumerable<LazerSkinFileInfo> currentFiles,
        IEnumerable<SkinExtraPackFile> incomingFiles,
        string packName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        ArgumentException.ThrowIfNullOrWhiteSpace(packName);

        var current = currentFiles
            .Where(file => !file.Filename.Equals("skin.ini", StringComparison.OrdinalIgnoreCase))
            .Where(file => SkinElementCategorizer.CategoryFor(file.Filename)
                .Equals(category, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(file => file.Filename, StringComparer.OrdinalIgnoreCase);
        var incoming = incomingFiles.ToDictionary(
            file => file.Filename.Replace('\\', '/'),
            StringComparer.OrdinalIgnoreCase);
        var changes = new List<SkinDraftChange>();

        foreach (var existing in current.Values.Where(file => !incoming.ContainsKey(file.Filename)))
        {
            changes.Add(new SkinDraftChange(
                existing.Filename,
                existing.Hash,
                [],
                $"{existing.Filename} (removed by Extras/{category}/{packName})",
                SkinDraftOperation.Delete));
        }

        foreach (var file in incoming.Values)
        {
            current.TryGetValue(file.Filename, out var existing);
            changes.Add(new SkinDraftChange(
                file.Filename,
                existing?.Hash,
                file.Bytes,
                existing is null
                    ? $"{file.Filename} (added from Extras/{category}/{packName})"
                    : $"{file.Filename} (replaced from Extras/{category}/{packName})"));
        }

        return changes;
    }

    public static SkinExtraApplicationPlan BuildFamilyPlan(
        SkinExtraPackManifest manifest,
        IEnumerable<LazerSkinFileInfo> currentFiles,
        IEnumerable<SkinExtraPackFile> incomingFiles,
        SkinIniDocument? currentIni,
        bool includeIniPatch = true,
        bool lazerUsedOnly = false,
        bool replaceEntireFamily = true,
        bool replaceSelectedLogicalElements = false)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var family = SkinExtraFamilyRegistry.ById(manifest.FamilyId);
        var patch = includeIniPatch
            ? manifest.IniPatch
                .Where(entry => !lazerUsedOnly
                                || SkinExtraLazerCompatibility.IsIniPatchUsed(
                                    manifest.FamilyId,
                                    entry))
                .ToList()
            : [];
        var incoming = incomingFiles
            .Where(file => !lazerUsedOnly
                           || SkinExtraLazerCompatibility.IsLazerUsed(
                               file.Filename,
                               manifest.FamilyId))
            .Where(file => !SkinCursorMiddlePolicy.IsCursorFamily(manifest.FamilyId)
                           || !SkinCursorMiddlePolicy.IsCursorMiddle(file.Filename))
            .ToArray();
        HashSet<string> owned;

        if (manifest.FamilyId.Equals("osu.number-font", StringComparison.OrdinalIgnoreCase))
        {
            var affectedPrefixKeys = manifest.FontRoles
                .Select(NumberFontPrefixKeyForRole)
                .Where(key => key is not null)
                .Cast<string>()
                .Concat(patch
                    .Where(entry => entry.Section.Equals(
                                        "Fonts",
                                        StringComparison.OrdinalIgnoreCase)
                                    && IsNumberFontPrefixKey(entry.Key))
                    .Select(entry => entry.Key))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var unaffectedPrefixes = NumberFontPrefixKeys
                .Where(key => !affectedPrefixKeys.Contains(key))
                .Select(key => CurrentNumberFontPrefix(currentIni, key))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var ownedPrefixes = affectedPrefixKeys
                .Select(key => CurrentNumberFontPrefix(currentIni, key))
                .Where(prefix => !unaffectedPrefixes.Contains(prefix))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            owned = currentFiles
                .Where(file => ownedPrefixes.Any(prefix =>
                    Path.GetFileName(file.Filename).StartsWith(
                        prefix + "-",
                        StringComparison.OrdinalIgnoreCase)))
                .Where(file => !lazerUsedOnly
                               || SkinExtraLazerCompatibility.IsLazerUsed(
                                   file.Filename,
                                   manifest.FamilyId))
                .Select(file => file.Filename)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            owned = currentFiles
                .Where(file => SkinExtraFamilyRegistry.ForFile(file.Filename)?.Id == family?.Id)
                .Where(file => !lazerUsedOnly
                               || SkinExtraLazerCompatibility.IsLazerUsed(
                                   file.Filename,
                                   manifest.FamilyId))
                .Select(file => file.Filename)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        var current = currentFiles.ToDictionary(file => file.Filename, StringComparer.OrdinalIgnoreCase);
        var incomingByName = incoming.ToDictionary(
            file => file.Filename.Replace('\\', '/'),
            StringComparer.OrdinalIgnoreCase);
        var changes = new List<SkinDraftChange>();
        var removals = replaceEntireFamily
            ? owned.Where(filename => !incomingByName.ContainsKey(filename))
            : replaceSelectedLogicalElements
                ? SkinExtraLogicalSelectionPlanner.FindReplacedCurrentFiles(
                    manifest.FamilyId,
                    owned,
                    incomingByName.Keys)
                : [];
        foreach (var filename in removals)
        {
            var existing = current[filename];
            changes.Add(new SkinDraftChange(
                existing.Filename,
                existing.Hash,
                [],
                $"{existing.Filename} (removed by {manifest.FamilyName})",
                SkinDraftOperation.Delete));
        }
        foreach (var file in incomingByName.Values)
        {
            current.TryGetValue(file.Filename, out var existing);
            changes.Add(new SkinDraftChange(
                file.Filename,
                existing?.Hash,
                file.Bytes,
                existing is null
                    ? $"{file.Filename} (added from {manifest.DisplayName})"
                    : $"{file.Filename} (replaced from {manifest.DisplayName})"));
        }
        return new SkinExtraApplicationPlan(changes, patch, owned);
    }

    private static readonly string[] NumberFontPrefixKeys =
        ["HitCirclePrefix", "ScorePrefix", "ComboPrefix"];

    private static bool IsNumberFontPrefixKey(string settingKey) =>
        NumberFontPrefixKeys.Contains(settingKey, StringComparer.OrdinalIgnoreCase);

    private static string? NumberFontPrefixKeyForRole(string role) =>
        role.ToLowerInvariant() switch
        {
            "hitcircle" => "HitCirclePrefix",
            "score" => "ScorePrefix",
            "combo" => "ComboPrefix",
            _ => null,
        };

    private static string CurrentNumberFontPrefix(
        SkinIniDocument? currentIni,
        string settingKey)
    {
        var configured = currentIni?.GetValue("Fonts", settingKey);
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.GetFileName(configured.Replace('\\', '/'));

        return settingKey.ToLowerInvariant() switch
        {
            "hitcircleprefix" => "default",
            _ => "score",
        };
    }
}

public sealed record SkinExtraApplicationPlan(
    IReadOnlyList<SkinDraftChange> Changes,
    IReadOnlyList<SkinExtraIniPatchEntry> IniPatch,
    IReadOnlySet<string> OwnedCurrentFiles);
