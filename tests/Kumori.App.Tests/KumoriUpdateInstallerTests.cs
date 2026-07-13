using Xunit;

namespace Kumori.App.Tests;

public sealed class KumoriUpdateInstallerTests
{
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData(Hash)]
    [InlineData(Hash + "  Kumori.exe")]
    [InlineData("sha256:" + Hash)]
    public void ParseSha256_AcceptsReleaseChecksumFormats(string text)
    {
        Assert.Equal(Hash, KumoriUpdateInstaller.ParseSha256(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-checksum")]
    [InlineData("abcd")]
    [InlineData(Hash + "0")]
    public void ParseSha256_RejectsInvalidValues(string text)
    {
        Assert.Throws<InvalidDataException>(() => KumoriUpdateInstaller.ParseSha256(text));
    }

    [Fact]
    public void TryRunUpdater_IgnoresNormalApplicationArguments()
    {
        Assert.False(KumoriUpdateInstaller.TryRunUpdater(["--show-changelog"]));
    }
}
