using System.IO;
using System.IO.Compression;

namespace Kumori.Skins;

public sealed record SkinExtractionFile(string Filename, byte[] Bytes);

public sealed class SkinExtractionSource
{
    public required string DisplayName { get; init; }
    public string? Author { get; init; }
    public required string SourceLabel { get; init; }
    public required IReadOnlyList<SkinExtractionFile> Files { get; init; }
}

public sealed class SkinExtractionFamily
{
    public required SkinExtraFamilyDefinition Definition { get; init; }
    public string? Variant { get; init; }
    public required IReadOnlyList<SkinExtractionFile> Files { get; init; }
    public required IReadOnlyList<SkinExtraIniPatchEntry> IniPatch { get; init; }
    public IReadOnlyList<string> FontRoles { get; init; } = [];
    public string SelectionId => Variant is null ? Definition.Id : $"{Definition.Id}:{Variant}";
    public string DisplayName => Variant is null ? Definition.Name : $"{Definition.Name} — {Variant}";
}

public enum SkinExtraExtractionStatus
{
    Extracted,
    ExactDuplicateSkipped,
}

public sealed record SkinExtraExtractionResult(
    SkinExtraExtractionStatus Status,
    string Family,
    string? DirectoryPath,
    string Message,
    string? SimilarPack = null);

public sealed class SkinExtrasExtractionService
{
    private const int MaxEntries = 4096;
    private const long MaxUncompressedBytes = 512L * 1024 * 1024;

    public SkinExtractionSource ReadFolder(string directory)
    {
        var root = Path.GetFullPath(directory);
        var files = Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .Where(info => IsSkinFile(info.Name))
            .Take(MaxEntries + 1)
            .ToArray();
        if (files.Length > MaxEntries) throw new InvalidDataException($"The skin has more than {MaxEntries} files.");
        if (files.Sum(file => file.Length) > MaxUncompressedBytes)
            throw new InvalidDataException("The skin is larger than the 512 MB extraction limit.");
        var sourceFiles = files.Select(info => new SkinExtractionFile(
            Path.GetRelativePath(root, info.FullName).Replace('\\', '/'),
            File.ReadAllBytes(info.FullName))).ToArray();
        return BuildSource(new DirectoryInfo(root).Name, root, sourceFiles);
    }

    public SkinExtractionSource ReadOsk(string oskPath)
    {
        using var archive = ZipFile.OpenRead(oskPath);
        if (archive.Entries.Count > MaxEntries)
            throw new InvalidDataException($"The archive has more than {MaxEntries} entries.");
        if (archive.Entries.Sum(entry => entry.Length) > MaxUncompressedBytes)
            throw new InvalidDataException("The archive expands beyond the 512 MB extraction limit.");
        var candidates = archive.Entries
            .Where(entry => !string.IsNullOrEmpty(entry.Name))
            .Select(entry => (Entry: entry, Name: entry.FullName.Replace('\\', '/')))
            .Where(item => !Path.IsPathRooted(item.Name)
                           && !item.Name.Split('/').Any(part => part == "..")
                           && IsSkinFile(item.Name))
            .ToArray();
        var skinIni = candidates
            .Where(item => Path.GetFileName(item.Name)
                .Equals("skin.ini", StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.Name.Count(character => character == '/'))
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        var skinRoot = skinIni.Entry is null
            ? ""
            : skinIni.Name[..Math.Max(0, skinIni.Name.LastIndexOf('/') + 1)];

        var files = new List<SkinExtractionFile>();
        foreach (var (entry, normalized) in candidates.Where(item =>
                     IsDirectChild(item.Name, skinRoot)))
        {
            using var input = entry.Open();
            using var memory = new MemoryStream((int)Math.Min(entry.Length, int.MaxValue));
            input.CopyTo(memory);
            files.Add(new SkinExtractionFile(
                normalized[skinRoot.Length..],
                memory.ToArray()));
        }
        return BuildSource(Path.GetFileNameWithoutExtension(oskPath), oskPath, files);
    }

    private static bool IsDirectChild(string filename, string root)
    {
        if (!filename.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false;
        return !filename[root.Length..].Contains('/');
    }

    public SkinExtractionSource BuildSource(
        string fallbackName,
        string sourceLabel,
        IReadOnlyList<SkinExtractionFile> files)
    {
        // Skin Extras deliberately imports only the skin root. osu!lazer skins
        // can retain archival folders (for example "Stable files" and
        // "Extras") in their Realm file list, so this boundary must enforce
        // the same rule as folder and .osk imports.
        var rootFiles = files.Where(file => IsRootSourceFile(file.Filename)).ToArray();
        var ini = rootFiles.FirstOrDefault(file =>
            Path.GetFileName(file.Filename).Equals("skin.ini", StringComparison.OrdinalIgnoreCase));
        SkinIniDocument? document = null;
        if (ini is not null)
        {
            try { document = SkinIniDocument.Parse(ini.Bytes); }
            catch { }
        }
        return new SkinExtractionSource
        {
            DisplayName = document?.GetValue("General", "Name") ?? fallbackName,
            Author = document?.GetValue("General", "Author"),
            SourceLabel = sourceLabel,
            Files = rootFiles,
        };
    }

    public static bool IsRootSourceFile(string filename)
    {
        if (string.IsNullOrWhiteSpace(filename) || Path.IsPathRooted(filename))
            return false;
        var normalized = filename.Replace('\\', '/').Trim();
        return normalized.Length > 0
               && !normalized.Contains('/')
               && normalized is not "." and not "..";
    }

    public IReadOnlyList<SkinExtractionFamily> Analyze(SkinExtractionSource source)
    {
        var iniFile = source.Files.FirstOrDefault(file =>
            Path.GetFileName(file.Filename).Equals("skin.ini", StringComparison.OrdinalIgnoreCase));
        SkinIniDocument? ini = null;
        if (iniFile is not null)
        {
            try { ini = SkinIniDocument.Parse(iniFile.Bytes); }
            catch { }
        }

        var assetFiles = ResolveTargetCollisions(source.Files.Where(file =>
                SkinMediaTypes.IsImage(file.Filename)
                || SkinMediaTypes.IsAudio(file.Filename))
            .Where(file => !SkinCursorMiddlePolicy.IsCursorMiddle(file.Filename)));
        var result = new List<SkinExtractionFamily>();
        foreach (var family in SkinExtraFamilyRegistry.All.Where(family =>
                     family.Id != "osu.combo-colours"
                     && family.Id != "osu.slider-colours"
                     && family.Id != "osu.number-font"
                     && family.Id != "audio.other"
                     && family.Id != "misc.other"
                     && family.Area != "Mania"))
        {
            var matches = assetFiles.Where(file =>
                    SkinExtraFamilyRegistry.ForFile(file.Filename)?.Id == family.Id)
                .ToArray();
            if (matches.Length == 0) continue;
            result.Add(new SkinExtractionFamily
            {
                Definition = family,
                Files = matches,
                IniPatch = ReadPatch(ini, family.IniKeys),
            });
        }

        foreach (var definition in new[]
                 {
                     SkinExtraFamilyRegistry.ById("osu.combo-colours")!,
                     SkinExtraFamilyRegistry.ById("osu.slider-colours")!,
                 })
        {
            var patch = ReadPatch(ini, definition.IniKeys);
            if (patch.Count == 0) continue;
            result.Add(new SkinExtractionFamily
            {
                Definition = definition,
                Files = [],
                IniPatch = patch,
            });
        }

        AddNumberFonts(result, assetFiles, ini);
        AddManiaFamilies(result, assetFiles, ini);
        AddUnclassified(result, assetFiles);
        return result.OrderBy(family => family.Definition.Area)
            .ThenBy(family => family.DisplayName)
            .ToArray();
    }

    internal static IReadOnlyList<SkinExtractionFile> ResolveTargetCollisions(
        IEnumerable<SkinExtractionFile> files)
    {
        return files
            .Select((file, index) => (File: file, Index: index))
            .GroupBy(
                item => Path.GetFileName(item.File.Filename),
                StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                // Extras packs are flat. BuildSource has already rejected every
                // nested source; this remains as a defensive target de-duplicator.
                var selected = group
                    .OrderBy(item => SourcePathDepth(item.File.Filename))
                    .ThenBy(item => item.Index)
                    .First();
                return (FirstIndex: group.Min(item => item.Index), selected.File);
            })
            .OrderBy(item => item.FirstIndex)
            .Select(item => item.File)
            .ToArray();
    }

    private static int SourcePathDepth(string filename) =>
        filename.Replace('\\', '/').Count(character => character == '/');

    public IReadOnlyList<SkinExtraExtractionResult> Extract(
        SkinExtractionSource source,
        IEnumerable<SkinExtractionFamily> selectedFamilies,
        string extrasRoot,
        string? packNameOverride = null,
        bool lazerUsedOnly = false)
    {
        lock (SkinExtrasMutationGate.SyncRoot)
            return ExtractCore(
                source,
                selectedFamilies,
                extrasRoot,
                packNameOverride,
                lazerUsedOnly);
    }

    private IReadOnlyList<SkinExtraExtractionResult> ExtractCore(
        SkinExtractionSource source,
        IEnumerable<SkinExtractionFamily> selectedFamilies,
        string extrasRoot,
        string? packNameOverride,
        bool lazerUsedOnly)
    {
        Directory.CreateDirectory(extrasRoot);
        var index = SkinExtraPackIndex.Scan(extrasRoot).ToList();
        var results = new List<SkinExtraExtractionResult>();
        foreach (var family in selectedFamilies)
        {
            var candidateFiles = ResolveTargetCollisions(family.Files
                .Where(file => !SkinCursorMiddlePolicy.IsCursorMiddle(file.Filename))
                .Where(file => !lazerUsedOnly
                               || SkinExtraLazerCompatibility.IsLazerUsed(
                                   file.Filename,
                                   family.Definition.Id))
                .ToArray());
            var iniPatch = family.IniPatch
                .Where(entry => !lazerUsedOnly
                                || SkinExtraLazerCompatibility.IsIniPatchUsed(
                                    family.Definition.Id,
                                    entry))
                .ToArray();
            if (candidateFiles.Count == 0 && iniPatch.Length == 0)
                continue;
            var candidateDescriptions = candidateFiles.Select(file =>
                SkinExtraFingerprint.Describe(file.Filename, Path.GetFileName(file.Filename), file.Bytes))
                .ToList();
            var filesToExtract = candidateFiles;
            var described = candidateDescriptions;
            var omittedAudioDuplicates = 0;
            if (IsDeltaSafeAudioFamily(family.Definition.Id))
            {
                var existingFiles = index
                    .Where(pack => pack.Manifest.FamilyId.Equals(
                        family.Definition.Id,
                        StringComparison.OrdinalIgnoreCase))
                    .SelectMany(pack => pack.Manifest.Files)
                    .ToArray();
                var unique = candidateFiles.Zip(candidateDescriptions)
                    .Where(pair => !existingFiles.Any(existing =>
                        SkinExtraFingerprint.EquivalentTargetFilename(
                            existing.TargetFilename,
                            pair.Second.TargetFilename)
                        && SkinExtraFingerprint.EquivalentFileContent(existing, pair.Second)))
                    .ToArray();
                omittedAudioDuplicates = candidateFiles.Count - unique.Length;
                filesToExtract = unique.Select(pair => pair.First).ToList();
                described = unique.Select(pair => pair.Second).ToList();
            }
            if (filesToExtract.Count == 0 && iniPatch.Length == 0)
            {
                var existingPack = index.FirstOrDefault(pack =>
                    pack.Manifest.FamilyId.Equals(
                        family.Definition.Id,
                        StringComparison.OrdinalIgnoreCase)
                    && candidateDescriptions.All(description => pack.Manifest.Files.Any(existing =>
                        SkinExtraFingerprint.EquivalentTargetFilename(
                            existing.TargetFilename,
                            description.TargetFilename)
                        && SkinExtraFingerprint.EquivalentFileContent(existing, description))));
                results.Add(new SkinExtraExtractionResult(
                    SkinExtraExtractionStatus.ExactDuplicateSkipped,
                    family.DisplayName,
                    existingPack?.DirectoryPath,
                    existingPack is null
                        ? $"All {omittedAudioDuplicates} audio asset(s) already exist in this family."
                        : $"Already exists as {existingPack.Manifest.DisplayName}."));
                continue;
            }
            var fingerprint = SkinExtraFingerprint.ForPack(
                family.Definition.Id + (family.Variant is null ? "" : $":{family.Variant}"),
                described,
                iniPatch);
            var duplicate = index.FirstOrDefault(pack =>
                pack.Manifest.FamilyId.Equals(family.Definition.Id, StringComparison.OrdinalIgnoreCase)
                && ExtractionDuplicateScopeMatches(pack.Manifest, family, source)
                && (pack.Manifest.Fingerprint.Equals(fingerprint, StringComparison.OrdinalIgnoreCase)
                    || SkinExtraFingerprint.EquivalentPackContent(
                        pack.Manifest.Files,
                        pack.Manifest.IniPatch,
                        described,
                        iniPatch)
                    || EquivalentTransparentPlaceholderPack(
                        pack,
                        filesToExtract,
                        iniPatch)));
            if (duplicate is not null)
            {
                results.Add(new SkinExtraExtractionResult(
                    SkinExtraExtractionStatus.ExactDuplicateSkipped,
                    family.DisplayName,
                    duplicate.DirectoryPath,
                    $"Already exists as {duplicate.Manifest.DisplayName}."));
                continue;
            }
            var metadataRefresh = index.FirstOrDefault(pack =>
                family.Definition.Id.Equals(
                    "osu.number-font",
                    StringComparison.OrdinalIgnoreCase)
                && pack.Manifest.FamilyId.Equals(
                    family.Definition.Id,
                    StringComparison.OrdinalIgnoreCase)
                && StringComparer.OrdinalIgnoreCase.Equals(
                    pack.Manifest.Variant ?? "",
                    family.Variant ?? "")
                && SameExtractionSource(pack.Manifest, source)
                && EquivalentFiles(pack.Manifest.Files, described));
            if (metadataRefresh is not null)
            {
                var refreshed = new SkinExtraPackManifest
                {
                    SchemaVersion = SkinExtraPackManifest.CurrentSchemaVersion,
                    Id = metadataRefresh.Manifest.Id,
                    DisplayName = metadataRefresh.Manifest.DisplayName,
                    FamilyId = metadataRefresh.Manifest.FamilyId,
                    Area = metadataRefresh.Manifest.Area,
                    FamilyName = metadataRefresh.Manifest.FamilyName,
                    Variant = metadataRefresh.Manifest.Variant,
                    SourceSkin = source.DisplayName,
                    SourceAuthor = source.Author,
                    Fingerprint = fingerprint,
                    ExtractedAt = metadataRefresh.Manifest.ExtractedAt,
                    Files = described,
                    IniPatch = iniPatch.ToList(),
                    FontRoles = family.FontRoles.ToList(),
                };
                var manifestPath = Path.Combine(
                    metadataRefresh.DirectoryPath,
                    "extras.json");
                var temporaryManifest = manifestPath
                                        + $".refresh-{Guid.NewGuid():N}";
                try
                {
                    File.WriteAllBytes(
                        temporaryManifest,
                        SkinExtraManifestSerializer.Serialize(refreshed));
                    File.Move(temporaryManifest, manifestPath, overwrite: true);
                }
                finally
                {
                    if (File.Exists(temporaryManifest))
                        File.Delete(temporaryManifest);
                }
                SkinExtrasPersistentIndex.Invalidate(extrasRoot);
                var descriptor = new SkinExtraPackDescriptor(
                    metadataRefresh.DirectoryPath,
                    refreshed,
                    false);
                index[index.IndexOf(metadataRefresh)] = descriptor;
                results.Add(new SkinExtraExtractionResult(
                    SkinExtraExtractionStatus.Extracted,
                    family.DisplayName,
                    metadataRefresh.DirectoryPath,
                    "Updated this existing number font to match the source skin's effective roles and settings."));
                continue;
            }
            var similar = index.FirstOrDefault(pack =>
                pack.Manifest.FamilyId.Equals(family.Definition.Id, StringComparison.OrdinalIgnoreCase)
                && LooksVisuallySimilar(described, pack.Manifest.Files));

            var baseName = string.IsNullOrWhiteSpace(packNameOverride)
                ? SkinExtraNaming.PackName(source.DisplayName, source.Author)
                : SkinExtraNaming.Sanitize(packNameOverride);
            baseName = SkinExtraNaming.PackNameForFamily(
                baseName,
                family.Definition.Id,
                iniPatch);
            var parent = SkinExtraNaming.StorageParent(
                extrasRoot,
                family.Definition.Area,
                family.Definition.Name);
            if (!string.IsNullOrWhiteSpace(family.Variant))
                parent = Path.Combine(parent, SkinExtraNaming.Sanitize(family.Variant));
            Directory.CreateDirectory(parent);
            var directory = Path.Combine(parent, baseName);
            if (Directory.Exists(directory))
                directory += "~" + fingerprint[..8];

            var staging = directory + $".extract-{Guid.NewGuid():N}";
            Directory.CreateDirectory(staging);
            try
            {
                foreach (var (file, description) in filesToExtract.Zip(described))
                    SkinExtraObjectStore.Materialize(
                        extrasRoot,
                        Path.Combine(staging, description.TargetFilename),
                        file.Bytes,
                        description.ByteHash);
                var manifest = new SkinExtraPackManifest
                {
                    Id = fingerprint[..16],
                    DisplayName = baseName,
                    FamilyId = family.Definition.Id,
                    Area = family.Definition.Area,
                    FamilyName = family.Definition.Name,
                    Variant = family.Variant,
                    SourceSkin = source.DisplayName,
                    SourceAuthor = source.Author,
                    Fingerprint = fingerprint,
                    Files = described,
                    IniPatch = iniPatch.ToList(),
                    FontRoles = family.FontRoles.ToList(),
                };
                File.WriteAllBytes(
                    Path.Combine(staging, "extras.json"),
                    SkinExtraManifestSerializer.Serialize(manifest));
                Directory.Move(staging, directory);
                SkinExtrasPersistentIndex.Invalidate(extrasRoot);
                var descriptor = new SkinExtraPackDescriptor(directory, manifest, false);
                index.Add(descriptor);
                results.Add(new SkinExtraExtractionResult(
                    SkinExtraExtractionStatus.Extracted,
                    family.DisplayName,
                    directory,
                    BuildExtractionMessage(described.Count, omittedAudioDuplicates, similar),
                    similar?.Manifest.DisplayName));
            }
            catch
            {
                if (Directory.Exists(staging)) Directory.Delete(staging, true);
                throw;
            }
        }
        return results;
    }

    private static bool SameExtractionSource(
        SkinExtraPackManifest existing,
        SkinExtractionSource source)
    {
        var existingSkin = string.IsNullOrWhiteSpace(existing.SourceSkin)
            ? existing.DisplayName
            : existing.SourceSkin;
        return existingSkin.Equals(
                   source.DisplayName,
                   StringComparison.OrdinalIgnoreCase)
               && StringComparer.OrdinalIgnoreCase.Equals(
                   existing.SourceAuthor ?? "",
                   source.Author ?? "");
    }

    private static bool EquivalentFiles(
        IReadOnlyCollection<SkinExtraManifestFile> left,
        IReadOnlyCollection<SkinExtraManifestFile> right)
    {
        if (left.Count != right.Count)
            return false;
        var unmatched = right.ToList();
        foreach (var file in left)
        {
            var match = unmatched.FindIndex(candidate =>
                SkinExtraFingerprint.EquivalentTargetFilename(
                    candidate.TargetFilename,
                    file.TargetFilename)
                && SkinExtraFingerprint.EquivalentFileContent(candidate, file));
            if (match < 0)
                return false;
            unmatched.RemoveAt(match);
        }
        return true;
    }

    private static bool ExtractionDuplicateScopeMatches(
        SkinExtraPackManifest existing,
        SkinExtractionFamily incoming,
        SkinExtractionSource source)
    {
        if (!incoming.Definition.Id.Equals(
                "osu.number-font",
                StringComparison.OrdinalIgnoreCase))
            return true;

        return SameExtractionSource(existing, source)
               && existing.FontRoles.ToHashSet(StringComparer.OrdinalIgnoreCase)
                   .SetEquals(incoming.FontRoles);
    }

    private static string BuildExtractionMessage(
        int extractedCount,
        int omittedAudioDuplicates,
        SkinExtraPackDescriptor? similar)
    {
        var message = $"Extracted {extractedCount} asset(s).";
        if (omittedAudioDuplicates > 0)
            message += $" Omitted {omittedAudioDuplicates} identical audio asset(s) already in Extras.";
        if (similar is not null)
            message += $" It looks similar to {similar.Manifest.DisplayName}.";
        return message;
    }

    private static bool IsDeltaSafeAudioFamily(string familyId) =>
        familyId.StartsWith("audio.", StringComparison.OrdinalIgnoreCase)
        && !familyId.StartsWith("audio.hitsounds.", StringComparison.OrdinalIgnoreCase);

    private static bool EquivalentTransparentPlaceholderPack(
        SkinExtraPackDescriptor existing,
        IReadOnlyList<SkinExtractionFile> incomingFiles,
        IReadOnlyList<SkinExtraIniPatchEntry> incomingPatch)
    {
        if (incomingFiles.Count == 0
            || existing.Manifest.Files.Count != incomingFiles.Count
            || !SkinExtraFingerprint.EquivalentPackContent(
                [],
                existing.Manifest.IniPatch,
                [],
                incomingPatch))
            return false;
        try
        {
            return incomingFiles.All(file =>
            {
                var manifestFile = existing.Manifest.Files.FirstOrDefault(candidate =>
                    SkinExtraFingerprint.EquivalentTargetFilename(
                        candidate.TargetFilename,
                        file.Filename));
                if (manifestFile is null
                    || !SkinMediaTypes.IsImage(file.Filename)
                    || !SkinImageAnalysis.IsFullyTransparent(file.Bytes))
                    return false;
                var existingPath = Path.Combine(
                    existing.DirectoryPath,
                    manifestFile.TargetFilename.Replace('/', Path.DirectorySeparatorChar));
                return File.Exists(existingPath)
                       && SkinImageAnalysis.IsFullyTransparent(
                           File.ReadAllBytes(existingPath));
            });
        }
        catch
        {
            return false;
        }
    }

    private static bool LooksVisuallySimilar(
        IReadOnlyList<SkinExtraManifestFile> left,
        IReadOnlyList<SkinExtraManifestFile> right)
    {
        var comparable = left.Where(file => file.SimilarityHash is not null)
            .Join(
                right.Where(file => file.SimilarityHash is not null),
                file => file.LogicalSlot,
                file => file.LogicalSlot,
                (a, b) => (a.SimilarityHash!, b.SimilarityHash!),
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (comparable.Length == 0) return false;
        var close = comparable.Count(pair =>
        {
            if (!ulong.TryParse(pair.Item1, System.Globalization.NumberStyles.HexNumber, null, out var a)
                || !ulong.TryParse(pair.Item2, System.Globalization.NumberStyles.HexNumber, null, out var b))
                return false;
            return System.Numerics.BitOperations.PopCount(a ^ b) <= 6;
        });
        return close >= Math.Ceiling(comparable.Length * 0.7);
    }

    private static void AddNumberFonts(
        ICollection<SkinExtractionFamily> result,
        IReadOnlyList<SkinExtractionFile> files,
        SkinIniDocument? ini)
    {
        var roles = new[]
        {
            (Role: "Hitcircle", PrefixKey: "HitCirclePrefix", OverlapKey: "HitCircleOverlap", Default: "default"),
            (Role: "Score", PrefixKey: "ScorePrefix", OverlapKey: "ScoreOverlap", Default: "score"),
            (Role: "Combo", PrefixKey: "ComboPrefix", OverlapKey: "ComboOverlap", Default: "score"),
        };
        foreach (var group in roles.GroupBy(role =>
                     ini?.GetValue("Fonts", role.PrefixKey) ?? role.Default,
                     StringComparer.OrdinalIgnoreCase))
        {
            var prefix = group.Key.Replace('\\', '/').TrimEnd('-');
            var matching = files.Where(file =>
            {
                var stem = Path.GetFileNameWithoutExtension(file.Filename);
                if (stem.EndsWith("@2x", StringComparison.OrdinalIgnoreCase)) stem = stem[..^3];
                var leafPrefix = Path.GetFileName(prefix);
                return stem.StartsWith(leafPrefix + "-", StringComparison.OrdinalIgnoreCase)
                       && (stem[(leafPrefix.Length + 1)..].All(char.IsDigit)
                           || stem[(leafPrefix.Length + 1)..] is "x" or "X"
                               or "comma" or "dot" or "percent");
            }).ToArray();
            if (!Enumerable.Range(0, 10).All(digit => matching.Any(file =>
                {
                    var stem = Path.GetFileNameWithoutExtension(file.Filename);
                    if (stem.EndsWith("@2x", StringComparison.OrdinalIgnoreCase))
                        stem = stem[..^3];
                    return stem.Equals(
                        Path.GetFileName(prefix) + "-" + digit,
                        StringComparison.OrdinalIgnoreCase);
                })))
                continue;
            var patch = new List<SkinExtraIniPatchEntry>();
            foreach (var role in group)
            {
                patch.Add(new SkinExtraIniPatchEntry("Fonts", role.PrefixKey, prefix));
                patch.Add(new SkinExtraIniPatchEntry(
                    "Fonts",
                    role.OverlapKey,
                    ini?.GetValue("Fonts", role.OverlapKey)
                    ?? (role.Role == "Hitcircle" ? "-2" : "0")));
            }
            result.Add(new SkinExtractionFamily
            {
                Definition = SkinExtraFamilyRegistry.ById("osu.number-font")!,
                Variant = Path.GetFileName(prefix),
                Files = matching,
                IniPatch = patch,
                FontRoles = group.Select(role => role.Role).ToArray(),
            });
        }
    }

    private static void AddUnclassified(
        ICollection<SkinExtractionFamily> result,
        IReadOnlyList<SkinExtractionFile> files)
    {
        var unknown = files.Where(file =>
            SkinExtraFamilyRegistry.ForFile(file.Filename) is null
            && !LooksLikeNumberAsset(file.Filename)
            && !LooksLikeInvalidKnownAsset(file.Filename)).ToArray();
        Add("audio.other", unknown.Where(file => SkinMediaTypes.IsAudio(file.Filename)).ToArray());
        Add("misc.other", unknown.Where(file => SkinMediaTypes.IsImage(file.Filename)).ToArray());

        void Add(string id, IReadOnlyList<SkinExtractionFile> matches)
        {
            if (matches.Count == 0) return;
            result.Add(new SkinExtractionFamily
            {
                Definition = SkinExtraFamilyRegistry.ById(id)!,
                Files = matches,
                IniPatch = [],
            });
        }
    }

    private static bool LooksLikeNumberAsset(string filename)
    {
        var stem = Path.GetFileNameWithoutExtension(filename);
        if (stem.EndsWith("@2x", StringComparison.OrdinalIgnoreCase))
            stem = stem[..^3];
        var dash = stem.LastIndexOf('-');
        if (dash < 0) return false;
        var suffix = stem[(dash + 1)..];
        return suffix.All(char.IsDigit) || suffix is "x" or "X" or "comma" or "dot" or "percent";
    }

    private static bool LooksLikeInvalidKnownAsset(string filename)
    {
        var stem = SkinExtraFamilyRegistry.NormalizedStem(filename);
        return SkinExtraFamilyRegistry.All
            .SelectMany(family => family.ExactNames)
            .Any(name => stem.StartsWith(name, StringComparison.OrdinalIgnoreCase)
                         && !stem.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    private static void AddManiaFamilies(
        ICollection<SkinExtractionFamily> result,
        IReadOnlyList<SkinExtractionFile> files,
        SkinIniDocument? ini)
    {
        if (ini is null) return;
        foreach (var section in ini.GetSections("Mania").Where(section => section.ManiaKeys is not null))
        {
            var keys = section.ManiaKeys!.Value;
            Add("mania.stage", key => IsStageSetting(key));
            Add("mania.keys", key => key.StartsWith("KeyImage", StringComparison.OrdinalIgnoreCase));
            Add("mania.notes", key => key.StartsWith("NoteImage", StringComparison.OrdinalIgnoreCase)
                                      && !IsHoldSetting(key));
            Add("mania.holds", IsHoldSetting);
            Add("mania.lighting", key => key.StartsWith("Lighting", StringComparison.OrdinalIgnoreCase));
            Add("mania.hitbursts", key => key.StartsWith("Hit", StringComparison.OrdinalIgnoreCase)
                                          && !key.Equals("HitPosition", StringComparison.OrdinalIgnoreCase));

            void Add(string familyId, Func<string, bool> ownsSetting)
            {
                var definition = SkinExtraFamilyRegistry.ById(familyId)!;
                var settings = section.Values.Where(entry =>
                        ownsSetting(entry.Key)
                        || (familyId == "mania.stage"
                            && entry.Key.Equals("Keys", StringComparison.OrdinalIgnoreCase)))
                    .Select(entry => new SkinExtraIniPatchEntry(
                        "Mania",
                        entry.Key,
                        entry.Value,
                        keys))
                    .ToArray();
                var references = section.Values.Where(entry => ownsSetting(entry.Key))
                    .Select(entry => entry.Value)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .ToArray();
                var matching = files.Where(file =>
                        references.Any(reference => MatchesReference(file.Filename, reference))
                        || (familyId == "mania.stage"
                            && SkinExtraFamilyRegistry.ForFile(file.Filename)?.Id == familyId))
                    .DistinctBy(file => file.Filename, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (matching.Length == 0 && settings.Length == 0) return;
                result.Add(new SkinExtractionFamily
                {
                    Definition = definition,
                    Variant = $"{keys}K",
                    Files = matching,
                    IniPatch = settings,
                });
            }
        }

        var combo = SkinExtraFamilyRegistry.ById("mania.comboburst")!;
        var comboFiles = files.Where(file =>
            SkinExtraFamilyRegistry.ForFile(file.Filename)?.Id == combo.Id).ToArray();
        if (comboFiles.Length > 0)
            result.Add(new SkinExtractionFamily
            {
                Definition = combo,
                Files = comboFiles,
                IniPatch = [],
            });
    }

    private static bool MatchesReference(string filename, string reference)
    {
        var normalizedReference = reference.Trim().Trim('"').Replace('\\', '/');
        var referenceStem = Path.GetFileNameWithoutExtension(normalizedReference);
        var stem = Path.GetFileNameWithoutExtension(filename);
        if (stem.EndsWith("@2x", StringComparison.OrdinalIgnoreCase))
            stem = stem[..^3];
        if (stem.Equals(referenceStem, StringComparison.OrdinalIgnoreCase))
            return true;
        return stem.StartsWith(referenceStem + "-", StringComparison.OrdinalIgnoreCase)
               && int.TryParse(stem[(referenceStem.Length + 1)..], out _);
    }

    private static bool IsHoldSetting(string key) =>
        key.Contains("Hold", StringComparison.OrdinalIgnoreCase)
        || (key.StartsWith("NoteImage", StringComparison.OrdinalIgnoreCase)
            && (key.EndsWith('H') || key.EndsWith('L') || key.EndsWith('T')));

    private static bool IsStageSetting(string key) =>
        key.StartsWith("Stage", StringComparison.OrdinalIgnoreCase)
        || key.StartsWith("Column", StringComparison.OrdinalIgnoreCase)
        || key is "Keys" or "BarlineHeight" or "HitPosition" or "LightPosition"
            or "ScorePosition" or "ComboPosition" or "JudgementLine"
            or "SpecialStyle" or "UpsideDown" or "SplitStages";

    private static IReadOnlyList<SkinExtraIniPatchEntry> ReadPatch(
        SkinIniDocument? ini,
        IEnumerable<SkinExtraIniKey> keys)
    {
        if (ini is null) return [];
        var result = new List<SkinExtraIniPatchEntry>();
        foreach (var key in keys)
        {
            if (key.Section.Equals("Mania", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var section in ini.GetSections("Mania"))
                {
                    var value = section.Values.GetValueOrDefault(key.Key);
                    if (value is not null)
                        result.Add(new SkinExtraIniPatchEntry("Mania", key.Key, value, section.ManiaKeys));
                }
            }
            else
            {
                var value = ini.GetValue(key.Section, key.Key);
                if (value is not null)
                    result.Add(new SkinExtraIniPatchEntry(key.Section, key.Key, value));
            }
        }
        return result;
    }

    private static bool IsSkinFile(string filename) =>
        Path.GetFileName(filename).Equals("skin.ini", StringComparison.OrdinalIgnoreCase)
        || SkinMediaTypes.IsImage(filename)
        || SkinMediaTypes.IsAudio(filename);
}
