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

    [Fact]
    public void NativeTools_install_requires_both_executables_and_replaces_atomically()
    {
        string temporary = Path.Combine(root, "native-temporary");
        string destination = Path.Combine(root, "native-runtime");
        Directory.CreateDirectory(temporary);
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(temporary, "Kumori.ReplayViewer.exe"), "viewer");
        File.WriteAllText(Path.Combine(temporary, "Kumori.SkinStudio.exe"), "studio");
        File.WriteAllText(Path.Combine(destination, "partial.dll"), "old");

        NativeToolsPayload.InstallExtractedPayload(temporary, destination);

        Assert.Equal("viewer", File.ReadAllText(Path.Combine(destination, "Kumori.ReplayViewer.exe")));
        Assert.Equal("studio", File.ReadAllText(Path.Combine(destination, "Kumori.SkinStudio.exe")));
        Assert.False(File.Exists(Path.Combine(destination, "partial.dll")));
        Assert.False(Directory.Exists(temporary));
    }

    [Fact]
    public void NativeTools_install_rejects_partial_bundle_without_replacing_runtime()
    {
        string temporary = Path.Combine(root, "native-temporary");
        string destination = Path.Combine(root, "native-runtime");
        Directory.CreateDirectory(temporary);
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(temporary, "Kumori.ReplayViewer.exe"), "viewer");
        File.WriteAllText(Path.Combine(destination, "existing.txt"), "preserved");

        Assert.Throws<InvalidDataException>(() =>
            NativeToolsPayload.InstallExtractedPayload(temporary, destination));

        Assert.Equal("preserved", File.ReadAllText(Path.Combine(destination, "existing.txt")));
    }

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch { }
    }
}
