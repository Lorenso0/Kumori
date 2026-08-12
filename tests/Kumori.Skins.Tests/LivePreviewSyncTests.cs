using Kumori.Skins;
using Xunit;

namespace Kumori.Skins.Tests;

public sealed class LivePreviewSyncTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        $"kumori-live-preview-{Guid.NewGuid():N}");

    [Fact]
    public void Permission_gate_prevents_any_player_access()
    {
        var workspace = new SkinDraftWorkspaceService(Path.Combine(root, "workspace"));
        var draft = workspace.Create("Draft", "Kumori");
        var store = new FakeStore();
        var service = createService(workspace, store);

        Assert.Throws<InvalidOperationException>(() =>
            service.Sync(draft.DraftId, Path.Combine(root, "osu"), false));
        Assert.Equal(0, store.AccessCount);
    }

    [Fact]
    public void First_sync_backs_up_referenced_data_then_imports_owned_copy()
    {
        var workspace = new SkinDraftWorkspaceService(Path.Combine(root, "workspace"));
        var draft = workspace.Create("Draft", "Kumori");
        workspace.StageFile(draft.DraftId, "cursor.png", [1, 2, 3], null, "cursor");
        var sourceId = Guid.NewGuid();
        var store = new FakeStore();
        store.Seed(sourceId, "Original skin", new Dictionary<string, byte[]>
        {
            ["cursor.png"] = [8, 8, 8],
        });
        var originalFingerprint = store.Fingerprint(sourceId);

        var result = createService(workspace, store).Sync(
            draft.DraftId,
            Path.Combine(root, "osu"),
            true);

        Assert.True(result.Created);
        Assert.StartsWith(LivePreviewSyncService.PreviewPrefix, result.SkinName);
        Assert.True(File.Exists(Path.Combine(result.BackupPath, "manifest.json")));
        Assert.True(Directory.EnumerateFiles(
            Path.Combine(result.BackupPath, "files")).Any());
        Assert.Equal(originalFingerprint, store.Fingerprint(sourceId));
        Assert.Equal(1, store.ImportCount);
        Assert.Equal(0, store.ApplyCount);
    }

    [Fact]
    public void External_preview_change_fails_closed_without_transaction()
    {
        var workspace = new SkinDraftWorkspaceService(Path.Combine(root, "workspace"));
        var draft = workspace.Create("Draft", "Kumori");
        workspace.StageFile(draft.DraftId, "cursor.png", [1], null, "cursor");
        var store = new FakeStore();
        var service = createService(workspace, store);
        var first = service.Sync(draft.DraftId, Path.Combine(root, "osu"), true);
        store.ExternalChange(first.SkinId, "cursor.png", [99]);

        var error = Assert.Throws<InvalidOperationException>(() =>
            service.Sync(draft.DraftId, Path.Combine(root, "osu"), true));

        Assert.Contains("outside Kumori", error.Message);
        Assert.Equal(0, store.ApplyCount);
    }

    [Theory]
    [InlineData("lazer is minimized")]
    [InlineData("lazer is foregrounded in menus")]
    [InlineData("lazer is in gameplay")]
    [InlineData("idle telemetry is unavailable")]
    public void Unproven_player_states_block_before_any_store_access(string detail)
    {
        var workspace = new SkinDraftWorkspaceService(Path.Combine(root, "workspace"));
        var draft = workspace.Create("Draft", "Kumori");
        var store = new FakeStore();
        var service = createService(
            workspace,
            store,
            new FixedIdleProbe(false, detail));

        var error = Assert.Throws<InvalidOperationException>(() =>
            service.Sync(draft.DraftId, Path.Combine(root, "osu"), true));

        Assert.Contains(detail, error.Message);
        Assert.Equal(0, store.AccessCount);
        Assert.Equal(0, store.ImportCount);
        Assert.Equal(0, store.ApplyCount);
    }

    [Fact]
    public void Explicit_live_edit_permission_allows_transactional_sync_while_player_runs()
    {
        var workspace = new SkinDraftWorkspaceService(Path.Combine(root, "workspace"));
        var draft = workspace.Create("Draft", "Kumori");
        workspace.StageFile(draft.DraftId, "cursor.png", [1, 2, 3], null, "cursor");
        var store = new FakeStore();
        var service = createService(
            workspace,
            store,
            new FixedIdleProbe(false, "lazer is running"));

        var result = service.Sync(
            draft.DraftId,
            Path.Combine(root, "osu"),
            liveSyncPermission: true,
            allowWhilePlayerRunning: true);

        Assert.True(result.Created);
        Assert.Equal(1, store.ImportCount);
    }

    [Fact]
    public void Repeated_sync_updates_only_the_mapped_preview_copy()
    {
        var workspace = new SkinDraftWorkspaceService(Path.Combine(root, "workspace"));
        var draft = workspace.Create("Draft", "Kumori");
        workspace.StageFile(draft.DraftId, "cursor.png", [1], null, "cursor");
        var sourceId = Guid.NewGuid();
        var store = new FakeStore();
        store.Seed(sourceId, "Original skin", new Dictionary<string, byte[]>
        {
            ["cursor.png"] = [8],
            ["hitcircle.png"] = [9],
        });
        var originalFingerprint = store.Fingerprint(sourceId);
        var service = createService(workspace, store);
        var first = service.Sync(draft.DraftId, Path.Combine(root, "osu"), true);

        workspace.StageFile(
            draft.DraftId,
            "cursor.png",
            [2],
            SkinDraftWorkspaceService.Hash([1]),
            "update cursor");
        var second = service.Sync(draft.DraftId, Path.Combine(root, "osu"), true);

        Assert.True(first.Created);
        Assert.False(second.Created);
        Assert.Equal(first.SkinId, second.SkinId);
        Assert.Equal(1, store.ImportCount);
        Assert.Equal(1, store.ApplyCount);
        Assert.Equal(originalFingerprint, store.Fingerprint(sourceId));
        Assert.NotEqual(originalFingerprint, store.Fingerprint(first.SkinId));
    }

    [Fact]
    public void Backup_blob_hash_failure_aborts_before_preview_import()
    {
        var workspace = new SkinDraftWorkspaceService(Path.Combine(root, "workspace"));
        var draft = workspace.Create("Draft", "Kumori");
        workspace.StageFile(draft.DraftId, "cursor.png", [1], null, "cursor");
        var store = new FakeStore { CorruptBlobReads = true };
        store.Seed(Guid.NewGuid(), "Original", new Dictionary<string, byte[]>
        {
            ["hitcircle.png"] = [9, 9, 9],
        });

        Assert.Throws<InvalidDataException>(() =>
            createService(workspace, store).Sync(
                draft.DraftId,
                Path.Combine(root, "osu"),
                true));

        Assert.Equal(0, store.ImportCount);
        Assert.Equal(0, store.ApplyCount);
        Assert.Empty(Directory.EnumerateFiles(
            Path.Combine(root, "backups"),
            "manifest.json",
            SearchOption.AllDirectories));
    }

    [Fact]
    public void Workspace_and_player_root_must_be_strictly_separated()
    {
        var player = Path.Combine(root, "osu");
        var workspace = new SkinDraftWorkspaceService(
            Path.Combine(player, "kumori-workspace"));
        var draft = workspace.Create("Draft", "Kumori");
        var store = new FakeStore();

        Assert.Throws<InvalidOperationException>(() =>
            createService(workspace, store).Sync(draft.DraftId, player, true));
        Assert.Equal(0, store.AccessCount);
    }

    [Fact]
    public void Unmapped_existing_preview_name_is_never_adopted()
    {
        var workspace = new SkinDraftWorkspaceService(Path.Combine(root, "workspace"));
        var draft = workspace.Create("Draft", "Kumori");
        var store = new FakeStore();
        store.Seed(
            Guid.NewGuid(),
            LivePreviewSyncService.PreviewPrefix + draft.Name,
            new Dictionary<string, byte[]> { ["cursor.png"] = [1] });

        var error = Assert.Throws<InvalidOperationException>(() =>
            createService(workspace, store).Sync(
                draft.DraftId,
                Path.Combine(root, "osu"),
                true));

        Assert.Contains("without assuming ownership", error.Message);
        Assert.Equal(0, store.ImportCount);
        Assert.Equal(0, store.ApplyCount);
    }

    [Fact]
    public void Post_import_verification_detects_any_source_skin_mutation()
    {
        var workspace = new SkinDraftWorkspaceService(Path.Combine(root, "workspace"));
        var draft = workspace.Create("Draft", "Kumori");
        workspace.StageFile(draft.DraftId, "cursor.png", [1], null, "cursor");
        var sourceId = Guid.NewGuid();
        var store = new FakeStore { MutateSourceOnImport = sourceId };
        store.Seed(sourceId, "Original", new Dictionary<string, byte[]>
        {
            ["hitcircle.png"] = [9],
        });

        var error = Assert.Throws<InvalidOperationException>(() =>
            createService(workspace, store).Sync(
                draft.DraftId,
                Path.Combine(root, "osu"),
                true));

        Assert.Contains("changed during live-preview verification", error.Message);
        Assert.Equal(1, store.ImportCount);
    }

    private LivePreviewSyncService createService(
        SkinDraftWorkspaceService workspace,
        FakeStore store,
        IPlayerIdleProbe? idleProbe = null) =>
        new(
            workspace,
            store,
            idleProbe ?? new AlwaysIdleProbe(),
            Path.Combine(root, "backups"));

    public void Dispose()
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
    }

    private sealed class AlwaysIdleProbe : IPlayerIdleProbe
    {
        public PlayerIdleState Probe(string playerRoot) =>
            new(true, "test player is closed");
    }

    private sealed class FixedIdleProbe(bool idle, string detail) : IPlayerIdleProbe
    {
        public PlayerIdleState Probe(string playerRoot) => new(idle, detail);
    }

    private sealed class FakeStore : ILivePreviewStore
    {
        private readonly Dictionary<Guid, StoredSkin> skins = [];
        private readonly Dictionary<string, byte[]> blobs =
            new(StringComparer.OrdinalIgnoreCase);

        public int AccessCount { get; private set; }
        public int ImportCount { get; private set; }
        public int ApplyCount { get; private set; }
        public bool CorruptBlobReads { get; init; }
        public Guid? MutateSourceOnImport { get; init; }

        public void Seed(Guid id, string name, IReadOnlyDictionary<string, byte[]> files)
        {
            skins[id] = makeSkin(id, name, "source", files);
        }

        public string Fingerprint(Guid id) =>
            string.Join(
                "|",
                skins[id].Files.OrderBy(pair => pair.Key)
                         .Select(pair => $"{pair.Key}:{pair.Value.Hash}"));

        public void ExternalChange(Guid id, string filename, byte[] bytes)
        {
            var hash = SkinDraftWorkspaceService.Hash(bytes);
            blobs[hash] = bytes;
            skins[id].Files[filename] = new StoredFile(hash, bytes.LongLength);
        }

        public IReadOnlyList<LivePreviewSkin> LoadCatalog(string playerRoot)
        {
            AccessCount++;
            return skins.Values.Select(convert).ToArray();
        }

        public LivePreviewSkin Import(
            string playerRoot,
            string name,
            string creator,
            IReadOnlyDictionary<string, byte[]> files)
        {
            AccessCount++;
            ImportCount++;
            var id = Guid.NewGuid();
            var skin = makeSkin(id, name, creator, files);
            skins[id] = skin;
            if (MutateSourceOnImport is { } mutate
                && skins.TryGetValue(mutate, out var source))
            {
                var changed = new byte[] { 42 };
                var changedHash = SkinDraftWorkspaceService.Hash(changed);
                blobs[changedHash] = changed;
                source.Files["hitcircle.png"] =
                    new StoredFile(changedHash, changed.LongLength);
            }
            return convert(skin);
        }

        public LivePreviewApplyResult Apply(
            string playerRoot,
            Guid skinId,
            IReadOnlyList<LivePreviewMutation> mutations)
        {
            AccessCount++;
            ApplyCount++;
            var skin = skins[skinId];
            foreach (var mutation in mutations)
            {
                skin.Files.TryGetValue(mutation.Filename, out var existing);
                if (!string.Equals(
                        existing?.Hash,
                        mutation.ExpectedHash,
                        StringComparison.OrdinalIgnoreCase))
                    return new(false, "expected hash mismatch");
            }
            foreach (var mutation in mutations)
            {
                if (mutation.IsDeletion)
                {
                    skin.Files.Remove(mutation.Filename);
                    continue;
                }
                var hash = SkinDraftWorkspaceService.Hash(mutation.Bytes);
                blobs[hash] = mutation.Bytes;
                skin.Files[mutation.Filename] =
                    new StoredFile(hash, mutation.Bytes.LongLength);
            }
            return new(true);
        }

        public byte[] ReadBlob(string playerRoot, string hash)
        {
            AccessCount++;
            var bytes = blobs[hash].ToArray();
            if (CorruptBlobReads && bytes.Length > 0)
                bytes[0] ^= 0xff;
            return bytes;
        }

        public string CreateRealmBackup(string playerRoot, string destinationDirectory)
        {
            AccessCount++;
            Directory.CreateDirectory(destinationDirectory);
            var path = Path.Combine(destinationDirectory, "client.realm");
            File.WriteAllBytes(path, [1, 2, 3, 4]);
            return path;
        }

        private StoredSkin makeSkin(
            Guid id,
            string name,
            string creator,
            IReadOnlyDictionary<string, byte[]> files)
        {
            var stored = new StoredSkin(id, name, creator);
            foreach (var (filename, bytes) in files)
            {
                var hash = SkinDraftWorkspaceService.Hash(bytes);
                blobs[hash] = bytes.ToArray();
                stored.Files[filename] = new StoredFile(hash, bytes.LongLength);
            }
            return stored;
        }

        private static LivePreviewSkin convert(StoredSkin skin) =>
            new(
                skin.Id,
                skin.Name,
                skin.Creator,
                skin.Files.Select(pair => new LivePreviewFile(
                    pair.Key,
                    pair.Value.Hash,
                    pair.Value.Size)).ToArray());

        private sealed class StoredSkin(Guid id, string name, string creator)
        {
            public Guid Id { get; } = id;
            public string Name { get; } = name;
            public string Creator { get; } = creator;
            public Dictionary<string, StoredFile> Files { get; } =
                new(StringComparer.OrdinalIgnoreCase);
        }

        private sealed record StoredFile(string Hash, long Size);
    }
}
