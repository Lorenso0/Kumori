using System.Diagnostics;
using Kumori.Skins;

namespace Kumori.Tracking;

public sealed class LazerLivePreviewStore : ILivePreviewStore
{
    private readonly ILazerSkinRealmService realm;

    public LazerLivePreviewStore(ILazerSkinRealmService? realm = null)
    {
        this.realm = realm ?? new LazerSkinRealmService();
    }

    public IReadOnlyList<LivePreviewSkin> LoadCatalog(string playerRoot) =>
        realm.LoadCatalog(playerRoot).Skins.Select(convert).ToArray();

    public LivePreviewSkin Import(
        string playerRoot,
        string name,
        string creator,
        IReadOnlyDictionary<string, byte[]> files) =>
        convert(realm.ImportSkin(
            playerRoot,
            name,
            creator,
            files.Select(pair => new LazerSkinImportFile(pair.Key, pair.Value)).ToArray()));

    public LivePreviewApplyResult Apply(
        string playerRoot,
        Guid skinId,
        IReadOnlyList<LivePreviewMutation> mutations)
    {
        var result = realm.ApplyBatch(
            playerRoot,
            skinId,
            mutations.Select(mutation => new LazerSkinBatchMutation(
                mutation.Filename,
                mutation.Bytes,
                mutation.ExpectedHash,
                mutation.IsDeletion)).ToArray());
        return new LivePreviewApplyResult(result.Succeeded, result.Message);
    }

    public byte[] ReadBlob(string playerRoot, string hash) =>
        realm.ReadFile(playerRoot, hash);

    public string CreateRealmBackup(string playerRoot, string destinationDirectory) =>
        realm.CreateBackup(playerRoot, destinationDirectory);

    private static LivePreviewSkin convert(LazerSkinInfo skin) =>
        new(
            skin.Id,
            skin.Name,
            skin.Creator,
            skin.Files.Select(file => new LivePreviewFile(
                file.Filename,
                file.Hash,
                file.SizeBytes)).ToArray());
}

public sealed class ClosedLazerIdleProbe : IPlayerIdleProbe
{
    public PlayerIdleState Probe(string playerRoot)
    {
        try
        {
            var running = Process.GetProcesses()
                                 .Where(process =>
                                     process.ProcessName.Equals("osu!", StringComparison.OrdinalIgnoreCase)
                                     || process.ProcessName.Equals("osu", StringComparison.OrdinalIgnoreCase))
                                 .ToArray();
            foreach (var process in running)
                process.Dispose();
            return running.Length == 0
                ? new PlayerIdleState(true, "osu!lazer is closed.")
                : new PlayerIdleState(
                    false,
                    "osu!lazer is running and no trustworthy idle telemetry is available.");
        }
        catch (Exception ex)
        {
            return new PlayerIdleState(
                false,
                $"process state could not be proven ({ex.Message}).");
        }
    }
}
