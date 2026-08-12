using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kumori.Skins;

namespace Kumori.SkinStudio;

internal sealed class StudioExtrasCatalogAcceptanceController : IDisposable
{
    private const string release_tag = "catalog-2026.07.30.1";
    private static readonly JsonSerializerOptions json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly ECDsa signingKey =
        ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly AcceptanceHandler handler;
    private readonly HttpClient http;
    private readonly Dictionary<int, AcceptanceRevision> revisions;

    public StudioExtrasCatalogAcceptanceController(
        string workspaceRoot,
        string extrasRoot)
    {
        var root = Path.Combine(
            Path.GetFullPath(workspaceRoot),
            "catalog-sync-acceptance");
        Directory.CreateDirectory(root);
        revisions = new Dictionary<int, AcceptanceRevision>
        {
            [1] = createRevision(root, 1, 523.25),
            [2] = createRevision(root, 2, 659.25),
        };
        handler = new AcceptanceHandler(this);
        http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        var catalogClient = new SkinExtrasCatalogClient(
            http,
            Path.Combine(root, "catalog-cache"),
            signingKey.ExportSubjectPublicKeyInfo());
        var downloader = new SkinExtrasPackageDownloader(
            http,
            Path.Combine(root, "download-cache"));
        Service = new SkinExtrasCatalogSyncService(
            catalogClient,
            downloader,
            extrasRoot,
            Path.Combine(root, "revision-backups"));
    }

    public SkinExtrasCatalogSyncService Service { get; }

    public int Revision { get; private set; } = 1;

    public bool Offline { get; private set; }

    public bool HoldLatestRequest { get; private set; }

    public void UseRevision(int revision)
    {
        if (!revisions.ContainsKey(revision))
            throw new ArgumentOutOfRangeException(nameof(revision));
        Revision = revision;
        Offline = false;
        HoldLatestRequest = false;
    }

    public void UseOfflineCache()
    {
        Offline = true;
        HoldLatestRequest = false;
    }

    public void HoldForCancellation()
    {
        Offline = false;
        HoldLatestRequest = true;
    }

    public void Dispose()
    {
        http.Dispose();
        signingKey.Dispose();
    }

    private AcceptanceRevision createRevision(
        string root,
        int revision,
        double frequency)
    {
        var staging = Path.Combine(root, $"source-r{revision}");
        Directory.CreateDirectory(staging);
        var extraction = new SkinExtrasExtractionService();
        var source = extraction.BuildSource(
            $"Catalog Acceptance r{revision}",
            $"generated catalog acceptance revision {revision}",
            [
                new SkinExtractionFile("welcome.wav", createWave(frequency)),
            ]);
        var family = extraction.Analyze(source).Single(candidate =>
            candidate.Definition.Id == "audio.welcome");
        var extracted = extraction.Extract(
            source,
            [family],
            staging,
            $"Catalog Acceptance r{revision}");
        if (extracted.Count != 1)
            throw new InvalidDataException(
                "Could not generate the catalog acceptance pack.");
        var descriptor = SkinExtraPackIndex.Scan(staging).Single();
        var packagePath = Path.Combine(
            root,
            $"catalog-acceptance-r{revision}.kextra");
        if (File.Exists(packagePath))
            File.Delete(packagePath);
        SkinExtraPortablePackage.Export(descriptor, packagePath);
        var package = File.ReadAllBytes(packagePath);
        using var archive = ZipFile.OpenRead(packagePath);
        return new AcceptanceRevision(
            descriptor,
            package,
            archive.Entries.Sum(entry => entry.Length),
            archive.Entries.Count);
    }

    private byte[] catalogBytes()
    {
        var current = revisions[Revision];
        var previous = Revision > 1 ? revisions[Revision - 1] : null;
        var catalog = new SkinExtrasRemoteCatalog
        {
            CatalogVersion = $"acceptance-r{Revision}",
            GeneratedAtUtc = new DateTimeOffset(
                2026,
                7,
                30,
                0,
                Revision,
                0,
                TimeSpan.Zero),
            SigningKeyId = SkinExtrasCatalogTrust.KeyId,
            Packs =
            [
                new SkinExtrasCatalogPack
                {
                    PackId = Guid.Parse(
                        "6765B637-0A86-4B60-9849-2CE1C3F3FBAA"),
                    Revision = Revision,
                    ContentFingerprint =
                        current.Descriptor.Manifest.Fingerprint,
                    SupersedesFingerprint =
                        previous?.Descriptor.Manifest.Fingerprint,
                    DisplayName = current.Descriptor.Manifest.DisplayName,
                    FamilyId = current.Descriptor.Manifest.FamilyId,
                    Area = current.Descriptor.Manifest.Area,
                    Variant = current.Descriptor.Manifest.Variant,
                    SourceSkin = current.Descriptor.Manifest.SourceSkin,
                    SourceAuthor = current.Descriptor.Manifest.SourceAuthor,
                    Compatibility = "lazer-used",
                    Package = new SkinExtrasCatalogAsset
                    {
                        ReleaseTag = release_tag,
                        AssetName =
                            $"catalog-acceptance-r{Revision}.kextra",
                        Sha256 = hash(current.Package),
                        DownloadBytes = current.Package.LongLength,
                        ExpandedBytes = current.ExpandedBytes,
                        EntryCount = current.EntryCount,
                    },
                },
            ],
        };
        return JsonSerializer.SerializeToUtf8Bytes(catalog, json);
    }

    private byte[] signatureBytes(byte[] catalog)
    {
        var signature = signingKey.SignData(
            catalog,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence);
        return JsonSerializer.SerializeToUtf8Bytes(
            new SkinExtrasCatalogSignature
            {
                KeyId = SkinExtrasCatalogTrust.KeyId,
                Algorithm = SkinExtrasCatalogSignature.AlgorithmName,
                Signature = Convert.ToBase64String(signature),
            },
            json);
    }

    private static string hash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static byte[] createWave(double frequency)
    {
        const int sampleRate = 22_050;
        const int sampleCount = sampleRate / 2;
        const short channels = 1;
        const short bits = 16;
        var dataLength = sampleCount * 2;
        using var stream = new MemoryStream(44 + dataLength);
        using var writer = new BinaryWriter(
            stream,
            Encoding.ASCII,
            leaveOpen: true);
        writer.Write("RIFF"u8.ToArray());
        writer.Write(36 + dataLength);
        writer.Write("WAVE"u8.ToArray());
        writer.Write("fmt "u8.ToArray());
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);
        writer.Write((short)2);
        writer.Write(bits);
        writer.Write("data"u8.ToArray());
        writer.Write(dataLength);
        for (var index = 0; index < sampleCount; index++)
        {
            writer.Write((short)(
                Math.Sin(2 * Math.PI * frequency * index / sampleRate)
                * short.MaxValue
                * 0.25));
        }
        writer.Flush();
        return stream.ToArray();
    }

    private sealed record AcceptanceRevision(
        SkinExtraPackDescriptor Descriptor,
        byte[] Package,
        long ExpandedBytes,
        int EntryCount);

    private sealed class AcceptanceHandler(
        StudioExtrasCatalogAcceptanceController owner) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var original = request.RequestUri?.AbsoluteUri
                           ?? throw new InvalidDataException(
                               "Acceptance request has no URI.");
            if (original.Equals(
                    "https://github.com/Lorenso0/Kumori-Extras/releases/latest",
                    StringComparison.OrdinalIgnoreCase))
            {
                if (owner.HoldLatestRequest)
                    await Task.Delay(
                        Timeout.InfiniteTimeSpan,
                        cancellationToken);
                if (owner.Offline)
                    throw new HttpRequestException(
                        "Simulated offline catalog transport.");
                request.RequestUri = new Uri(
                    "https://github.com/Lorenso0/Kumori-Extras/"
                    + $"releases/tag/{release_tag}");
                return response(request, []);
            }

            if (owner.Offline)
                throw new HttpRequestException(
                    "Simulated offline catalog transport.");
            request.RequestUri = new Uri(
                "https://release-assets.githubusercontent.com/"
                + "kumori-acceptance");
            if (original.EndsWith(
                    "/catalog-v1.json",
                    StringComparison.OrdinalIgnoreCase))
            {
                return response(request, owner.catalogBytes());
            }
            if (original.EndsWith(
                    "/catalog-v1.sig",
                    StringComparison.OrdinalIgnoreCase))
            {
                var catalog = owner.catalogBytes();
                return response(request, owner.signatureBytes(catalog));
            }
            var revision = owner.revisions[owner.Revision];
            if (original.EndsWith(
                    $"/catalog-acceptance-r{owner.Revision}.kextra",
                    StringComparison.OrdinalIgnoreCase))
            {
                return response(request, revision.Package);
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                RequestMessage = request,
            };
        }

        private static HttpResponseMessage response(
            HttpRequestMessage request,
            byte[] bytes) =>
            new(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new ByteArrayContent(bytes),
            };
    }
}
