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

    [Theory]
    [InlineData("{\"result_recovery\":{\"reason\":\"tosu_gameplay_values_missing\",\"simulation\":\"completed\"}}", true)]
    [InlineData("{\"result_recovery\":{\"reason\":\"tosu_gameplay_values_missing\",\"simulation_schema\":1}}", true)]
    [InlineData("{\"result_recovery\":{\"reason\":\"tosu_gameplay_values_missing\",\"simulation_schema\":2}}", false)]
    [InlineData("{}", false)]
    public void Detects_result_recoveries_that_need_current_simulation_schema(string json, bool expected)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);

        Assert.Equal(expected, PersistedReplayReconciliationService.NeedsCurrentResultSimulation(document.RootElement));
    }
}
