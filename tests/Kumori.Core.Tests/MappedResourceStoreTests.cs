using Kumori.ReplayViewer;
using Xunit;

namespace Kumori.Core.Tests;

public sealed class MappedResourceStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"kumori-resource-map-{Guid.NewGuid():N}");

    [Fact]
    public void ResolvesFlattenedRealmFileForSubfolderReference()
    {
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "content-hash");
        File.WriteAllText(source, "direct realm audio");
        using var store = new MappedResourceStore(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["audio.mp3"] = source,
        });

        using var stream = store.GetStream("media/audio.mp3");
        using var reader = new StreamReader(stream!);

        Assert.Equal("direct realm audio", reader.ReadToEnd());
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
