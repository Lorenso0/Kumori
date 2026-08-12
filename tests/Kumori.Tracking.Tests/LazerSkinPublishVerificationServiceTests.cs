using System.Security.Cryptography;
using System.Text.Json;
using Kumori.Skins;
using Kumori.Tracking;
using Xunit;

namespace Kumori.Tracking.Tests;

public sealed class LazerSkinPublishVerificationServiceTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        $"kumori-publish-verification-{Guid.NewGuid():N}");

    [Fact]
    public void Imported_catalog_entry_must_match_identity_and_every_file()
    {
        var ini = "[General]\nName: Published\n"u8.ToArray();
        var image = new byte[] { 1, 2, 3, 4 };
        var files = new Dictionary<string, byte[]>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["skin.ini"] = ini,
            ["cursor.png"] = image,
        };
        var skin = new LazerSkinInfo(
            Guid.NewGuid(),
            "Published",
            "Kumori",
            files.Select(pair => new LazerSkinFileInfo(
                pair.Key,
                hash(pair.Value),
                pair.Value.LongLength)).ToArray());

        Assert.True(LazerSkinPublishVerificationService.Matches(
            skin,
            "Published",
            "Kumori",
            files));
        Assert.True(LazerSkinPublishVerificationService.Matches(
            skin with { Name = "Published [Published-20260730-120000]" },
            "Published",
            "Kumori",
            files));
        Assert.False(LazerSkinPublishVerificationService.Matches(
            skin with { Name = "Different" },
            "Published",
            "Kumori",
            files));
        Assert.False(LazerSkinPublishVerificationService.Matches(
            skin with
            {
                Files =
                [
                    .. skin.Files.Take(1),
                    skin.Files[1] with { Hash = new string('0', 64) },
                ],
            },
            "Published",
            "Kumori",
            files));
    }

    [Fact]
    public void Lazer_canonical_skin_ini_is_verified_semantically()
    {
        var expected =
            "; comment retained in the archive\r\n"
            + "[General]\r\n"
            + "Name: Published\r\n"
            + "Author: Kumori\r\n"
            + "Version: 2.7\r\n"
            + "AnimationFramerate: 24\r\n"
            + "UnknownProperty: archive-only\r\n";
        var imported =
            "[General]\n"
            + "Name: Published [Published-20260730]\n"
            + "Author: Kumori\n"
            + "AnimationFramerate: 24\n"
            + "Version: 2.7\n\n"
            + "[Colours]\n\n[Fonts]\n\n[CatchTheBeat]\n";

        Assert.True(
            LazerSkinPublishVerificationService.SkinIniMatchesAfterImport(
                System.Text.Encoding.UTF8.GetBytes(expected),
                System.Text.Encoding.UTF8.GetBytes(imported),
                "Published [Published-20260730]",
                "Kumori"));
        Assert.False(
            LazerSkinPublishVerificationService.SkinIniMatchesAfterImport(
                System.Text.Encoding.UTF8.GetBytes(expected),
                System.Text.Encoding.UTF8.GetBytes(
                    imported.Replace(
                        "Version: 2.7",
                        "Version: 1.0",
                        StringComparison.Ordinal)),
                "Published [Published-20260730]",
                "Kumori"));
    }

    [Fact]
    public void Catalog_backup_reopens_realm_and_hashes_every_referenced_blob()
    {
        var ini = "[General]\nName: Safe\n"u8.ToArray();
        var cursor = new byte[] { 9, 8, 7 };
        var fake = new FakeLivePreviewStore(
            [
                new LivePreviewSkin(
                    Guid.NewGuid(),
                    "Safe",
                    "Kumori",
                    [
                        new LivePreviewFile(
                            "skin.ini",
                            hash(ini),
                            ini.LongLength),
                        new LivePreviewFile(
                            "cursor.png",
                            hash(cursor),
                            cursor.LongLength),
                    ]),
            ],
            new Dictionary<string, byte[]>(
                StringComparer.OrdinalIgnoreCase)
            {
                [hash(ini)] = ini,
                [hash(cursor)] = cursor,
            });

        var result = new LazerCatalogBackupService(fake).CreateVerified(
            Path.Combine(root, "player"),
            Path.Combine(root, "backups"),
            "before publish");

        Assert.Equal(1, result.SkinCount);
        Assert.Equal(2, result.ReferencedBlobCount);
        Assert.True(File.Exists(result.RealmBackupPath));
        using var manifest = JsonDocument.Parse(
            File.ReadAllText(result.ManifestPath));
        Assert.Equal(
            "passed",
            manifest.RootElement.GetProperty("verification").GetString());
        Assert.Equal(
            2,
            manifest.RootElement.GetProperty("referenced_blobs")
                .GetArrayLength());
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    private static string hash(byte[] bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    private sealed class FakeLivePreviewStore : ILivePreviewStore
    {
        private readonly IReadOnlyList<LivePreviewSkin> catalog;
        private readonly IReadOnlyDictionary<string, byte[]> blobs;

        public FakeLivePreviewStore(
            IReadOnlyList<LivePreviewSkin> catalog,
            IReadOnlyDictionary<string, byte[]> blobs)
        {
            this.catalog = catalog;
            this.blobs = blobs;
        }

        public IReadOnlyList<LivePreviewSkin> LoadCatalog(string playerRoot) =>
            catalog;

        public LivePreviewSkin Import(
            string playerRoot,
            string name,
            string creator,
            IReadOnlyDictionary<string, byte[]> files) =>
            throw new NotSupportedException();

        public LivePreviewApplyResult Apply(
            string playerRoot,
            Guid skinId,
            IReadOnlyList<LivePreviewMutation> mutations) =>
            throw new NotSupportedException();

        public byte[] ReadBlob(string playerRoot, string fileHash) =>
            blobs[fileHash].ToArray();

        public string CreateRealmBackup(
            string playerRoot,
            string destinationDirectory)
        {
            Directory.CreateDirectory(destinationDirectory);
            var path = Path.Combine(destinationDirectory, "client.realm");
            File.WriteAllBytes(path, "realm snapshot"u8.ToArray());
            return path;
        }
    }
}
