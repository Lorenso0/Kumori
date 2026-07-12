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
/// Hit-offset scatter plot: each hit is placed by sequence and timing offset,
/// matching the compact inspector presentation while retaining hover detail.
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

    public static readonly DependencyProperty PointBrushProperty = BrushProperty(nameof(PointBrush), "#E46AA5");
    public static readonly DependencyProperty GridBrushProperty = BrushProperty(nameof(GridBrush), "#3A3047");
    public static readonly DependencyProperty MeanBrushProperty = BrushProperty(nameof(MeanBrush), "#E94FAE");
    public static readonly DependencyProperty HoverBrushProperty = BrushProperty(nameof(HoverBrush), "#82728E");
    public static readonly DependencyProperty AxisTextBrushProperty = BrushProperty(nameof(AxisTextBrush), "#82728E");
    private double? _hoverOffset;
    private int? _hoverIndex;
    private (double Left, double Right, double Top, double Bottom, double Bound, IReadOnlyList<double> Offsets) _lastState;

    public IReadOnlyList<double>? Offsets
    {
        get => (IReadOnlyList<double>?)GetValue(OffsetsProperty);
        set => SetValue(OffsetsProperty, value);
    }

    public string HoverReadout { get => (string)GetValue(HoverReadoutProperty); set => SetValue(HoverReadoutProperty, value); }
    public Brush PointBrush { get => (Brush)GetValue(PointBrushProperty); set => SetValue(PointBrushProperty, value); }
    public Brush GridBrush { get => (Brush)GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }
    public Brush MeanBrush { get => (Brush)GetValue(MeanBrushProperty); set => SetValue(MeanBrushProperty, value); }
    public Brush HoverBrush { get => (Brush)GetValue(HoverBrushProperty); set => SetValue(HoverBrushProperty, value); }
    public Brush AxisTextBrush { get => (Brush)GetValue(AxisTextBrushProperty); set => SetValue(AxisTextBrushProperty, value); }

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
        var top = 5d;
        var bottom = height - 16;
        var plotHeight = bottom - top;

        var bound = Math.Max(10.0, offsets.Max(Math.Abs));
        _lastState = (left, right, top, bottom, bound, offsets);
        for (var i = 0; i < offsets.Count; i++)
        {
            var x = left + (offsets.Count == 1 ? plotWidth / 2 : i / (double)(offsets.Count - 1) * plotWidth);
            var y = top + (bound - Math.Clamp(offsets[i], -bound, bound)) / (2 * bound) * plotHeight;
            dc.DrawEllipse(PointBrush, null, new Point(x, y), 1.6, 1.6);
        }

        var centreY = top + plotHeight / 2;
        dc.DrawLine(new Pen(GridBrush, 1), new Point(left, centreY), new Point(right, centreY));

        var mean = offsets.Average();
        var meanY = top + (bound - mean) / (2 * bound) * plotHeight;
        dc.DrawLine(new Pen(MeanBrush, 1) { DashStyle = new DashStyle(new double[] { 2, 3 }, 0) }, new Point(left, meanY), new Point(right, meanY));

        if (_hoverIndex is { } hoverIndex)
        {
            var x = left + (offsets.Count == 1 ? plotWidth / 2 : hoverIndex / (double)(offsets.Count - 1) * plotWidth);
            dc.DrawLine(new Pen(HoverBrush, 1) { DashStyle = new DashStyle(new double[] { 2, 2 }, 0) }, new Point(x, top), new Point(x, bottom));
        }

        DrawText(dc, "0", left, bottom + 2, TextAlignment.Left);
        DrawText(dc, Invariant($"{offsets.Count:N0} hits"), right, bottom + 2, TextAlignment.Right);
    }

    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (Offsets is not { Count: > 0 } || _lastState.Right <= _lastState.Left || _lastState.Bound <= 0)
        {
            return;
        }

        var position = e.GetPosition(this);
        var (left, right, _, _, _, offsets) = _lastState;
        if (position.X < left || position.X > right)
        {
            ClearHover();
            return;
        }

        var ratio = (position.X - left) / Math.Max(1, right - left);
        var index = Math.Clamp((int)Math.Round(ratio * (offsets.Count - 1)), 0, offsets.Count - 1);
        var offset = offsets[index];
        _hoverOffset = offset;
        _hoverIndex = index;
        HoverReadout = Invariant($"hit {index + 1:N0} · {offset:+0.0;-0.0;0.0}ms");
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
        _hoverIndex = null;
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
            AxisTextBrush,
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

    private static DependencyProperty BrushProperty(string name, string fallback) =>
        DependencyProperty.Register(name, typeof(Brush), typeof(HitTimingGraph),
            new FrameworkPropertyMetadata(Frozen(fallback), FrameworkPropertyMetadataOptions.AffectsRender));
}
