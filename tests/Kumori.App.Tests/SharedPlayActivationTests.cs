using Kumori.Native;
using Kumori.Core.Models;
using Kumori.App.ViewModels;
using Xunit;

namespace Kumori.App.Tests;

public sealed class SharedPlayActivationTests
{
    [Fact]
    public void ImportsSessionGroupingBindingTargetsWritableProperty()
    {
        Assert.True(typeof(ImportsViewModel)
            .GetProperty(nameof(ImportsViewModel.IsGroupSessions))!
            .CanWrite);
    }

    [Fact]
    public void FileAssociationCommandQuotesExecutableAndSharedPlayPath()
    {
        Assert.Equal(
            "\"C:\\Program Files\\Kumori\\Kumori.exe\" --import \"%1\"",
            KumoriFileAssociation.BuildOpenCommand(
                @"C:\Program Files\Kumori\Kumori.exe"));
    }

    [Fact]
    public void ImportArgumentAcceptsOneAbsoluteUnicodePath()
    {
        string path = Path.Combine(
            Path.GetTempPath(), "共有 plays", "Sender - Song.kumori");

        Assert.True(App.TryParseImportArgument(
            ["--import", path],
            out string? parsed,
            out string? error));
        Assert.Equal(Path.GetFullPath(path), parsed);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("--import", "relative.kumori")]
    [InlineData("--import", @"C:\plays\share.zip")]
    public void ImportArgumentRejectsUnsupportedPaths(string option, string path)
    {
        Assert.False(App.TryParseImportArgument(
            [option, path],
            out string? parsed,
            out string? error));
        Assert.Null(parsed);
        Assert.NotNull(error);
    }

    [Fact]
    public void ImportArgumentRejectsMoreThanOneSharedPlay()
    {
        Assert.False(App.TryParseImportArgument(
            ["--import", @"C:\plays\one.kumori", "--import", @"C:\plays\two.kumori"],
            out _,
            out string? error));
        Assert.Contains("one", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ImportArgumentRejectsMissingPath()
    {
        Assert.False(App.TryParseImportArgument(
            ["--import"],
            out _,
            out string? error));
        Assert.Contains("requires", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SecondaryInstanceForwardsUnicodeImportQueuedDuringStartup()
    {
        string suffix = Guid.NewGuid().ToString("N");
        using var primary = new SingleInstance(
            $"Kumori.Tests.SingleInstance.{suffix}",
            $"Kumori.Tests.Activation.{suffix}");
        using var secondary = new SingleInstance(
            $"Kumori.Tests.SingleInstance.{suffix}",
            $"Kumori.Tests.Activation.{suffix}");
        Assert.True(primary.IsPrimaryInstance);
        Assert.False(secondary.IsPrimaryInstance);

        string expected = Path.Combine(
            Path.GetTempPath(), "共有 plays", "A replay.kumori");
        secondary.SignalPrimaryInstance(expected);

        var received = new TaskCompletionSource<SingleInstanceActivationRequest>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        primary.ListenForActivation(request => received.TrySetResult(request));

        SingleInstanceActivationRequest activation =
            await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(expected, activation.ImportPath);
    }

    [Fact]
    public void SecondaryInstanceRejectsOversizedActivationPayload()
    {
        string suffix = Guid.NewGuid().ToString("N");
        using var primary = new SingleInstance(
            $"Kumori.Tests.SingleInstance.{suffix}",
            $"Kumori.Tests.Activation.{suffix}");
        using var secondary = new SingleInstance(
            $"Kumori.Tests.SingleInstance.{suffix}",
            $"Kumori.Tests.Activation.{suffix}");

        Assert.Throws<ArgumentOutOfRangeException>(
            () => secondary.SignalPrimaryInstance(
                @"C:\" + new string('a', 40_000) + ".kumori"));
    }

    [Fact]
    public void LazerContentStoreBeatmapReceivesPortableOsuFilename()
    {
        var details = new AttemptDetails
        {
            Summary = new AttemptSummary
            {
                Artist = "Artist",
                Title = "Title",
                Difficulty = "Insane",
            },
            Mapper = "Mapper",
        };

        string logicalName = ShareMediaResolver.PortableBeatmapName(
            @"C:\osu\files\4f8b58fc969b765e72f52a2dd8f968c4",
            details);

        Assert.Equal("Artist - Title (Mapper) [Insane].osu", logicalName);
    }
}
