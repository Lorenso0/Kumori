using System.Text;
using Kumori.Tracking;
using Xunit;

namespace Kumori.Tracking.Tests;

public sealed class TosuMediaCacheTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        $"kumori-media-cache-tests-{Guid.NewGuid():N}");

    [Fact]
    public void CopyStreamIntoCache_CancellationPreservesExistingFileAndCleansTemporaryFile()
    {
        Directory.CreateDirectory(root);
        var destination = Path.Combine(root, "audio.mp3");
        File.WriteAllText(destination, "existing");
        using var cancellation = new CancellationTokenSource();
        using var input = new CancellingReadStream(new byte[128 * 1024], cancellation);

        Assert.Throws<OperationCanceledException>(() =>
            TosuMediaCache.CopyStreamIntoCache(input, destination, cancellation.Token));

        Assert.Equal("existing", File.ReadAllText(destination));
        Assert.Empty(Directory.EnumerateFiles(root, "*.tmp"));
    }

    [Fact]
    public void CopyStreamIntoCache_CommitsCompleteReplacementAtomically()
    {
        Directory.CreateDirectory(root);
        var destination = Path.Combine(root, "beatmap.osu");
        File.WriteAllText(destination, "old");
        using var input = new MemoryStream(Encoding.UTF8.GetBytes("complete replacement"));

        TosuMediaCache.CopyStreamIntoCache(input, destination, CancellationToken.None);

        Assert.Equal("complete replacement", File.ReadAllText(destination));
        Assert.Empty(Directory.EnumerateFiles(root, "*.tmp"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
    }

    private sealed class CancellingReadStream : MemoryStream
    {
        private readonly CancellationTokenSource cancellation;

        public CancellingReadStream(byte[] buffer, CancellationTokenSource cancellation)
            : base(buffer)
        {
            this.cancellation = cancellation;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = base.Read(buffer, offset, count);
            cancellation.Cancel();
            return read;
        }
    }
}
