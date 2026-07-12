using Kumori.App;
using Xunit;

namespace Kumori.App.Tests;

public sealed class PersistedReplayReconciliationServiceTests
{
    [Fact]
    public void ReplayFileTimeReadsStableFileTimeSuffix()
    {
        var expected = new DateTimeOffset(2026, 7, 12, 11, 15, 30, TimeSpan.Zero);
        string path = $"a193ddaf85ceb38b73c9baff4c80a9a0-{expected.ToFileTime()}.osr";

        Assert.Equal(expected, PersistedReplayReconciliationService.ReplayFileTime(path));
    }
}
