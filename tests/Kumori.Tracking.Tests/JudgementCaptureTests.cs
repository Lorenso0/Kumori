using Kumori.Tracking;
using Xunit;

namespace Kumori.Tracking.Tests;

public class JudgementCaptureTests
{
    private static JudgementCapture.PlayValues Values(
        double h100 = 0, double h50 = 0, double miss = 0, double sb = 0,
        double combo = 0, double ppPeak = 0) => new()
    {
        Hit100 = h100, Hit50 = h50, Miss = miss, SliderBreak = sb,
        Combo = combo, PpPeak = ppPeak,
    };

    private static List<JudgementCapture.CapturedEvent> NonCheckpoint(
        JudgementCapture capture, JudgementCapture.PlayValues values) =>
        capture.Capture(values, includeCheckpoint: false);

    [Fact]
    public void FirstObservation_SeedsSilently()
    {
        var capture = new JudgementCapture();
        // Attaching mid-attempt with existing counts must not fabricate events.
        var events = NonCheckpoint(capture, Values(h100: 5, miss: 2));
        Assert.DoesNotContain(events, e => e.EventType is "hit_100" or "miss");
    }

    [Fact]
    public void Hit100Increase_OneCumulativeRowWithDelta()
    {
        var capture = new JudgementCapture();
        NonCheckpoint(capture, Values(h100: 3));
        var events = NonCheckpoint(capture, Values(h100: 6));

        var hit = Assert.Single(events, e => e.EventType == "hit_100");
        Assert.Equal(6, hit.Value);              // cumulative
        Assert.Contains("\"delta\":3", hit.DataJson);
    }

    [Fact]
    public void MissIncrease_PerIncrementRows()
    {
        var capture = new JudgementCapture();
        NonCheckpoint(capture, Values(miss: 1));
        var events = NonCheckpoint(capture, Values(miss: 4));

        var misses = events.Where(e => e.EventType == "miss").ToList();
        Assert.Equal(3, misses.Count);
        Assert.Equal(new double[] { 2, 3, 4 }, misses.Select(m => m.Value));
    }

    [Fact]
    public void Decrease_EmitsNothing_ButResetsState()
    {
        var capture = new JudgementCapture();
        NonCheckpoint(capture, Values(miss: 5));
        Assert.Empty(NonCheckpoint(capture, Values(miss: 0)));   // reset: no rows
        // After the reset, a rise from 0 counts from the new baseline.
        var events = NonCheckpoint(capture, Values(miss: 2));
        Assert.Equal(2, events.Count(e => e.EventType == "miss"));
    }

    [Fact]
    public void ComboAndPpPeak_SeedAtZero_EmitNewPeaks()
    {
        var capture = new JudgementCapture();
        var first = NonCheckpoint(capture, Values(combo: 50, ppPeak: 10));
        Assert.Single(first, e => e.EventType == "new_combo");
        Assert.Single(first, e => e.EventType == "new_pp_peak");

        Assert.Empty(NonCheckpoint(capture, Values(combo: 40, ppPeak: 10)));  // below peak
        var third = NonCheckpoint(capture, Values(combo: 60, ppPeak: 10));
        Assert.Single(third, e => e.EventType == "new_combo");
    }

    [Fact]
    public void Checkpoint_CarriesFullPayload()
    {
        var capture = new JudgementCapture();
        var events = capture.Capture(new JudgementCapture.PlayValues
        {
            Hit300 = 100, Hit100 = 5, Miss = 1, PpCurrent = 42.5,
            Accuracy = 97.1, Combo = 200, Progress = 0.5,
        });
        var checkpoint = Assert.Single(events, e => e.EventType == "checkpoint");
        Assert.Equal(42.5, checkpoint.Value);
        Assert.Contains("\"n300\":100", checkpoint.DataJson);
        Assert.Contains("\"progress\":0.5", checkpoint.DataJson);
    }

    [Fact]
    public void Reset_ClearsSeedsForNextAttempt()
    {
        var capture = new JudgementCapture();
        NonCheckpoint(capture, Values(h100: 10));
        capture.Reset();
        // New attempt: first packet seeds again, no events for existing counts.
        Assert.Empty(NonCheckpoint(capture, Values(h100: 4)));
    }
}
