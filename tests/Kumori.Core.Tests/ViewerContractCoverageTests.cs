using Kumori.ReplayViewer;
using Xunit;

namespace Kumori.Core.Tests;

public sealed class ViewerContractCoverageTests
{
    [Fact]
    public void CompletedAttemptWithTruncatedFramesIsBoundedByCapturedEvidence()
    {
        ViewerContract contract = Contract("completed", Sample(46_017));

        Assert.Equal(46_017, contract.ResolveAnalysisCoverageEnd(85_383));
        Assert.Equal(46_017, contract.ResolveReplayPlaybackEnd(85_383));
    }

    [Fact]
    public void CompletedAttemptWhoseFramesReachMapEndRemainsUnrestricted()
    {
        ViewerContract contract = Contract("completed", Sample(84_500));

        Assert.Null(contract.ResolveAnalysisCoverageEnd(85_383));
        Assert.Null(contract.ResolveReplayPlaybackEnd(85_383));
    }

    [Fact]
    public void PartialAttemptIsBoundedEvenWhenCaptureReachesMapEnd()
    {
        ViewerContract contract = Contract("quit", Sample(85_383));

        Assert.Equal(85_383, contract.ResolveAnalysisCoverageEnd(85_383));
        Assert.Equal(85_383, contract.ResolveReplayPlaybackEnd(85_383));
    }

    [Fact]
    public void PausedTailDoesNotExtendReplayAndCapturedJudgementCanExtendAnalysis()
    {
        ViewerContract contract = Contract(
            "quit",
            Sample(12_000),
            Sample(80_000, flags: 0x02)) with
        {
            JudgementEvents =
            [
                new JudgementEventContract { MapTimeMs = 12_150, Kind = "miss" },
            ],
        };

        Assert.Equal(12_150, contract.ResolveAnalysisCoverageEnd(85_383));
        Assert.Equal(12_000, contract.ResolveReplayPlaybackEnd(85_383));
    }

    private static ViewerContract Contract(string outcome, params MovementSample[] samples) => new()
    {
        ContractVersion = ViewerContract.CurrentVersion,
        Attempt = new AttemptContract { Outcome = outcome },
        BeatmapPath = "unused.osu",
        Samples = [.. samples],
    };

    private static MovementSample Sample(double time, int flags = 0) => new()
    {
        MapTimeMs = time,
        Flags = flags,
    };
}
