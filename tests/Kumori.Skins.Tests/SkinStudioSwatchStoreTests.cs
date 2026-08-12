using Kumori.Skins;
using Xunit;

namespace Kumori.Skins.Tests;

public sealed class SkinStudioSwatchStoreTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        $"kumori-swatches-{Guid.NewGuid():N}");

    [Fact]
    public void Swatches_are_normalized_deduplicated_and_persistent()
    {
        var store = new SkinStudioSwatchStore(root);

        store.Add("ff0080");
        store.Add("#00aaff");
        var values = store.Add("#FF0080");

        Assert.Equal(["#FF0080", "#00AAFF"], values.Select(value => value.Hex));
        Assert.Equal(
            ["#FF0080", "#00AAFF"],
            new SkinStudioSwatchStore(root).List().Select(value => value.Hex));
        Assert.Throws<InvalidDataException>(() => store.Add("#xyz"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
    }
}
