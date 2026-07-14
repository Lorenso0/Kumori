using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using MediaBrush = System.Windows.Media.Brush;
using MediaBrushes = System.Windows.Media.Brushes;
using MediaPen = System.Windows.Media.Pen;
using WpfSize = System.Windows.Size;
using WpfPoint = System.Windows.Point;

namespace Kumori.App.Controls;

public sealed class RankProgressBadge : FrameworkElement
{
    public static readonly DependencyProperty RankTextProperty =
        DependencyProperty.Register(nameof(RankText), typeof(string), typeof(RankProgressBadge), new FrameworkPropertyMetadata("-", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ProgressProperty =
        DependencyProperty.Register(nameof(Progress), typeof(double), typeof(RankProgressBadge), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AccentBrushProperty =
        DependencyProperty.Register(nameof(AccentBrush), typeof(MediaBrush), typeof(RankProgressBadge), new FrameworkPropertyMetadata(MediaBrushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TextBrushProperty =
        DependencyProperty.Register(nameof(TextBrush), typeof(MediaBrush), typeof(RankProgressBadge), new FrameworkPropertyMetadata(MediaBrushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty =
        DependencyProperty.Register(nameof(TrackBrush), typeof(MediaBrush), typeof(RankProgressBadge), new FrameworkPropertyMetadata(MediaBrushes.DimGray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BackgroundBrushProperty =
        DependencyProperty.Register(nameof(BackgroundBrush), typeof(MediaBrush), typeof(RankProgressBadge), new FrameworkPropertyMetadata(MediaBrushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));

    public string RankText
    {
        get => (string)GetValue(RankTextProperty);
        set => SetValue(RankTextProperty, value);
    }

    public double Progress
    {
        get => (double)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    public MediaBrush AccentBrush
    {
        get => (MediaBrush)GetValue(AccentBrushProperty);
        set => SetValue(AccentBrushProperty, value);
    }

    public MediaBrush TextBrush
    {
        get => (MediaBrush)GetValue(TextBrushProperty);
        set => SetValue(TextBrushProperty, value);
    }

    public MediaBrush TrackBrush
    {
        get => (MediaBrush)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public MediaBrush BackgroundBrush
    {
        get => (MediaBrush)GetValue(BackgroundBrushProperty);
        set => SetValue(BackgroundBrushProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        var side = Math.Min(ActualWidth, ActualHeight);
        if (side <= 0)
        {
            return;
        }

        var center = new WpfPoint(ActualWidth / 2d, ActualHeight / 2d);
        var radius = Math.Max(0, side / 2d - 2d);
        var progress = Math.Clamp(Progress, 0d, 1d);
        drawingContext.DrawEllipse(BackgroundBrush, null, center, radius - 1.4d, radius - 1.4d);

        var outerTrack = new MediaPen(TrackBrush, 4.4d)
        {
            StartLineCap = PenLineCap.Flat,
            EndLineCap = PenLineCap.Flat
        };
        drawingContext.DrawEllipse(null, outerTrack, center, radius - 2.2d, radius - 2.2d);

        if (progress > 0.002d)
        {
            var outerAccent = new MediaPen(AccentBrush, 4.4d)
            {
                StartLineCap = PenLineCap.Flat,
                EndLineCap = PenLineCap.Flat
            };
            drawingContext.DrawGeometry(null, outerAccent, createArcGeometry(center, radius - 2.2d, progress));
        }

        var innerRadius = Math.Max(2d, radius - 9.1d);
        var separator = new MediaPen(BackgroundBrush, 4.4d);
        drawingContext.DrawEllipse(null, separator, center, innerRadius, innerRadius);

        foreach (var segment in rankSegments)
        {
            drawSegment(drawingContext, center, innerRadius, segment.Start, segment.End, progress, WithOpacity(AccentBrush, segment.Opacity));
        }

        drawingContext.DrawEllipse(null, new MediaPen(TrackBrush, 1.2d), center, innerRadius - 3.1d, innerRadius - 3.1d);

        var text = string.IsNullOrWhiteSpace(RankText) ? "-" : RankText;
        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI Semibold"),
            text.Length > 1 ? 9.5d : 13d,
            TextBrush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        drawingContext.DrawText(formatted, new WpfPoint(center.X - formatted.Width / 2d, center.Y - formatted.Height / 2d));
    }

    private static readonly (double Start, double End, double Opacity)[] rankSegments =
    [
        (0.00d, 0.70d, 0.40d),
        (0.70d, 0.80d, 0.52d),
        (0.80d, 0.90d, 0.64d),
        (0.90d, 0.95d, 0.76d),
        (0.95d, 0.99d, 0.88d),
        (0.99d, 1.00d, 1.00d),
    ];

    private static void drawSegment(DrawingContext drawingContext, WpfPoint center, double radius, double start, double end, double progress, MediaBrush brush)
    {
        const double gradeSpacing = 2d / 360d;
        var segmentStart = Math.Min(1d, start + gradeSpacing * 0.5d);
        var segmentEnd = Math.Min(progress, end - gradeSpacing * 0.5d);
        if (segmentEnd <= segmentStart)
        {
            return;
        }

        var pen = new MediaPen(brush, 2.6d)
        {
            StartLineCap = PenLineCap.Flat,
            EndLineCap = PenLineCap.Flat
        };
        drawingContext.DrawGeometry(null, pen, createArcSegmentGeometry(center, radius, segmentStart, segmentEnd));
    }

    private static MediaBrush WithOpacity(MediaBrush source, double opacity)
    {
        var brush = source.CloneCurrentValue();
        brush.Opacity = opacity;
        brush.Freeze();
        return brush;
    }

    private static Geometry createArcSegmentGeometry(WpfPoint center, double radius, double startProgress, double endProgress)
    {
        if (endProgress - startProgress >= 0.999)
        {
            return new EllipseGeometry(center, radius, radius);
        }

        var startAngle = -90d + startProgress * 360d;
        var endAngle = -90d + endProgress * 360d;
        var start = pointOnCircle(center, radius, startAngle);
        var end = pointOnCircle(center, radius, endAngle);
        var largeArc = endProgress - startProgress > 0.5d;

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(start, false, false);
            context.ArcTo(end, new WpfSize(radius, radius), 0d, largeArc, SweepDirection.Clockwise, true, false);
        }

        geometry.Freeze();
        return geometry;
    }

    private static Geometry createArcGeometry(WpfPoint center, double radius, double progress)
    {
        if (progress >= 0.999)
        {
            return new EllipseGeometry(center, radius, radius);
        }

        var endAngle = -90d + progress * 360d;
        var start = pointOnCircle(center, radius, -90d);
        var end = pointOnCircle(center, radius, endAngle);
        var largeArc = progress > 0.5;

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(start, false, false);
            context.ArcTo(end, new WpfSize(radius, radius), 0d, largeArc, SweepDirection.Clockwise, true, false);
        }

        geometry.Freeze();
        return geometry;
    }

    private static WpfPoint pointOnCircle(WpfPoint center, double radius, double angleDegrees)
    {
        var angle = angleDegrees * Math.PI / 180d;
        return new WpfPoint(center.X + Math.Cos(angle) * radius, center.Y + Math.Sin(angle) * radius);
    }
}
