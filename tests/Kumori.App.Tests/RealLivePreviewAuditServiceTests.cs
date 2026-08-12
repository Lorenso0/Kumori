using System.Text.Json;
using Kumori.Skins;
using Kumori.Tracking;
using Xunit;

namespace Kumori.App.Tests;

public sealed class RealLivePreviewAuditServiceTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        $"kumori-real-live-audit-{Guid.NewGuid():N}");

    [Fact]
    public void Verified_backup_and_unchanged_catalog_pass_read_only_audit()
    {
        var fixture = createFixture();

        var result = new RealLivePreviewAuditService(fixture.Store)
            .Verify(fixture.DraftManifestPath);

        Assert.Equal("passed", result.Verification);
        Assert.Equal(1, result.SourceSkinCount);
        Assert.Equal(1, result.ReferencedBlobCount);
        Assert.Equal(2, result.CurrentSkinCount);
        Assert.Equal(fixture.PreviewId, result.PreviewSkinId);
        Assert.Equal(1, fixture.Store.CatalogReads);
        Assert.Equal(1, fixture.Store.BlobReads);
        Assert.Equal(0, fixture.Store.WriteAttempts);
    }

    [Fact]
    public void Source_skin_mutation_fails_the_independent_audit()
    {
        var fixture = createFixture();
        fixture.Store.ReplaceSource([7, 7, 7]);

        var error = Assert.Throws<InvalidDataException>(() =>
            new RealLivePreviewAuditService(fixture.Store)
                .Verify(fixture.DraftManifestPath));

        Assert.Contains("source skin", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, fixture.Store.WriteAttempts);
    }

    [Fact]
    public void Corrupt_backup_blob_fails_before_real_store_is_read()
    {
        var fixture = createFixture();
        File.WriteAllBytes(fixture.BackupBlobPath, [99]);

        var error = Assert.Throws<InvalidDataException>(() =>
            new RealLivePreviewAuditService(fixture.Store)
                .Verify(fixture.DraftManifestPath));

        Assert.Contains("backup blob", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, fixture.Store.CatalogReads);
        Assert.Equal(0, fixture.Store.WriteAttempts);
    }

    [Fact]
    public void Fingerprint_is_stable_across_file_order()
    {
        var id = Guid.NewGuid();
        var files = new[]
        {
            new LivePreviewFile("Cursor.png", new string('a', 64), 1),
            new LivePreviewFile("hitcircle.png", new string('b', 64), 2),
        };

        var first = LivePreviewSyncService.Fingerprint(
            new LivePreviewSkin(id, "skin", "creator", files));
        var second = LivePreviewSyncService.Fingerprint(
            new LivePreviewSkin(id, "skin", "creator", files.Reverse().ToArray()));

        Assert.Equal(first, second);
    }

    private AuditFixture createFixture()
    {
        Directory.CreateDirectory(root);
        var backup = Path.Combine(root, "backup");
        var realmDirectory = Path.Combine(backup, "realm");
        var filesDirectory = Path.Combine(backup, "files");
        Directory.CreateDirectory(realmDirectory);
        Directory.CreateDirectory(filesDirectory);

        var sourceId = Guid.NewGuid();
        var previewId = Guid.NewGuid();
        var sourceBytes = new byte[] { 1, 2, 3 };
        var previewBytes = new byte[] { 4, 5 };
        var sourceHash = SkinDraftWorkspaceService.Hash(sourceBytes);
        var previewHash = SkinDraftWorkspaceService.Hash(previewBytes);
        var source = new LivePreviewSkin(
            sourceId,
            "Original",
            "source creator",
            [new LivePreviewFile("cursor.png", sourceHash, sourceBytes.LongLength)]);
        var preview = new LivePreviewSkin(
            previewId,
            LivePreviewSyncService.PreviewPrefix + "Draft",
            "Kumori",
            [new LivePreviewFile("hitcircle.png", previewHash, previewBytes.LongLength)]);
        var store = new AuditStore(source, preview, sourceBytes, previewBytes);

        var realmPath = Path.Combine(realmDirectory, "client.realm");
        File.WriteAllBytes(realmPath, [8, 9, 10]);
        var realmHash = SkinDraftWorkspaceService.Hash(File.ReadAllBytes(realmPath));
        var backupBlobPath = Path.Combine(filesDirectory, sourceHash);
        File.WriteAllBytes(backupBlobPath, sourceBytes);
        var playerRoot = Path.Combine(root, "player");
        Directory.CreateDirectory(playerRoot);

        File.WriteAllText(
            Path.Combine(backup, "manifest.json"),
            JsonSerializer.Serialize(
                new
                {
                    verification = "passed",
                    player_root = playerRoot,
                    realm_backup = "realm/client.realm",
                    realm_sha256 = realmHash,
                    skins = new[]
                    {
                        new
                        {
                            id = source.Id,
                            name = source.Name,
                            creator = source.Creator,
                            fingerprint = LivePreviewSyncService.Fingerprint(source),
                            files = source.Files.Select(file => new
                            {
                                filename = file.Filename,
                                hash = file.Hash,
                                size = file.SizeBytes,
                            }),
                        },
                    },
                    referenced_blobs = new[]
                    {
                        new { hash = sourceHash, size = sourceBytes.LongLength },
                    },
                },
                SkinStudioLaunchContract.JsonOptions));

        var draftManifestPath = Path.Combine(root, "draft.json");
        File.WriteAllText(
            draftManifestPath,
            JsonSerializer.Serialize(
                new
                {
                    live_preview_skin_id = preview.Id,
                    live_preview_fingerprint =
                        LivePreviewSyncService.Fingerprint(preview),
                    live_preview_backup_path = backup,
                },
                SkinStudioLaunchContract.JsonOptions));

        return new AuditFixture(
            store,
            draftManifestPath,
            backupBlobPath,
            previewId);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
        catch
        {
        }
    }

    private sealed record AuditFixture(
        AuditStore Store,
        string DraftManifestPath,
        string BackupBlobPath,
        Guid PreviewId);

    private sealed class AuditStore : ILivePreviewStore
    {
        private readonly Guid sourceId;
        private readonly Guid previewId;
        private readonly LivePreviewSkin preview;
        private readonly Dictionary<string, byte[]> blobs =
            new(StringComparer.OrdinalIgnoreCase);
        private LivePreviewSkin source;

        public AuditStore(
            LivePreviewSkin source,
            LivePreviewSkin preview,
            byte[] sourceBytes,
            byte[] previewBytes)
        {
            this.source = source;
            this.preview = preview;
            sourceId = source.Id;
            previewId = preview.Id;
            blobs[source.Files.Single().Hash] = sourceBytes;
            blobs[preview.Files.Single().Hash] = previewBytes;
        }

        public int CatalogReads { get; private set; }
        public int BlobReads { get; private set; }
        public int WriteAttempts { get; private set; }

        public void ReplaceSource(byte[] bytes)
        {
            var hash = SkinDraftWorkspaceService.Hash(bytes);
            blobs[hash] = bytes;
            source = source with
            {
                Files =
                [
                    new LivePreviewFile(
                        source.Files.Single().Filename,
                        hash,
                        bytes.LongLength),
                ],
            };
        }

        public IReadOnlyList<LivePreviewSkin> LoadCatalog(string playerRoot)
        {
            CatalogReads++;
            return [source, preview];
        }

        public byte[] ReadBlob(string playerRoot, string hash)
        {
            BlobReads++;
            return blobs[hash].ToArray();
        }

        public LivePreviewSkin Import(
            string playerRoot,
            string name,
            string creator,
            IReadOnlyDictionary<string, byte[]> files)
        {
            WriteAttempts++;
            throw new NotSupportedException();
        }

        public LivePreviewApplyResult Apply(
            string playerRoot,
            Guid skinId,
            IReadOnlyList<LivePreviewMutation> mutations)
        {
            WriteAttempts++;
            throw new NotSupportedException();
        }

        public string CreateRealmBackup(
            string playerRoot,
            string destinationDirectory)
        {
            WriteAttempts++;
            throw new NotSupportedException();
        }
    }
}
