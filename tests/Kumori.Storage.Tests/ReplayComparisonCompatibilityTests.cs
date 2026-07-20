using Kumori.Core.Models;
using Kumori.Storage;
using Xunit;

namespace Kumori.Storage.Tests;

public sealed class ReplayComparisonCompatibilityTests
{
    [Fact]
    public void BpmAdjustIsAlignmentSensitiveAndCanonicalizesEquivalentSettings()
    {
        string left = ReplayComparisonCompatibility.Signature(
            "BPM",
            [new ModEntry("BPM", """{"target_bpm":174.5,"audio_mode":0,"scale_map_stats_with_bpm":true}""")]);
        string equivalent = ReplayComparisonCompatibility.Signature(
            "BPM",
            [new ModEntry("BPM", """{"audio_mode":"Nightcore","target_bpm":174.50}""")]);
        string different = ReplayComparisonCompatibility.Signature(
            "BPM",
            [new ModEntry("BPM", """{"target_bpm":180,"audio_mode":0,"scale_map_stats_with_bpm":true}""")]);
        string differentScaling = ReplayComparisonCompatibility.Signature(
            "BPM",
            [new ModEntry("BPM", """{"target_bpm":174.5,"scale_map_stats_with_bpm":false}""")]);

        Assert.Equal(left, equivalent);
        Assert.NotEqual(left, different);
        Assert.NotEqual(left, differentScaling);
        Assert.StartsWith("BPM:", left);
    }
}
