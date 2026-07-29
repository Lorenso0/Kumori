using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Kumori.App.Skins;
using Xunit;

namespace Kumori.App.Tests;

public sealed class SkinExtrasCatalogTests
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    [Fact]
    public void Signed_catalog_round_trips_and_rejects_changed_bytes()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var catalogBytes = JsonSerializer.SerializeToUtf8Bytes(CreateCatalog(), Json);
        var signatureBytes = Sign(key, catalogBytes);
        var cache = Path.Combine(
            Path.GetTempPath(),
            $"kumori-catalog-tests-{Guid.NewGuid():N}");
        try
        {
            var client = new SkinExtrasCatalogClient(
                cacheDirectory: cache,
                publicKey: key.ExportSubjectPublicKeyInfo());
            var catalog = client.ParseAndVerify(catalogBytes, signatureBytes);
            Assert.Single(catalog.Packs);

            catalogBytes[^2] ^= 1;
            Assert.Throws<InvalidDataException>(() =>
                client.ParseAndVerify(catalogBytes, signatureBytes));
        }
        finally
        {
            if (Directory.Exists(cache)) Directory.Delete(cache, true);
        }
    }

    [Fact]
    public void Release_asset_urls_are_fixed_to_the_catalog_repository()
    {
        Assert.Equal(
            "https://github.com/Lorenso0/Kumori-Extras/releases/download/catalog-2026.07.28.1/pack.kextra",
            SkinExtrasCatalogClient.BuildReleaseAssetUrl(
                "catalog-2026.07.28.1",
                "pack.kextra"));
        Assert.Equal(
            "https://github.com/Lorenso0/Kumori-Extras/releases/download/catalog-2026.07.28.1-part-001/pack.kextra",
            SkinExtrasCatalogClient.BuildReleaseAssetUrl(
                "catalog-2026.07.28.1-part-001",
                "pack.kextra"));
        Assert.Throws<InvalidDataException>(() =>
            SkinExtrasCatalogClient.BuildReleaseAssetUrl(
                "../other",
                "pack.kextra"));
        Assert.Throws<InvalidDataException>(() =>
            SkinExtrasCatalogClient.BuildReleaseAssetUrl(
                "catalog-2026.07.28.1",
                "../pack.kextra"));
    }

    [Fact]
    public async Task Bundled_packages_download_once_and_extract_verified_complete_entries()
    {
        var first = "first complete package"u8.ToArray();
        var second = "second complete package"u8.ToArray();
        var firstName = $"packages/{new string('a', 64)}.kextra";
        var secondName = $"packages/{new string('c', 64)}.kextra";
        byte[] bundle;
        using (var stream = new MemoryStream())
        {
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                WriteEntry(archive, firstName, first);
                WriteEntry(archive, secondName, second);
            }
            bundle = stream.ToArray();
        }

        var handler = new StaticResponseHandler(bundle);
        var cache = Path.Combine(
            Path.GetTempPath(),
            $"kumori-bundle-download-tests-{Guid.NewGuid():N}");
        try
        {
            var downloader = new SkinExtrasPackageDownloader(
                new HttpClient(handler),
                cache);
            var firstPack = BundledPack('a', firstName, first, bundle);
            var secondPack = BundledPack('c', secondName, second, bundle);
            var firstProgress = new RecordingProgress<SkinExtrasDownloadProgress>();
            var secondProgress = new RecordingProgress<SkinExtrasDownloadProgress>();

            var firstPath = await downloader.DownloadAsync(
                firstPack,
                firstProgress,
                CancellationToken.None);
            var secondPath = await downloader.DownloadAsync(
                secondPack,
                secondProgress,
                CancellationToken.None);

            Assert.Equal(first, await File.ReadAllBytesAsync(firstPath));
            Assert.Equal(second, await File.ReadAllBytesAsync(secondPath));
            Assert.Equal(1, handler.RequestCount);
            Assert.NotEmpty(firstProgress.Values);
            Assert.Empty(secondProgress.Values);
        }
        finally
        {
            if (Directory.Exists(cache)) Directory.Delete(cache, true);
        }
    }

    [Fact]
    public void Synchronization_journal_persists_resume_state()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"kumori-sync-journal-{Guid.NewGuid():N}");
        var id = Guid.NewGuid();
        try
        {
            SkinExtrasSyncJournalStore.Write(
                root,
                new SkinExtrasSyncJournal
                {
                    CatalogVersion = "catalog-2026.07.28.2",
                    Stage = "installing",
                    CurrentPackId = id,
                    CurrentRevision = 4,
                    OldRelativeDirectory = Path.Combine("Cursor", "Old"),
                    RemainingPackIds = [id],
                });

            var restored = Assert.IsType<SkinExtrasSyncJournal>(
                SkinExtrasSyncJournalStore.Read(root));
            Assert.Equal("installing", restored.Stage);
            Assert.Equal(id, restored.CurrentPackId);
            Assert.Equal([id], restored.RemainingPackIds);

            SkinExtrasSyncJournalStore.Clear(root);
            Assert.Null(SkinExtrasSyncJournalStore.Read(root));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Remote_registry_round_trips_managed_pack_state()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"kumori-remote-registry-{Guid.NewGuid():N}");
        var id = Guid.NewGuid();
        try
        {
            var registry = new SkinExtrasRemoteRegistry();
            registry.Installs[id] = new SkinExtrasRemoteInstall
            {
                PackId = id,
                Revision = 3,
                Fingerprint = new string('a', 64),
                RelativeDirectory = Path.Combine("osu!", "Cursor", "Pack"),
                CatalogDisplayName = "Pack",
                LastSynchronizedUtc = DateTimeOffset.UtcNow,
                Withdrawn = true,
                LocallyModified = false,
            };
            SkinExtrasRemoteRegistryStore.Write(root, registry);

            var restored = SkinExtrasRemoteRegistryStore.Read(root);
            var item = Assert.Single(restored.Installs).Value;
            Assert.Equal(3, item.Revision);
            Assert.True(item.Withdrawn);
            Assert.Equal("Pack", item.CatalogDisplayName);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static SkinExtrasRemoteCatalog CreateCatalog() => new()
    {
        CatalogVersion = "catalog-2026.07.28.1",
        GeneratedAtUtc = DateTimeOffset.UtcNow,
        SigningKeyId = SkinExtrasCatalogTrust.KeyId,
        Packs =
        [
            new SkinExtrasCatalogPack
            {
                PackId = Guid.NewGuid(),
                Revision = 1,
                ContentFingerprint = new string('a', 64),
                DisplayName = "Test Pack",
                FamilyId = "osu.cursor",
                Area = "osu!",
                Compatibility = "Lazer used",
                Package = new SkinExtrasCatalogAsset
                {
                    ReleaseTag = "catalog-2026.07.28.1",
                    AssetName = "catalog-bundle-001.zip",
                    Sha256 = new string('b', 64),
                    DownloadBytes = 4096,
                    EntryName = $"packages/{new string('a', 64)}.kextra",
                    EntrySha256 = new string('c', 64),
                    EntryDownloadBytes = 1024,
                    ExpandedBytes = 2048,
                    EntryCount = 2,
                },
            },
        ],
    };

    private static byte[] Sign(ECDsa key, byte[] catalogBytes)
    {
        var signature = key.SignData(
            catalogBytes,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence);
        return JsonSerializer.SerializeToUtf8Bytes(
            new SkinExtrasCatalogSignature
            {
                KeyId = SkinExtrasCatalogTrust.KeyId,
                Algorithm = SkinExtrasCatalogSignature.AlgorithmName,
                Signature = Convert.ToBase64String(signature),
            },
            Json);
    }

    private static void WriteEntry(ZipArchive archive, string name, byte[] bytes)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
        using var output = entry.Open();
        output.Write(bytes);
    }

    private static SkinExtrasCatalogPack BundledPack(
        char fingerprintCharacter,
        string entryName,
        byte[] entryBytes,
        byte[] bundle)
    {
        var fingerprint = new string(fingerprintCharacter, 64);
        return new SkinExtrasCatalogPack
        {
            PackId = Guid.NewGuid(),
            Revision = 1,
            ContentFingerprint = fingerprint,
            DisplayName = fingerprint,
            FamilyId = "osu.cursor",
            Area = "osu!",
            Package = new SkinExtrasCatalogAsset
            {
                ReleaseTag = "catalog-2026.07.28.3",
                AssetName = "catalog-bundle-001.zip",
                Sha256 = Convert.ToHexStringLower(SHA256.HashData(bundle)),
                DownloadBytes = bundle.LongLength,
                EntryName = entryName,
                EntrySha256 = Convert.ToHexStringLower(SHA256.HashData(entryBytes)),
                EntryDownloadBytes = entryBytes.LongLength,
                ExpandedBytes = entryBytes.LongLength,
                EntryCount = 1,
            },
        };
    }

    private sealed class StaticResponseHandler(byte[] content) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content),
                RequestMessage = request,
            });
        }
    }

    private sealed class RecordingProgress<T> : IProgress<T>
    {
        public List<T> Values { get; } = [];

        public void Report(T value) => Values.Add(value);
    }
}
