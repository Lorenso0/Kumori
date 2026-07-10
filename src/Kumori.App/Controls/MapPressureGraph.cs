using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using Kumori.Core.Models;
using Serilog;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;

namespace Kumori.App.Controls;

/// <summary>A normalized map-pressure sample: original map time (ms) and 0..1 strain.</summary>
public readonly record struct PressurePoint(int TimeMs, double Value);

/// <summary>An in-play UR checkpoint: original map time (ms) and unstable rate.</summary>
public readonly record struct UrPoint(int TimeMs, double Ur);

/// <summary>
/// Map pressure, miss/break markers, and normalized UR — a port of
/// osu_tracking._map_performance_graph. The strain curve is computed from the
/// beatmap's hit objects (see <see cref="BuildDifficultyCurve"/>); if it is
/// unavailable the graph falls back to an event-density approximation.
/// </summary>
public sealed class MapPressureGraph : FrameworkElement
{
    public static readonly DependencyProperty DetailsProperty =
        DependencyProperty.Register(nameof(Details), typeof(AttemptDetails), typeof(MapPressureGraph),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CurveProperty =
        DependencyProperty.Register(nameof(Curve), typeof(IReadOnlyList<PressurePoint>), typeof(MapPressureGraph),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty UrSamplesProperty =
        DependencyProperty.Register(nameof(UrSamples), typeof(IReadOnlyList<UrPoint>), typeof(MapPressureGraph),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowMissProperty =
        DependencyProperty.Register(nameof(ShowMiss), typeof(bool), typeof(MapPressureGraph),
            new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowBreakProperty =
        DependencyProperty.Register(nameof(ShowBreak), typeof(bool), typeof(MapPressureGraph),
            new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowUrProperty =
        DependencyProperty.Register(nameof(ShowUr), typeof(bool), typeof(MapPressureGraph),
            new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty HoverReadoutProperty =
        DependencyProperty.Register(nameof(HoverReadout), typeof(string), typeof(MapPressureGraph),
            new FrameworkPropertyMetadata(""));

    private static readonly Brush FillBrush = Frozen("#33211A2B");
    private static readonly Pen LinePen = new(Frozen("#C78BC3"), 1);
    private static readonly Pen BaselinePen = new(Frozen("#3A3047"), 1);
    private static readonly Pen MissPen = new(Frozen("#FF4F8B"), 1.5);
    private static readonly Pen BreakPen = new(Frozen("#FFD84A"), 1.5);
    private static readonly Pen UrPen = new(Frozen("#82728E"), 1) { DashStyle = new DashStyle(new double[] { 2, 3 }, 0) };
    private static readonly Brush MissTextBrush = Frozen("#FF4F8B");
    private static readonly Brush EmptyTextBrush = Frozen("#82728E");
    private int? _hoverTimeMs;
    private (double Left, double Right, double Top, double Bottom, int End) _lastBounds;

    public AttemptDetails? Details { get => (AttemptDetails?)GetValue(DetailsProperty); set => SetValue(DetailsProperty, value); }
    public IReadOnlyList<PressurePoint>? Curve { get => (IReadOnlyList<PressurePoint>?)GetValue(CurveProperty); set => SetValue(CurveProperty, value); }
    public IReadOnlyList<UrPoint>? UrSamples { get => (IReadOnlyList<UrPoint>?)GetValue(UrSamplesProperty); set => SetValue(UrSamplesProperty, value); }
    public bool ShowMiss { get => (bool)GetValue(ShowMissProperty); set => SetValue(ShowMissProperty, value); }
    public bool ShowBreak { get => (bool)GetValue(ShowBreakProperty); set => SetValue(ShowBreakProperty, value); }
    public bool ShowUr { get => (bool)GetValue(ShowUrProperty); set => SetValue(ShowUrProperty, value); }
    public string HoverReadout { get => (string)GetValue(HoverReadoutProperty); set => SetValue(HoverReadoutProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var width = ActualWidth;
        var height = ActualHeight;
        if (width < 40 || height < 24)
        {
            return;
        }

        if (Details is not { } details)
        {
            DrawEmpty(dc, 12, height / 2);
            return;
        }

        var left = 8.0;
        var right = width - 8;
        var top = 16.0;
        var bottom = height - 18;
        var eventY = height - 8;

        var events = details.Events.Where(e => e.MapTimeMs is >= 0).ToArray();
        var missTimes = events.Where(e => e.EventType == "miss" && e.MapTimeMs is not null)
            .Select(e => (int)e.MapTimeMs!.Value).OrderBy(t => t).ToArray();
        var breakTimes = events.Where(e => e.EventType == "slider_break" && e.MapTimeMs is not null)
            .Select(e => (int)e.MapTimeMs!.Value).ToArray();

        var curve = Curve is { Count: > 1 } c ? c : FallbackCurve(events, details);
        var samples = UrSamples ?? System.Array.Empty<UrPoint>();

        var end = 1;
        foreach (var p in curve) end = System.Math.Max(end, p.TimeMs);
        foreach (var t in missTimes) end = System.Math.Max(end, t);
        foreach (var t in breakTimes) end = System.Math.Max(end, t);
        var visibleSamples = samples
            .Where(s => s.TimeMs >= 0 && s.TimeMs <= end)
            .ToArray();

        double X(int t) => left + (double)t / end * (right - left);
        double Y(double value) => bottom - value * (bottom - top);
        _lastBounds = (left, right, top, bottom, end);

        // Pressure area + line.
        if (curve.Count >= 2)
        {
            var area = new StreamGeometry();
            using (var ctx = area.Open())
            {
                ctx.BeginFigure(new Point(X(curve[0].TimeMs), bottom), isFilled: true, isClosed: true);
                foreach (var p in curve) ctx.LineTo(new Point(X(p.TimeMs), Y(p.Value)), false, false);
                ctx.LineTo(new Point(X(curve[^1].TimeMs), bottom), false, false);
            }
            area.Freeze();
            dc.DrawGeometry(FillBrush, null, area);

            var line = new StreamGeometry();
            using (var ctx = line.Open())
            {
                ctx.BeginFigure(new Point(X(curve[0].TimeMs), Y(curve[0].Value)), isFilled: false, isClosed: false);
                for (var i = 1; i < curve.Count; i++) ctx.LineTo(new Point(X(curve[i].TimeMs), Y(curve[i].Value)), true, true);
            }
            line.Freeze();
            dc.DrawGeometry(null, LinePen, line);
        }

        // Event baseline.
        dc.DrawLine(BaselinePen, new Point(left, eventY), new Point(right, eventY));

        // Slider-break markers.
        if (ShowBreak)
        {
            foreach (var t in breakTimes)
            {
                var x = X(t);
                dc.DrawLine(BreakPen, new Point(x, eventY - 5), new Point(x, eventY));
            }
        }

        // Miss markers: cluster nearby timestamps and mark the curve location.
        if (ShowMiss && missTimes.Length > 0)
        {
            var clusters = new List<List<int>>();
            foreach (var t in missTimes)
            {
                if (clusters.Count > 0 && X(t) - X(clusters[^1][^1]) <= 8)
                {
                    clusters[^1].Add(t);
                }
                else
                {
                    clusters.Add(new List<int> { t });
                }
            }
            foreach (var cluster in clusters)
            {
                var avgTime = (int)cluster.Average();
                var x = X(avgTime);
                var y = curve.Count > 0
                    ? Y(PressureAt(curve, avgTime))
                    : bottom;
                var size = cluster.Count == 1 ? 3.0 : 4.0;
                dc.DrawLine(MissPen, new Point(x - size, y - size), new Point(x + size, y + size));
                dc.DrawLine(MissPen, new Point(x - size, y + size), new Point(x + size, y - size));
                if (cluster.Count > 1)
                {
                    DrawText(dc, cluster.Count.ToString(CultureInfo.InvariantCulture), MissTextBrush, x, y - 15, center: true);
                }
            }
        }

        // Normalized UR line.
        if (ShowUr && visibleSamples.Length >= 2)
        {
            var low = visibleSamples.Min(s => s.Ur);
            var high = visibleSamples.Max(s => s.Ur);
            var span = System.Math.Max(1.0, high - low);
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                var first = visibleSamples[0];
                ctx.BeginFigure(new Point(X(first.TimeMs), Y((first.Ur - low) / span)), isFilled: false, isClosed: false);
                for (var i = 1; i < visibleSamples.Length; i++)
                {
                    ctx.LineTo(new Point(X(visibleSamples[i].TimeMs), Y((visibleSamples[i].Ur - low) / span)), true, false);
                }
            }
            geo.Freeze();
            dc.DrawGeometry(null, UrPen, geo);
        }

        if (_hoverTimeMs is { } hover)
        {
            var clamped = Math.Clamp(hover, 0, end);
            var x = X(clamped);
            var pressure = curve.Count > 0 ? PressureAt(curve, clamped) : 0;
            var y = Y(pressure);
            var hoverPen = new Pen(EmptyTextBrush, 1) { DashStyle = new DashStyle(new double[] { 2, 2 }, 0) };
            hoverPen.Freeze();
            dc.DrawLine(hoverPen, new Point(x, top), new Point(x, bottom));
            dc.DrawEllipse(FillBrush, LinePen, new Point(x, y), 3, 3);
        }
    }

    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (Details is null || _lastBounds.End <= 1)
        {
            return;
        }

        var position = e.GetPosition(this);
        var (left, right, _, _, end) = _lastBounds;
        if (position.X < left || position.X > right)
        {
            ClearHover();
            return;
        }

        var mapTime = (int)Math.Clamp((position.X - left) / Math.Max(1, right - left) * end, 0, end);
        _hoverTimeMs = mapTime;
        HoverReadout = BuildHoverSummary(mapTime, PressureAt(Curve is { Count: > 1 } c ? c : FallbackCurve(Details.Events, Details), mapTime));
        ToolTip = BuildHoverText(mapTime);
        InvalidateVisual();
    }

    protected override void OnMouseLeave(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        ClearHover();
    }

    protected override HitTestResult? HitTestCore(PointHitTestParameters hitTestParameters)
    {
        var point = hitTestParameters.HitPoint;
        return point.X >= 0
               && point.X <= ActualWidth
               && point.Y >= 0
               && point.Y <= ActualHeight
            ? new PointHitTestResult(this, point)
            : null;
    }

    /// <summary>
    /// Port of osu_tracking._difficulty_curve: a lightweight aim/speed strain
    /// curve read straight from the beatmap's [HitObjects]. Runs off the UI thread.
    /// </summary>
    public static IReadOnlyList<PressurePoint> BuildDifficultyCurve(
        string osuFilePath,
        IReadOnlyList<ModEntry>? mods = null)
    {
        try
        {
            var exact = DifficultyStrainCurveBuilder.Build(osuFilePath, mods ?? System.Array.Empty<ModEntry>());
            if (exact.Count > 0)
            {
                return exact;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Exact map pressure curve build failed for {OsuFilePath}", osuFilePath);
            // Fall through to the lightweight parser below. The inspector can
            // still render useful context even when lazer rejects a beatmap.
        }

        return BuildFallbackDifficultyCurve(osuFilePath);
    }

    /// <summary>
    /// Port of osu_tracking._difficulty_curve: a lightweight aim/speed strain
    /// curve read straight from the beatmap's [HitObjects].
    /// </summary>
    private static IReadOnlyList<PressurePoint> BuildFallbackDifficultyCurve(string osuFilePath)
    {
        var objects = new List<(int Time, double X, double Y, int Type)>();
        var inObjects = false;
        foreach (var raw in File.ReadLines(osuFilePath))
        {
            var line = raw.Trim();
            if (line == "[HitObjects]")
            {
                inObjects = true;
                continue;
            }
            if (inObjects && line.StartsWith('['))
            {
                break;
            }
            if (!inObjects || line.Length == 0 || line.StartsWith("//"))
            {
                continue;
            }
            var f = line.Split(',');
            if (f.Length < 4)
            {
                continue;
            }
            if (int.TryParse(f[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var t)
                && double.TryParse(f[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
                && double.TryParse(f[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
                && int.TryParse(f[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var type))
            {
                objects.Add((t, x, y, type));
            }
        }
        if (objects.Count < 2)
        {
            return System.Array.Empty<PressurePoint>();
        }

        const int section = 400;
        var end = objects[^1].Time;
        var peaks = new double[end / section + 2];
        var strain = 0.0;
        var prev = objects[0];
        for (var i = 1; i < objects.Count; i++)
        {
            var cur = objects[i];
            double delta = System.Math.Max(25, cur.Time - prev.Time);
            var distance = System.Math.Sqrt(System.Math.Pow(cur.X - prev.X, 2) + System.Math.Pow(cur.Y - prev.Y, 2));
            var speed = 1000.0 / delta;
            var aim = distance / 100.0 * speed;
            var sliderBonus = (cur.Type & 2) != 0 ? 0.18 : 0.0;
            var impulse = System.Math.Pow(speed, 1.22) + aim * 0.72 + sliderBonus;
            strain = strain * System.Math.Exp(-delta / 650.0) + impulse;
            var index = System.Math.Min(peaks.Length - 1, cur.Time / section);
            peaks[index] = System.Math.Max(peaks[index], strain);
            prev = cur;
        }
        for (var i = 1; i < peaks.Length; i++)
        {
            peaks[i] = System.Math.Max(peaks[i], peaks[i - 1] * 0.72);
        }
        var nonzero = peaks.Where(v => v > 0).OrderBy(v => v).ToArray();
        var scale = nonzero.Length > 0 ? nonzero[System.Math.Min(nonzero.Length - 1, (int)(nonzero.Length * 0.95))] : 1.0;
        var result = new PressurePoint[peaks.Length];
        for (var i = 0; i < peaks.Length; i++)
        {
            result[i] = new PressurePoint(i * section, System.Math.Min(1.0, peaks[i] / System.Math.Max(scale, 0.001)));
        }
        return result;
    }

    private static IReadOnlyList<PressurePoint> FallbackCurve(IReadOnlyList<JudgementEvent> events, AttemptDetails details)
    {
        var duration = MapDuration(details);
        if (duration <= 1 || events.Count == 0)
        {
            return System.Array.Empty<PressurePoint>();
        }
        const int sampleCount = 80;
        var result = new PressurePoint[sampleCount];
        for (var i = 0; i < sampleCount; i++)
        {
            var t = duration * i / (double)(sampleCount - 1);
            var local = 0.0;
            foreach (var e in events)
            {
                var distance = System.Math.Abs((e.MapTimeMs ?? 0) - t);
                if (distance > 5000)
                {
                    continue;
                }
                var weight = 1.0 - distance / 5000.0;
                local += e.EventType switch
                {
                    "miss" => 1.4 * weight,
                    "slider_break" => 1.0 * weight,
                    "hit_50" => 0.75 * weight,
                    "hit_100" => 0.5 * weight,
                    _ => 0.2 * weight,
                };
            }
            result[i] = new PressurePoint((int)t, 0.12 + System.Math.Min(local, 1.1));
        }
        var peak = System.Math.Max(0.2, result.Max(p => p.Value));
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = result[i] with { Value = result[i].Value / peak };
        }
        return result;
    }

    private static int MapDuration(AttemptDetails details)
    {
        var fromEvents = details.Events.Where(e => e.MapTimeMs is > 0)
            .Select(e => (int)e.MapTimeMs!.Value).DefaultIfEmpty(0).Max();
        var fromDuration = (int)System.Math.Round(details.DurationSeconds * 1000);
        return System.Math.Max(System.Math.Max(fromEvents, fromDuration), 1);
    }

    private string BuildHoverText(int mapTime)
    {
        var details = Details;
        var curve = Curve is { Count: > 1 } c ? c : details is null ? Array.Empty<PressurePoint>() : FallbackCurve(details.Events, details);
        var pressure = curve.Count > 0 ? PressureAt(curve, mapTime) : 0;
        var samples = UrSamples ?? Array.Empty<UrPoint>();
        var nearestUr = samples.Count > 0
            ? samples.OrderBy(s => Math.Abs(s.TimeMs - mapTime)).First()
            : (UrPoint?)null;
        var missCount = details?.Events.Count(e => e.EventType == "miss" && e.MapTimeMs is { } t && Math.Abs(t - mapTime) <= 500) ?? 0;
        var breakCount = details?.Events.Count(e => e.EventType == "slider_break" && e.MapTimeMs is { } t && Math.Abs(t - mapTime) <= 500) ?? 0;
        var seconds = mapTime / 1000;
        var parts = new List<string>
        {
            $"{seconds / 60}:{seconds % 60:00}",
            $"pressure {pressure * 100:0}%",
        };
        if (nearestUr is { } ur)
        {
            parts.Add($"{ur.Ur:0.0} UR");
        }
        if (missCount > 0)
        {
            parts.Add(missCount == 1 ? "1 miss" : $"{missCount} misses");
        }
        if (breakCount > 0)
        {
            parts.Add(breakCount == 1 ? "1 break" : $"{breakCount} breaks");
        }
        return string.Join("  -  ", parts);
    }

    private static string BuildHoverSummary(int mapTime, double pressure)
    {
        var seconds = mapTime / 1000;
        return $"{seconds / 60}:{seconds % 60:00} - pressure {pressure * 100:0}%";
    }

    private void ClearHover()
    {
        if (_hoverTimeMs is null)
        {
            return;
        }
        _hoverTimeMs = null;
        HoverReadout = "";
        ToolTip = null;
        InvalidateVisual();
    }

    private static double PressureAt(IReadOnlyList<PressurePoint> curve, int mapTime)
    {
        if (curve.Count == 0)
        {
            return 0;
        }
        if (mapTime <= curve[0].TimeMs)
        {
            return curve[0].Value;
        }
        for (var i = 1; i < curve.Count; i++)
        {
            if (mapTime > curve[i].TimeMs)
            {
                continue;
            }
            var previous = curve[i - 1];
            var current = curve[i];
            var span = Math.Max(1, current.TimeMs - previous.TimeMs);
            var ratio = (mapTime - previous.TimeMs) / (double)span;
            return previous.Value + (current.Value - previous.Value) * ratio;
        }
        return curve[^1].Value;
    }

    private void DrawText(DrawingContext dc, string text, Brush brush, double x, double y, bool center)
    {
        var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 9, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(formatted, new Point(center ? x - formatted.Width / 2 : x, y));
    }

    private void DrawEmpty(DrawingContext dc, double x, double y)
    {
        var formatted = new FormattedText("No map pressure data", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 10, EmptyTextBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(formatted, new Point(x, y - formatted.Height / 2));
    }

    private static SolidColorBrush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
        brush.Freeze();
        return brush;
    }
}
