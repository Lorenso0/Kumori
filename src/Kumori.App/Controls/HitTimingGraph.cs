using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Input;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using SystemColors = System.Windows.SystemColors;

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
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnOffsetsChanged));

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
    private int? _keyboardIndex;
    private (double Left, double Right, double Top, double Bottom, double Bound, IReadOnlyList<double> Offsets) _lastState;

    public HitTimingGraph()
    {
        Focusable = true;
        KeyboardNavigation.SetIsTabStop(this, true);
        AutomationProperties.SetLiveSetting(this, AutomationLiveSetting.Polite);
    }

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
            _lastState = default;
            DrawKeyboardFocus(dc);
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

        if ((_hoverIndex ?? _keyboardIndex) is { } hoverIndex)
        {
            hoverIndex = Math.Clamp(hoverIndex, 0, offsets.Count - 1);
            var x = left + (offsets.Count == 1 ? plotWidth / 2 : hoverIndex / (double)(offsets.Count - 1) * plotWidth);
            dc.DrawLine(new Pen(HoverBrush, 1) { DashStyle = new DashStyle(new double[] { 2, 2 }, 0) }, new Point(x, top), new Point(x, bottom));
        }

        DrawText(dc, "0", left, bottom + 2, TextAlignment.Left);
        DrawText(dc, Invariant($"{offsets.Count:N0} hits"), right, bottom + 2, TextAlignment.Right);
        DrawKeyboardFocus(dc);
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
        HoverReadout = BuildHitReadout(index, offset, offsets.Count);
        ToolTip = HoverReadout;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
    }

    protected override void OnMouseLeave(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        ClearHover();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            if (_keyboardIndex is not null)
            {
                SetKeyboardIndex(null);
                e.Handled = true;
            }
            return;
        }

        if (Offsets is not { Count: > 0 } offsets)
        {
            return;
        }

        var current = Math.Clamp(_keyboardIndex ?? 0, 0, offsets.Count - 1);
        int? next = e.Key switch
        {
            Key.Left => current - 1,
            Key.Right => _keyboardIndex is null ? 0 : current + 1,
            Key.PageUp => current - 10,
            Key.PageDown => _keyboardIndex is null ? 0 : current + 10,
            Key.Home => 0,
            Key.End => offsets.Count - 1,
            Key.Enter or Key.Space => current,
            _ => null,
        };
        if (next is null)
        {
            return;
        }

        SetKeyboardIndex(
            Math.Clamp(next.Value, 0, offsets.Count - 1),
            forceAnnouncement: e.Key is Key.Enter or Key.Space);
        e.Handled = true;
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        InvalidateVisual();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        InvalidateVisual();
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new HitTimingGraphAutomationPeer(this);

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
        UpdateReadoutFromKeyboardSelection();
        InvalidateVisual();
    }

    private void SetKeyboardIndex(int? index, bool forceAnnouncement = false)
    {
        var oldValue = AccessibleValue;
        _hoverIndex = null;
        _hoverOffset = null;
        _keyboardIndex = index;
        UpdateReadoutFromKeyboardSelection();
        InvalidateVisual();
        var newValue = AccessibleValue;
        if (forceAnnouncement || !string.Equals(oldValue, newValue, StringComparison.Ordinal))
        {
            RaiseAccessibleValueChanged(oldValue, newValue);
        }
    }

    private void UpdateReadoutFromKeyboardSelection()
    {
        if (_keyboardIndex is { } index && Offsets is { Count: > 0 } offsets)
        {
            index = Math.Clamp(index, 0, offsets.Count - 1);
            _keyboardIndex = index;
            HoverReadout = BuildHitReadout(index, offsets[index], offsets.Count);
            ToolTip = HoverReadout;
            return;
        }

        _keyboardIndex = null;
        HoverReadout = "";
        ToolTip = null;
    }

    private void DrawKeyboardFocus(DrawingContext dc)
    {
        if (!IsKeyboardFocused || ActualWidth < 3 || ActualHeight < 3)
        {
            return;
        }
        var pen = new Pen(System.Windows.SystemColors.HighlightBrush, 1.5)
        {
            DashStyle = new DashStyle(new double[] { 2, 2 }, 0),
        };
        dc.DrawRectangle(null, pen, new Rect(1, 1, ActualWidth - 2, ActualHeight - 2));
    }

    private string AccessibleName => Offsets is { Count: > 0 } offsets
        ? Invariant($"Hit timing graph, {offsets.Count:N0} hits")
        : "Hit timing graph, no samples";

    private string AccessibleHelpText
    {
        get
        {
            if (Offsets is not { Count: > 0 } offsets)
            {
                return "No hit-timing samples are available.";
            }
            var mean = offsets.Average();
            var deviation = Math.Sqrt(offsets.Average(offset => Math.Pow(offset - mean, 2)));
            var summary = Invariant(
                $"Mean {mean:+0.0;-0.0;0.0} milliseconds; standard deviation {deviation:0.0} milliseconds; range {offsets.Min():+0.0;-0.0;0.0} to {offsets.Max():+0.0;-0.0;0.0} milliseconds.");
            return summary +
                " " +
                "Use Left and Right Arrow to inspect hits, Page Up and Page Down to move ten hits, " +
                "Home and End to jump, and Escape to clear the cursor.";
        }
    }

    private string AccessibleValue =>
        BuildAccessibleValue(Offsets, _hoverIndex ?? _keyboardIndex);

    private void RaiseAccessibleValueChanged(string oldValue, string newValue)
    {
        if (UIElementAutomationPeer.FromElement(this) is HitTimingGraphAutomationPeer peer)
        {
            peer.RaiseValueChanged(oldValue, newValue);
        }
    }

    private static void OnOffsetsChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        var graph = (HitTimingGraph)dependencyObject;
        var oldValue = BuildAccessibleValue(
            (IReadOnlyList<double>?)e.OldValue,
            graph._hoverIndex ?? graph._keyboardIndex);
        graph._hoverIndex = null;
        graph._hoverOffset = null;
        graph._lastState = default;
        graph.UpdateReadoutFromKeyboardSelection();
        graph.RaiseAccessibleValueChanged(oldValue, graph.AccessibleValue);
    }

    private static string BuildHitReadout(int index, double offset, int count) =>
        Invariant($"hit {index + 1:N0} of {count:N0} - {offset:+0.0;-0.0;0.0}ms");

    private static string BuildHitAccessibleValue(int index, double offset, int count) =>
        Invariant($"Hit {index + 1:N0} of {count:N0}: {offset:+0.0;-0.0;0.0} milliseconds");

    private static string BuildAccessibleValue(IReadOnlyList<double>? offsets, int? index)
    {
        if (offsets is not { Count: > 0 })
        {
            return "No hit-timing samples";
        }
        if (index is { } selected)
        {
            selected = Math.Clamp(selected, 0, offsets.Count - 1);
            return BuildHitAccessibleValue(selected, offsets[selected], offsets.Count);
        }
        return Invariant($"{offsets.Count:N0} hits; mean offset {offsets.Average():+0.0;-0.0;0.0} milliseconds");
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

    private sealed class HitTimingGraphAutomationPeer(HitTimingGraph owner)
        : FrameworkElementAutomationPeer(owner), IValueProvider
    {
        private HitTimingGraph Graph => (HitTimingGraph)Owner;
        public bool IsReadOnly => true;
        public string Value => Graph.AccessibleValue;

        public override object? GetPattern(PatternInterface pattern) =>
            pattern == PatternInterface.Value ? this : base.GetPattern(pattern);

        public void SetValue(string value) =>
            throw new InvalidOperationException("The hit timing graph is read-only.");

        protected override string GetNameCore()
        {
            var explicitName = base.GetNameCore();
            return string.IsNullOrWhiteSpace(explicitName) ? Graph.AccessibleName : explicitName;
        }

        protected override string GetHelpTextCore()
        {
            var explicitHelp = base.GetHelpTextCore();
            return string.IsNullOrWhiteSpace(explicitHelp)
                ? Graph.AccessibleHelpText
                : $"{explicitHelp} {Graph.AccessibleHelpText}";
        }

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Custom;
        protected override string GetClassNameCore() => nameof(HitTimingGraph);
        protected override string GetLocalizedControlTypeCore() => "interactive chart";
        protected override bool IsKeyboardFocusableCore() => Graph.Focusable && Graph.IsEnabled;

        internal void RaiseValueChanged(string oldValue, string newValue)
        {
            RaisePropertyChangedEvent(ValuePatternIdentifiers.ValueProperty, oldValue, newValue);
            RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
    }
}
