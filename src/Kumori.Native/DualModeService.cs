using System.Runtime.InteropServices;
using Kumori.Core.Settings;
using Serilog;

namespace Kumori.Native;

public static class DualModeService
{
    private const int DualWidth = 1920;
    private const int DualHeight = 1080;
    private const int DualRefresh = 330;
    public static bool IsDualModeActive() =>
        CurrentDisplayModes().Any(m =>
            m.Width == DualWidth &&
            m.Height == DualHeight &&
            m.Frequency >= DualRefresh - 1);

    public static bool Activate(KumoriSettings settings)
    {
        if (!settings.Display.AutoSwitchDualMode || IsDualModeActive())
        {
            return true;
        }
        return ToggleAndWait(settings, active: true);
    }

    public static bool Deactivate(KumoriSettings settings)
    {
        if (!settings.Display.AutoSwitchDualMode || !IsDualModeActive())
        {
            return true;
        }
        return ToggleAndWait(settings, active: false);
    }

    public static bool Toggle(KumoriSettings settings) => Trigger(settings);

    private static bool ToggleAndWait(KumoriSettings settings, bool active)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            if (!Trigger(settings))
            {
                return false;
            }
            if (WaitFor(active, TimeSpan.FromSeconds(6)))
            {
                return true;
            }
        }
        return WaitFor(active, TimeSpan.FromSeconds(4));
    }

    private static bool Trigger(KumoriSettings settings)
    {
        return SendDdcDualModeToggle();
    }

    private static bool WaitFor(bool active, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                if (IsDualModeActive() == active)
                {
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Display mode probe failed");
            }
            Thread.Sleep(500);
        }
        return false;
    }

    private static bool SendDdcDualModeToggle()
    {
        try
        {
            var monitors = EnumeratePhysicalMonitors();
            var targets = monitors.Where(m =>
                    m.Description.Contains("lg", StringComparison.OrdinalIgnoreCase) ||
                    m.Description.Contains("ultragear", StringComparison.OrdinalIgnoreCase) ||
                    m.Description.Contains("5k2k", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (targets.Length == 0)
            {
                targets = monitors.ToArray();
            }
            var success = false;
            foreach (var monitor in targets)
            {
                if (monitor.Handle == IntPtr.Zero)
                {
                    continue;
                }
                if (NativeMethods.SetVCPFeature(monitor.Handle, 0xB1, 0x2F00))
                {
                    success = true;
                }
            }
            DestroyPhysicalMonitors(monitors);
            return success;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "LG dual-mode DDC command failed");
            return false;
        }
    }

    private static IReadOnlyList<PhysicalMonitor> EnumeratePhysicalMonitors()
    {
        var result = new List<PhysicalMonitor>();
        NativeMethods.MonitorEnumProc callback = (hMonitor, _, _, _) =>
        {
            if (!NativeMethods.GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out var count) || count == 0)
            {
                return true;
            }
            var monitors = new NativePhysicalMonitor[count];
            if (!NativeMethods.GetPhysicalMonitorsFromHMONITOR(hMonitor, count, monitors))
            {
                return true;
            }
            result.AddRange(monitors.Select(m => new PhysicalMonitor(m.hPhysicalMonitor, m.szPhysicalMonitorDescription)));
            return true;
        };
        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        return result;
    }

    private static void DestroyPhysicalMonitors(IReadOnlyList<PhysicalMonitor> monitors)
    {
        if (monitors.Count == 0)
        {
            return;
        }
        var native = monitors
            .Where(m => m.Handle != IntPtr.Zero)
            .Select(m => new NativePhysicalMonitor { hPhysicalMonitor = m.Handle, szPhysicalMonitorDescription = m.Description })
            .ToArray();
        if (native.Length > 0)
        {
            NativeMethods.DestroyPhysicalMonitors((uint)native.Length, native);
        }
    }

    private static IEnumerable<DisplayMode> CurrentDisplayModes()
    {
        var index = 0;
        while (true)
        {
            var device = new DisplayDevice { cb = Marshal.SizeOf<DisplayDevice>() };
            if (!NativeMethods.EnumDisplayDevices(null, index, ref device, 0))
            {
                yield break;
            }

            if ((device.StateFlags & 0x1) != 0)
            {
                var mode = new DevMode();
                mode.dmSize = (short)Marshal.SizeOf<DevMode>();
                if (NativeMethods.EnumDisplaySettings(device.DeviceName, -1, ref mode))
                {
                    yield return new DisplayMode(device.DeviceName, device.DeviceString,
                        mode.dmPelsWidth, mode.dmPelsHeight, mode.dmDisplayFrequency);
                }
            }
            index++;
        }
    }

    private sealed record DisplayMode(string DeviceName, string DeviceString, int Width, int Height, int Frequency);
    private sealed record PhysicalMonitor(IntPtr Handle, string Description);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativePhysicalMonitor
    {
        public IntPtr hPhysicalMonitor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szPhysicalMonitorDescription;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", EntryPoint = "EnumDisplayDevicesW", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumDisplayDevices(
            string? lpDevice, int iDevNum, ref DisplayDevice lpDisplayDevice, int dwFlags);

        [DllImport("user32.dll", EntryPoint = "EnumDisplaySettingsW", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumDisplaySettings(string lpszDeviceName, int iModeNum, ref DevMode lpDevMode);

        public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, IntPtr lprcMonitor, IntPtr dwData);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

        [DllImport("dxva2.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint count);

        [DllImport("dxva2.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetPhysicalMonitorsFromHMONITOR(
            IntPtr hMonitor,
            uint count,
            [Out] NativePhysicalMonitor[] monitors);

        [DllImport("dxva2.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DestroyPhysicalMonitors(uint count, [In] NativePhysicalMonitor[] monitors);

        [DllImport("dxva2.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetVCPFeature(IntPtr monitor, byte code, uint value);
    }
}
