using Realms;
using Xunit;

namespace Kumori.Tracking.Tests;

public sealed class LazerMediaStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"kumori-lazer-store-{Guid.NewGuid():N}");

    [Fact]
    public void TryLink_creates_a_second_name_for_the_same_file()
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "source.mp3");
        var destination = Path.Combine(_root, "cache", "audio.mp3");
        File.WriteAllText(source, "audio-data");

        Assert.True(LazerMediaStore.TryLink(source, destination));
        Assert.Equal("audio-data", File.ReadAllText(destination));

        File.AppendAllText(destination, "-updated");
        Assert.Equal("audio-data-updated", File.ReadAllText(source));
    }

    [Fact]
    public void TryLink_atomically_replaces_an_existing_copy()
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "source.mp3");
        var destination = Path.Combine(_root, "cache", "audio.mp3");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllText(source, "realm-audio");
        File.WriteAllText(destination, "old-copy");

        Assert.True(LazerMediaStore.TryLink(source, destination));
        File.AppendAllText(source, "-updated");
        Assert.Equal("realm-audio-updated", File.ReadAllText(destination));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(destination)!, "*.link-*"));
    }

    [Fact]
    public void ResolveFiles_returns_null_when_lazer_store_is_not_present()
    {
        var result = LazerMediaStore.ResolveFiles(new TosuMediaInfo
        {
            BeatmapSetId = 10,
            GameFolder = _root,
        });

        Assert.Null(result);
    }

    [Fact]
    public async Task ResolveFiles_reads_named_assets_from_read_only_realm()
    {
        var result = await Task.Run(() =>
        {
            Directory.CreateDirectory(_root);
            var filesRoot = Path.Combine(_root, "files");
            Directory.CreateDirectory(filesRoot);
            const string beatmapHash = "abcdef0123456789";
            const string audioHash = "1234567890abcdef";
            var beatmapPath = Store(filesRoot, beatmapHash,
                "osu file format v14\n[General]\nAudioFilename: audio.mp3\n[Metadata]\nBeatmapID:42\n");
            var audioPath = Store(filesRoot, audioHash, "audio");

            var configuration = new RealmConfiguration(Path.Combine(_root, "client.realm"))
            {
                SchemaVersion = 51,
                Schema = new[] { typeof(LazerBeatmapSet), typeof(LazerNamedFileUsage), typeof(LazerRealmFile) },
            };
            using (var realm = Realm.GetInstance(configuration))
            {
                realm.Write(() =>
                {
                    var beatmap = realm.Add(new LazerRealmFile { Hash = beatmapHash });
                    var audio = realm.Add(new LazerRealmFile { Hash = audioHash });
                    var set = realm.Add(new LazerBeatmapSet { Id = Guid.NewGuid(), OnlineId = 99 });
                    set.Files.Add(new LazerNamedFileUsage { Filename = "Artist - Song [Extra].osu", File = beatmap });
                    set.Files.Add(new LazerNamedFileUsage { Filename = "audio.mp3", File = audio });
                });
            }

            var files = LazerMediaStore.ResolveFiles(new TosuMediaInfo
            {
                BeatmapId = 42,
                BeatmapSetId = 99,
                GameFolder = _root,
            });
            var assets = LazerStorage.ResolveBeatmapAssets(42, 99, "Extra", _root);
            return (Files: files, Assets: assets, BeatmapPath: beatmapPath, AudioPath: audioPath);
        });

        Assert.NotNull(result.Files);
        Assert.Equal(result.BeatmapPath, result.Files["Artist - Song [Extra].osu"]);
        Assert.Equal(result.AudioPath, result.Files["audio.mp3"]);
        Assert.NotNull(result.Assets);
        Assert.Equal(result.BeatmapPath, result.Assets.BeatmapPath);
        Assert.Equal(result.AudioPath, result.Assets.AudioPath);
    }

    [Fact]
    public async Task ResolveReplayFiles_refreshes_and_prioritizes_case_insensitive_hash_match()
    {
        var result = await Task.Run(() =>
        {
            Directory.CreateDirectory(_root);
            var filesRoot = Path.Combine(_root, "files");
            Directory.CreateDirectory(filesRoot);
            var date = DateTimeOffset.UtcNow;
            const string exactFileHash = "abcdef0123456789";
            const string nearbyFileHash = "1234567890abcdef";
            var exactPath = Store(filesRoot, exactFileHash, "exact replay");
            var nearbyPath = Store(filesRoot, nearbyFileHash, "nearby replay");

            var configuration = new RealmConfiguration(Path.Combine(_root, "client.realm"))
            {
                SchemaVersion = 51,
                Schema = new[] { typeof(LazerScore), typeof(LazerNamedFileUsage), typeof(LazerRealmFile) },
            };
            using (var realm = Realm.GetInstance(configuration))
            {
                realm.Write(() =>
                {
                    var exactFile = realm.Add(new LazerRealmFile { Hash = exactFileHash });
                    var nearbyFile = realm.Add(new LazerRealmFile { Hash = nearbyFileHash });
                    var exact = realm.Add(new LazerScore
                    {
                        Id = Guid.NewGuid(),
                        BeatmapHash = "BEATMAP-HASH",
                        Date = date.AddSeconds(-2),
                    });
                    exact.Files.Add(new LazerNamedFileUsage { Filename = "replay.osr", File = exactFile });
                    var nearby = realm.Add(new LazerScore
                    {
                        Id = Guid.NewGuid(),
                        BeatmapHash = "another-map",
                        Date = date,
                    });
                    nearby.Files.Add(new LazerNamedFileUsage { Filename = "replay.osr", File = nearbyFile });
                });
            }

            return (ExactPath: exactPath, NearbyPath: nearbyPath, Candidates: LazerMediaStore.ResolveReplayFiles(
                "beatmap-hash", date.AddSeconds(-10), _root, date.AddSeconds(1)));
        });

        Assert.Equal(result.ExactPath, result.Candidates[0]);
        Assert.Contains(result.NearbyPath, result.Candidates);
    }

    private static string Store(string filesRoot, string hash, string contents)
    {
        var path = Path.Combine(filesRoot, hash[..1], hash[..2], hash);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
