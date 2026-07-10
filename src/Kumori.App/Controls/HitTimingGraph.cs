using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;

namespace Kumori.App.Controls;

/// <summary>
/// Hit-offset distribution, ported from osu_tracking._timing_histogram:
/// centred bars, a centre line, a dashed mean marker, and -/0/+ ms axis labels.
/// </summary>
public sealed class HitTimingGraph : FrameworkElement
{
    public static readonly DependencyProperty OffsetsProperty =
        DependencyProperty.Register(
            nameof(Offsets),
            typeof(IReadOnlyList<double>),
            typeof(HitTimingGraph),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty HoverReadoutProperty =
        DependencyProperty.Register(nameof(HoverReadout), typeof(string), typeof(HitTimingGraph),
            new FrameworkPropertyMetadata(""));

    private static readonly Brush BarBrush = Frozen("#D89ACF");
    private static readonly Pen CentrePen = new(Frozen("#3A3047"), 1);
    private static readonly Pen MeanPen = new(Frozen("#E94FAE"), 1) { DashStyle = new DashStyle(new double[] { 2, 3 }, 0) };
    private static readonly Pen HoverPen = new(Frozen("#82728E"), 1) { DashStyle = new DashStyle(new double[] { 2, 2 }, 0) };
    private static readonly Brush AxisBrush = Frozen("#82728E");
    private double? _hoverOffset;
    private (double Left, double Right, double BaseY, double Bound, int[] Counts) _lastState;

    public IReadOnlyList<double>? Offsets
    {
        get => (IReadOnlyList<double>?)GetValue(OffsetsProperty);
        set => SetValue(OffsetsProperty, value);
    }

    public string HoverReadout { get => (string)GetValue(HoverReadoutProperty); set => SetValue(HoverReadoutProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var width = ActualWidth;
        var height = ActualHeight;
        if (width < 40 || height < 20 || Offsets is not { Count: > 0 } offsets)
        {
            return;
        }

        const double margin = 8;
        var left = margin;
        var right = width - margin;
        var plotWidth = right - left;
        var baseY = height - 16;

        var bound = Math.Max(10.0, offsets.Max(Math.Abs));
        var buckets = (int)Math.Clamp(plotWidth / 7.0, 24, 96);
        var counts = new int[buckets];
        foreach (var value in offsets)
        {
            var index = (int)((value + bound) / (2 * bound) * (buckets - 1));
            counts[Math.Clamp(index, 0, buckets - 1)]++;
        }
        var peak = Math.Max(1, counts.Max());
        var barWidth = plotWidth / buckets;
        _lastState = (left, right, baseY, bound, counts);
        for (var i = 0; i < buckets; i++)
        {
            if (counts[i] == 0)
            {
                continue;
            }
            var barHeight = Math.Max(2, counts[i] / (double)peak * (baseY - 4));
            var x = left + i * barWidth;
            dc.DrawRectangle(BarBrush, null,
                new Rect(x + 1, baseY - barHeight, Math.Max(1, barWidth - 2), barHeight));
        }

        var centreX = left + plotWidth / 2;
        dc.DrawLine(CentrePen, new Point(centreX, 2), new Point(centreX, baseY));

        var mean = offsets.Average();
        var meanX = left + (mean + bound) / (2 * bound) * plotWidth;
        dc.DrawLine(MeanPen, new Point(meanX, 2), new Point(meanX, baseY));

        if (_hoverOffset is { } hover)
        {
            var x = left + (hover + bound) / (2 * bound) * plotWidth;
            dc.DrawLine(HoverPen, new Point(x, 2), new Point(x, baseY));
        }

        DrawText(dc, Invariant($"-{bound:0}ms"), left, baseY + 2, TextAlignment.Left);
        DrawText(dc, "0", centreX, baseY + 2, TextAlignment.Center);
        DrawText(dc, Invariant($"+{bound:0}ms"), right, baseY + 2, TextAlignment.Right);
    }

    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (Offsets is not { Count: > 0 } || _lastState.Right <= _lastState.Left || _lastState.Bound <= 0)
        {
            return;
        }

        var position = e.GetPosition(this);
        var (left, right, _, bound, counts) = _lastState;
        if (position.X < left || position.X > right)
        {
            ClearHover();
            return;
        }

        var ratio = (position.X - left) / Math.Max(1, right - left);
        var offset = ratio * 2 * bound - bound;
        _hoverOffset = offset;
        var bucket = Math.Clamp((int)(ratio * counts.Length), 0, counts.Length - 1);
        var hits = counts[bucket];
        HoverReadout = Invariant($"offset {offset:+0;-0;0}ms - {hits} hit{(hits == 1 ? "" : "s")}");
        ToolTip = HoverReadout;
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

    private void ClearHover()
    {
        if (_hoverOffset is null)
        {
            return;
        }

        _hoverOffset = null;
        HoverReadout = "";
        ToolTip = null;
        InvalidateVisual();
    }

    private void DrawText(DrawingContext dc, string text, double x, double y, TextAlignment alignment)
    {
        var formatted = new FormattedText(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            9,
            AxisBrush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        var drawX = alignment switch
        {
            TextAlignment.Center => x - formatted.Width / 2,
            TextAlignment.Right => x - formatted.Width,
            _ => x,
        };
        dc.DrawText(formatted, new Point(drawX, y));
    }

    private static string Invariant(FormattableString value) => value.ToString(CultureInfo.InvariantCulture);

    private static SolidColorBrush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
        brush.Freeze();
        return brush;
    }
}
