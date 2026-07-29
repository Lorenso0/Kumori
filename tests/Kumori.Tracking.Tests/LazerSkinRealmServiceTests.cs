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
    public async Task Create_skin_adds_a_complete_blank_skin_to_the_dynamic_realm()
    {
        await Task.Run(() =>
        {
            Directory.CreateDirectory(Path.Combine(root, "files"));
            using (Realm.GetInstance(Configuration())) { }

            var contents = System.Text.Encoding.UTF8.GetBytes("[General]\nName: Blank\n");
            var created = new LazerSkinRealmService().CreateSkin(root, "Blank", "Kumori", contents);

            Assert.Equal("Blank (Kumori)", created.DisplayName);
            var catalog = new LazerSkinRealmService().LoadCatalog(root);
            var persisted = Assert.Single(catalog.Skins);
            Assert.Equal(created.Id, persisted.Id);
            var ini = Assert.Single(persisted.Files);
            Assert.Equal("skin.ini", ini.Filename);
            Assert.Equal(contents, new LazerSkinRealmService().ReadFile(root, ini.Hash));
        });
    }

    [Fact]
    public async Task Updating_identity_changes_the_catalog_and_skin_ini_together()
    {
        await Task.Run(() =>
        {
            Directory.CreateDirectory(Path.Combine(root, "files"));
            using (Realm.GetInstance(Configuration())) { }
            var service = new LazerSkinRealmService();
            var initial = System.Text.Encoding.UTF8.GetBytes("[General]\nName: Old\nAuthor: Old\n");
            var created = service.CreateSkin(root, "Old", "Old", initial);
            var updatedIni = System.Text.Encoding.UTF8.GetBytes("[General]\nName: New\nAuthor: Kumori\n");

            var result = service.UpdateSkinIdentity(
                root,
                created.Id,
                "New",
                "Kumori",
                updatedIni,
                created.Files.Single().Hash);

            Assert.True(result.Changed);
            var updated = Assert.Single(service.LoadCatalog(root).Skins);
            Assert.Equal("New", updated.Name);
            Assert.Equal("Kumori", updated.Creator);
            Assert.Equal(updatedIni, service.ReadFile(root, updated.Files.Single().Hash));
        });
    }

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

            var deleteConflict = service.DeleteFile(
                root,
                skinId,
                "cursor.png",
                original.Hash);
            Assert.Equal(LazerSkinWriteStatus.Conflict, deleteConflict.Status);

            var deleted = service.DeleteFile(
                root,
                skinId,
                "cursor.png",
                saved.Hash);
            Assert.Equal(LazerSkinWriteStatus.Deleted, deleted.Status);
            Assert.True(deleted.Changed);
            Assert.DoesNotContain(
                service.LoadCatalog(root).Skins.Single().Files,
                file => file.Filename == "cursor.png");
            Assert.Equal(
                "edited",
                System.Text.Encoding.UTF8.GetString(service.ReadFile(root, saved.Hash)));

            var backupDirectory = Path.Combine(root, "backups");
            var backup = service.CreateBackup(root, backupDirectory);
            Assert.True(File.Exists(backup));
            Assert.True(new FileInfo(backup).Length > 0);
        });
    }

    [Fact]
    public async Task Batch_apply_is_atomic_on_conflict_and_commits_all_mutations_together()
    {
        await Task.Run(() =>
        {
            Directory.CreateDirectory(Path.Combine(root, "files"));
            var cursor = Store("cursor-old");
            var trail = Store("trail-old");
            var skinId = Guid.NewGuid();
            using (var realm = Realm.GetInstance(Configuration()))
            {
                realm.Write(() =>
                {
                    var cursorFile = realm.Add(new LazerRealmFile { Hash = cursor.Hash });
                    var trailFile = realm.Add(new LazerRealmFile { Hash = trail.Hash });
                    var skin = realm.Add(new LazerSkin { Id = skinId, Name = "Batch" });
                    skin.Files.Add(new LazerNamedFileUsage { Filename = "cursor.png", File = cursorFile });
                    skin.Files.Add(new LazerNamedFileUsage { Filename = "cursortrail.png", File = trailFile });
                });
            }
            var service = new LazerSkinRealmService();
            var rejected = service.ApplyBatch(
                root,
                skinId,
                [
                    new("cursor.png", System.Text.Encoding.UTF8.GetBytes("cursor-new"), cursor.Hash),
                    new("cursortrail.png", [], new string('f', 64), IsDeletion: true),
                ]);
            Assert.False(rejected.Succeeded);
            var unchanged = service.LoadCatalog(root).Skins.Single();
            Assert.Equal(cursor.Hash, unchanged.Files.Single(file => file.Filename == "cursor.png").Hash);
            Assert.Contains(unchanged.Files, file => file.Filename == "cursortrail.png");

            var applied = service.ApplyBatch(
                root,
                skinId,
                [
                    new("cursor.png", System.Text.Encoding.UTF8.GetBytes("cursor-new"), cursor.Hash),
                    new("cursortrail.png", [], trail.Hash, IsDeletion: true),
                    new("cursormiddle.png", System.Text.Encoding.UTF8.GetBytes("middle"), null),
                ]);
            Assert.True(applied.Succeeded);
            Assert.Equal(3, applied.Results.Count);
            var changed = service.LoadCatalog(root).Skins.Single();
            Assert.Equal(2, changed.Files.Count);
            Assert.DoesNotContain(changed.Files, file => file.Filename == "cursortrail.png");
            Assert.Contains(changed.Files, file => file.Filename == "cursormiddle.png");
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
