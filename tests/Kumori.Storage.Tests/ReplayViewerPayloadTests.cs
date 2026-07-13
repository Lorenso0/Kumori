using Xunit;

namespace Kumori.Storage.Tests;

public sealed class ReplayViewerPayloadTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"kumori-viewer-payload-{Guid.NewGuid():N}");

    [Fact]
    public void InstallExtractedPayload_replaces_an_incomplete_existing_runtime()
    {
        string temporary = Path.Combine(root, "temporary");
        string destination = Path.Combine(root, "runtime");
        Directory.CreateDirectory(temporary);
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(temporary, "Kumori.ReplayViewer.exe"), "new viewer");
        File.WriteAllText(Path.Combine(destination, "partial-resource.dll"), "interrupted extraction");

        ReplayViewerPayload.InstallExtractedPayload(temporary, destination);

        Assert.Equal("new viewer", File.ReadAllText(Path.Combine(destination, "Kumori.ReplayViewer.exe")));
        Assert.False(File.Exists(Path.Combine(destination, "partial-resource.dll")));
        Assert.False(Directory.Exists(temporary));
    }

    [Fact]
    public void InstallExtractedPayload_rejects_a_bundle_without_the_viewer_executable()
    {
        string temporary = Path.Combine(root, "temporary");
        string destination = Path.Combine(root, "runtime");
        Directory.CreateDirectory(temporary);
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(temporary, "resource.dll"), "invalid payload");
        File.WriteAllText(Path.Combine(destination, "existing.txt"), "preserved");

        Assert.Throws<InvalidDataException>(() =>
            ReplayViewerPayload.InstallExtractedPayload(temporary, destination));

        Assert.True(File.Exists(Path.Combine(destination, "existing.txt")));
    }

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch { }
    }
}
