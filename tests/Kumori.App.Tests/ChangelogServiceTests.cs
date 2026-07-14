using Xunit;

namespace Kumori.App.Tests;

public sealed class ChangelogServiceTests
{
    [Fact]
    public void Parse_SupportsGroupedAndMajorChanges()
    {
        var releases = ChangelogService.Parse(
            """
            [{"version":"1.2.3","date":"2026-07-13","major":["Big change"],"features":["Feature"],"improvements":["Better"],"fixes":["Fixed"]}]
            """);

        var release = Assert.Single(releases);
        Assert.Equal("13/07/2026", release.DisplayDate);
        Assert.Equal("Big change", Assert.Single(release.Major));
        Assert.Equal("Feature", Assert.Single(release.Features));
        Assert.Equal("Better", Assert.Single(release.Improvements));
        Assert.Equal("Fixed", Assert.Single(release.Fixes));
    }

    [Fact]
    public void Parse_RejectsEntriesWithoutVersion()
    {
        Assert.Throws<InvalidDataException>(() => ChangelogService.Parse("[{\"features\":[\"x\"]}]"));
    }

    [Fact]
    public void DisplayDate_DoesNotLeakInvalidStoredDate()
    {
        var release = Assert.Single(ChangelogService.Parse("[{\"version\":\"1.2.3\",\"date\":\"2026/07/13\"}]"));

        Assert.Equal("Unknown date", release.DisplayDate);
    }
}
