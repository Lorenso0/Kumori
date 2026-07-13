using System.Text.Json;
using Kumori.Core;
using Xunit;

namespace Kumori.Core.Tests;

public sealed class LogRetentionPolicyTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"kumori-log-policy-{Guid.NewGuid():N}");

    [Fact]
    public void ReadConfiguredDays_IsReadOnlyAndSupportsUnifiedAndLegacyNames()
    {
        var settings = Path.Combine(root, "config", "settings.v2.json");
        Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
        File.WriteAllText(settings, JsonSerializer.Serialize(new { Developer = new { LogRetentionDays = 14 } }));
        Assert.Equal(14, LogRetentionPolicy.ReadConfiguredDays(settings));

        File.WriteAllText(settings, JsonSerializer.Serialize(new { Developer = new { CacheActivityLogRotationDays = 9 } }));
        Assert.Equal(9, LogRetentionPolicy.ReadConfiguredDays(settings));

        var missing = Path.Combine(root, "missing.json");
        Assert.Equal(AppPaths.DefaultLogRetentionDays, LogRetentionPolicy.ReadConfiguredDays(missing));
        Assert.False(File.Exists(missing));
    }

    [Fact]
    public void AppendWithSizeRotation_BoundsPlainTextLogs()
    {
        var log = Path.Combine(root, "logs", "app", "crash-net.log");
        LogRetentionPolicy.AppendWithSizeRotation(log, "12345", maxBytes: 8,
            now: new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero));
        LogRetentionPolicy.AppendWithSizeRotation(log, "6789", maxBytes: 8,
            now: new DateTimeOffset(2026, 7, 13, 12, 0, 1, TimeSpan.Zero));

        Assert.Equal("6789", File.ReadAllText(log));
        Assert.Equal("12345", File.ReadAllText(Assert.Single(Directory.EnumerateFiles(
            Path.GetDirectoryName(log)!, "crash-net-*.log"))));
    }

    [Fact]
    public void AppendWithSizeRotation_RotatesLongLivedActiveLog()
    {
        var log = Path.Combine(root, "logs", "app", "crash-net.log");
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        File.WriteAllText(log, "old");
        File.SetCreationTimeUtc(log, new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc));

        LogRetentionPolicy.AppendWithSizeRotation(
            log,
            "new",
            maxAgeDays: 7,
            now: new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero));

        Assert.Equal("new", File.ReadAllText(log));
        Assert.Equal("old", File.ReadAllText(Assert.Single(Directory.EnumerateFiles(
            Path.GetDirectoryName(log)!, "crash-net-*.log"))));
    }

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch { }
    }
}
