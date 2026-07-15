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

    [Fact]
    public void HealthHandshake_RequiresAValidTokenAndWritesAnAtomicMarker()
    {
        var root = Path.Combine(Path.GetTempPath(), $"kumori-update-health-{Guid.NewGuid():N}");
        var token = Guid.NewGuid().ToString("N");
        try
        {
            Assert.False(KumoriUpdateInstaller.SignalHealthy(["--update-health-token", "invalid"], root));
            Assert.True(KumoriUpdateInstaller.SignalHealthy(["--show-changelog", "--update-health-token", token], root));
            Assert.Single(Directory.EnumerateFiles(root, $"update-health-{token}.ready"));
            Assert.Empty(Directory.EnumerateFiles(root, "*.new"));
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void RestoreBackup_DoesNotRemoveCurrentTargetBeforeReplacementIsReady()
    {
        var root = Path.Combine(Path.GetTempPath(), $"kumori-update-restore-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var backup = Path.Combine(root, "previous.exe");
        var target = Path.Combine(root, "Kumori.exe");
        try
        {
            File.WriteAllText(backup, "known-good");
            File.WriteAllText(target, "failed-update");

            KumoriUpdateInstaller.RestoreBackup(backup, target);

            Assert.Equal("known-good", File.ReadAllText(target));
            Assert.Equal("known-good", File.ReadAllText(backup));
            Assert.Empty(Directory.EnumerateFiles(root, ".Kumori.restore-*.exe"));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void WaitForHealthyStartup_UsesTheHealthMarker()
    {
        var elapsed = TimeSpan.Zero;
        var markerExists = false;

        KumoriUpdateInstaller.WaitForHealthyStartup(
            () => markerExists,
            () => false,
            () => 0,
            TimeSpan.FromSeconds(2),
            () => 123,
            _ => elapsed,
            delay =>
            {
                elapsed += delay;
                markerExists = true;
            });
    }

    [Fact]
    public void WaitForHealthyStartup_ReportsAnEarlyProcessExit()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            KumoriUpdateInstaller.WaitForHealthyStartup(
                () => false,
                () => true,
                () => 23,
                TimeSpan.FromSeconds(2),
                () => 123,
                _ => TimeSpan.Zero,
                _ => { }));

        Assert.Contains("code 23", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WaitForHealthyStartup_TimesOutUsingElapsedTime()
    {
        var elapsed = TimeSpan.Zero;

        Assert.Throws<TimeoutException>(() =>
            KumoriUpdateInstaller.WaitForHealthyStartup(
                () => false,
                () => false,
                () => 0,
                TimeSpan.FromSeconds(1),
                () => 123,
                _ => elapsed,
                delay => elapsed += delay));

        Assert.Equal(TimeSpan.FromMinutes(10), KumoriUpdateInstaller.UpdatedStartupTimeout);
    }
}
