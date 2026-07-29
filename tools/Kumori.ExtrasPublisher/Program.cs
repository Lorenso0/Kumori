#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Media.Imaging;
using Kumori.App.Skins;

namespace Kumori.ExtrasPublisher;

internal static class Program
{
    private const string SigningKeyId = "kumori-extras-2026-01";
    private const int BundleCount = 2;
    private const long MaxCompressedBytes = 256L * 1024 * 1024;
    private const long MaxExpandedBytes = 512L * 1024 * 1024;
    private const int MaxEntries = 4096;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 10
                && args[0].Equals("--reconcile-catalog", StringComparison.OrdinalIgnoreCase))
            {
                var values = ParsePairs(args);
                ReconcilePublishedAssets(
                    Path.GetFullPath(values["reconcile-catalog"]),
                    Path.GetFullPath(values["reconcile-state"]),
                    Path.GetFullPath(values["assets-root"]),
                    values["catalog-version"],
                    Path.GetFullPath(values["output"]));
                Console.WriteLine("Published asset hashes reconciled into a corrective catalog.");
                return 0;
            }
            if (args.Length == 6
                && args[0].Equals("--audit-catalog", StringComparison.OrdinalIgnoreCase))
            {
                var values = ParsePairs(args);
                AuditCatalogPackages(
                    Path.GetFullPath(values["audit-catalog"]),
                    Path.GetFullPath(values["assets-root"]),
                    Path.GetFullPath(values["install-root"]));
                return 0;
            }
            if (args.Length == 4
                && args[0].Equals("--verify-catalog", StringComparison.OrdinalIgnoreCase)
                && args[2].Equals("--verify-signature", StringComparison.OrdinalIgnoreCase))
            {
                VerifyCatalogSignature(
                    Path.GetFullPath(args[1]),
                    Path.GetFullPath(args[3]));
                Console.WriteLine("Catalog ECDSA P-256 signature verified.");
                return 0;
            }
            var options = PublisherOptions.Parse(args);
            Directory.CreateDirectory(options.OutputDirectory);
            var result = Build(options);
            Console.WriteLine(
                $"Catalog {result.Summary.CatalogVersion}: "
                + $"{result.Summary.ActivePackCount} unique active packs, "
                + $"{result.Summary.Additions} additions, "
                + $"{result.Summary.Revisions} revisions, "
                + $"{result.Summary.MetadataChanges} metadata changes, "
                + $"{result.Summary.Withdrawals} withdrawals.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.ToString());
            return 1;
        }
    }

    private static Dictionary<string, string> ParsePairs(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length || !args[index].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException("Arguments must be --name value pairs.");
            values[args[index][2..]] = args[index + 1];
        }
        return values;
    }

    private static void ReconcilePublishedAssets(
        string catalogPath,
        string statePath,
        string assetsRoot,
        string catalogVersion,
        string output)
    {
        var source = JsonSerializer.Deserialize<SkinExtrasRemoteCatalog>(
                         File.ReadAllBytes(catalogPath),
                         Json)
                     ?? throw new InvalidDataException("The source catalog is invalid.");
        var state = PublisherState.Read(statePath);
        var correctedPacks = source.Packs.Select(pack => ClonePack(
            pack,
            ActualAsset(pack.Package, assetsRoot),
            pack.Preview is null ? null : ActualAsset(pack.Preview, assetsRoot))).ToList();
        var byId = correctedPacks.ToDictionary(pack => pack.PackId);
        var correctedState = new PublisherState
        {
            Packs = state.Packs.Select(item => new PublisherPackState
            {
                LocalRelativePath = item.LocalRelativePath,
                WithdrawnAtUtc = item.WithdrawnAtUtc,
                Pack = byId.GetValueOrDefault(item.Pack.PackId) ?? item.Pack,
            }).ToList(),
        };
        var correctedCatalog = new SkinExtrasRemoteCatalog
        {
            CatalogVersion = catalogVersion,
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            SigningKeyId = SigningKeyId,
            Packs = correctedPacks,
            Withdrawals = source.Withdrawals,
        };
        var summary = new PublicationSummary
        {
            Mode = "Correction",
            CatalogVersion = catalogVersion,
            LocalPackCount = correctedPacks.Count,
            ActivePackCount = correctedPacks.Count,
            Changed = true,
            Releases =
            [
                new PublicationRelease
                {
                    Tag = catalogVersion,
                    IsLatestCatalogShard = true,
                },
            ],
        };
        Directory.CreateDirectory(output);
        WriteJson(Path.Combine(output, "catalog-v1.json"), correctedCatalog);
        WriteJson(Path.Combine(output, "catalog-state.json"), correctedState);
        WriteJson(Path.Combine(output, "publication-summary.json"), summary);
    }

    private static SkinExtrasCatalogAsset ActualAsset(
        SkinExtrasCatalogAsset asset,
        string assetsRoot)
    {
        var path = Path.Combine(assetsRoot, asset.ReleaseTag, asset.AssetName);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Published asset is missing: {asset.AssetName}", path);
        using var stream = File.OpenRead(path);
        return new SkinExtrasCatalogAsset
        {
            ReleaseTag = asset.ReleaseTag,
            AssetName = asset.AssetName,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(stream)),
            DownloadBytes = stream.Length,
            EntryName = asset.EntryName,
            EntrySha256 = asset.EntrySha256,
            EntryDownloadBytes = asset.EntryDownloadBytes,
            ExpandedBytes = asset.ExpandedBytes,
            EntryCount = asset.EntryCount,
        };
    }

    private static SkinExtrasCatalogPack ClonePack(
        SkinExtrasCatalogPack pack,
        SkinExtrasCatalogAsset package,
        SkinExtrasCatalogAsset? preview) => new()
    {
        PackId = pack.PackId,
        Revision = pack.Revision,
        ContentFingerprint = pack.ContentFingerprint,
        SupersedesFingerprint = pack.SupersedesFingerprint,
        DisplayName = pack.DisplayName,
        FamilyId = pack.FamilyId,
        Area = pack.Area,
        Variant = pack.Variant,
        SourceSkin = pack.SourceSkin,
        SourceAuthor = pack.SourceAuthor,
        Compatibility = pack.Compatibility,
        MinimumKumoriVersion = pack.MinimumKumoriVersion,
        Package = package,
        Preview = preview,
    };

    private static void AuditCatalogPackages(
        string catalogPath,
        string assetsRoot,
        string installRoot)
    {
        var catalog = JsonSerializer.Deserialize<SkinExtrasRemoteCatalog>(
                          File.ReadAllBytes(catalogPath),
                          Json)
                      ?? throw new InvalidDataException("The audit catalog is invalid.");
        Directory.CreateDirectory(installRoot);
        var installed = 0;
        foreach (var pack in catalog.Packs)
        {
            var assetPath = Path.Combine(
                assetsRoot,
                pack.Package.ReleaseTag,
                pack.Package.AssetName);
            var packagePath = assetPath;
            string? extracted = null;
            try
            {
                if (!string.IsNullOrWhiteSpace(pack.Package.EntryName))
                {
                    extracted = Path.Combine(
                        Path.GetTempPath(),
                        $"kumori-bundle-audit-{Guid.NewGuid():N}.kextra");
                    ExtractCatalogEntry(pack.Package, assetPath, extracted);
                    packagePath = extracted;
                }
                var imported = SkinExtraPortablePackage.Import(packagePath, installRoot);
                if (!imported.Pack.Manifest.Fingerprint.Equals(
                        pack.ContentFingerprint,
                        StringComparison.OrdinalIgnoreCase)
                    || !SkinExtraPackValidator.Validate(imported.Pack).IsHealthy)
                    throw new InvalidDataException(
                        $"Published package failed internal audit: {pack.DisplayName}");
                installed++;
            }
            finally
            {
                if (extracted is not null)
                    try { File.Delete(extracted); } catch { }
            }
        }
        Console.WriteLine(
            $"Audited {installed} complete public packages; "
            + $"{SkinExtraPackIndex.Scan(installRoot).Count} unique packs reconstructed.");
    }

    private static void ExtractCatalogEntry(
        SkinExtrasCatalogAsset asset,
        string bundlePath,
        string destination)
    {
        using var archive = ZipFile.OpenRead(bundlePath);
        var matches = archive.Entries
            .Where(entry => entry.FullName.Equals(
                asset.EntryName,
                StringComparison.Ordinal))
            .ToArray();
        if (matches.Length != 1
            || matches[0].Length != asset.EntryDownloadBytes)
            throw new InvalidDataException(
                $"Bundle entry is missing or invalid: {asset.EntryName}");
        using var input = matches[0].Open();
        using var output = File.Create(destination);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        long total = 0;
        while (true)
        {
            var read = input.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            total += read;
            hash.AppendData(buffer, 0, read);
            output.Write(buffer, 0, read);
        }
        var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
        if (total != asset.EntryDownloadBytes
            || !actual.Equals(asset.EntrySha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"Bundle entry hash is invalid: {asset.EntryName}");
    }

    private static void VerifyCatalogSignature(string catalogPath, string signaturePath)
    {
        const string publicKey =
            "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEB7/jWKNZL6xAVfNyMIHLAVb/V5mBz8nJXTNAV9LIyhapCdJEOxP+icLenEh8icPSjX/PZ8Zsw9IhThUn/sj+Mg==";
        var catalogBytes = File.ReadAllBytes(catalogPath);
        var signature = JsonSerializer.Deserialize<SkinExtrasCatalogSignature>(
                            File.ReadAllBytes(signaturePath),
                            Json)
                        ?? throw new InvalidDataException("The catalog signature file is invalid.");
        if (!signature.KeyId.Equals(SigningKeyId, StringComparison.Ordinal)
            || !signature.Algorithm.Equals(
                SkinExtrasCatalogSignature.AlgorithmName,
                StringComparison.Ordinal))
            throw new InvalidDataException("The catalog uses an unexpected signing identity.");
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
        if (!key.VerifyData(
                catalogBytes,
                Convert.FromBase64String(signature.Signature),
                HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence))
            throw new InvalidDataException("The public catalog signature is invalid.");
    }

    private static PublisherResult Build(PublisherOptions options)
    {
        if (!Directory.Exists(options.ExtrasRoot))
            throw new DirectoryNotFoundException($"Extras root does not exist: {options.ExtrasRoot}");

        var prior = PublisherState.Read(options.StatePath);
        var scanned = SkinExtraPackIndex.Scan(options.ExtrasRoot)
            .OrderBy(pack => Path.GetRelativePath(options.ExtrasRoot, pack.DirectoryPath),
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (scanned.Length == 0)
            throw new InvalidOperationException("The local Extras library contains no valid packs.");

        for (var index = 0; index < scanned.Length; index++)
        {
            var pack = scanned[index];
            var validation = SkinExtraPackValidator.Validate(pack);
            var repairable = validation.Issues.All(issue =>
                issue.Severity != SkinExtraHealthSeverity.Error
                || issue.Code.Equals("pack-fingerprint", StringComparison.Ordinal)
                || issue.Code.Equals("byte-hash", StringComparison.Ordinal)
                || issue.Code.Equals("duplicate-target", StringComparison.Ordinal));
            if (!validation.IsHealthy
                && options.Mode.Equals("Repair", StringComparison.OrdinalIgnoreCase)
                && repairable)
            {
                pack = SkinExtraPackValidator.Repair(pack);
                scanned[index] = pack;
                validation = SkinExtraPackValidator.Validate(pack);
            }
            if (!validation.IsHealthy)
                throw new InvalidDataException(
                    $"Pack '{pack.Manifest.DisplayName}' is unhealthy: "
                    + string.Join("; ", validation.Issues.Select(issue => issue.Message)));
        }
        var local = scanned
            .GroupBy(pack => pack.Manifest.Fingerprint, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderBy(pack => pack.IsLegacy)
                .ThenBy(pack => Path.GetRelativePath(options.ExtrasRoot, pack.DirectoryPath),
                    StringComparer.OrdinalIgnoreCase)
                .First())
            .OrderBy(pack => Path.GetRelativePath(options.ExtrasRoot, pack.DirectoryPath),
                StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var now = DateTimeOffset.UtcNow;
        var republishAll = options.Mode.Equals(
            "Republish",
            StringComparison.OrdinalIgnoreCase);
        var unmatched = prior.Packs.ToHashSet();
        var planned = new List<PlannedPack>();
        var additions = 0;
        var revisions = 0;
        var metadataChanges = 0;

        foreach (var pack in local)
        {
            var relative = NormalizeRelativePath(
                Path.GetRelativePath(options.ExtrasRoot, pack.DirectoryPath));
            var match = Match(pack, relative, unmatched);
            if (match is not null)
                unmatched.Remove(match);

            var contentChanged = match is not null
                                 && !match.Pack.ContentFingerprint.Equals(
                                     pack.Manifest.Fingerprint,
                                     StringComparison.OrdinalIgnoreCase);
            var isNew = match is null;
            var metadataChanged = match is not null
                                  && MetadataChanged(match.Pack, pack.Manifest);
            if (isNew) additions++;
            else if (contentChanged) revisions++;
            else if (metadataChanged || match!.WithdrawnAtUtc is not null) metadataChanges++;

            planned.Add(new PlannedPack(
                pack,
                relative,
                match,
                isNew,
                contentChanged,
                metadataChanged));
        }

        var newlyWithdrawn = unmatched.Count(state => state.WithdrawnAtUtc is null);
        var changedAtAll =
            republishAll
            || additions + revisions + metadataChanges + newlyWithdrawn > 0;
        var publishBundles = changedAtAll;
        var releaseTags = new List<string> { options.ReleaseTag };

        var assetsRoot = Path.Combine(options.OutputDirectory, "assets");
        Directory.CreateDirectory(assetsRoot);

        var nextState = new PublisherState();
        var catalog = new SkinExtrasRemoteCatalog
        {
            CatalogVersion = options.ReleaseTag,
            GeneratedAtUtc = now,
            SigningKeyId = SigningKeyId,
        };
        var releaseAssets = releaseTags.ToDictionary(
            tag => tag,
            tag => new List<string>(),
            StringComparer.Ordinal);

        foreach (var item in planned)
        {
            SkinExtrasCatalogAsset package;
            SkinExtrasCatalogAsset? preview;
            if (publishBundles)
            {
                var packageName = item.Local.Manifest.Fingerprint + ".kextra";
                var packagePath = Path.Combine(assetsRoot, packageName);
                if (File.Exists(packagePath))
                    File.Delete(packagePath);
                SkinExtraPortablePackage.Export(item.Local, packagePath);
                var packageFacts = InspectPackage(packagePath);
                package = new SkinExtrasCatalogAsset
                {
                    ReleaseTag = options.ReleaseTag,
                    AssetName = packageName,
                    Sha256 = packageFacts.Sha256,
                    DownloadBytes = packageFacts.CompressedBytes,
                    ExpandedBytes = packageFacts.ExpandedBytes,
                    EntryCount = packageFacts.EntryCount,
                };
                ValidatePackageFacts(package, item.Local.Manifest.DisplayName);

                preview = CreatePreview(item.Local, assetsRoot, options.ReleaseTag);
            }
            else
            {
                package = item.Match!.Pack.Package;
                preview = item.Match.Pack.Preview;
            }

            var revision = item.Match is null
                ? 1
                : item.ContentChanged
                    ? checked(item.Match.Pack.Revision + 1)
                    : item.Match.Pack.Revision;
            var remote = new SkinExtrasCatalogPack
            {
                PackId = item.Match?.Pack.PackId
                         ?? StablePackId(item.Local.Manifest.Fingerprint),
                Revision = revision,
                ContentFingerprint = item.Local.Manifest.Fingerprint,
                SupersedesFingerprint = item.ContentChanged
                    ? item.Match!.Pack.ContentFingerprint
                    : item.Match?.Pack.SupersedesFingerprint,
                DisplayName = item.Local.Manifest.DisplayName,
                FamilyId = item.Local.Manifest.FamilyId,
                Area = item.Local.Manifest.Area,
                Variant = item.Local.Manifest.Variant,
                SourceSkin = item.Local.Manifest.SourceSkin,
                SourceAuthor = item.Local.Manifest.SourceAuthor,
                Compatibility = Compatibility(item.Local.Manifest),
                MinimumKumoriVersion = options.MinimumKumoriVersion,
                Package = package,
                Preview = preview,
            };
            catalog.Packs.Add(remote);
            nextState.Packs.Add(new PublisherPackState
            {
                LocalRelativePath = item.RelativePath,
                Pack = remote,
            });
        }

        foreach (var old in unmatched)
        {
            var withdrawnAt = old.WithdrawnAtUtc ?? now;
            nextState.Packs.Add(new PublisherPackState
            {
                LocalRelativePath = old.LocalRelativePath,
                Pack = old.Pack,
                WithdrawnAtUtc = withdrawnAt,
            });
        }

        catalog.Packs.Sort((left, right) =>
            StringComparer.OrdinalIgnoreCase.Compare(left.DisplayName, right.DisplayName));
        if (publishBundles)
            BuildBundles(
                catalog.Packs,
                assetsRoot,
                options.ReleaseTag,
                releaseAssets[options.ReleaseTag]);
        catalog.Withdrawals.AddRange(nextState.Packs
            .Where(pack => pack.WithdrawnAtUtc is not null)
            .Select(pack => new SkinExtrasCatalogWithdrawal
            {
                PackId = pack.Pack.PackId,
                LastRevision = pack.Pack.Revision,
                ContentFingerprint = pack.Pack.ContentFingerprint,
                WithdrawnAtUtc = pack.WithdrawnAtUtc!.Value,
                Reason = "No longer present in the owner's publishing library.",
            })
            .OrderBy(item => item.WithdrawnAtUtc));

        var summary = new PublicationSummary
        {
            Mode = options.Mode,
            CatalogVersion = options.ReleaseTag,
            LocalPackCount = scanned.Length,
            DuplicateLocalPacks = scanned.Length - local.Length,
            ActivePackCount = catalog.Packs.Count,
            Additions = additions,
            Revisions = revisions,
            MetadataChanges = metadataChanges,
            Withdrawals = newlyWithdrawn,
            Republished = publishBundles ? planned.Count : 0,
            Changed = changedAtAll,
            Releases = releaseTags.Select(tag => new PublicationRelease
            {
                Tag = tag,
                Assets = releaseAssets[tag],
                IsLatestCatalogShard = tag.Equals(options.ReleaseTag, StringComparison.Ordinal),
            }).ToList(),
        };

        WriteJson(Path.Combine(options.OutputDirectory, "catalog-v1.json"), catalog);
        WriteJson(Path.Combine(options.OutputDirectory, "catalog-state.json"), nextState);
        WriteJson(Path.Combine(options.OutputDirectory, "publication-summary.json"), summary);
        return new PublisherResult(catalog, nextState, summary);
    }

    private static PublisherPackState? Match(
        SkinExtraPackDescriptor local,
        string relativePath,
        HashSet<PublisherPackState> candidates)
    {
        var fingerprint = candidates.Where(candidate =>
            candidate.Pack.ContentFingerprint.Equals(
                local.Manifest.Fingerprint,
                StringComparison.OrdinalIgnoreCase)).ToArray();
        if (fingerprint.Length == 1) return fingerprint[0];

        var path = candidates.Where(candidate =>
            candidate.LocalRelativePath.Equals(relativePath, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (path.Length == 1) return path[0];

        var localKey = LogicalKey(local.Manifest);
        var logical = candidates.Where(candidate =>
            LogicalKey(candidate.Pack).Equals(localKey, StringComparison.Ordinal)).ToArray();
        return logical.Length == 1 ? logical[0] : null;
    }

    private static string LogicalKey(SkinExtraPackManifest manifest) =>
        LogicalKey(manifest.FamilyId, manifest.Variant, manifest.SourceSkin, manifest.SourceAuthor);

    private static string LogicalKey(SkinExtrasCatalogPack pack) =>
        LogicalKey(pack.FamilyId, pack.Variant, pack.SourceSkin, pack.SourceAuthor);

    private static string LogicalKey(
        string family,
        string? variant,
        string? sourceSkin,
        string? sourceAuthor) =>
        string.Join('\u001f',
            NormalizeIdentity(family),
            NormalizeIdentity(variant),
            NormalizeIdentity(sourceSkin),
            NormalizeIdentity(sourceAuthor));

    private static string NormalizeIdentity(string? value) =>
        string.Join(' ', (value ?? "").Trim().ToLowerInvariant()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static bool MetadataChanged(
        SkinExtrasCatalogPack prior,
        SkinExtraPackManifest current) =>
        !prior.DisplayName.Equals(current.DisplayName, StringComparison.Ordinal)
        || !prior.FamilyId.Equals(current.FamilyId, StringComparison.Ordinal)
        || !prior.Area.Equals(current.Area, StringComparison.Ordinal)
        || !StringEquals(prior.Variant, current.Variant)
        || !StringEquals(prior.SourceSkin, current.SourceSkin)
        || !StringEquals(prior.SourceAuthor, current.SourceAuthor)
        || !StringEquals(prior.Compatibility, Compatibility(current));

    private static bool StringEquals(string? left, string? right) =>
        string.Equals(left, right, StringComparison.Ordinal);

    private static string Compatibility(SkinExtraPackManifest manifest)
    {
        var badge = SkinExtraLazerCompatibility.CompatibilityBadge(manifest);
        return string.IsNullOrWhiteSpace(badge) ? "Lazer used" : badge;
    }

    private static void BuildBundles(
        IReadOnlyList<SkinExtrasCatalogPack> packs,
        string assetsRoot,
        string releaseTag,
        List<string> releaseAssets)
    {
        for (var bundleIndex = 0; bundleIndex < BundleCount; bundleIndex++)
        {
            var bundlePacks = packs
                .Where(pack => BundleIndex(pack.PackId) == bundleIndex)
                .OrderBy(pack => pack.ContentFingerprint, StringComparer.Ordinal)
                .ToArray();
            if (bundlePacks.Length == 0) continue;

            var bundleName = $"catalog-bundle-{bundleIndex + 1:000}.zip";
            var bundlePath = Path.Combine(assetsRoot, bundleName);
            if (File.Exists(bundlePath)) File.Delete(bundlePath);
            using (var archive = ZipFile.Open(
                       bundlePath,
                       ZipArchiveMode.Create,
                       entryNameEncoding: System.Text.Encoding.UTF8))
            {
                foreach (var pack in bundlePacks)
                {
                    AddBundleEntry(
                        archive,
                        Path.Combine(assetsRoot, pack.Package.AssetName),
                        $"packages/{pack.Package.AssetName}");
                    if (pack.Preview is not null)
                    {
                        AddBundleEntry(
                            archive,
                            Path.Combine(assetsRoot, pack.Preview.AssetName),
                            $"previews/{pack.Preview.AssetName}");
                    }
                }
            }

            var bundleInfo = new FileInfo(bundlePath);
            if (bundleInfo.Length > MaxCompressedBytes)
                throw new InvalidDataException(
                    $"Bundle '{bundleName}' exceeds the 256 MiB release-asset limit.");
            using var bundleStream = File.OpenRead(bundlePath);
            var bundleHash = Convert.ToHexStringLower(SHA256.HashData(bundleStream));
            foreach (var pack in bundlePacks)
            {
                var packageEntry = pack.Package;
                pack.Package = BundleReference(
                    packageEntry,
                    releaseTag,
                    bundleName,
                    bundleHash,
                    bundleInfo.Length,
                    $"packages/{packageEntry.AssetName}");
                if (pack.Preview is not null)
                {
                    var previewEntry = pack.Preview;
                    pack.Preview = BundleReference(
                        previewEntry,
                        releaseTag,
                        bundleName,
                        bundleHash,
                        bundleInfo.Length,
                        $"previews/{previewEntry.AssetName}");
                }
            }
            releaseAssets.Add(bundleName);
        }

        foreach (var entry in Directory.EnumerateFiles(assetsRoot))
        {
            var extension = Path.GetExtension(entry);
            if (extension.Equals(".kextra", StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(entry).EndsWith(
                    ".preview.png",
                    StringComparison.OrdinalIgnoreCase))
                File.Delete(entry);
        }
    }

    private static int BundleIndex(Guid packId) =>
        packId.ToByteArray()[0] % BundleCount;

    private static void AddBundleEntry(
        ZipArchive archive,
        string sourcePath,
        string entryName)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.NoCompression);
        entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
        entry.ExternalAttributes = 0;
        using var source = File.OpenRead(sourcePath);
        using var destination = entry.Open();
        source.CopyTo(destination);
    }

    private static SkinExtrasCatalogAsset BundleReference(
        SkinExtrasCatalogAsset entry,
        string releaseTag,
        string bundleName,
        string bundleHash,
        long bundleBytes,
        string entryName) => new()
    {
        ReleaseTag = releaseTag,
        AssetName = bundleName,
        Sha256 = bundleHash,
        DownloadBytes = bundleBytes,
        EntryName = entryName,
        EntrySha256 = entry.Sha256,
        EntryDownloadBytes = entry.DownloadBytes,
        ExpandedBytes = entry.ExpandedBytes,
        EntryCount = entry.EntryCount,
    };

    private static PackageFacts InspectPackage(string path)
    {
        var info = new FileInfo(path);
        using var archive = ZipFile.OpenRead(path);
        using var stream = File.OpenRead(path);
        return new PackageFacts(
            Convert.ToHexStringLower(SHA256.HashData(stream)),
            info.Length,
            archive.Entries.Sum(entry => entry.Length),
            archive.Entries.Count);
    }

    private static void ValidatePackageFacts(
        SkinExtrasCatalogAsset package,
        string displayName)
    {
        if (package.DownloadBytes > MaxCompressedBytes)
            throw new InvalidDataException($"Pack '{displayName}' exceeds the 256 MiB package limit.");
        if (package.ExpandedBytes > MaxExpandedBytes)
            throw new InvalidDataException($"Pack '{displayName}' exceeds the 512 MiB expanded limit.");
        if (package.EntryCount > MaxEntries)
            throw new InvalidDataException($"Pack '{displayName}' exceeds the 4,096-entry limit.");
    }

    private static SkinExtrasCatalogAsset? CreatePreview(
        SkinExtraPackDescriptor pack,
        string outputDirectory,
        string releaseTag)
    {
        var image = pack.Manifest.Files.FirstOrDefault(file =>
            IsImageExtension(Path.GetExtension(file.TargetFilename)));
        if (image is null) return null;
        var source = Path.Combine(pack.DirectoryPath, image.TargetFilename);
        var bitmap = SkinImageTools.Decode(File.ReadAllBytes(source), 768);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var assetName = pack.Manifest.Fingerprint + ".preview.png";
        var destination = Path.Combine(outputDirectory, assetName);
        using (var stream = File.Create(destination))
            encoder.Save(stream);
        var info = new FileInfo(destination);
        if (info.Length > 4 * 1024 * 1024)
            throw new InvalidDataException(
                $"Pack '{pack.Manifest.DisplayName}' preview exceeds the 4 MiB catalog limit.");
        using var previewStream = File.OpenRead(destination);
        return new SkinExtrasCatalogAsset
        {
            ReleaseTag = releaseTag,
            AssetName = assetName,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(previewStream)),
            DownloadBytes = info.Length,
            ExpandedBytes = info.Length,
            EntryCount = 1,
        };
    }

    private static bool IsImageExtension(string extension) =>
        extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".gif", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeRelativePath(string path) => path.Replace('\\', '/');

    private static Guid StablePackId(string fingerprint)
    {
        var bytes = Convert.FromHexString(fingerprint)[..16];
        bytes[6] = (byte)((bytes[6] & 0x0f) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        return new Guid(bytes);
    }

    private static void WriteJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(value, Json));
    }

    private sealed record PackageFacts(
        string Sha256,
        long CompressedBytes,
        long ExpandedBytes,
        int EntryCount);

    private sealed class PlannedPack(
        SkinExtraPackDescriptor local,
        string relativePath,
        PublisherPackState? match,
        bool isNew,
        bool contentChanged,
        bool metadataChanged)
    {
        public SkinExtraPackDescriptor Local { get; } = local;
        public string RelativePath { get; } = relativePath;
        public PublisherPackState? Match { get; } = match;
        public bool IsNew { get; } = isNew;
        public bool ContentChanged { get; } = contentChanged;
        public bool MetadataChanged { get; } = metadataChanged;
        public string? ReleaseTag { get; set; }
    }
}

internal sealed class PublisherOptions
{
    public required string Mode { get; init; }
    public required string ExtrasRoot { get; init; }
    public required string StatePath { get; init; }
    public required string OutputDirectory { get; init; }
    public required string ReleaseTag { get; init; }
    public required string MinimumKumoriVersion { get; init; }

    public static PublisherOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length || !args[index].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException(
                    $"Publisher arguments must be --name value pairs; received "
                    + $"{args.Length}: {string.Join(" | ", args)}");
            values[args[index][2..]] = args[index + 1];
        }
        string Required(string name) => values.GetValueOrDefault(name)
            ?? throw new ArgumentException($"Missing --{name}.");
        var mode = Required("mode");
        if (!mode.Equals("Validate", StringComparison.OrdinalIgnoreCase)
            && !mode.Equals("Stage", StringComparison.OrdinalIgnoreCase)
            && !mode.Equals("Repair", StringComparison.OrdinalIgnoreCase)
            && !mode.Equals("Republish", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                "--mode must be Validate, Stage, Repair, or Republish.");
        return new PublisherOptions
        {
            Mode = mode,
            ExtrasRoot = Path.GetFullPath(Required("extras-root")),
            StatePath = Path.GetFullPath(Required("state")),
            OutputDirectory = Path.GetFullPath(Required("output")),
            ReleaseTag = Required("release-tag"),
            MinimumKumoriVersion = values.GetValueOrDefault("minimum-kumori-version") ?? "0.6.0",
        };
    }
}

internal sealed class PublisherState
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public int SchemaVersion { get; init; } = 1;
    public List<PublisherPackState> Packs { get; init; } = [];

    public static PublisherState Read(string path)
    {
        if (!File.Exists(path)) return new PublisherState();
        return JsonSerializer.Deserialize<PublisherState>(File.ReadAllBytes(path), Json)
               ?? throw new InvalidDataException("The publisher state is invalid.");
    }
}

internal sealed class PublisherPackState
{
    public required string LocalRelativePath { get; init; }
    public required SkinExtrasCatalogPack Pack { get; init; }
    public DateTimeOffset? WithdrawnAtUtc { get; init; }
}

internal sealed record PublisherResult(
    SkinExtrasRemoteCatalog Catalog,
    PublisherState State,
    PublicationSummary Summary);

internal sealed class PublicationSummary
{
    public required string Mode { get; init; }
    public required string CatalogVersion { get; init; }
    public int LocalPackCount { get; init; }
    public int DuplicateLocalPacks { get; init; }
    public int ActivePackCount { get; init; }
    public int Additions { get; init; }
    public int Revisions { get; init; }
    public int MetadataChanges { get; init; }
    public int Withdrawals { get; init; }
    public int Republished { get; init; }
    public bool Changed { get; init; }
    public List<PublicationRelease> Releases { get; init; } = [];
}

internal sealed class PublicationRelease
{
    public required string Tag { get; init; }
    public bool IsLatestCatalogShard { get; init; }
    public List<string> Assets { get; init; } = [];
}
