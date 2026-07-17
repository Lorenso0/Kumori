using System.Text;
using System.Runtime.InteropServices;
using Kumori.Core.Settings;
using Microsoft.Win32;
using Serilog;

namespace Kumori.Native;

public static class DualModeService
{
    private const int DualWidth = 1920;
    private const int DualHeight = 1080;
    private const int DualRefresh = 330;
    private const int TransitionAttemptCount = 4;
    private const int PollsPerAttempt = 13;
    private const int FinalPollCount = 8;
    private static readonly TimeSpan TransitionPollInterval = TimeSpan.FromMilliseconds(500);

    public static bool HasCompatibleMonitor()
    {
        try
        {
            var monitors = EnumeratePhysicalMonitors();
            try
            {
                var connectedDescriptions = ConnectedMonitorDescriptions();
                var compatible = monitors.Any(IsCompatibleMonitor)
                    || connectedDescriptions.Any(IsCompatibleMonitorDescription);
                Log.Debug(
                    "LG dual-mode compatibility probe: Compatible={Compatible}, Physical={PhysicalDescriptions}, Logical={LogicalDescriptions}, Connected={ConnectedDescriptions}",
                    compatible,
                    monitors.Select(m => m.PhysicalDescription).ToArray(),
                    monitors.Select(m => m.LogicalDescription).ToArray(),
                    connectedDescriptions);
                return compatible;
            }
            finally
            {
                DestroyPhysicalMonitors(monitors);
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not detect a compatible LG dual-mode monitor");
            return false;
        }
    }

    internal static bool IsCompatibleMonitorDescription(string? description) =>
        !string.IsNullOrWhiteSpace(description)
        && (description.Contains("lg", StringComparison.OrdinalIgnoreCase)
            || description.Contains("ultragear", StringComparison.OrdinalIgnoreCase)
            || description.Contains("5k2k", StringComparison.OrdinalIgnoreCase));

    public static bool IsDualModeActive() =>
        CurrentDisplayModes().Any(m =>
            m.Width == DualWidth &&
            m.Height == DualHeight &&
            m.Frequency >= DualRefresh - 1);

    public static bool Activate(
        KumoriSettings settings,
        CancellationToken cancellationToken = default,
        Func<Func<bool>, bool>? executeTransition = null)
    {
        if (!settings.Display.AutoSwitchDualMode || IsDualModeActive())
        {
            return true;
        }
        bool Transition() => ToggleAndWait(settings, active: true, cancellationToken);
        return executeTransition is null ? Transition() : executeTransition(Transition);
    }

    public static bool Deactivate(KumoriSettings settings, CancellationToken cancellationToken = default)
    {
        if (!settings.Display.AutoSwitchDualMode || !IsDualModeActive())
        {
            return true;
        }
        return ToggleAndWait(settings, active: false, cancellationToken);
    }

    public static bool Toggle(KumoriSettings settings) => Trigger(settings);

    private static bool ToggleAndWait(
        KumoriSettings settings,
        bool active,
        CancellationToken cancellationToken)
    {
        return SendWithRetriesAndPoll(
            () => Trigger(settings),
            () => IsDualModeActive() == active,
            TransitionAttemptCount,
            PollsPerAttempt,
            FinalPollCount,
            () => WaitForNextPoll(cancellationToken),
            cancellationToken);
    }

    private static bool Trigger(KumoriSettings settings)
    {
        return SendDdcDualModeToggle();
    }

    internal static bool SendOnceAndPoll(
        Func<bool> send,
        Func<bool> targetReached,
        int pollCount,
        Action waitForNextPoll,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(send);
        ArgumentNullException.ThrowIfNull(targetReached);
        ArgumentNullException.ThrowIfNull(waitForNextPoll);
        if (pollCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(pollCount));

        cancellationToken.ThrowIfCancellationRequested();
        if (!send())
            return false;

        return PollForTarget(targetReached, pollCount, waitForNextPoll, cancellationToken);
    }

    internal static bool SendWithRetriesAndPoll(
        Func<bool> send,
        Func<bool> targetReached,
        int attemptCount,
        int pollsPerAttempt,
        int finalPollCount,
        Action waitForNextPoll,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(send);
        ArgumentNullException.ThrowIfNull(targetReached);
        ArgumentNullException.ThrowIfNull(waitForNextPoll);
        if (attemptCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(attemptCount));
        if (pollsPerAttempt <= 0)
            throw new ArgumentOutOfRangeException(nameof(pollsPerAttempt));
        if (finalPollCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(finalPollCount));

        for (var attempt = 0; attempt < attemptCount; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TargetReached(targetReached))
                return true;
            if (SendOnceAndPoll(
                    send,
                    targetReached,
                    pollsPerAttempt,
                    waitForNextPoll,
                    cancellationToken))
            {
                return true;
            }
        }

        return PollForTarget(targetReached, finalPollCount, waitForNextPoll, cancellationToken);
    }

    private static bool PollForTarget(
        Func<bool> targetReached,
        int pollCount,
        Action waitForNextPoll,
        CancellationToken cancellationToken)
    {
        for (var poll = 0; poll < pollCount; poll++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TargetReached(targetReached))
                return true;

            if (poll + 1 < pollCount)
            {
                waitForNextPoll();
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
        return false;
    }

    private static bool TargetReached(Func<bool> targetReached)
    {
        try
        {
            return targetReached();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Display mode probe failed");
            return false;
        }
    }

    private static void WaitForNextPoll(CancellationToken cancellationToken)
    {
        if (!cancellationToken.CanBeCanceled)
        {
            Thread.Sleep(TransitionPollInterval);
            return;
        }

        if (cancellationToken.WaitHandle.WaitOne(TransitionPollInterval))
            cancellationToken.ThrowIfCancellationRequested();
    }

    private static bool SendDdcDualModeToggle()
    {
        try
        {
            var monitors = EnumeratePhysicalMonitors();
            try
            {
                var targets = monitors.Where(IsCompatibleMonitor)
                    .ToArray();
                if (targets.Length == 0)
                {
                    var connectedDescriptions = ConnectedMonitorDescriptions();
                    if (!connectedDescriptions.Any(IsCompatibleMonitorDescription))
                    {
                        Log.Information("No compatible LG dual-mode monitor was detected");
                        return false;
                    }

                    // Some GPU/monitor combinations expose every DDC handle as
                    // "Generic PnP Monitor". Windows still provides the EDID
                    // friendly name through EnumDisplayDevices. In that case,
                    // retain the proven fallback: unsupported monitors reject
                    // the LG-specific VCP command while the LG accepts it.
                    targets = monitors.ToArray();
                    Log.Information(
                        "Using generic DDC handles because Windows detected a compatible display: {ConnectedDescriptions}",
                        connectedDescriptions);
                }

                Log.Information(
                    "Sending LG dual-mode DDC command to {MonitorCount} physical monitor(s): {MonitorDescriptions}",
                    targets.Length,
                    targets.Select(target => target.DisplayName).ToArray());
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
                    else
                    {
                        Log.Warning(
                            "LG dual-mode DDC write failed for {MonitorDescription} with Win32 error {Win32Error}",
                            monitor.DisplayName,
                            Marshal.GetLastWin32Error());
                    }
                }
                Log.Information("LG dual-mode DDC command accepted: {Accepted}", success);
                return success;
            }
            finally
            {
                DestroyPhysicalMonitors(monitors);
            }
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
            var displayDescription = LogicalDisplayDescription(hMonitor);
            result.AddRange(monitors.Select(m => new PhysicalMonitor(
                m.hPhysicalMonitor,
                m.szPhysicalMonitorDescription,
                displayDescription)));
            return true;
        };
        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        return result;
    }

    private static bool IsCompatibleMonitor(PhysicalMonitor monitor) =>
        IsCompatibleMonitorDescription(monitor.PhysicalDescription)
        || IsCompatibleMonitorDescription(monitor.LogicalDescription);

    private static string[] ConnectedMonitorDescriptions()
    {
        var result = new List<string>();
        for (var adapterIndex = 0; ; adapterIndex++)
        {
            var adapter = new DisplayDevice { cb = Marshal.SizeOf<DisplayDevice>() };
            if (!NativeMethods.EnumDisplayDevices(null, adapterIndex, ref adapter, 0))
                break;

            for (var monitorIndex = 0; ; monitorIndex++)
            {
                var monitor = new DisplayDevice { cb = Marshal.SizeOf<DisplayDevice>() };
                if (!NativeMethods.EnumDisplayDevices(adapter.DeviceName, monitorIndex, ref monitor, 1))
                    break;
                if (!string.IsNullOrWhiteSpace(monitor.DeviceString))
                    result.Add(monitor.DeviceString);
                result.AddRange(EdidFriendlyNames(monitor.DeviceID));
            }
        }

        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IReadOnlyList<string> EdidFriendlyNames(string? deviceId)
    {
        var result = new List<string>();
        var hardwareId = MonitorHardwareId(deviceId);
        if (hardwareId is null)
            return result;

        try
        {
            using var modelKey = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Enum\DISPLAY\{hardwareId}");
            if (modelKey is null)
                return result;

            foreach (var instanceName in modelKey.GetSubKeyNames())
            {
                using var parameters = modelKey.OpenSubKey(
                    $@"{instanceName}\Device Parameters");
                if (parameters?.GetValue("EDID") is byte[] edid
                    && EdidMonitorName(edid) is { Length: > 0 } name)
                {
                    result.Add(name);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not resolve EDID friendly name for monitor {HardwareId}", hardwareId);
        }

        return result;
    }

    internal static string? MonitorHardwareId(string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return null;

        var parts = deviceId
            .Replace('#', '\\')
            .Split('\\', StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index + 1 < parts.Length; index++)
        {
            if (parts[index].Equals("DISPLAY", StringComparison.OrdinalIgnoreCase)
                || parts[index].Equals("MONITOR", StringComparison.OrdinalIgnoreCase))
            {
                return parts[index + 1];
            }
        }

        return null;
    }

    internal static string? EdidMonitorName(byte[] edid)
    {
        ArgumentNullException.ThrowIfNull(edid);
        for (var offset = 54; offset + 18 <= edid.Length && offset <= 108; offset += 18)
        {
            if (edid[offset] != 0
                || edid[offset + 1] != 0
                || edid[offset + 2] != 0
                || edid[offset + 3] != 0xFC
                || edid[offset + 4] != 0)
            {
                continue;
            }

            var name = Encoding.ASCII
                .GetString(edid, offset + 5, 13)
                .Trim('\0', '\r', '\n', ' ');
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }

        return null;
    }

    private static string LogicalDisplayDescription(IntPtr hMonitor)
    {
        var monitorInfo = new MonitorInfoEx
        {
            cbSize = Marshal.SizeOf<MonitorInfoEx>(),
        };
        if (!NativeMethods.GetMonitorInfo(hMonitor, ref monitorInfo)
            || string.IsNullOrWhiteSpace(monitorInfo.szDevice))
        {
            return string.Empty;
        }

        var device = new DisplayDevice { cb = Marshal.SizeOf<DisplayDevice>() };
        return NativeMethods.EnumDisplayDevices(monitorInfo.szDevice, 0, ref device, 0)
            ? device.DeviceString
            : string.Empty;
    }

    private static void DestroyPhysicalMonitors(IReadOnlyList<PhysicalMonitor> monitors)
    {
        if (monitors.Count == 0)
        {
            return;
        }
        var native = monitors
            .Where(m => m.Handle != IntPtr.Zero)
            .Select(m => new NativePhysicalMonitor
            {
                hPhysicalMonitor = m.Handle,
                szPhysicalMonitorDescription = m.PhysicalDescription,
            })
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
    private sealed record PhysicalMonitor(
        IntPtr Handle,
        string PhysicalDescription,
        string LogicalDescription)
    {
        public string DisplayName =>
            string.IsNullOrWhiteSpace(LogicalDescription)
                ? PhysicalDescription
                : $"{LogicalDescription} ({PhysicalDescription})";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int cbSize;
        public NativeRect rcMonitor;
        public NativeRect rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

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

        [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfoEx monitorInfo);

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
