using Kumori.ReplayViewer;
using Xunit;

namespace Kumori.Core.Tests;

public sealed class ReplayViewerSkinImportTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        $"kumori-viewer-skin-{Guid.NewGuid():N}");

    [Fact]
    public void PrepareSkinImportPath_CopiesArchiveInsteadOfGivingImporterLibrarySource()
    {
        Directory.CreateDirectory(root);
        string source = Path.Combine(root, "persistent.osk");
        File.WriteAllBytes(source, [1, 2, 3, 4]);

        string importPath = ReplayViewerGame.PrepareSkinImportPath(source);
        try
        {
            Assert.NotEqual(source, importPath);
            Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(importPath));

            File.Delete(importPath);
            Assert.True(File.Exists(source));
        }
        finally
        {
            if (File.Exists(importPath)) File.Delete(importPath);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}
