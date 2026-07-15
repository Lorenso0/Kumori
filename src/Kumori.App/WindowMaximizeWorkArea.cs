using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Kumori.App;

/// <summary>Keeps a borderless maximized window inside its monitor's taskbar-aware work area.</summary>
internal static class WindowMaximizeWorkArea
{
    private const int wm_getminmaxinfo = 0x0024;
    private const uint monitor_defaulttonearest = 0x00000002;

    public static void Attach(Window window)
    {
        nint handle = new WindowInteropHelper(window).Handle;
        if (handle != 0 && HwndSource.FromHwnd(handle) is { } source)
        {
            source.AddHook((nint hwnd, int message, nint wParam, nint lParam, ref bool handled) =>
                windowProc(window, hwnd, message, wParam, lParam, ref handled));
        }
    }

    private static nint windowProc(
        Window window,
        nint hwnd,
        int message,
        nint wParam,
        nint lParam,
        ref bool handled)
    {
        if (message != wm_getminmaxinfo || lParam == 0)
            return 0;

        var minMax = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        var dpi = VisualTreeHelper.GetDpi(window);
        var minimum = ComputeMinimumTrackSize(window.MinWidth, window.MinHeight, dpi.DpiScaleX, dpi.DpiScaleY);
        minMax.MinTrackSize.X = Math.Max(minMax.MinTrackSize.X, minimum.Width);
        minMax.MinTrackSize.Y = Math.Max(minMax.MinTrackSize.Y, minimum.Height);

        nint monitor = MonitorFromWindow(hwnd, monitor_defaulttonearest);
        var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor != 0 && GetMonitorInfo(monitor, ref monitorInfo))
        {
            minMax.MaxPosition.X = monitorInfo.WorkArea.Left - monitorInfo.Monitor.Left;
            minMax.MaxPosition.Y = monitorInfo.WorkArea.Top - monitorInfo.Monitor.Top;
            minMax.MaxSize.X = monitorInfo.WorkArea.Right - monitorInfo.WorkArea.Left;
            minMax.MaxSize.Y = monitorInfo.WorkArea.Bottom - monitorInfo.WorkArea.Top;
        }

        Marshal.StructureToPtr(minMax, lParam, false);
        handled = true;
        return 0;
    }

    internal static (int Width, int Height) ComputeMinimumTrackSize(
        double minimumWidth,
        double minimumHeight,
        double dpiScaleX,
        double dpiScaleY)
    {
        static int ToDevicePixels(double value, double scale) =>
            double.IsFinite(value) && value > 0 && double.IsFinite(scale) && scale > 0
                ? (int)Math.Ceiling(value * scale)
                : 0;

        return (ToDevicePixels(minimumWidth, dpiScaleX), ToDevicePixels(minimumHeight, dpiScaleY));
    }

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint window, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo monitorInfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct PointInt
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public PointInt Reserved;
        public PointInt MaxSize;
        public PointInt MaxPosition;
        public PointInt MinTrackSize;
        public PointInt MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RectInt
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int Size;
        public RectInt Monitor;
        public RectInt WorkArea;
        public uint Flags;
    }
}
