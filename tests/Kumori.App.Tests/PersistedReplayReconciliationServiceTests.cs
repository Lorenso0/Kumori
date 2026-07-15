using Kumori.App;
using Kumori.Storage;
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
    [InlineData("{\"result_recovery\":{\"reason\":\"tosu_gameplay_values_missing\",\"simulation_schema\":3}}", false)]
    [InlineData("{}", false)]
    public void Detects_result_recoveries_that_need_current_simulation_schema(string json, bool expected)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);

        Assert.Equal(expected, PersistedReplayReconciliationService.NeedsCurrentResultSimulation(document.RootElement));
    }

    [Theory]
    [InlineData("{\"result_recovery\":{\"simulated_fields\":[\"accuracy\"]}}", true)]
    [InlineData("{\"result_recovery\":{\"simulated_fields\":[\"accuracy\"],\"accuracy_source\":\"replay_or_tosu\"}}", false)]
    [InlineData("{\"result_recovery\":{\"simulated_fields\":[\"misses\"]}}", false)]
    [InlineData("{}", false)]
    public void Detects_only_legacy_simulated_accuracy_that_needs_authority_repair(string json, bool expected)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);

        Assert.Equal(expected, ReplayResultRecoveryStore.NeedsAccuracyAuthorityRepair(document.RootElement));
    }

    [Theory]
    [InlineData("{\"result_recovery\":{\"reason\":\"tosu_gameplay_values_missing\",\"fields\":[\"300\",\"100\",\"misses\"]}}", 100, 13, 0, 9, true)]
    [InlineData("{\"result_recovery\":{\"reason\":\"tosu_gameplay_values_missing\",\"fields\":[\"100\"],\"accuracy_source\":\"replay_or_tosu\"}}", 100, 13, 0, 0, true)]
    [InlineData("{\"result_recovery\":{\"reason\":\"tosu_gameplay_values_missing\",\"fields\":[\"100\"]}}", 97.1, 13, 0, 0, false)]
    public void Detects_perfect_placeholder_from_earlier_replay_recovery(
        string json, double accuracy, int n100, int n50, int misses, bool expected)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);

        Assert.Equal(expected, ReplayResultRecoveryStore.NeedsAccuracyAuthorityRepair(
            document.RootElement, accuracy, n100, n50, misses));
    }
}
