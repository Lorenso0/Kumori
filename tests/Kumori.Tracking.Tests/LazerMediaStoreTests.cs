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
    public void ResolveFiles_returns_null_when_lazer_store_is_not_present()
    {
        var result = LazerMediaStore.ResolveFiles(new TosuMediaInfo
        {
            BeatmapSetId = 10,
            GameFolder = _root,
        });

        Assert.Null(result);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
