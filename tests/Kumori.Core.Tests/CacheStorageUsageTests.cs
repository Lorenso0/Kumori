using Xunit;
using System.Runtime.InteropServices;

namespace Kumori.Core.Tests;

public sealed class CacheStorageUsageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"kumori-cache-size-{Guid.NewGuid():N}");

    [Fact]
    public void GetAdditionalBytes_excludes_hard_linked_files()
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "source.mp3");
        var cache = Path.Combine(_root, "cache");
        var linked = Path.Combine(cache, "audio.mp3");
        File.WriteAllText(source, "shared-media");
        Directory.CreateDirectory(cache);
        Assert.True(CreateHardLink(linked, source, IntPtr.Zero));

        Assert.Equal(0, CacheStorageUsage.GetAdditionalBytes(cache));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [DllImport("Kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);
}
