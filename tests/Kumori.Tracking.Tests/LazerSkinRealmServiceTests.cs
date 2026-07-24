using System.Security.Cryptography;
using Realms;
using Xunit;

namespace Kumori.Tracking.Tests;

public sealed class LazerSkinRealmServiceTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        $"kumori-lazer-skins-{Guid.NewGuid():N}");

    [Fact]
    public async Task Catalog_commit_add_conflict_and_backup_use_dynamic_realm()
    {
        await Task.Run(() =>
        {
            Directory.CreateDirectory(Path.Combine(root, "files"));
            var original = Store("original");
            var skinId = Guid.NewGuid();
            var configuration = Configuration();
            using (var realm = Realm.GetInstance(configuration))
            {
                realm.Write(() =>
                {
                    var file = realm.Add(new LazerRealmFile { Hash = original.Hash });
                    var skin = realm.Add(new LazerSkin
                    {
                        Id = skinId,
                        Name = "Test Skin",
                        Creator = "Kumori",
                    });
                    skin.Files.Add(new LazerNamedFileUsage { Filename = "cursor.png", File = file });
                });
            }

            var service = new LazerSkinRealmService();
            var catalog = service.LoadCatalog(root);
            var skinInfo = Assert.Single(catalog.Skins);
            Assert.Equal("Test Skin (Kumori)", skinInfo.DisplayName);
            Assert.Equal("original", System.Text.Encoding.UTF8.GetString(
                service.ReadFile(root, Assert.Single(skinInfo.Files).Hash)));

            var saved = service.CommitFile(
                root,
                skinId,
                "cursor.png",
                System.Text.Encoding.UTF8.GetBytes("edited"),
                original.Hash);
            Assert.Equal(LazerSkinWriteStatus.Saved, saved.Status);
            Assert.Equal("edited", System.Text.Encoding.UTF8.GetString(service.ReadFile(root, saved.Hash)));

            var conflict = service.CommitFile(
                root,
                skinId,
                "cursor.png",
                System.Text.Encoding.UTF8.GetBytes("stale"),
                original.Hash);
            Assert.Equal(LazerSkinWriteStatus.Conflict, conflict.Status);
            Assert.Equal(saved.Hash, conflict.CurrentHash);

            var added = service.AddOrReplaceFile(
                root,
                skinId,
                "skin.ini",
                System.Text.Encoding.UTF8.GetBytes("[General]\nName: Test\n"),
                expectedHash: null);
            Assert.Equal(LazerSkinWriteStatus.Added, added.Status);

            var backupDirectory = Path.Combine(root, "backups");
            var backup = service.CreateBackup(root, backupDirectory);
            Assert.True(File.Exists(backup));
            Assert.True(new FileInfo(backup).Length > 0);
        });
    }

    private RealmConfiguration Configuration() => new(Path.Combine(root, "client.realm"))
    {
        Schema = new[] { typeof(LazerSkin), typeof(LazerNamedFileUsage), typeof(LazerRealmFile) },
    };

    private (string Hash, string Path) Store(string contents)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(contents);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var path = Path.Combine(root, "files", hash[..1], hash[..2], hash);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return (hash, path);
    }

    public void Dispose()
    {
        Realm.DeleteRealm(Configuration());
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}
