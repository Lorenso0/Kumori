using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Kumori.Core;

namespace Kumori.Skins;

public sealed class SkinExtrasRemoteCatalog
{
    public const int CurrentSchemaVersion = 1;
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public required string CatalogVersion { get; init; }
    public DateTimeOffset GeneratedAtUtc { get; init; }
    public required string SigningKeyId { get; init; }
    public List<SkinExtrasCatalogPack> Packs { get; init; } = [];
    public List<SkinExtrasCatalogWithdrawal> Withdrawals { get; init; } = [];
}

public sealed class SkinExtrasCatalogPack
{
    public required Guid PackId { get; init; }
    public required int Revision { get; init; }
    public required string ContentFingerprint { get; init; }
    public string? SupersedesFingerprint { get; init; }
    public required string DisplayName { get; init; }
    public required string FamilyId { get; init; }
    public required string Area { get; init; }
    public string? Variant { get; init; }
    public string? SourceSkin { get; init; }
    public string? SourceAuthor { get; init; }
    public string? Compatibility { get; init; }
    public string? MinimumKumoriVersion { get; init; }
    public required SkinExtrasCatalogAsset Package { get; set; }
    public SkinExtrasCatalogAsset? Preview { get; set; }
}

public sealed class SkinExtrasCatalogAsset
{
    public required string ReleaseTag { get; init; }
    public required string AssetName { get; init; }
    public required string Sha256 { get; init; }
    public required long DownloadBytes { get; init; }
    public string? EntryName { get; init; }
    public string? EntrySha256 { get; init; }
    public long? EntryDownloadBytes { get; init; }
    public long? ExpandedBytes { get; init; }
    public int? EntryCount { get; init; }
}

public sealed class SkinExtrasCatalogWithdrawal
{
    public required Guid PackId { get; init; }
    public required int LastRevision { get; init; }
    public required string ContentFingerprint { get; init; }
    public DateTimeOffset WithdrawnAtUtc { get; init; }
    public string? Reason { get; init; }
}

public sealed class SkinExtrasCatalogSignature
{
    public const string AlgorithmName = "ecdsa-p256-sha256";
    public int SchemaVersion { get; init; } = 1;
    public required string KeyId { get; init; }
    public required string Algorithm { get; init; }
    public required string Signature { get; init; }
}

public sealed record SkinExtrasCatalogFetchResult(
    SkinExtrasRemoteCatalog Catalog,
    string ReleaseTag,
    bool UsedCachedCatalog,
    string Message);

public static class SkinExtrasCatalogTrust
{
    public const string KeyId = "kumori-extras-2026-01";
    public const string PublicKeySubjectPublicKeyInfoBase64 =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEB7/jWKNZL6xAVfNyMIHLAVb/V5mBz8nJXTNAV9LIyhapCdJEOxP+icLenEh8icPSjX/PZ8Zsw9IhThUn/sj+Mg==";
}

public sealed class SkinExtrasCatalogClient
{
    internal const string RepositoryRoot = "https://github.com/Lorenso0/Kumori-Extras";
    internal const string LatestReleaseUrl = RepositoryRoot + "/releases/latest";
    private const int MaxCatalogBytes = 8 * 1024 * 1024;
    private const int MaxSignatureBytes = 16 * 1024;
    private const int MaxCatalogPacks = 50_000;
    private static readonly Regex Sha256Pattern = new(
        "^[a-fA-F0-9]{64}$",
        RegexOptions.CultureInvariant);
    private static readonly Regex SafeAssetPattern = new(
        "^[A-Za-z0-9][A-Za-z0-9._-]{0,199}$",
        RegexOptions.CultureInvariant);
    private static readonly Regex SafeTagPattern = new(
        "^catalog-[0-9]{4}\\.[0-9]{2}\\.[0-9]{2}\\.[0-9]+(?:-part-[0-9]{3})?$",
        RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly HttpClient http;
    private readonly string cacheDirectory;
    private readonly byte[] publicKey;

    public SkinExtrasCatalogClient(
        HttpClient? http = null,
        string? cacheDirectory = null,
        byte[]? publicKey = null)
    {
        this.http = http ?? CreateHttpClient();
        this.cacheDirectory = cacheDirectory ?? AppPaths.SkinExtrasCatalogCacheDir;
        this.publicKey = publicKey
                         ?? Convert.FromBase64String(
                             SkinExtrasCatalogTrust.PublicKeySubjectPublicKeyInfoBase64);
    }

    public async Task<SkinExtrasCatalogFetchResult> FetchAsync(
        CancellationToken cancellationToken = default)
    {
        Exception? remoteFailure = null;
        try
        {
            var releaseTag = await ResolveLatestReleaseTagAsync(cancellationToken)
                .ConfigureAwait(false);
            var cachedState = ReadCacheState();
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                BuildReleaseAssetUrl(releaseTag, "catalog-v1.json"));
            if (cachedState?.ReleaseTag.Equals(
                    releaseTag,
                    StringComparison.OrdinalIgnoreCase) == true)
            {
                if (!string.IsNullOrWhiteSpace(cachedState.ETag))
                    request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Parse(cachedState.ETag));
                if (cachedState.LastModifiedUtc is not null)
                    request.Headers.IfModifiedSince = cachedState.LastModifiedUtc;
            }

            using var response = await http.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotModified)
                return LoadCached(releaseTag, "Extras catalog is unchanged.");

            response.EnsureSuccessStatusCode();
            ValidateFinalDownloadHost(response.RequestMessage?.RequestUri);
            var catalogBytes = await ReadBoundedAsync(
                    response.Content,
                    MaxCatalogBytes,
                    "Extras catalog",
                    cancellationToken)
                .ConfigureAwait(false);
            var signatureBytes = await DownloadBoundedAsync(
                    BuildReleaseAssetUrl(releaseTag, "catalog-v1.sig"),
                    MaxSignatureBytes,
                    "Extras catalog signature",
                    cancellationToken)
                .ConfigureAwait(false);
            var catalog = ParseAndVerify(catalogBytes, signatureBytes);
            WriteCache(
                releaseTag,
                catalogBytes,
                signatureBytes,
                response.Headers.ETag?.ToString(),
                response.Content.Headers.LastModified);
            return new SkinExtrasCatalogFetchResult(
                catalog,
                releaseTag,
                false,
                $"Loaded Extras catalog {catalog.CatalogVersion}.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            remoteFailure = ex;
        }

        try
        {
            var cached = LoadCached(null, "GitHub is unavailable; using the last verified Extras catalog.");
            return cached;
        }
        catch
        {
            throw new InvalidOperationException(
                $"Could not load the Extras catalog: {remoteFailure?.Message}",
                remoteFailure);
        }
    }

    public SkinExtrasRemoteCatalog ParseAndVerify(byte[] catalogBytes, byte[] signatureBytes)
    {
        var signature = JsonSerializer.Deserialize<SkinExtrasCatalogSignature>(
                            signatureBytes,
                            Json)
                        ?? throw new InvalidDataException("The Extras catalog signature is invalid.");
        if (signature.SchemaVersion != 1
            || !signature.KeyId.Equals(SkinExtrasCatalogTrust.KeyId, StringComparison.Ordinal)
            || !signature.Algorithm.Equals(
                SkinExtrasCatalogSignature.AlgorithmName,
                StringComparison.Ordinal))
            throw new InvalidDataException("The Extras catalog uses an unsupported signing key or algorithm.");

        byte[] signatureValue;
        try
        {
            signatureValue = Convert.FromBase64String(signature.Signature);
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("The Extras catalog signature is not valid base64.", ex);
        }

        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(publicKey, out var bytesRead);
        if (bytesRead != publicKey.Length
            || !key.VerifyData(
                catalogBytes,
                signatureValue,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence))
            throw new InvalidDataException("The Extras catalog signature could not be verified.");

        var catalog = JsonSerializer.Deserialize<SkinExtrasRemoteCatalog>(catalogBytes, Json)
                      ?? throw new InvalidDataException("The Extras catalog is invalid.");
        ValidateCatalog(catalog);
        return catalog;
    }

    public static string BuildReleaseAssetUrl(string releaseTag, string assetName)
    {
        if (!SafeTagPattern.IsMatch(releaseTag) || !SafeAssetPattern.IsMatch(assetName))
            throw new InvalidDataException("The Extras catalog contains an unsafe release asset reference.");
        return $"{RepositoryRoot}/releases/download/{Uri.EscapeDataString(releaseTag)}/{Uri.EscapeDataString(assetName)}";
    }

    private async Task<string> ResolveLatestReleaseTagAsync(CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(
                LatestReleaseUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var final = response.RequestMessage?.RequestUri
                    ?? throw new InvalidDataException("GitHub did not resolve the latest Extras release.");
        if (!final.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("GitHub redirected the latest Extras release unexpectedly.");
        const string marker = "/Lorenso0/Kumori-Extras/releases/tag/";
        var index = final.AbsolutePath.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        var tag = index < 0
            ? ""
            : Uri.UnescapeDataString(final.AbsolutePath[(index + marker.Length)..].Trim('/'));
        if (!SafeTagPattern.IsMatch(tag))
            throw new InvalidDataException("GitHub did not return a supported Extras release tag.");
        return tag;
    }

    private async Task<byte[]> DownloadBoundedAsync(
        string url,
        int limit,
        string description,
        CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(
                url,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        ValidateFinalDownloadHost(response.RequestMessage?.RequestUri);
        return await ReadBoundedAsync(response.Content, limit, description, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        int limit,
        string description,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > 0
            && content.Headers.ContentLength > limit)
            throw new InvalidDataException($"{description} exceeds its size limit.");
        await using var input = await content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > limit)
                throw new InvalidDataException($"{description} exceeds its size limit.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static void ValidateCatalog(SkinExtrasRemoteCatalog catalog)
    {
        if (catalog.SchemaVersion != SkinExtrasRemoteCatalog.CurrentSchemaVersion)
            throw new InvalidDataException("This Extras catalog requires a different Kumori version.");
        if (string.IsNullOrWhiteSpace(catalog.CatalogVersion)
            || !catalog.SigningKeyId.Equals(SkinExtrasCatalogTrust.KeyId, StringComparison.Ordinal))
            throw new InvalidDataException("The Extras catalog identity is invalid.");
        if (catalog.Packs.Count > MaxCatalogPacks)
            throw new InvalidDataException("The Extras catalog contains too many packs.");
        if (catalog.Packs.Select(pack => pack.PackId).Distinct().Count() != catalog.Packs.Count)
            throw new InvalidDataException("The Extras catalog contains duplicate pack identities.");
        if (catalog.Packs.Select(pack => pack.ContentFingerprint)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count() != catalog.Packs.Count)
            throw new InvalidDataException("The Extras catalog contains duplicate pack fingerprints.");

        foreach (var pack in catalog.Packs)
        {
            if (pack.PackId == Guid.Empty
                || pack.Revision <= 0
                || string.IsNullOrWhiteSpace(pack.DisplayName)
                || string.IsNullOrWhiteSpace(pack.FamilyId)
                || !Sha256Pattern.IsMatch(pack.ContentFingerprint)
                || (pack.SupersedesFingerprint is not null
                    && !Sha256Pattern.IsMatch(pack.SupersedesFingerprint)))
                throw new InvalidDataException("The Extras catalog contains invalid pack metadata.");
            ValidateAsset(pack.Package, package: true);
            if (pack.Preview is not null) ValidateAsset(pack.Preview, package: false);
        }

        if (catalog.Withdrawals.Select(item => item.PackId).Distinct().Count()
            != catalog.Withdrawals.Count)
            throw new InvalidDataException("The Extras catalog contains duplicate withdrawals.");
        if (catalog.Withdrawals.Any(item =>
                item.PackId == Guid.Empty
                || item.LastRevision <= 0
                || !Sha256Pattern.IsMatch(item.ContentFingerprint)
                || catalog.Packs.Any(pack => pack.PackId == item.PackId)))
            throw new InvalidDataException("The Extras catalog contains invalid withdrawal metadata.");
    }

    private static void ValidateAsset(SkinExtrasCatalogAsset asset, bool package)
    {
        var bundled = !string.IsNullOrWhiteSpace(asset.EntryName);
        if (!SafeTagPattern.IsMatch(asset.ReleaseTag)
            || !SafeAssetPattern.IsMatch(asset.AssetName)
            || !Sha256Pattern.IsMatch(asset.Sha256)
            || asset.DownloadBytes <= 0
            || asset.DownloadBytes > SkinExtraPortablePackage.MaxCompressedBytes
            || (bundled && !asset.AssetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            || (bundled && !IsSafeBundleEntry(asset.EntryName!))
            || (bundled && !Sha256Pattern.IsMatch(asset.EntrySha256 ?? ""))
            || (bundled && asset.EntryDownloadBytes is <= 0
                or > SkinExtraPortablePackage.MaxCompressedBytes)
            || (package && bundled
                && !asset.EntryName!.EndsWith(".kextra", StringComparison.OrdinalIgnoreCase))
            || (!package && bundled
                && !asset.EntryName!.EndsWith(".preview.png", StringComparison.OrdinalIgnoreCase))
            || (package && asset.ExpandedBytes is <= 0 or > SkinExtraPortablePackage.MaxExpandedBytes)
            || (package && asset.EntryCount is <= 0 or > SkinExtraPortablePackage.MaxEntries)
            || (!bundled && package
                && !asset.AssetName.EndsWith(".kextra", StringComparison.OrdinalIgnoreCase))
            || (!bundled && !package && asset.DownloadBytes > 4 * 1024 * 1024))
            throw new InvalidDataException("The Extras catalog contains an invalid release asset.");
    }

    private static bool IsSafeBundleEntry(string entryName)
    {
        var normalized = entryName.Replace('\\', '/');
        return normalized.Length is > 0 and <= 300
               && !normalized.StartsWith("/", StringComparison.Ordinal)
               && !normalized.Contains("../", StringComparison.Ordinal)
               && !normalized.Contains("/..", StringComparison.Ordinal)
               && !Path.IsPathRooted(entryName);
    }

    private SkinExtrasCatalogFetchResult LoadCached(string? expectedReleaseTag, string message)
    {
        var state = ReadCacheState()
                    ?? throw new FileNotFoundException("No cached Extras catalog is available.");
        if (expectedReleaseTag is not null
            && !state.ReleaseTag.Equals(expectedReleaseTag, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The cached Extras catalog belongs to a different release.");
        var catalogBytes = File.ReadAllBytes(Path.Combine(cacheDirectory, "catalog-v1.json"));
        var signatureBytes = File.ReadAllBytes(Path.Combine(cacheDirectory, "catalog-v1.sig"));
        var catalog = ParseAndVerify(catalogBytes, signatureBytes);
        return new SkinExtrasCatalogFetchResult(catalog, state.ReleaseTag, true, message);
    }

    private CacheState? ReadCacheState()
    {
        try
        {
            var path = Path.Combine(cacheDirectory, "state.json");
            return File.Exists(path)
                ? JsonSerializer.Deserialize<CacheState>(File.ReadAllBytes(path), Json)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private void WriteCache(
        string releaseTag,
        byte[] catalogBytes,
        byte[] signatureBytes,
        string? etag,
        DateTimeOffset? lastModified)
    {
        Directory.CreateDirectory(cacheDirectory);
        WriteAtomically(Path.Combine(cacheDirectory, "catalog-v1.json"), catalogBytes);
        WriteAtomically(Path.Combine(cacheDirectory, "catalog-v1.sig"), signatureBytes);
        WriteAtomically(
            Path.Combine(cacheDirectory, "state.json"),
            JsonSerializer.SerializeToUtf8Bytes(
                new CacheState(releaseTag, etag, lastModified),
                Json));
    }

    private static void WriteAtomically(string path, byte[] bytes)
    {
        var pending = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(pending, bytes);
            File.Move(pending, path, true);
        }
        finally
        {
            try { File.Delete(pending); } catch { }
        }
    }

    private static void ValidateFinalDownloadHost(Uri? uri)
    {
        var host = uri?.Host ?? "";
        if (!host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            && !host.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
            && !host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("GitHub redirected an Extras download to an unexpected host.");
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Kumori");
        return client;
    }

    private sealed record CacheState(
        string ReleaseTag,
        string? ETag,
        DateTimeOffset? LastModifiedUtc);
}

public sealed record SkinExtrasDownloadProgress(long BytesReceived, long TotalBytes);

public sealed class SkinExtrasPackageDownloader
{
    private readonly object verifiedFilesGate = new();
    private readonly HttpClient http;
    private readonly string cacheDirectory;
    private readonly Dictionary<string, VerifiedFileStamp> verifiedFiles =
        new(StringComparer.OrdinalIgnoreCase);

    public SkinExtrasPackageDownloader(
        HttpClient? http = null,
        string? cacheDirectory = null)
    {
        this.http = http ?? CreateHttpClient();
        this.cacheDirectory = cacheDirectory ?? AppPaths.SkinExtrasDownloadCacheDir;
    }

    public async Task<string> DownloadAsync(
        SkinExtrasCatalogPack pack,
        IProgress<SkinExtrasDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(cacheDirectory);
        var bundled = !string.IsNullOrWhiteSpace(pack.Package.EntryName);
        var final = Path.Combine(
            cacheDirectory,
            pack.Package.Sha256.ToLowerInvariant() + (bundled ? ".zip" : ".kextra"));
        if (File.Exists(final)
            && new FileInfo(final).Length == pack.Package.DownloadBytes
            && (IsVerifiedFile(final, pack.Package.Sha256, pack.Package.DownloadBytes)
                || await VerifyFileAsync(
                        final,
                        pack.Package.Sha256,
                        pack.Package.DownloadBytes,
                        progress,
                        cancellationToken)
                    .ConfigureAwait(false)))
            return bundled
                ? await ExtractBundleEntryAsync(pack.Package, final, cancellationToken)
                    .ConfigureAwait(false)
                : final;

        var pending = final + $".{Guid.NewGuid():N}.part";
        try
        {
            using var response = await http.GetAsync(
                    SkinExtrasCatalogClient.BuildReleaseAssetUrl(
                        pack.Package.ReleaseTag,
                        pack.Package.AssetName),
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            ValidateFinalDownloadHost(response.RequestMessage?.RequestUri);
            if (response.Content.Headers.ContentLength is > 0
                && response.Content.Headers.ContentLength > SkinExtraPortablePackage.MaxCompressedBytes)
                throw new InvalidDataException("The Extras package download exceeds the safe size limit.");

            long total = 0;
            string actual;
            await using (var input = await response.Content
                             .ReadAsStreamAsync(cancellationToken)
                             .ConfigureAwait(false))
            await using (var output = new FileStream(
                             pending,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             128 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[128 * 1024];
                progress?.Report(new SkinExtrasDownloadProgress(0, pack.Package.DownloadBytes));
                while (true)
                {
                    var read = await input.ReadAsync(buffer, cancellationToken)
                        .ConfigureAwait(false);
                    if (read == 0) break;
                    total += read;
                    if (total > SkinExtraPortablePackage.MaxCompressedBytes
                        || total > pack.Package.DownloadBytes)
                        throw new InvalidDataException(
                            "The Extras package download exceeds its declared size.");
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                        .ConfigureAwait(false);
                    progress?.Report(
                        new SkinExtrasDownloadProgress(total, pack.Package.DownloadBytes));
                }
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                actual = Convert.ToHexStringLower(hash.GetHashAndReset());
            }
            if (total != pack.Package.DownloadBytes
                || !actual.Equals(pack.Package.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The downloaded Extras package failed size or hash verification.");
            File.Move(pending, final, true);
            MarkVerifiedFile(final, pack.Package.Sha256, pack.Package.DownloadBytes);
            return bundled
                ? await ExtractBundleEntryAsync(pack.Package, final, cancellationToken)
                    .ConfigureAwait(false)
                : final;
        }
        finally
        {
            try { File.Delete(pending); } catch { }
        }
    }

    private async Task<string> ExtractBundleEntryAsync(
        SkinExtrasCatalogAsset asset,
        string bundlePath,
        CancellationToken cancellationToken)
    {
        var expectedName = asset.EntryName
                           ?? throw new InvalidDataException(
                               "The Extras bundle entry is missing.");
        var expectedHash = asset.EntrySha256
                           ?? throw new InvalidDataException(
                               "The Extras bundle entry hash is missing.");
        var expectedBytes = asset.EntryDownloadBytes
                            ?? throw new InvalidDataException(
                                "The Extras bundle entry size is missing.");
        var destination = Path.Combine(
            cacheDirectory,
            expectedHash.ToLowerInvariant() + ".kextra");
        if (File.Exists(destination)
            && new FileInfo(destination).Length == expectedBytes
            && (IsVerifiedFile(destination, expectedHash, expectedBytes)
                || await VerifyFileAsync(
                        destination,
                        expectedHash,
                        expectedBytes,
                        progress: null,
                        cancellationToken)
                    .ConfigureAwait(false)))
            return destination;

        var pending = destination + $".{Guid.NewGuid():N}.part";
        try
        {
            using var archive = ZipFile.OpenRead(bundlePath);
            if (archive.Entries.Count > SkinExtraPortablePackage.MaxEntries)
                throw new InvalidDataException("The Extras bundle contains too many entries.");
            var matches = archive.Entries
                .Where(entry => entry.FullName.Equals(
                    expectedName,
                    StringComparison.Ordinal))
                .ToArray();
            if (matches.Length != 1
                || matches[0].Length != expectedBytes
                || matches[0].Length > SkinExtraPortablePackage.MaxCompressedBytes)
                throw new InvalidDataException(
                    "The Extras bundle entry is missing, duplicated, or has the wrong size.");

            long total = 0;
            string actual;
            await using (var input = matches[0].Open())
            await using (var output = new FileStream(
                             pending,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             128 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[128 * 1024];
                while (true)
                {
                    var read = await input.ReadAsync(buffer, cancellationToken)
                        .ConfigureAwait(false);
                    if (read == 0) break;
                    total += read;
                    if (total > expectedBytes)
                        throw new InvalidDataException(
                            "The Extras bundle entry exceeds its declared size.");
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                        .ConfigureAwait(false);
                }
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                actual = Convert.ToHexStringLower(hash.GetHashAndReset());
            }
            if (total != expectedBytes
                || !actual.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "The Extras bundle entry failed size or hash verification.");
            File.Move(pending, destination, true);
            MarkVerifiedFile(destination, expectedHash, expectedBytes);
            return destination;
        }
        finally
        {
            try { File.Delete(pending); } catch { }
        }
    }

    private async Task<bool> VerifyFileAsync(
        string path,
        string expectedHash,
        long expectedBytes,
        IProgress<SkinExtrasDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        long total = 0;
        progress?.Report(new SkinExtrasDownloadProgress(0, expectedBytes));
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
            hash.AppendData(buffer, 0, read);
            progress?.Report(new SkinExtrasDownloadProgress(total, expectedBytes));
        }

        var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
        if (total != expectedBytes
            || !actual.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            return false;
        MarkVerifiedFile(path, expectedHash, expectedBytes);
        return true;
    }

    private bool IsVerifiedFile(string path, string expectedHash, long expectedBytes)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != expectedBytes) return false;
        lock (verifiedFilesGate)
        {
            return verifiedFiles.TryGetValue(path, out var verified)
                   && verified.Length == expectedBytes
                   && verified.LastWriteUtc == info.LastWriteTimeUtc
                   && verified.Hash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase);
        }
    }

    private void MarkVerifiedFile(string path, string expectedHash, long expectedBytes)
    {
        var info = new FileInfo(path);
        lock (verifiedFilesGate)
        {
            verifiedFiles[path] = new VerifiedFileStamp(
                expectedHash,
                expectedBytes,
                info.LastWriteTimeUtc);
        }
    }

    private sealed record VerifiedFileStamp(
        string Hash,
        long Length,
        DateTime LastWriteUtc);

    private static void ValidateFinalDownloadHost(Uri? uri)
    {
        var host = uri?.Host ?? "";
        if (!host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            && !host.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
            && !host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("GitHub redirected an Extras package to an unexpected host.");
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Kumori");
        return client;
    }
}

public sealed class SkinExtrasRemoteInstall
{
    public required Guid PackId { get; init; }
    public required int Revision { get; set; }
    public required string Fingerprint { get; set; }
    public required string RelativeDirectory { get; set; }
    public required string CatalogDisplayName { get; set; }
    public DateTimeOffset LastSynchronizedUtc { get; set; }
    public bool Withdrawn { get; set; }
    public bool LocallyModified { get; set; }
}

public sealed class SkinExtrasRemoteRegistry
{
    public int SchemaVersion { get; init; } = 1;
    public Dictionary<Guid, SkinExtrasRemoteInstall> Installs { get; init; } = [];
}

public static class SkinExtrasRemoteRegistryStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static SkinExtrasRemoteRegistry Read(string extrasRoot)
    {
        try
        {
            var path = RegistryPath(extrasRoot);
            return File.Exists(path)
                ? JsonSerializer.Deserialize<SkinExtrasRemoteRegistry>(
                      File.ReadAllBytes(path),
                      Json)
                  ?? new SkinExtrasRemoteRegistry()
                : new SkinExtrasRemoteRegistry();
        }
        catch
        {
            return new SkinExtrasRemoteRegistry();
        }
    }

    public static void Write(string extrasRoot, SkinExtrasRemoteRegistry registry)
    {
        var path = RegistryPath(extrasRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var pending = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(
                pending,
                JsonSerializer.SerializeToUtf8Bytes(registry, Json));
            File.Move(pending, path, true);
        }
        finally
        {
            try { File.Delete(pending); } catch { }
        }
    }

    private static string RegistryPath(string extrasRoot) =>
        Path.Combine(extrasRoot, ".kumori", "remote-installs.json");
}

public sealed class SkinExtrasSyncJournal
{
    public int SchemaVersion { get; init; } = 1;
    public required string CatalogVersion { get; init; }
    public required string Stage { get; set; }
    public Guid? CurrentPackId { get; set; }
    public int? CurrentRevision { get; set; }
    public string? OldRelativeDirectory { get; set; }
    public List<Guid> RemainingPackIds { get; init; } = [];
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public static class SkinExtrasSyncJournalStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static SkinExtrasSyncJournal? Read(string extrasRoot)
    {
        try
        {
            var path = Path(extrasRoot);
            return File.Exists(path)
                ? JsonSerializer.Deserialize<SkinExtrasSyncJournal>(
                    File.ReadAllBytes(path),
                    Json)
                : null;
        }
        catch
        {
            return null;
        }
    }

    public static void Write(string extrasRoot, SkinExtrasSyncJournal journal)
    {
        journal.UpdatedAtUtc = DateTimeOffset.UtcNow;
        var path = Path(extrasRoot);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var pending = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(
                pending,
                JsonSerializer.SerializeToUtf8Bytes(journal, Json));
            File.Move(pending, path, true);
        }
        finally
        {
            try { File.Delete(pending); } catch { }
        }
    }

    public static void Clear(string extrasRoot)
    {
        try { File.Delete(Path(extrasRoot)); } catch { }
    }

    private static string Path(string extrasRoot) =>
        System.IO.Path.Combine(extrasRoot, ".kumori", "catalog-sync-journal.json");
}

public enum SkinExtrasSyncStage
{
    Checking,
    Planning,
    Downloading,
    Installing,
    UpToDate,
    Offline,
    Paused,
    Failed,
}

public sealed record SkinExtrasSyncProgress(
    SkinExtrasSyncStage Stage,
    string Message,
    int CompletedPacks = 0,
    int TotalPacks = 0,
    long BytesReceived = 0,
    long TotalBytes = 0,
    bool IsManual = false);

public sealed record SkinExtrasSyncResult(
    int Installed,
    int Updated,
    int Adopted,
    int Withdrawn,
    int Unchanged,
    string Message);

public sealed class SkinExtrasCatalogSyncService
{
    private const long FreeSpaceSafetyBytes = 1024L * 1024 * 1024;
    private static readonly Lazy<SkinExtrasCatalogSyncService> SharedLazy =
        new(() => new SkinExtrasCatalogSyncService());
    private readonly object activeRunGate = new();
    private readonly SkinExtrasCatalogClient catalogClient;
    private readonly SkinExtrasPackageDownloader downloader;
    private readonly string extrasRoot;
    private readonly string revisionBackupsDirectory;
    private Task<SkinExtrasSyncResult>? activeRun;
    private CancellationTokenSource? activeRunCancellation;
    private bool activeRunIsManual;

    public static SkinExtrasCatalogSyncService Shared => SharedLazy.Value;

    public SkinExtrasCatalogSyncService(
        SkinExtrasCatalogClient? catalogClient = null,
        SkinExtrasPackageDownloader? downloader = null,
        string? extrasRoot = null,
        string? revisionBackupsDirectory = null)
    {
        this.catalogClient = catalogClient ?? new SkinExtrasCatalogClient();
        this.downloader = downloader ?? new SkinExtrasPackageDownloader();
        this.extrasRoot = Path.GetFullPath(
            extrasRoot ?? AppPaths.SkinExtrasDir);
        this.revisionBackupsDirectory = Path.GetFullPath(
            revisionBackupsDirectory
            ?? (extrasRoot is null
                ? AppPaths.SkinExtrasRevisionBackupsDir
                : Path.Combine(
                    this.extrasRoot,
                    ".kumori",
                    "revision-backups")));
    }

    public event EventHandler<SkinExtrasSyncProgress>? ProgressChanged;
    public event EventHandler? LibraryChanged;
    public SkinExtrasSyncProgress? CurrentProgress { get; private set; }

    public Task<SkinExtrasSyncResult> SynchronizeAsync(
        bool manual,
        CancellationToken cancellationToken = default)
    {
        lock (activeRunGate)
        {
            if (activeRun is { IsCompleted: false })
                return activeRun;
            activeRunCancellation?.Dispose();
            activeRunCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            activeRunIsManual = manual;
            activeRun = SynchronizeWithTerminalStateAsync(
                manual,
                activeRunCancellation.Token);
            return activeRun;
        }
    }

    public bool CancelActiveSynchronization()
    {
        lock (activeRunGate)
        {
            if (activeRun is not { IsCompleted: false }
                || activeRunCancellation is null)
                return false;
            activeRunCancellation.Cancel();
            return true;
        }
    }

    private async Task<SkinExtrasSyncResult> SynchronizeWithTerminalStateAsync(
        bool manual,
        CancellationToken cancellationToken)
    {
        try
        {
            return await SynchronizeCoreAsync(manual, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Report(
                SkinExtrasSyncStage.Paused,
                "Extras synchronization was canceled. It will resume on the next check.");
            throw;
        }
        catch (Exception ex)
        {
            Report(
                SkinExtrasSyncStage.Failed,
                $"Extras synchronization failed: {ex.Message}");
            throw;
        }
    }

    private async Task<SkinExtrasSyncResult> SynchronizeCoreAsync(
        bool manual,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(extrasRoot);
        Report(SkinExtrasSyncStage.Checking, manual
            ? "Checking Extras for updates…"
            : "Checking the Extras catalog…");

        SkinExtrasCatalogFetchResult fetched;
        try
        {
            fetched = await catalogClient.FetchAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Report(SkinExtrasSyncStage.Failed, $"Extras update check failed: {ex.Message}");
            throw;
        }

        Report(SkinExtrasSyncStage.Planning, "Comparing the complete Extras catalog…");
        var registry = SkinExtrasRemoteRegistryStore.Read(extrasRoot);
        var localPacks = SkinExtraPackIndex.Scan(extrasRoot).ToList();
        RecoverInterruptedReplacement(
            extrasRoot,
            fetched.Catalog,
            registry,
            localPacks);
        localPacks = SkinExtraPackIndex.Scan(extrasRoot).ToList();
        var pending = new List<(SkinExtrasCatalogPack Pack, SkinExtraPackDescriptor? Existing)>();
        var adopted = 0;
        var unchanged = 0;
        var now = DateTimeOffset.UtcNow;

        foreach (var pack in fetched.Catalog.Packs.OrderBy(pack => pack.DisplayName))
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureCompatible(pack);
            registry.Installs.TryGetValue(pack.PackId, out var managed);
            var existing = ResolveManagedPack(extrasRoot, managed, localPacks);
            if (managed is not null
                && managed.Revision == pack.Revision
                && managed.Fingerprint.Equals(
                    pack.ContentFingerprint,
                    StringComparison.OrdinalIgnoreCase)
                && existing is not null)
            {
                var report = SkinExtraPackValidator.Validate(existing);
                managed.LocallyModified = !report.IsHealthy;
                managed.Withdrawn = false;
                if (report.IsHealthy)
                {
                    unchanged++;
                    continue;
                }
            }

            var exact = localPacks.FirstOrDefault(candidate =>
                candidate.Manifest.Fingerprint.Equals(
                    pack.ContentFingerprint,
                    StringComparison.OrdinalIgnoreCase));
            if (exact is not null && SkinExtraPackValidator.Validate(exact).IsHealthy)
            {
                registry.Installs[pack.PackId] = CreateRegistryEntry(
                    extrasRoot,
                    pack,
                    exact,
                    now);
                adopted++;
                continue;
            }

            pending.Add((pack, existing));
        }

        EnsureFreeSpace(extrasRoot, pending);
        var installed = 0;
        var updated = 0;
        var totalDownloadBytes = pending
            .Select(item => item.Pack.Package)
            .GroupBy(DownloadIdentity, StringComparer.Ordinal)
            .Sum(group => group.First().DownloadBytes);
        long completedDownloadBytes = 0;
        var completedDownloads = new HashSet<string>(StringComparer.Ordinal);
        var journal = new SkinExtrasSyncJournal
        {
            CatalogVersion = fetched.Catalog.CatalogVersion,
            Stage = "planned",
            RemainingPackIds = pending.Select(item => item.Pack.PackId).ToList(),
        };
        if (pending.Count > 0)
            SkinExtrasSyncJournalStore.Write(extrasRoot, journal);

        for (var index = 0; index < pending.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (pack, existing) = pending[index];
            journal.Stage = "downloading";
            journal.CurrentPackId = pack.PackId;
            journal.CurrentRevision = pack.Revision;
            journal.OldRelativeDirectory = existing is null
                ? null
                : Path.GetRelativePath(extrasRoot, existing.DirectoryPath);
            SkinExtrasSyncJournalStore.Write(extrasRoot, journal);
            Report(
                SkinExtrasSyncStage.Downloading,
                $"Downloading {index + 1} of {pending.Count}: {pack.DisplayName}",
                index,
                pending.Count,
                completedDownloadBytes,
                totalDownloadBytes);
            var downloadProgress = new InlineProgress<SkinExtrasDownloadProgress>(value =>
                Report(
                    SkinExtrasSyncStage.Downloading,
                    $"Downloading {index + 1} of {pending.Count}: {pack.DisplayName}",
                    index,
                    pending.Count,
                    completedDownloadBytes + value.BytesReceived,
                    totalDownloadBytes));
            var packagePath = await downloader.DownloadAsync(
                    pack,
                    downloadProgress,
                    cancellationToken)
                .ConfigureAwait(false);
            if (completedDownloads.Add(DownloadIdentity(pack.Package)))
                completedDownloadBytes += pack.Package.DownloadBytes;
            try
            {
                journal.Stage = "installing";
                SkinExtrasSyncJournalStore.Write(extrasRoot, journal);
                Report(
                    SkinExtrasSyncStage.Installing,
                    $"Installing {index + 1} of {pending.Count}: {pack.DisplayName}",
                    index,
                    pending.Count,
                    completedDownloadBytes,
                    totalDownloadBytes);
                var replacement = existing is not null;
                var imported = InstallPack(
                    extrasRoot,
                    pack,
                    existing,
                    packagePath,
                    cancellationToken,
                    phase => Report(
                        SkinExtrasSyncStage.Installing,
                        $"Installing {index + 1} of {pending.Count}: "
                        + $"{pack.DisplayName} — {phase}",
                        index,
                        pending.Count,
                        completedDownloadBytes,
                        totalDownloadBytes));
                if (existing is not null)
                {
                    localPacks.RemoveAll(candidate =>
                        Path.GetFullPath(candidate.DirectoryPath).Equals(
                            Path.GetFullPath(existing.DirectoryPath),
                            StringComparison.OrdinalIgnoreCase));
                }
                localPacks.RemoveAll(candidate =>
                    candidate.Manifest.Fingerprint.Equals(
                        imported.Manifest.Fingerprint,
                        StringComparison.OrdinalIgnoreCase));
                localPacks.Add(imported);
                registry.Installs[pack.PackId] = CreateRegistryEntry(
                    extrasRoot,
                    pack,
                    imported,
                    DateTimeOffset.UtcNow);
                SkinExtrasRemoteRegistryStore.Write(extrasRoot, registry);
                journal.RemainingPackIds.Remove(pack.PackId);
                journal.Stage = "committed";
                SkinExtrasSyncJournalStore.Write(extrasRoot, journal);
                if (replacement) updated++;
                else installed++;
                LibraryChanged?.Invoke(this, EventArgs.Empty);
            }
            finally
            {
                try { File.Delete(packagePath); } catch { }
            }
        }

        var activeIds = fetched.Catalog.Packs.Select(pack => pack.PackId).ToHashSet();
        var withdrawnIds = fetched.Catalog.Withdrawals.Select(item => item.PackId).ToHashSet();
        var withdrawn = 0;
        foreach (var entry in registry.Installs.Values)
        {
            var shouldWithdraw = withdrawnIds.Contains(entry.PackId)
                                 || !activeIds.Contains(entry.PackId);
            if (shouldWithdraw && !entry.Withdrawn) withdrawn++;
            entry.Withdrawn = shouldWithdraw;
        }
        SkinExtrasRemoteRegistryStore.Write(extrasRoot, registry);
        SkinExtrasSyncJournalStore.Clear(extrasRoot);
        SkinExtrasPersistentIndex.Invalidate(extrasRoot);

        var summary = pending.Count == 0 && adopted == 0 && withdrawn == 0
            ? "Extras are up to date."
            : $"Extras synchronized: {installed} installed, {updated} updated, "
              + $"{adopted} adopted, {withdrawn} withdrawn.";
        Report(
            fetched.UsedCachedCatalog
                ? SkinExtrasSyncStage.Offline
                : SkinExtrasSyncStage.UpToDate,
            fetched.UsedCachedCatalog
                ? $"{summary} Using the last verified catalog because the network is unavailable."
                : summary,
            pending.Count,
            pending.Count);
        return new SkinExtrasSyncResult(
            installed,
            updated,
            adopted,
            withdrawn,
            unchanged,
            summary);
    }

    private SkinExtraPackDescriptor InstallPack(
        string extrasRoot,
        SkinExtrasCatalogPack catalogPack,
        SkinExtraPackDescriptor? existing,
        string packagePath,
        CancellationToken cancellationToken,
        Action<string>? progress)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? backup = null;
        if (existing is not null)
            backup = BackupExistingPack(catalogPack, existing);

        var importedResult = SkinExtraPortablePackage.ImportForCatalog(
            packagePath,
            extrasRoot,
            cancellationToken,
            progress);
        var imported = importedResult.Pack;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Invoke("Committing library state");
            var report = SkinExtraPackValidator.Validate(imported);
            if (!report.IsHealthy
                || !importedResult.SourceFingerprint.Equals(
                    catalogPack.ContentFingerprint,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The synchronized Extras pack failed validation.");

            if (existing is not null
                && !Path.GetFullPath(existing.DirectoryPath).Equals(
                    Path.GetFullPath(imported.DirectoryPath),
                    StringComparison.OrdinalIgnoreCase))
            {
                SkinExtrasLibraryStateStore.Transfer(
                    extrasRoot,
                    existing.Manifest.Fingerprint,
                    imported.Manifest.Fingerprint);
                var existingState = SkinExtrasLibraryStateStore.Get(
                    extrasRoot,
                    imported.Manifest.Fingerprint);
                if (string.IsNullOrWhiteSpace(existingState.DisplayNameOverride)
                    && !existing.Manifest.DisplayName.Equals(
                        imported.Manifest.DisplayName,
                        StringComparison.Ordinal))
                {
                    SkinExtrasLibraryStateStore.Update(
                        extrasRoot,
                        imported.Manifest.Fingerprint,
                        state => state.DisplayNameOverride = existing.Manifest.DisplayName);
                }
                var oldPath = SkinExtraPackDeletion.ResolvePackDirectory(
                    extrasRoot,
                    existing.DirectoryPath);
                Directory.Delete(oldPath, true);
            }
            PruneBackups(catalogPack.PackId);
            return imported;
        }
        catch
        {
            if (!importedResult.WasDuplicate
                && (existing is null
                    || !Path.GetFullPath(existing.DirectoryPath).Equals(
                        Path.GetFullPath(imported.DirectoryPath),
                        StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    var path = SkinExtraPackDeletion.ResolvePackDirectory(
                        extrasRoot,
                        imported.DirectoryPath);
                    Directory.Delete(path, true);
                }
                catch { }
            }
            throw new InvalidDataException(
                backup is null
                    ? "The Extras pack update failed before it could be installed."
                    : $"The Extras pack update failed. The previous revision backup is at '{backup}'.");
        }
    }

    private string BackupExistingPack(
        SkinExtrasCatalogPack pack,
        SkinExtraPackDescriptor existing)
    {
        var directory = Path.Combine(
            revisionBackupsDirectory,
            pack.PackId.ToString("D"));
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(
            directory,
            $"revision-{pack.Revision - 1}-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.zip");
        ZipFile.CreateFromDirectory(
            existing.DirectoryPath,
            destination,
            CompressionLevel.Optimal,
            includeBaseDirectory: true);
        return destination;
    }

    private void PruneBackups(Guid packId)
    {
        var directory = Path.Combine(
            revisionBackupsDirectory,
            packId.ToString("D"));
        if (!Directory.Exists(directory)) return;
        foreach (var obsolete in Directory.EnumerateFiles(directory, "*.zip")
                     .Select(path => new FileInfo(path))
                     .OrderByDescending(file => file.CreationTimeUtc)
                     .Skip(3))
        {
            try { obsolete.Delete(); } catch { }
        }
    }

    private static SkinExtrasRemoteInstall CreateRegistryEntry(
        string extrasRoot,
        SkinExtrasCatalogPack pack,
        SkinExtraPackDescriptor descriptor,
        DateTimeOffset synchronizedAt) => new()
        {
            PackId = pack.PackId,
            Revision = pack.Revision,
            Fingerprint = pack.ContentFingerprint,
            RelativeDirectory = Path.GetRelativePath(extrasRoot, descriptor.DirectoryPath),
            CatalogDisplayName = pack.DisplayName,
            LastSynchronizedUtc = synchronizedAt,
            Withdrawn = false,
            LocallyModified = false,
        };

    private static SkinExtraPackDescriptor? ResolveManagedPack(
        string extrasRoot,
        SkinExtrasRemoteInstall? managed,
        IReadOnlyList<SkinExtraPackDescriptor> localPacks)
    {
        if (managed is null) return null;
        var candidate = Path.GetFullPath(Path.Combine(extrasRoot, managed.RelativeDirectory));
        var relative = Path.GetRelativePath(extrasRoot, candidate);
        if (Path.IsPathRooted(relative)
            || relative.Equals("..", StringComparison.Ordinal)
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            return null;
        return localPacks.FirstOrDefault(pack =>
                   Path.GetFullPath(pack.DirectoryPath).Equals(
                       candidate,
                       StringComparison.OrdinalIgnoreCase))
               ?? localPacks.FirstOrDefault(pack =>
                   pack.Manifest.Fingerprint.Equals(
                       managed.Fingerprint,
                       StringComparison.OrdinalIgnoreCase));
    }

    private static void EnsureCompatible(SkinExtrasCatalogPack pack)
    {
        if (string.IsNullOrWhiteSpace(pack.MinimumKumoriVersion)) return;
        if (!Version.TryParse(pack.MinimumKumoriVersion, out var minimum))
            throw new InvalidDataException(
                $"Extras pack '{pack.DisplayName}' has an invalid minimum Kumori version.");
        var current = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0);
        if (minimum > current)
            throw new InvalidOperationException(
                $"Extras pack '{pack.DisplayName}' requires Kumori {minimum} or newer.");
    }

    private static void EnsureFreeSpace(
        string extrasRoot,
        IEnumerable<(SkinExtrasCatalogPack Pack, SkinExtraPackDescriptor? Existing)> pending)
    {
        var work = pending.ToArray();
        if (work.Length == 0) return;
        var backupBytes = work
            .Where(item => item.Existing is not null)
            .Sum(item => DirectorySize(item.Existing!.DirectoryPath));
        var required = checked(
            work.Select(item => item.Pack.Package)
                .GroupBy(DownloadIdentity, StringComparer.Ordinal)
                .Sum(group => group.First().DownloadBytes)
            + work.Sum(item => item.Pack.Package.ExpandedBytes ?? 0)
            + backupBytes
            + FreeSpaceSafetyBytes);
        var root = Path.GetPathRoot(Path.GetFullPath(extrasRoot))
                   ?? throw new IOException("Could not determine the Extras storage drive.");
        var available = new DriveInfo(root).AvailableFreeSpace;
        if (available < required)
            throw new IOException(
                $"Extras synchronization needs {FormatBytes(required)} free, "
                + $"but only {FormatBytes(available)} is available.");
    }

    private static long DirectorySize(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Sum(path => new FileInfo(path).Length);
        }
        catch
        {
            return 0;
        }
    }

    private static string DownloadIdentity(SkinExtrasCatalogAsset asset) =>
        $"{asset.ReleaseTag}\n{asset.AssetName}\n{asset.Sha256}";

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private static void RecoverInterruptedReplacement(
        string extrasRoot,
        SkinExtrasRemoteCatalog catalog,
        SkinExtrasRemoteRegistry registry,
        IReadOnlyList<SkinExtraPackDescriptor> localPacks)
    {
        var journal = SkinExtrasSyncJournalStore.Read(extrasRoot);
        if (journal?.CurrentPackId is not Guid packId
            || !journal.Stage.Equals("installing", StringComparison.OrdinalIgnoreCase))
            return;
        var catalogPack = catalog.Packs.FirstOrDefault(pack => pack.PackId == packId);
        if (catalogPack is null) return;
        var replacement = localPacks.FirstOrDefault(pack =>
            pack.Manifest.Fingerprint.Equals(
                catalogPack.ContentFingerprint,
                StringComparison.OrdinalIgnoreCase)
            && SkinExtraPackValidator.Validate(pack).IsHealthy);
        if (replacement is null) return;

        if (!string.IsNullOrWhiteSpace(journal.OldRelativeDirectory))
        {
            var oldCandidate = Path.GetFullPath(
                Path.Combine(extrasRoot, journal.OldRelativeDirectory));
            var relative = Path.GetRelativePath(extrasRoot, oldCandidate);
            if (!Path.IsPathRooted(relative)
                && !relative.Equals("..", StringComparison.Ordinal)
                && !relative.StartsWith(
                    $"..{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal)
                && Directory.Exists(oldCandidate)
                && !oldCandidate.Equals(
                    Path.GetFullPath(replacement.DirectoryPath),
                    StringComparison.OrdinalIgnoreCase))
            {
                var old = localPacks.FirstOrDefault(pack =>
                    Path.GetFullPath(pack.DirectoryPath).Equals(
                        oldCandidate,
                        StringComparison.OrdinalIgnoreCase));
                if (old is not null)
                    SkinExtrasLibraryStateStore.Transfer(
                        extrasRoot,
                        old.Manifest.Fingerprint,
                        replacement.Manifest.Fingerprint);
                Directory.Delete(oldCandidate, true);
            }
        }

        registry.Installs[packId] = CreateRegistryEntry(
            extrasRoot,
            catalogPack,
            replacement,
            DateTimeOffset.UtcNow);
        SkinExtrasRemoteRegistryStore.Write(extrasRoot, registry);
        SkinExtrasSyncJournalStore.Clear(extrasRoot);
    }

    private void Report(
        SkinExtrasSyncStage stage,
        string message,
        int completedPacks = 0,
        int totalPacks = 0,
        long bytesReceived = 0,
        long totalBytes = 0)
    {
        CurrentProgress = new SkinExtrasSyncProgress(
            stage,
            message,
            completedPacks,
            totalPacks,
            bytesReceived,
            totalBytes,
            activeRunIsManual);
        ProgressChanged?.Invoke(this, CurrentProgress);
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.#} {units[unit]}";
    }
}
