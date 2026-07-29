using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Kumori.App.Skins;

public static class SkinExtrasPersistentIndex
{
    private const int SchemaVersion = 5;
    private static readonly object gate = new();
    private static readonly JsonSerializerOptions json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static IReadOnlyList<SkinExtraPackDescriptor> Scan(string extrasRoot)
    {
        if (!Directory.Exists(extrasRoot)) return [];
        lock (gate)
        {
            var cache = ReadCache(extrasRoot);
            var cached = cache.Packs.ToDictionary(
                pack => pack.RelativePath,
                StringComparer.OrdinalIgnoreCase);
            var next = new List<CachedPack>();
            var result = new List<SkinExtraPackDescriptor>();
            foreach (var directory in SkinExtraPackIndex.FindCandidateDirectories(extrasRoot))
            {
                var relative = Path.GetRelativePath(extrasRoot, directory);
                var stamp = Stamp(directory);
                if (cached.TryGetValue(relative, out var existing)
                    && existing.Stamp.Equals(stamp, StringComparison.Ordinal))
                {
                    next.Add(existing);
                    result.Add(new SkinExtraPackDescriptor(
                        directory,
                        existing.Manifest,
                        existing.IsLegacy));
                    continue;
                }

                SkinExtraPackDescriptor? descriptor;
                try { descriptor = SkinExtraPackIndex.TryBuildDescriptor(extrasRoot, directory); }
                catch { continue; }
                if (descriptor is null) continue;
                if (descriptor.Manifest.Files.Any(file =>
                        SkinElementCategorizer.IsAudio(file.TargetFilename))
                    || SkinCursorMiddlePolicy.IsCursorFamily(
                        descriptor.Manifest.FamilyId))
                    descriptor = SkinExtraPackValidator.CanonicalizeDuplicateTargets(
                        descriptor,
                        forceRebuild: true);
                var fresh = new CachedPack
                {
                    RelativePath = relative,
                    Stamp = stamp,
                    Manifest = descriptor.Manifest,
                    IsLegacy = descriptor.IsLegacy,
                };
                next.Add(fresh);
                result.Add(descriptor);
            }
            WriteCache(extrasRoot, new IndexCache
            {
                Schema = SchemaVersion,
                Packs = next,
            });
            return result;
        }
    }

    public static void Invalidate(string extrasRoot)
    {
        var path = IndexPath(extrasRoot);
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static string Stamp(string directory)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                     .Where(path => Path.GetFileName(path).Equals("extras.json", StringComparison.OrdinalIgnoreCase)
                                    || SkinElementCategorizer.IsImage(path)
                                    || SkinElementCategorizer.IsAudio(path))
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var info = new FileInfo(file);
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(
                Path.GetRelativePath(directory, file).Replace('\\', '/').ToLowerInvariant()));
            hash.AppendData(BitConverter.GetBytes(info.Length));
            hash.AppendData(BitConverter.GetBytes(info.LastWriteTimeUtc.Ticks));
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static IndexCache ReadCache(string root)
    {
        try
        {
            var path = IndexPath(root);
            if (!File.Exists(path)) return new IndexCache();
            var value = JsonSerializer.Deserialize<IndexCache>(File.ReadAllBytes(path), json);
            // Schema 5 changed cursor presentation policy, not the cache shape.
            // Reusing schema 4 avoids a full cold rebuild of large libraries;
            // cursor-middle filtering is enforced at every use boundary.
            return value?.Schema is 4 or SchemaVersion
                ? value
                : new IndexCache();
        }
        catch { return new IndexCache(); }
    }

    private static void WriteCache(string root, IndexCache cache)
    {
        var path = IndexPath(root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var pending = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(pending, JsonSerializer.SerializeToUtf8Bytes(cache, json));
            File.Move(pending, path, true);
        }
        finally
        {
            try { File.Delete(pending); } catch { }
        }
    }

    private static string IndexPath(string root) =>
        Path.Combine(root, ".kumori", "index-v1.json");

    private sealed class IndexCache
    {
        public int Schema { get; set; } = SchemaVersion;
        public List<CachedPack> Packs { get; set; } = [];
    }

    private sealed class CachedPack
    {
        public required string RelativePath { get; set; }
        public required string Stamp { get; set; }
        public required SkinExtraPackManifest Manifest { get; set; }
        public bool IsLegacy { get; set; }
    }
}

public static class SkinExtraObjectStore
{
    public static string Materialize(
        string extrasRoot,
        string destination,
        byte[] bytes,
        string? knownHash = null)
    {
        var hash = knownHash ?? Convert.ToHexStringLower(SHA256.HashData(bytes));
        var objectPath = Path.Combine(extrasRoot, ".kumori", "objects", hash[..2], hash);
        Directory.CreateDirectory(Path.GetDirectoryName(objectPath)!);
        if (!File.Exists(objectPath))
        {
            var pending = objectPath + $".{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllBytes(pending, bytes);
                try { File.Move(pending, objectPath, false); }
                catch (IOException) when (File.Exists(objectPath)) { }
            }
            finally
            {
                try { File.Delete(pending); } catch { }
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (File.Exists(destination)) File.Delete(destination);
        if (!TryCreateHardLink(destination, objectPath))
            File.Copy(objectPath, destination, true);
        return objectPath;
    }

    public static (int Objects, long Bytes) GetStatistics(string extrasRoot)
    {
        var root = Path.Combine(extrasRoot, ".kumori", "objects");
        if (!Directory.Exists(root)) return (0, 0);
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => new FileInfo(path)).ToArray();
        return (files.Length, files.Sum(file => file.Length));
    }

    private static bool TryCreateHardLink(string destination, string existing)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try { return CreateHardLink(destination, existing, IntPtr.Zero); }
        catch { return false; }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(
        string newFileName,
        string existingFileName,
        IntPtr securityAttributes);
}

public sealed class SkinExtrasLibraryItemState
{
    public bool Favorite { get; set; }
    public List<string> Tags { get; set; } = [];
    public DateTimeOffset? LastUsedUtc { get; set; }
    public string? DisplayNameOverride { get; set; }
}

public static class SkinExtraPackRenamer
{
    public static SkinExtraPackDescriptor Rename(
        string extrasRoot,
        SkinExtraPackDescriptor pack,
        string requestedName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedName);
        var name = SkinExtraNaming.Sanitize(requestedName);
        var source = Path.GetFullPath(pack.DirectoryPath);
        var parent = Directory.GetParent(source)?.FullName
                     ?? throw new InvalidOperationException("The pack has no parent folder.");
        var destination = Path.Combine(parent, name);
        var moveRequired = !source.Equals(destination, StringComparison.OrdinalIgnoreCase);
        if (moveRequired && Directory.Exists(destination))
            throw new IOException($"A pack named '{name}' already exists in this family.");

        if (pack.IsLegacy)
        {
            if (moveRequired) Directory.Move(source, destination);
            SkinExtrasPersistentIndex.Invalidate(extrasRoot);
            return SkinExtraPackIndex.TryBuildDescriptor(
                       extrasRoot,
                       moveRequired ? destination : source)
                   ?? throw new InvalidDataException("The renamed legacy pack could not be indexed.");
        }

        var updated = CopyWithDisplayName(pack.Manifest, name);
        var finalDirectory = moveRequired ? destination : source;
        var moved = false;
        try
        {
            if (moveRequired)
            {
                Directory.Move(source, destination);
                moved = true;
            }
            WriteManifestAtomically(finalDirectory, updated);
            SkinExtrasPersistentIndex.Invalidate(extrasRoot);
            return new SkinExtraPackDescriptor(finalDirectory, updated, false);
        }
        catch
        {
            if (moved)
            {
                try
                {
                    if (!Directory.Exists(source) && Directory.Exists(destination))
                        Directory.Move(destination, source);
                }
                catch { }
            }
            throw;
        }
    }

    private static SkinExtraPackManifest CopyWithDisplayName(
        SkinExtraPackManifest manifest,
        string displayName) => new()
        {
            SchemaVersion = manifest.SchemaVersion,
            Id = manifest.Id,
            DisplayName = displayName,
            FamilyId = manifest.FamilyId,
            Area = manifest.Area,
            FamilyName = manifest.FamilyName,
            Variant = manifest.Variant,
            SourceSkin = manifest.SourceSkin,
            SourceAuthor = manifest.SourceAuthor,
            Fingerprint = manifest.Fingerprint,
            ExtractedAt = manifest.ExtractedAt,
            Files = manifest.Files.ToList(),
            IniPatch = manifest.IniPatch.ToList(),
            FontRoles = manifest.FontRoles.ToList(),
        };

    private static void WriteManifestAtomically(
        string directory,
        SkinExtraPackManifest manifest)
    {
        var path = Path.Combine(directory, "extras.json");
        var pending = path + $".{Guid.NewGuid():N}.rename";
        try
        {
            File.WriteAllBytes(pending, SkinExtraManifestSerializer.Serialize(manifest));
            File.Move(pending, path, true);
        }
        finally
        {
            try { File.Delete(pending); } catch { }
        }
    }
}

public static class SkinExtraPackDeletion
{
    public static string ResolvePackDirectory(string extrasRoot, string packDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extrasRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(packDirectory);
        var root = Path.GetFullPath(extrasRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var target = Path.GetFullPath(packDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var relative = Path.GetRelativePath(root, target);
        if (relative.Equals(".", StringComparison.Ordinal)
            || Path.IsPathRooted(relative)
            || relative.Equals("..", StringComparison.Ordinal)
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || relative.Split(
                    [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                    StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault()
                ?.Equals(".kumori", StringComparison.OrdinalIgnoreCase) == true)
            throw new InvalidOperationException(
                "Only an Extras pack folder inside the library can be deleted.");
        return target;
    }
}

public static class SkinExtrasLibraryStateStore
{
    private static readonly object gate = new();
    private static readonly JsonSerializerOptions json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static SkinExtrasLibraryItemState Get(string root, string fingerprint)
    {
        lock (gate)
        {
            var state = Read(root);
            return state.Items.TryGetValue(fingerprint, out var item)
                ? Copy(item)
                : new SkinExtrasLibraryItemState();
        }
    }

    public static IReadOnlyDictionary<string, SkinExtrasLibraryItemState> GetAll(string root)
    {
        lock (gate)
        {
            return Read(root).Items.ToDictionary(
                pair => pair.Key,
                pair => Copy(pair.Value),
                StringComparer.OrdinalIgnoreCase);
        }
    }

    public static void Update(
        string root,
        string fingerprint,
        Action<SkinExtrasLibraryItemState> update)
    {
        lock (gate)
        {
            var state = Read(root);
            if (!state.Items.TryGetValue(fingerprint, out var item))
                state.Items[fingerprint] = item = new SkinExtrasLibraryItemState();
            update(item);
            item.Tags = item.Tags.Where(tag => !string.IsNullOrWhiteSpace(tag))
                .Select(tag => tag.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase)
                .ToList();
            Write(root, state);
        }
    }

    public static void Transfer(string root, string sourceFingerprint, string targetFingerprint)
    {
        lock (gate)
        {
            var state = Read(root);
            if (!state.Items.TryGetValue(sourceFingerprint, out var source))
                return;
            state.Items[targetFingerprint] = Copy(source);
            Write(root, state);
        }
    }

    private static LibraryState Read(string root)
    {
        try
        {
            var path = Path.Combine(root, ".kumori", "library-state.json");
            if (!File.Exists(path)) return new LibraryState();
            return JsonSerializer.Deserialize<LibraryState>(File.ReadAllBytes(path), json)
                   ?? new LibraryState();
        }
        catch { return new LibraryState(); }
    }

    private static SkinExtrasLibraryItemState Copy(SkinExtrasLibraryItemState item) =>
        new()
        {
            Favorite = item.Favorite,
            Tags = item.Tags.ToList(),
            LastUsedUtc = item.LastUsedUtc,
            DisplayNameOverride = item.DisplayNameOverride,
        };

    private static void Write(string root, LibraryState state)
    {
        var path = Path.Combine(root, ".kumori", "library-state.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var pending = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(pending, JsonSerializer.SerializeToUtf8Bytes(state, json));
            File.Move(pending, path, true);
        }
        finally { try { File.Delete(pending); } catch { } }
    }

    private sealed class LibraryState
    {
        public Dictionary<string, SkinExtrasLibraryItemState> Items { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
    }
}

public enum SkinExtraHealthSeverity
{
    Information,
    Warning,
    Error,
}

public sealed record SkinExtraHealthIssue(
    SkinExtraHealthSeverity Severity,
    string Code,
    string Message,
    string? Filename = null);

public sealed record SkinExtraHealthReport(
    IReadOnlyList<SkinExtraHealthIssue> Issues)
{
    public bool IsHealthy => Issues.All(issue => issue.Severity != SkinExtraHealthSeverity.Error);
    public int Errors => Issues.Count(issue => issue.Severity == SkinExtraHealthSeverity.Error);
    public int Warnings => Issues.Count(issue => issue.Severity == SkinExtraHealthSeverity.Warning);
}

public static class SkinExtraPackValidator
{
    public static SkinExtraHealthReport Validate(SkinExtraPackDescriptor pack, bool verifyContent = true)
    {
        var issues = new List<SkinExtraHealthIssue>();
        var manifest = pack.Manifest;
        if (manifest.SchemaVersion > SkinExtraPackManifest.CurrentSchemaVersion)
            issues.Add(Error("future-schema", "This pack was created by a newer Kumori version."));
        else if (manifest.SchemaVersion < SkinExtraPackManifest.CurrentSchemaVersion)
            issues.Add(Warn("old-schema", "The manifest can be upgraded to the current format."));
        if (string.IsNullOrWhiteSpace(manifest.FamilyId)
            || SkinExtraFamilyRegistry.ById(manifest.FamilyId) is null)
            issues.Add(Warn("unknown-family", $"Unknown family '{manifest.FamilyId}'."));

        foreach (var duplicate in manifest.Files.GroupBy(
                     file => file.TargetFilename,
                     StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
            issues.Add(Error("duplicate-target", $"Duplicate target filename '{duplicate.Key}'.", duplicate.Key));

        var described = new List<SkinExtraManifestFile>();
        foreach (var file in manifest.Files)
        {
            if (!TryResolvePackPath(pack.DirectoryPath, file.TargetFilename, out var path))
            {
                issues.Add(Error("unsafe-path", "The manifest contains an unsafe path.", file.TargetFilename));
                continue;
            }
            if (!File.Exists(path))
            {
                issues.Add(Error("missing-file", "The asset is missing.", file.TargetFilename));
                continue;
            }
            if (!verifyContent) continue;
            try
            {
                var bytes = File.ReadAllBytes(path);
                var actual = SkinExtraFingerprint.Describe(
                    file.SourceFilename,
                    file.TargetFilename,
                    bytes);
                described.Add(actual);
                if (!actual.ByteHash.Equals(file.ByteHash, StringComparison.OrdinalIgnoreCase))
                    issues.Add(Error("byte-hash", "The file changed after extraction.", file.TargetFilename));
                if (SkinElementCategorizer.IsImage(path))
                    _ = SkinImageTools.Decode(bytes);
                // Empty audio is a valid osu! skin convention for intentionally
                // silencing a sound, so it participates in canonical deduplication.
            }
            catch (Exception ex)
            {
                issues.Add(Error("corrupt-media", $"Could not decode or hash the asset: {ex.Message}", file.TargetFilename));
            }
        }

        if (verifyContent && described.Count == manifest.Files.Count)
        {
            var fingerprint = SkinExtraFingerprint.ForPack(
                manifest.FamilyId + (manifest.Variant is null ? "" : $":{manifest.Variant}"),
                described,
                manifest.IniPatch);
            if (!fingerprint.Equals(manifest.Fingerprint, StringComparison.OrdinalIgnoreCase))
                issues.Add(Error("pack-fingerprint", "The pack fingerprint no longer matches its contents."));
        }

        if (manifest.FamilyId.Equals("osu.number-font", StringComparison.OrdinalIgnoreCase))
        {
            var stems = manifest.Files.Select(file => Path.GetFileNameWithoutExtension(file.TargetFilename))
                .Select(stem => stem.EndsWith("@2x", StringComparison.OrdinalIgnoreCase) ? stem[..^3] : stem)
                .ToArray();
            for (var digit = 0; digit < 10; digit++)
                if (!stems.Any(stem => stem.EndsWith($"-{digit}", StringComparison.OrdinalIgnoreCase)))
                    issues.Add(Error("missing-digit", $"Number font is missing digit {digit}."));
        }

        if (manifest.FamilyId.StartsWith("audio.hitsounds.", StringComparison.OrdinalIgnoreCase))
        {
            var stems = manifest.Files.Select(file =>
                Path.GetFileNameWithoutExtension(file.TargetFilename).ToLowerInvariant()).ToArray();
            foreach (var suffix in new[] { "hitnormal", "hitwhistle", "hitfinish", "hitclap" })
                if (!stems.Any(stem => stem.EndsWith(suffix, StringComparison.Ordinal)))
                    issues.Add(Warn("incomplete-hitsounds", $"Optional hitsound '{suffix}' is missing."));
        }

        return new SkinExtraHealthReport(issues);
    }

    public static SkinExtraPackDescriptor Repair(SkinExtraPackDescriptor pack)
    {
        var repaired = CanonicalizeDuplicateTargets(pack, forceRebuild: true);
        File.WriteAllBytes(
            Path.Combine(pack.DirectoryPath, "extras.json"),
            SkinExtraManifestSerializer.Serialize(repaired.Manifest));
        if (SkinCursorMiddlePolicy.IsCursorFamily(pack.Manifest.FamilyId))
        {
            foreach (var file in pack.Manifest.Files.Where(file =>
                         SkinCursorMiddlePolicy.IsCursorMiddle(
                             file.TargetFilename)))
            {
                if (TryResolvePackPath(
                        pack.DirectoryPath,
                        file.TargetFilename,
                        out var path)
                    && File.Exists(path))
                    File.Delete(path);
            }
        }
        var root = FindExtrasRoot(pack.DirectoryPath);
        if (root is not null) SkinExtrasPersistentIndex.Invalidate(root);
        return repaired;
    }

    internal static SkinExtraPackDescriptor CanonicalizeDuplicateTargets(
        SkinExtraPackDescriptor pack,
        bool forceRebuild = false)
    {
        var hasDuplicateTargets = pack.Manifest.Files
            .GroupBy(file => file.TargetFilename, StringComparer.OrdinalIgnoreCase)
            .Any(group => group.Count() > 1);
        var hasCursorMiddle =
            SkinCursorMiddlePolicy.IsCursorFamily(pack.Manifest.FamilyId)
            && pack.Manifest.Files.Any(file =>
                SkinCursorMiddlePolicy.IsCursorMiddle(file.TargetFilename));
        if (!forceRebuild && !hasDuplicateTargets && !hasCursorMiddle)
            return pack;

        var described = new List<SkinExtraManifestFile>();
        foreach (var file in pack.Manifest.Files
                     .GroupBy(
                         file => file.TargetFilename,
                         StringComparer.OrdinalIgnoreCase)
                     .Select(group => group.Last())
                     .Where(file => !SkinCursorMiddlePolicy.IsCursorFamily(
                                        pack.Manifest.FamilyId)
                                    || !SkinCursorMiddlePolicy.IsCursorMiddle(
                                        file.TargetFilename)))
        {
            if (!TryResolvePackPath(pack.DirectoryPath, file.TargetFilename, out var path)
                || !File.Exists(path))
                continue;
            described.Add(SkinExtraFingerprint.Describe(
                file.SourceFilename,
                file.TargetFilename,
                File.ReadAllBytes(path)));
        }
        var fingerprint = SkinExtraFingerprint.ForPack(
            pack.Manifest.FamilyId + (pack.Manifest.Variant is null ? "" : $":{pack.Manifest.Variant}"),
            described,
            pack.Manifest.IniPatch);
        var upgraded = new SkinExtraPackManifest
        {
            SchemaVersion = SkinExtraPackManifest.CurrentSchemaVersion,
            Id = string.IsNullOrWhiteSpace(pack.Manifest.Id)
                ? fingerprint[..16]
                : pack.Manifest.Id,
            DisplayName = pack.Manifest.DisplayName,
            FamilyId = pack.Manifest.FamilyId,
            Area = pack.Manifest.Area,
            FamilyName = pack.Manifest.FamilyName,
            Variant = pack.Manifest.Variant,
            SourceSkin = pack.Manifest.SourceSkin,
            SourceAuthor = pack.Manifest.SourceAuthor,
            ExtractedAt = pack.Manifest.ExtractedAt,
            Fingerprint = fingerprint,
            Files = described,
            IniPatch = pack.Manifest.IniPatch.ToList(),
            FontRoles = pack.Manifest.FontRoles.ToList(),
        };
        return new SkinExtraPackDescriptor(pack.DirectoryPath, upgraded, false);
    }

    internal static bool TryResolvePackPath(string packDirectory, string relative, out string path)
    {
        path = Path.GetFullPath(Path.Combine(
            packDirectory,
            relative.Replace('/', Path.DirectorySeparatorChar)));
        var check = Path.GetRelativePath(packDirectory, path);
        return !Path.IsPathRooted(check)
               && !check.Equals("..", StringComparison.Ordinal)
               && !check.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static string? FindExtrasRoot(string path)
    {
        var current = new DirectoryInfo(path);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, ".kumori"))
                || current.Name.Equals("Extras", StringComparison.OrdinalIgnoreCase))
                return current.FullName;
            current = current.Parent;
        }
        return null;
    }

    private static SkinExtraHealthIssue Error(string code, string message, string? file = null) =>
        new(SkinExtraHealthSeverity.Error, code, message, file);
    private static SkinExtraHealthIssue Warn(string code, string message, string? file = null) =>
        new(SkinExtraHealthSeverity.Warning, code, message, file);
}

public sealed record SkinExtraPortableImportResult(
    SkinExtraPackDescriptor Pack,
    bool WasDuplicate,
    string Message,
    string SourceFingerprint);

public static class SkinExtraPortablePackage
{
    private static readonly DateTimeOffset DeterministicArchiveTimestamp =
        new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
    internal const int MaxEntries = 4096;
    internal const long MaxExpandedBytes = 512L * 1024 * 1024;
    internal const long MaxCompressedBytes = 256L * 1024 * 1024;
    internal const long MaxSingleEntryBytes = 256L * 1024 * 1024;
    internal const int MaxManifestBytes = 2 * 1024 * 1024;
    private const long CompressionRatioThresholdBytes = 1024 * 1024;
    private const long MaxCompressionRatio = 1000;

    public static void Export(SkinExtraPackDescriptor pack, string destination)
    {
        if (SkinCursorMiddlePolicy.IsCursorFamily(pack.Manifest.FamilyId))
            pack = SkinExtraPackValidator.CanonicalizeDuplicateTargets(
                pack,
                forceRebuild: true);
        using var archive = ZipFile.Open(destination, ZipArchiveMode.Create);
        var manifestEntry = archive.CreateEntry("extras.json", CompressionLevel.Optimal);
        manifestEntry.LastWriteTime = DeterministicArchiveTimestamp;
        using (var stream = manifestEntry.Open())
            stream.Write(SkinExtraManifestSerializer.Serialize(pack.Manifest));
        foreach (var file in pack.Manifest.Files.OrderBy(
                     file => file.TargetFilename,
                     StringComparer.OrdinalIgnoreCase))
        {
            if (!SkinExtraPackValidator.TryResolvePackPath(
                    pack.DirectoryPath,
                    file.TargetFilename,
                    out var path)
                || !File.Exists(path))
                throw new InvalidDataException($"Missing pack asset: {file.TargetFilename}");
            var entry = archive.CreateEntry(
                "assets/" + file.TargetFilename.Replace('\\', '/'),
                CompressionLevel.Optimal);
            entry.LastWriteTime = DeterministicArchiveTimestamp;
            using var output = entry.Open();
            using var input = File.OpenRead(path);
            input.CopyTo(output);
        }
    }

    public static SkinExtraPortableImportResult Import(string packagePath, string extrasRoot)
    {
        lock (SkinExtrasMutationGate.SyncRoot)
            return ImportCore(packagePath, extrasRoot);
    }

    internal static SkinExtraPortableImportResult ImportForCatalog(
        string packagePath,
        string extrasRoot,
        CancellationToken cancellationToken,
        Action<string>? progress)
    {
        lock (SkinExtrasMutationGate.SyncRoot)
            return ImportCore(
                packagePath,
                extrasRoot,
                checkDuplicates: false,
                cancellationToken,
                progress);
    }

    private static SkinExtraPortableImportResult ImportCore(
        string packagePath,
        string extrasRoot,
        bool checkDuplicates = true,
        CancellationToken cancellationToken = default,
        Action<string>? progress = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Invoke("Validating package");
        using var archive = ZipFile.OpenRead(packagePath);
        if (archive.Entries.Count > MaxEntries
            || archive.Entries.Sum(entry => entry.Length) > MaxExpandedBytes)
            throw new InvalidDataException("The Extras package exceeds the safe extraction limits.");

        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            var normalized = NormalizeArchivePath(entry.FullName);
            if (string.IsNullOrWhiteSpace(normalized)
                || normalized.EndsWith("/", StringComparison.Ordinal)
                || !entries.TryAdd(normalized, entry))
                throw new InvalidDataException("The Extras package contains duplicate or invalid entries.");
            if (entry.Length > MaxSingleEntryBytes)
                throw new InvalidDataException($"Package entry '{normalized}' exceeds the per-file limit.");
            if (entry.Length > CompressionRatioThresholdBytes
                && entry.CompressedLength > 0
                && entry.Length / entry.CompressedLength > MaxCompressionRatio)
                throw new InvalidDataException($"Package entry '{normalized}' has an unsafe compression ratio.");
        }

        var manifestEntry = entries.GetValueOrDefault("extras.json")
            ?? throw new InvalidDataException("The package has no extras.json manifest.");
        if (manifestEntry.Length is <= 0 or > MaxManifestBytes)
            throw new InvalidDataException("The Extras manifest exceeds the safe size limit.");

        SkinExtraPackManifest manifest;
        using (var stream = manifestEntry.Open())
        using (var memory = new MemoryStream())
        {
            stream.CopyTo(memory);
            manifest = SkinExtraManifestSerializer.Deserialize(memory.ToArray())
                       ?? throw new InvalidDataException("The Extras manifest is invalid.");
        }
        if (manifest.SchemaVersion > SkinExtraPackManifest.CurrentSchemaVersion)
            throw new InvalidDataException("This package requires a newer Kumori version.");
        if (manifest.Files
            .GroupBy(file => file.TargetFilename, StringComparer.OrdinalIgnoreCase)
            .Any(group => group.Count() > 1))
            throw new InvalidDataException("The package contains duplicate target filenames.");

        var declaredEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "extras.json",
        };
        var packageFiles = new List<SkinExtraManifestFile>();
        foreach (var declared in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var archivePath = NormalizeArchivePath(
                "assets/" + declared.TargetFilename.Replace('\\', '/'));
            if (!declaredEntries.Add(archivePath))
                throw new InvalidDataException(
                    $"The package declares the same asset more than once: {declared.TargetFilename}");
            var entry = entries.GetValueOrDefault(archivePath)
                ?? throw new InvalidDataException($"Package asset is missing: {declared.TargetFilename}");
            using var input = entry.Open();
            using var memory = new MemoryStream();
            input.CopyTo(memory);
            var bytes = memory.ToArray();
            var actual = SkinExtraFingerprint.Describe(
                declared.SourceFilename,
                declared.TargetFilename,
                bytes);
            var verified = !string.IsNullOrWhiteSpace(declared.ByteHash)
                ? actual.ByteHash.Equals(declared.ByteHash, StringComparison.OrdinalIgnoreCase)
                : actual.SemanticHash.Equals(
                    declared.SemanticHash,
                    StringComparison.OrdinalIgnoreCase);
            if (!verified)
                throw new InvalidDataException(
                    $"Package asset failed verification: {declared.TargetFilename}");
            packageFiles.Add(actual);
        }
        var undeclared = entries.Keys.FirstOrDefault(path => !declaredEntries.Contains(path));
        if (undeclared is not null)
            throw new InvalidDataException($"The package contains an undeclared entry: {undeclared}");
        var sourceFingerprint = SkinExtraFingerprint.ForPack(
            manifest.FamilyId + (manifest.Variant is null ? "" : $":{manifest.Variant}"),
            packageFiles,
            manifest.IniPatch);
        if (SkinCursorMiddlePolicy.IsCursorFamily(manifest.FamilyId))
            packageFiles.RemoveAll(file =>
                SkinCursorMiddlePolicy.IsCursorMiddle(file.TargetFilename));
        if (packageFiles.Count == 0 && manifest.IniPatch.Count == 0)
            throw new InvalidDataException(
                "The Extras package has no usable files or settings.");

        var canonicalFingerprint = SkinExtraFingerprint.ForPack(
            manifest.FamilyId + (manifest.Variant is null ? "" : $":{manifest.Variant}"),
            packageFiles,
            manifest.IniPatch);
        manifest = new SkinExtraPackManifest
        {
            SchemaVersion = SkinExtraPackManifest.CurrentSchemaVersion,
            Id = canonicalFingerprint[..16],
            DisplayName = manifest.DisplayName,
            FamilyId = manifest.FamilyId,
            Area = manifest.Area,
            FamilyName = manifest.FamilyName,
            Variant = manifest.Variant,
            SourceSkin = manifest.SourceSkin,
            SourceAuthor = manifest.SourceAuthor,
            Fingerprint = canonicalFingerprint,
            ExtractedAt = manifest.ExtractedAt,
            Files = packageFiles,
            IniPatch = manifest.IniPatch.ToList(),
            FontRoles = manifest.FontRoles.ToList(),
        };
        if (checkDuplicates)
        {
            progress?.Invoke("Checking the library");
            var duplicate = SkinExtraPackIndex.Scan(extrasRoot).FirstOrDefault(pack =>
                pack.Manifest.FamilyId.Equals(manifest.FamilyId, StringComparison.OrdinalIgnoreCase)
                && (pack.Manifest.Fingerprint.Equals(
                        manifest.Fingerprint,
                        StringComparison.OrdinalIgnoreCase)
                    || SkinExtraFingerprint.EquivalentPackContent(
                        pack.Manifest.Files,
                        pack.Manifest.IniPatch,
                        manifest.Files,
                        manifest.IniPatch)));
            if (duplicate is not null)
            {
                return new SkinExtraPortableImportResult(
                    duplicate,
                    true,
                    "An exact copy is already in the library.",
                    sourceFingerprint);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Invoke("Writing files");
        var parent = SkinExtraNaming.StorageParent(
            extrasRoot,
            manifest.Area,
            manifest.FamilyName);
        if (!string.IsNullOrWhiteSpace(manifest.Variant))
            parent = Path.Combine(parent, SkinExtraNaming.Sanitize(manifest.Variant));
        Directory.CreateDirectory(parent);
        var directory = Path.Combine(parent, SkinExtraNaming.Sanitize(manifest.DisplayName));
        if (Directory.Exists(directory)) directory += "~" + manifest.Fingerprint[..8];
        var staging = directory + $".import-{Guid.NewGuid():N}";
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var file in packageFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!SkinExtraPackValidator.TryResolvePackPath(staging, file.TargetFilename, out var destination))
                    throw new InvalidDataException($"Unsafe package asset path: {file.TargetFilename}");
                var archivePath = NormalizeArchivePath(
                    "assets/" + file.TargetFilename.Replace('\\', '/'));
                var entry = entries[archivePath];
                using var input = entry.Open();
                using var memory = new MemoryStream(
                    entry.Length <= int.MaxValue ? (int)entry.Length : 0);
                input.CopyTo(memory);
                var bytes = memory.ToArray();
                SkinExtraObjectStore.Materialize(extrasRoot, destination, bytes, file.ByteHash);
            }
            File.WriteAllBytes(
                Path.Combine(staging, "extras.json"),
                SkinExtraManifestSerializer.Serialize(manifest));
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Invoke("Verifying installed files");
            var stagingDescriptor = new SkinExtraPackDescriptor(staging, manifest, false);
            var report = SkinExtraPackValidator.Validate(stagingDescriptor);
            if (!report.IsHealthy)
                throw new InvalidDataException($"Imported pack has {report.Errors} validation error(s).");
            Directory.Move(staging, directory);
            SkinExtrasPersistentIndex.Invalidate(extrasRoot);
            var descriptor = new SkinExtraPackDescriptor(directory, manifest, false);
            return new SkinExtraPortableImportResult(
                descriptor,
                false,
                "Extras package imported.",
                sourceFingerprint);
        }
        catch
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { }
            throw;
        }
    }

    private static string NormalizeArchivePath(string path)
    {
        var normalized = path.Replace('\\', '/').TrimStart('/');
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0
            || parts.Any(part => part is "." or "..")
            || Path.IsPathRooted(normalized)
            || normalized.Contains(':', StringComparison.Ordinal))
            return "";
        return string.Join('/', parts);
    }
}
