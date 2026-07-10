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
    private readonly Dictionary<string, double> _state = new();

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
    public void Reset() => _state.Clear();

    public List<CapturedEvent> CaptureCritical(PlayValues current)
    {
        var events = new List<CapturedEvent>();
        var values = new Dictionary<string, double>
        {
            ["hit_300"] = current.Hit300,
            ["hit_100"] = current.Hit100,
            ["hit_50"] = current.Hit50,
            ["miss"] = current.Miss,
            ["slider_break"] = current.SliderBreak,
        };

        foreach (var eventType in new[] { "hit_100", "hit_50" })
        {
            var value = values[eventType];
            var previous = _state.TryGetValue(eventType, out var p) ? p : value;
            if (value > previous)
            {
                events.Add(new CapturedEvent(
                    eventType,
                    value,
                    JsonSerializer.Serialize(new { delta = (int)(value - previous) })));
            }
            _state[eventType] = value;
        }

        foreach (var eventType in new[] { "miss", "slider_break" })
        {
            var value = values[eventType];
            var previous = _state.TryGetValue(eventType, out var p) ? p : value;
            if (value > previous)
            {
                for (var count = 0; count < (int)(value - previous); count++)
                {
                    events.Add(new CapturedEvent(eventType, previous + count + 1, "{}"));
                }
            }
            _state[eventType] = value;
        }

        _state["hit_300"] = values["hit_300"];
        return events;
    }

    public List<CapturedEvent> Capture(PlayValues current, bool includeCheckpoint = true)
    {
        var events = new List<CapturedEvent>();
        var values = new Dictionary<string, double>
        {
            ["hit_300"] = current.Hit300,
            ["hit_100"] = current.Hit100,
            ["hit_50"] = current.Hit50,
            ["miss"] = current.Miss,
            ["slider_break"] = current.SliderBreak,
            ["combo"] = current.Combo,
            ["pp_peak"] = current.PpPeak,
        };

        foreach (var eventType in new[] { "hit_100", "hit_50" })
        {
            // Python: previous = state.get(et, current[et]) â€” first sight seeds silently.
            var previous = _state.TryGetValue(eventType, out var p) ? p : values[eventType];
            if (values[eventType] > previous)
            {
                events.Add(new CapturedEvent(
                    eventType,
                    values[eventType],
                    JsonSerializer.Serialize(new { delta = (int)(values[eventType] - previous) })));
            }
        }

        foreach (var eventType in new[] { "miss", "slider_break" })
        {
            var previous = _state.TryGetValue(eventType, out var p) ? p : values[eventType];
            if (values[eventType] > previous)
            {
                for (var count = 0; count < (int)(values[eventType] - previous); count++)
                {
                    events.Add(new CapturedEvent(eventType, previous + count + 1, "{}"));
                }
            }
        }

        foreach (var eventType in new[] { "combo", "pp_peak" })
        {
            // Python: previous = state.get(et, 0) â€” peaks seed at zero.
            var previous = _state.TryGetValue(eventType, out var p) ? p : 0.0;
            if (values[eventType] > previous)
            {
                events.Add(new CapturedEvent($"new_{eventType}", values[eventType], "{}"));
            }
        }

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

        // Python: self._event_state = current â€” full overwrite, including decreases.
        _state.Clear();
        foreach (var (key, value) in values)
        {
            _state[key] = value;
        }
        return events;
    }
}
