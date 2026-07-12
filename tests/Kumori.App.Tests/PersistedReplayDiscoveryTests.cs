using Kumori.Tracking;
using Realms;
using Xunit;

namespace Kumori.App.Tests;

public sealed class PersistedReplayDiscoveryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"kumori-replay-discovery-{Guid.NewGuid():N}");

    [Fact]
    public void StableDataStore_IncludesExtensionlessInternalReplays()
    {
        var internalDirectory = Path.Combine(root, "Data", "r");
        var exportsDirectory = Path.Combine(root, "Replays");
        Directory.CreateDirectory(internalDirectory);
        Directory.CreateDirectory(exportsDirectory);
        var internalReplay = Path.Combine(internalDirectory, "0123456789abcdef");
        var exportedReplay = Path.Combine(exportsDirectory, "play.osr");
        File.WriteAllText(internalReplay, "replay");
        File.WriteAllText(exportedReplay, "replay");

        var files = PersistedReplayReconciliationService.ReplayFiles(root).ToArray();

        Assert.Contains(internalReplay, files);
        Assert.Contains(exportedReplay, files);
    }

    [Fact]
    public async Task LazerReadOnlyRealm_ResolvesPersistedReplayWithoutRefresh()
    {
        var resolved = await Task.Run(() =>
        {
            Directory.CreateDirectory(root);
            var filesRoot = Path.Combine(root, "files");
            Directory.CreateDirectory(filesRoot);
            var realmPath = Path.Combine(root, "client.realm");
            var date = DateTimeOffset.UtcNow;
            const string beatmapHash = "beatmap-hash";
            const string fileHash = "abcdef0123456789";
            var storedPath = Path.Combine(filesRoot, "a", "ab", fileHash);
            Directory.CreateDirectory(Path.GetDirectoryName(storedPath)!);
            File.WriteAllText(storedPath, "replay");

            var configuration = new RealmConfiguration(realmPath)
            {
                SchemaVersion = 51,
                Schema = new[] { typeof(LazerScore), typeof(LazerNamedFileUsage), typeof(LazerRealmFile) },
            };
            using (var realm = Realm.GetInstance(configuration))
            {
                realm.Write(() =>
                {
                    var file = realm.Add(new LazerRealmFile { Hash = fileHash });
                    var score = realm.Add(new LazerScore
                    {
                        Id = Guid.NewGuid(),
                        BeatmapHash = beatmapHash,
                        Date = date,
                    });
                    score.Files.Add(new LazerNamedFileUsage { Filename = "replay.osr", File = file });
                });
            }
            return (Expected: storedPath, Actual: LazerStorage.ResolveReplayFile(
                beatmapHash, date.AddSeconds(-1), root, date.AddSeconds(1)));
        });

        Assert.Equal(resolved.Expected, resolved.Actual);
    }

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch { }
    }
}
