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
    [InlineData("{\"result_recovery\":{\"reason\":\"tosu_gameplay_values_missing\",\"simulation_schema\":2}}", true)]
    [InlineData("{\"result_recovery\":{\"reason\":\"tosu_gameplay_values_missing\",\"simulation_schema\":3}}", false)]
    [InlineData("{\"result_recovery\":{\"simulation_schema\":2}}", true)]
    [InlineData("{\"result_recovery\":{\"simulation_schema\":3}}", false)]
    [InlineData("{}", false)]
    public void Detects_result_recoveries_that_need_current_simulation_schema(string json, bool expected)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);

        Assert.Equal(expected, PersistedReplayReconciliationService.NeedsCurrentResultSimulation(document.RootElement));
    }

    [Theory]
    [InlineData("{\"result_recovery\":{\"simulation_schema\":2}}", false)]
    [InlineData("{\"result_recovery\":{\"simulation_schema\":3}}", true)]
    [InlineData("{\"result_recovery\":{\"simulation_schema\":1}}", false)]
    [InlineData("{}", false)]
    public void Detects_current_partial_capture_simulation(string json, bool expected)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);

        Assert.Equal(expected, PersistedReplayReconciliationService.HasCurrentResultSimulation(document.RootElement));
    }

    [Theory]
    [InlineData("{\"result_recovery\":{\"reason\":\"tosu_gameplay_values_missing\"}}", true)]
    [InlineData("{\"result_recovery\":{\"reason\":\"normal_partial_simulation\"}}", false)]
    [InlineData("{}", false)]
    public void Detects_whether_partial_core_authority_belongs_to_simulation(string json, bool expected)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);

        Assert.Equal(expected, PersistedReplayReconciliationService.ResultWasMissing(document.RootElement));
    }

    [Theory]
    [InlineData("quit", "lazer_memory", 1000, 20, 10, 1, 0, 1, 11, "{}", true, false)]
    [InlineData("retried", "lazer_replay_frame", 0, 0, 0, 0, 0, 0, 11, "{}", true, true)]
    [InlineData("failed", "lazer_memory", 1000, 20, 10, 1, 0, 1, 11, "{\"result_recovery\":{\"simulation_schema\":3}}", false, false)]
    [InlineData("completed", "lazer_memory", 1000, 20, 10, 1, 0, 1, 11, "{}", false, false)]
    [InlineData("quit", "stable_memory", 1000, 20, 10, 1, 0, 1, 11, "{}", true, false)]
    [InlineData("quit", "lazer_memory", 0, 0, 0, 0, 0, 0, 0, "{}", false, false)]
    [InlineData("abandoned", "lazer_memory", 25, 3, 2, 0, 0, 0, 2, "{\"result_recovery\":{\"reason\":\"tosu_gameplay_values_missing\",\"simulation_schema\":2}}", true, false)]
    public void Partial_simulation_trigger_and_core_authority_are_explicit(
        string outcome,
        string movementSource,
        long score,
        int combo,
        int n300,
        int n100,
        int n50,
        int misses,
        int timingCount,
        string sourceJson,
        bool shouldSimulate,
        bool ownsCore)
    {
        using var source = System.Text.Json.JsonDocument.Parse(sourceJson);

        PersistedReplayReconciliationService.PartialSimulationDecision decision =
            PersistedReplayReconciliationService.DecidePartialSimulation(
                outcome, movementSource, score, combo, n300, n100, n50, misses,
                timingCount, source.RootElement);

        Assert.Equal(shouldSimulate, decision.ShouldSimulate);
        Assert.Equal(ownsCore, decision.SimulationOwnsCoreResult);
    }

    [Theory]
    [InlineData("completed", "stable_memory", "", true)]
    [InlineData("completed", "lazer_memory", "", true)]
    [InlineData("completed", "stable_memory", "known", false)]
    [InlineData("completed", "stable_replay", "", false)]
    [InlineData("quit", "stable_memory", "known", true)]
    public void Retained_simulation_is_used_when_exact_replay_matching_is_impossible(
        string outcome, string movementSource, string checksum, bool expected)
    {
        using var source = System.Text.Json.JsonDocument.Parse("{}");

        var decision = PersistedReplayReconciliationService.DecideRetainedCaptureSimulation(
            outcome, movementSource, checksum, 1000, 20, 10, 1, 0, 1, 11, source.RootElement);

        Assert.Equal(expected, decision.ShouldSimulate);
        Assert.False(decision.SimulationOwnsCoreResult);
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
