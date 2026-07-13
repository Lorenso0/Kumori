using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

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
            source.AddHook(windowProc);
    }

    private static nint windowProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != wm_getminmaxinfo || lParam == 0)
            return 0;

        nint monitor = MonitorFromWindow(hwnd, monitor_defaulttonearest);
        var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == 0 || !GetMonitorInfo(monitor, ref monitorInfo))
            return 0;

        var minMax = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        minMax.MaxPosition.X = monitorInfo.WorkArea.Left - monitorInfo.Monitor.Left;
        minMax.MaxPosition.Y = monitorInfo.WorkArea.Top - monitorInfo.Monitor.Top;
        minMax.MaxSize.X = monitorInfo.WorkArea.Right - monitorInfo.WorkArea.Left;
        minMax.MaxSize.Y = monitorInfo.WorkArea.Bottom - monitorInfo.WorkArea.Top;
        Marshal.StructureToPtr(minMax, lParam, false);
        handled = true;
        return 0;
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
