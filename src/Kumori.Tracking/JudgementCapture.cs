using System.Text.Json;

namespace Kumori.Tracking;

/// <summary>
/// Pure port of the legacy judgement-capture decision logic
/// (DB writes removed - caller persists).
///
/// Exact semantics preserved:
/// - hit_100 / hit_50: ONE event per packet increase, Value = cumulative
///   count, payload {"delta": increase}.
/// - miss / slider_break: one event PER increment, Value = previous+n+1.
/// - First observation of hit/miss counters seeds state with the current
///   value and emits NOTHING (mid-attempt attach must not fabricate events).
/// - combo / pp_peak: seed 0; emit "new_combo"/"new_pp_peak" when exceeded.
/// - Decreases emit nothing but overwrite state (judgement reset behavior).
/// - checkpoint: full snapshot payload; cadence is the caller's concern
///   (Python emits it every _capture_events call, i.e. WRITE_INTERVAL).
/// - hit_300 is never emitted as an event; final n300 comes from the
///   attempt row (compare final totals instead).
/// </summary>
public sealed class JudgementCapture
{
    private bool _hitCountersSeeded;
    private double _hit300;
    private double _hit100;
    private double _hit50;
    private double _miss;
    private double _sliderBreak;
    private double _combo;
    private double _ppPeak;

    public sealed record CapturedEvent(string EventType, double Value, string DataJson);

    public sealed record PlayValues
    {
        public double Hit300 { get; init; }
        public double Hit100 { get; init; }
        public double Hit50 { get; init; }
        public double Miss { get; init; }
        public double Geki { get; init; }
        public double Katu { get; init; }
        public double SliderBreak { get; init; }
        public double LargeTickHit { get; init; }
        public double LargeTickMiss { get; init; }
        public double SmallTickHit { get; init; }
        public double SmallTickMiss { get; init; }
        public double SliderTailHit { get; init; }
        public double SliderTailMiss { get; init; }
        public double Combo { get; init; }
        public double PpPeak { get; init; }
        public double PpCurrent { get; init; }
        public double Accuracy { get; init; }
        public double Health { get; init; }
        public double UnstableRate { get; init; }
        public double? Progress { get; init; }
        public IReadOnlyList<double> HitErrors { get; init; } = Array.Empty<double>();
    }

    /// <summary>Reset per attempt, exactly like TosuTracker._event_state.</summary>
    public void Reset()
    {
        _hitCountersSeeded = false;
        _hit300 = _hit100 = _hit50 = _miss = _sliderBreak = _combo = _ppPeak = 0;
    }

    public IReadOnlyList<CapturedEvent> CaptureCritical(PlayValues current)
    {
        if (!_hitCountersSeeded)
        {
            SeedHitCounters(current);
            return Array.Empty<CapturedEvent>();
        }

        List<CapturedEvent>? events = null;
        AddCumulative(ref events, "hit_100", current.Hit100, _hit100);
        AddCumulative(ref events, "hit_50", current.Hit50, _hit50);
        AddPerIncrement(ref events, "miss", current.Miss, _miss);
        AddPerIncrement(ref events, "slider_break", current.SliderBreak, _sliderBreak);

        SeedHitCounters(current);
        return events is null ? Array.Empty<CapturedEvent>() : events;
    }

    public List<CapturedEvent> Capture(PlayValues current, bool includeCheckpoint = true)
    {
        var events = new List<CapturedEvent>();
        if (!_hitCountersSeeded)
        {
            SeedHitCounters(current);
        }
        else
        {
            AddCumulative(events, "hit_100", current.Hit100, _hit100);
            AddCumulative(events, "hit_50", current.Hit50, _hit50);
            AddPerIncrement(events, "miss", current.Miss, _miss);
            AddPerIncrement(events, "slider_break", current.SliderBreak, _sliderBreak);
        }

        if (current.Combo > _combo)
            events.Add(new CapturedEvent("new_combo", current.Combo, "{}"));
        if (current.PpPeak > _ppPeak)
            events.Add(new CapturedEvent("new_pp_peak", current.PpPeak, "{}"));

        if (includeCheckpoint)
        {
            events.Add(new CapturedEvent("checkpoint", current.PpCurrent,
                JsonSerializer.Serialize(new
                {
                    accuracy = current.Accuracy,
                    health = current.Health,
                    combo = current.Combo,
                    pp = current.PpCurrent,
                    misses = current.Miss,
                    slider_breaks = current.SliderBreak,
                    progress = current.Progress,
                    unstable_rate = current.UnstableRate,
                    n300 = current.Hit300,
                    n100 = current.Hit100,
                    n50 = current.Hit50,
                })));
        }

        SeedHitCounters(current);
        _combo = current.Combo;
        _ppPeak = current.PpPeak;
        return events;
    }

    private void SeedHitCounters(PlayValues current)
    {
        _hitCountersSeeded = true;
        _hit300 = current.Hit300;
        _hit100 = current.Hit100;
        _hit50 = current.Hit50;
        _miss = current.Miss;
        _sliderBreak = current.SliderBreak;
    }

    private static void AddCumulative(
        ref List<CapturedEvent>? events,
        string eventType,
        double value,
        double previous)
    {
        if (value <= previous)
            return;
        events ??= [];
        AddCumulative(events, eventType, value, previous);
    }

    private static void AddCumulative(
        List<CapturedEvent> events,
        string eventType,
        double value,
        double previous)
    {
        if (value > previous)
            events.Add(new CapturedEvent(eventType, value,
                JsonSerializer.Serialize(new { delta = (int)(value - previous) })));
    }

    private static void AddPerIncrement(
        ref List<CapturedEvent>? events,
        string eventType,
        double value,
        double previous)
    {
        if (value <= previous)
            return;
        events ??= [];
        AddPerIncrement(events, eventType, value, previous);
    }

    private static void AddPerIncrement(
        List<CapturedEvent> events,
        string eventType,
        double value,
        double previous)
    {
        for (var count = 0; count < (int)(value - previous); count++)
            events.Add(new CapturedEvent(eventType, previous + count + 1, "{}"));
    }
}
