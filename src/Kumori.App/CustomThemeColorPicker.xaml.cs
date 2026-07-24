using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using MediaColor = System.Windows.Media.Color;
using MediaColorConverter = System.Windows.Media.ColorConverter;

namespace Kumori.App;

public partial class CustomThemeColorPicker : UserControl
{
    private bool updating;
    private bool draggingPlane;
    private double hue;
    private double saturation;
    private double brightness;
    private byte alpha = byte.MaxValue;

    public CustomThemeColorPicker()
    {
        InitializeComponent();
        SaturationValuePlane.SizeChanged += (_, _) => PositionMarker();
    }

    public event Action<string>? ColourChanged;
    public event Action? CloseRequested;

    public void Open(string value, string title, string description, bool allowOpacity = true)
    {
        RoleTitle.Text = title;
        RoleDescription.Text = description;
        if (!TryParse(value, out var color))
            color = MediaColor.FromRgb(byte.MaxValue, byte.MaxValue, byte.MaxValue);
        OpacityLabel.Visibility = allowOpacity ? Visibility.Visible : Visibility.Collapsed;
        OpacityControl.Visibility = allowOpacity ? Visibility.Visible : Visibility.Collapsed;
        if (!allowOpacity)
            color.A = byte.MaxValue;
        SetFromColor(color, raiseChanged: false);
        Focus();
    }

    private void Plane_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        draggingPlane = true;
        SaturationValuePlane.CaptureMouse();
        UpdatePlane(e.GetPosition(SaturationValuePlane));
    }

    private void Plane_MouseMove(object sender, MouseEventArgs e)
    {
        if (draggingPlane && e.LeftButton == MouseButtonState.Pressed)
            UpdatePlane(e.GetPosition(SaturationValuePlane));
    }

    private void Plane_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!draggingPlane)
            return;
        UpdatePlane(e.GetPosition(SaturationValuePlane));
        draggingPlane = false;
        SaturationValuePlane.ReleaseMouseCapture();
    }

    private void UpdatePlane(System.Windows.Point point)
    {
        if (SaturationValuePlane.ActualWidth <= 0 || SaturationValuePlane.ActualHeight <= 0)
            return;
        saturation = Math.Clamp(point.X / SaturationValuePlane.ActualWidth, 0, 1);
        brightness = 1 - Math.Clamp(point.Y / SaturationValuePlane.ActualHeight, 0, 1);
        UpdateFromHsv();
    }

    private void HueSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (updating)
            return;
        hue = e.NewValue % 360;
        UpdateFromHsv();
    }

    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (updating)
            return;
        alpha = (byte)Math.Round(e.NewValue);
        UpdateFromHsv();
    }

    private void Slider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Slider slider || e.OriginalSource is not DependencyObject source)
            return;

        // Preserve the Slider's built-in thumb drag. A click anywhere else on
        // the coloured track maps directly to that exact position.
        if (FindAncestor<Thumb>(source) is not null)
            return;

        var thumbWidth = 12d;
        var usableWidth = Math.Max(1, slider.ActualWidth - thumbWidth);
        var x = e.GetPosition(slider).X - (thumbWidth / 2);
        var ratio = Math.Clamp(x / usableWidth, 0, 1);
        slider.Value = slider.Minimum + ratio * (slider.Maximum - slider.Minimum);
        slider.Focus();
        e.Handled = true;
    }

    private void HexValue_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (updating || !TryParse(HexValue.Text, out var color))
            return;
        SetFromColor(color, raiseChanged: true);
    }

    private void UpdateFromHsv()
    {
        var color = FromHsv(hue, saturation, brightness, alpha);
        updating = true;
        ApplyVisuals(color, updateHex: true);
        updating = false;
        ColourChanged?.Invoke(ToHex(color));
    }

    private void SetFromColor(MediaColor color, bool raiseChanged)
    {
        ToHsv(color, out hue, out saturation, out brightness);
        alpha = color.A;
        updating = true;
        HueSlider.Value = hue;
        OpacitySlider.Value = alpha;
        ApplyVisuals(color, updateHex: true);
        updating = false;
        if (raiseChanged)
            ColourChanged?.Invoke(ToHex(color));
    }

    private void ApplyVisuals(MediaColor color, bool updateHex)
    {
        HueSurface.Background = new SolidColorBrush(FromHsv(hue, 1, 1, byte.MaxValue));
        SelectedColourPreview.Background = new SolidColorBrush(color);
        OpacityGradientStop.Color = FromHsv(hue, saturation, brightness, byte.MaxValue);
        OpacityValue.Text = $"{Math.Round(alpha / 255d * 100):0}%";
        if (updateHex)
            HexValue.Text = ToHex(color);
        PositionMarker();
    }

    private void PositionMarker()
    {
        if (PlaneMarker is null || SaturationValuePlane is null)
            return;
        Canvas.SetLeft(PlaneMarker, saturation * SaturationValuePlane.ActualWidth - PlaneMarker.Width / 2);
        Canvas.SetTop(PlaneMarker, (1 - brightness) * SaturationValuePlane.ActualHeight - PlaneMarker.Height / 2);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();

    private static T? FindAncestor<T>(DependencyObject source) where T : DependencyObject
    {
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is T match)
                return match;
        return null;
    }

    private static bool TryParse(string? value, out MediaColor color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(value) || value.Length is not (7 or 9) || value[0] != '#')
            return false;
        try
        {
            color = (MediaColor)MediaColorConverter.ConvertFromString(value);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or NotSupportedException)
        {
            return false;
        }
    }

    private static string ToHex(MediaColor color) => color.A == byte.MaxValue
        ? $"#{color.R:X2}{color.G:X2}{color.B:X2}"
        : $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";

    internal static MediaColor FromHsv(double hue, double saturation, double brightness, byte alpha)
    {
        hue = ((hue % 360) + 360) % 360;
        saturation = Math.Clamp(saturation, 0, 1);
        brightness = Math.Clamp(brightness, 0, 1);
        var chroma = brightness * saturation;
        var x = chroma * (1 - Math.Abs((hue / 60 % 2) - 1));
        var m = brightness - chroma;
        (double r, double g, double b) = hue switch
        {
            < 60 => (chroma, x, 0d),
            < 120 => (x, chroma, 0d),
            < 180 => (0d, chroma, x),
            < 240 => (0d, x, chroma),
            < 300 => (x, 0d, chroma),
            _ => (chroma, 0d, x),
        };
        return MediaColor.FromArgb(alpha,
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }

    internal static void ToHsv(MediaColor color, out double hue, out double saturation, out double brightness)
    {
        double r = color.R / 255d;
        double g = color.G / 255d;
        double b = color.B / 255d;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;
        hue = delta == 0 ? 0
            : max == r ? 60 * (((g - b) / delta) % 6)
            : max == g ? 60 * (((b - r) / delta) + 2)
            : 60 * (((r - g) / delta) + 4);
        if (hue < 0) hue += 360;
        saturation = max == 0 ? 0 : delta / max;
        brightness = max;
    }
}
