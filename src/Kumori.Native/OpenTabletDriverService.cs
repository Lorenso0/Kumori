using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Serilog;

namespace Kumori.Native;

public sealed record OpenTabletDriverInstallation(
    string ExecutablePath,
    string Version,
    bool IsCompatible,
    bool IsPortable);

public static partial class OpenTabletDriverService
{
    private const string OtdExe = "OpenTabletDriver.UX.Wpf.exe";
    private const string SupportedVersion = "0.6.7";
    private static readonly object LaunchGate = new();
    private static readonly object MappingGate = new();
    private static readonly Dictionary<int, OwnedProcessIdentity> OwnedProcesses = [];
    private static string? refreshExecutablePath;
    private static IReadOnlyList<OtdMonitor>? appliedTopology;
    private static IReadOnlyList<OtdMonitor>? pendingTopology;
    private static DateTimeOffset pendingTopologySince;

    public static OpenTabletDriverInstallation? Detect(string configuredPath = "")
    {
        var executable = CandidatePaths(configuredPath)
            .Select(NormalizeUxExecutablePath)
            .FirstOrDefault(File.Exists);
        return executable is null ? null : Inspect(executable);
    }

    public static OpenTabletDriverInstallation Inspect(string executablePath)
    {
        var fullPath = Path.GetFullPath(executablePath);
        var version = ReadVersion(fullPath);
        var portable = Directory.Exists(Path.Combine(Path.GetDirectoryName(fullPath)!, "userdata"));
        return new OpenTabletDriverInstallation(
            fullPath,
            version,
            version.StartsWith(SupportedVersion, StringComparison.OrdinalIgnoreCase),
            portable);
    }

    public static bool Launch(string executablePath)
    {
        lock (LaunchGate)
        {
            // A daemon may survive without the UX/tray process. Starting the UX
            // in that state attaches to the existing daemon; OTD's watchdog
            // checks for it and does not create a duplicate daemon.
            if (IsUiRunning())
                return false;
            return LaunchCore(executablePath);
        }
    }

    public static bool IsRunning() => AnyProcess(_ => true);

    public static bool IsUiRunning() => AnyProcess(process =>
        string.Equals(process.ProcessName, "OpenTabletDriver.UX.Wpf", StringComparison.OrdinalIgnoreCase));

    private static bool IsDaemonRunning() => AnyProcess(process =>
        string.Equals(process.ProcessName, "OpenTabletDriver.Daemon", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Closes only the OTD process tree started by this Kumori process. An OTD
    /// instance that was already running when Kumori opened is never claimed.
    /// </summary>
    public static void CloseOwned()
    {
        lock (LaunchGate)
        {
            var owned = OwnedProcesses.Values.ToArray();
            OwnedProcesses.Clear();
            if (owned.Length == 0)
                return;

            var processes = new List<Process>();
            foreach (var identity in owned)
            {
                var process = OpenMatchingOwnedProcess(identity);
                if (process is not null)
                {
                    processes.Add(process);
                }
                else
                {
                    Log.Warning(
                        "Skipped closing former OpenTabletDriver process id {ProcessId} because its process identity changed",
                        identity.ProcessId);
                }
            }

            foreach (var process in processes)
            {
                try { process.CloseMainWindow(); } catch { }
            }

            var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
            foreach (var process in processes)
            {
                try
                {
                    var remaining = deadline - DateTimeOffset.UtcNow;
                    if (remaining > TimeSpan.Zero)
                        process.WaitForExit((int)remaining.TotalMilliseconds);
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch { }
                finally { process.Dispose(); }
            }
        }
    }

    /// <summary>
    /// Enables automatic OTD refresh after Windows display topology changes.
    /// OTD 0.6.7 caches the monitor layout in both its daemon and UX process,
    /// so updating the live profile alone cannot repair absolute cursor output.
    /// </summary>
    public static bool ConfigureDisplayMappingRefresh(string otdExecutablePath)
    {
        var executablePath = Path.GetFullPath(otdExecutablePath);
        lock (MappingGate)
        {
            refreshExecutablePath = File.Exists(executablePath) ? executablePath : null;
            appliedTopology = refreshExecutablePath is null ? null : CurrentTopology();
            pendingTopology = null;
            return refreshExecutablePath is not null;
        }
    }

    public static void StopDisplayMappingRefresh()
    {
        lock (MappingGate)
        {
            refreshExecutablePath = null;
            appliedTopology = null;
            pendingTopology = null;
        }
    }

    /// <summary>
    /// Restarts OTD in the tray after a stable Windows topology change. This
    /// refreshes OTD's cached virtual-desktop dimensions while reloading the
    /// user's existing settings file without modifying it.
    /// </summary>
    public static bool RefreshDisplayMappingsIfChanged()
    {
        lock (MappingGate)
        {
            if (refreshExecutablePath is null || !IsRunning())
                return false;

            var current = CurrentTopology();
            if (current.Count == 0)
                return false;
            if (appliedTopology is null)
            {
                appliedTopology = current;
                return false;
            }
            if (TopologyEquals(appliedTopology, current))
            {
                pendingTopology = null;
                return false;
            }

            // LG Dual Mode can report several short-lived layouts. Wait until
            // one layout remains unchanged before updating the cursor mapping.
            if (pendingTopology is null || !TopologyEquals(pendingTopology, current))
            {
                pendingTopology = current;
                pendingTopologySince = DateTimeOffset.UtcNow;
                return false;
            }
            if (DateTimeOffset.UtcNow - pendingTopologySince < TimeSpan.FromSeconds(1))
                return false;

            if (RestartForDisplayChange(refreshExecutablePath))
            {
                appliedTopology = current;
                pendingTopology = null;
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Immediately refreshes OTD after a display transition that has already
    /// been confirmed by the caller, such as LG Dual Mode activation.
    /// </summary>
    public static bool RefreshAfterDisplayTransition()
    {
        lock (MappingGate)
        {
            if (refreshExecutablePath is null || !IsRunning())
                return false;
            var current = CurrentTopology();
            if (current.Count == 0 || !RestartForDisplayChange(refreshExecutablePath))
                return false;
            appliedTopology = current;
            pendingTopology = null;
            return true;
        }
    }

    private static IReadOnlyList<OtdMonitor> CurrentTopology()
    {
        var displays = new List<OtdMonitor>();
        var index = 0;
        while (true)
        {
            var device = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
            if (!EnumDisplayDevices(null, index++, ref device, 0))
                break;
            const int AttachedToDesktop = 0x1;
            if ((device.StateFlags & AttachedToDesktop) == 0)
                continue;

            var mode = new DevMode { Size = (short)Marshal.SizeOf<DevMode>() };
            if (EnumDisplaySettings(device.DeviceName, -1, ref mode))
            {
                displays.Add(new OtdMonitor(
                    device.DeviceName,
                    mode.PositionX,
                    mode.PositionY,
                    mode.Width,
                    mode.Height));
            }
        }
        return displays.OrderBy(d => d.DeviceName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static bool TopologyEquals(IReadOnlyList<OtdMonitor> left, IReadOnlyList<OtdMonitor> right) =>
        left.Count == right.Count && left.SequenceEqual(right);

    private static bool LaunchCore(string executablePath)
    {
        if (!File.Exists(executablePath))
            throw new FileNotFoundException("OpenTabletDriver executable was not found.", executablePath);

        var installationDirectory = Path.GetDirectoryName(Path.GetFullPath(executablePath))!;
        var daemonWasRunning = IsDaemonRunning();
        var before = ProcessIds();
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = Path.GetDirectoryName(executablePath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        // OTD 0.6.7 handles this itself by hiding the window/taskbar entry
        // after initializing its notification-area icon.
        startInfo.ArgumentList.Add("--minimized");
        var process = Process.Start(startInfo);
        var launched = process is not null;
        Log.Information(
            "OpenTabletDriver UX launch requested: Started={Started}, ExistingDaemon={ExistingDaemon}, Executable={Executable}",
            launched,
            daemonWasRunning,
            executablePath);
        if (process is not null)
        {
            OwnedProcesses.Clear();
            RememberOwnedProcess(process, executablePath);
            process.Dispose();
            if (!daemonWasRunning)
                TrackOwnedProcesses(before, installationDirectory);
        }
        return launched;
    }

    private static bool RestartForDisplayChange(string executablePath)
    {
        lock (LaunchGate)
        {
            if (!File.Exists(executablePath))
                return false;

            var installationDirectory = Path.GetDirectoryName(Path.GetFullPath(executablePath));
            if (installationDirectory is null)
                return false;

            var processes = OpenTabletDriverProcesses()
                .Where(process => IsFromInstallation(process, installationDirectory))
                .OrderBy(process => string.Equals(
                    process.ProcessName,
                    "OpenTabletDriver.Daemon",
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (processes.Length == 0)
                return false;

            Log.Information(
                "Restarting OpenTabletDriver after display topology change without writing its settings file");
            OwnedProcesses.Clear();
            try
            {
                foreach (var process in processes)
                {
                    try
                    {
                        if (!process.HasExited)
                            process.Kill(entireProcessTree: true);
                    }
                    catch (Exception ex)
                    {
                        Log.Debug(ex, "Could not stop OpenTabletDriver process {ProcessId} for display refresh", process.Id);
                    }
                }

                var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
                foreach (var process in processes)
                {
                    try
                    {
                        var remaining = deadline - DateTimeOffset.UtcNow;
                        if (!process.HasExited && remaining > TimeSpan.Zero)
                            process.WaitForExit((int)remaining.TotalMilliseconds);
                        if (!process.HasExited)
                        {
                            Log.Warning(
                                "OpenTabletDriver display refresh was aborted because process {ProcessId} did not exit",
                                process.Id);
                            return false;
                        }
                    }
                    catch (InvalidOperationException) { }
                }
            }
            finally
            {
                foreach (var process in processes)
                    process.Dispose();
            }

            // Let Windows release OTD's single-instance handles before the
            // minimized UX starts a daemon with a fresh display snapshot.
            Thread.Sleep(150);
            var launched = LaunchCore(executablePath);
            if (launched)
                Log.Information("OpenTabletDriver display cache refreshed and tray relaunched");
            return launched;
        }
    }

    private static bool IsFromInstallation(Process process, string installationDirectory)
    {
        try
        {
            var processPath = process.MainModule?.FileName;
            return IsExecutableFromInstallation(processPath, installationDirectory);
        }
        catch
        {
            return false;
        }
    }

    internal static bool IsExecutableFromInstallation(
        string? executablePath,
        string installationDirectory)
    {
        if (string.IsNullOrWhiteSpace(executablePath)
            || string.IsNullOrWhiteSpace(installationDirectory))
            return false;

        try
        {
            var processDirectory = Path.GetDirectoryName(Path.GetFullPath(executablePath));
            if (processDirectory is null)
                return false;
            var expectedDirectory = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(installationDirectory));
            var actualDirectory = Path.TrimEndingDirectorySeparator(processDirectory);
            return string.Equals(
                actualDirectory,
                expectedDirectory,
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static HashSet<int> ProcessIds()
    {
        var ids = new HashSet<int>();
        foreach (var process in OpenTabletDriverProcesses())
        {
            using (process)
            {
                try { ids.Add(process.Id); } catch { }
            }
        }
        return ids;
    }

    private static void TrackOwnedProcesses(
        HashSet<int> before,
        string installationDirectory)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var foundDaemon = false;
            foreach (var process in OpenTabletDriverProcesses())
            {
                using (process)
                {
                    try
                    {
                        if (!before.Contains(process.Id)
                            && IsFromInstallation(process, installationDirectory))
                            RememberOwnedProcess(process);
                        if (OwnedProcesses.ContainsKey(process.Id)
                            && string.Equals(process.ProcessName, "OpenTabletDriver.Daemon", StringComparison.OrdinalIgnoreCase))
                        {
                            foundDaemon = true;
                        }
                    }
                    catch { }
                }
            }
            if (foundDaemon)
                return;
            Thread.Sleep(100);
        }
    }

    private static void RememberOwnedProcess(Process process, string? knownExecutablePath = null)
    {
        var identity = CaptureProcessIdentity(process, knownExecutablePath);
        if (identity is { } owned)
            OwnedProcesses[owned.ProcessId] = owned;
    }

    private static Process? OpenMatchingOwnedProcess(OwnedProcessIdentity expected)
    {
        Process? process = null;
        try
        {
            process = Process.GetProcessById(expected.ProcessId);
            var actual = CaptureProcessIdentity(process);
            if (actual is not { } identity || !ProcessIdentityMatches(expected, identity))
            {
                process.Dispose();
                return null;
            }
            return process;
        }
        catch
        {
            process?.Dispose();
            return null;
        }
    }

    private static OwnedProcessIdentity? CaptureProcessIdentity(
        Process process,
        string? knownExecutablePath = null)
    {
        try
        {
            var path = knownExecutablePath ?? process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(path))
                return null;
            return new OwnedProcessIdentity(
                process.Id,
                process.StartTime.ToUniversalTime().Ticks,
                Path.GetFullPath(path));
        }
        catch
        {
            // If Windows will not provide a stable identity, fail closed and
            // leave the process running rather than risking an unrelated PID.
            return null;
        }
    }

    internal static bool ProcessIdentityMatches(
        OwnedProcessIdentity expected,
        OwnedProcessIdentity actual) =>
        expected.ProcessId == actual.ProcessId
        && expected.StartTimeUtcTicks == actual.StartTimeUtcTicks
        && string.Equals(
            Path.GetFullPath(expected.ExecutablePath),
            Path.GetFullPath(actual.ExecutablePath),
            StringComparison.OrdinalIgnoreCase);

    internal readonly record struct OwnedProcessIdentity(
        int ProcessId,
        long StartTimeUtcTicks,
        string ExecutablePath);

    private static IEnumerable<string> CandidatePaths(string configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            yield return configuredPath;
        }

        foreach (var process in OpenTabletDriverProcesses())
        {
            using (process)
            {
                string? path = null;
                try { path = process.MainModule?.FileName; } catch { }
                if (!string.IsNullOrWhiteSpace(path))
                {
                    yield return path;
                }
            }
        }

        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        }.Where(r => !string.IsNullOrWhiteSpace(r) && Directory.Exists(r));

        foreach (var root in roots)
        {
            yield return Path.Combine(root, "OpenTabletDriver", OtdExe);
            yield return Path.Combine(root, OtdExe);
            foreach (var directory in SafeEnumerateDirectories(root, "OpenTabletDriver*"))
            {
                yield return Path.Combine(directory, OtdExe);
            }
        }
    }

    private static string NormalizeUxExecutablePath(string path)
    {
        if (string.Equals(
                Path.GetFileName(path),
                "OpenTabletDriver.Daemon.exe",
                StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine(Path.GetDirectoryName(path)!, OtdExe);
        }
        return path;
    }

    private static string ReadVersion(string executablePath)
    {
        // Never execute the daemon as a version probe. Some OTD builds perform
        // device/HID initialization before handling --version, which can disturb
        // the cursor even though this is nominally only an inspection.
        try
        {
            var metadata = FileVersionInfo.GetVersionInfo(executablePath);
            foreach (var text in new[] { metadata.ProductVersion, metadata.FileVersion })
            {
                if (!string.IsNullOrWhiteSpace(text))
                {
                    var match = VersionRegex().Match(text);
                    if (match.Success)
                        return match.Value;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "OpenTabletDriver file-version inspection failed");
        }

        var folder = Path.GetFileName(Path.GetDirectoryName(executablePath)) ?? "";
        var folderMatch = VersionRegex().Match(folder);
        return folderMatch.Success ? folderMatch.Value : "";
    }

    private static IEnumerable<Process> OpenTabletDriverProcesses()
    {
        foreach (var name in new[] { "OpenTabletDriver.UX.Wpf", "OpenTabletDriver.Daemon" })
        {
            Process[] processes;
            try { processes = Process.GetProcessesByName(name); }
            catch { continue; }
            foreach (var process in processes)
                yield return process;
        }
    }

    private static bool AnyProcess(Func<Process, bool> predicate)
    {
        var found = false;
        foreach (var process in OpenTabletDriverProcesses())
        {
            using (process)
            {
                try { found |= predicate(process); }
                catch { }
            }
        }
        return found;
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string root, string pattern)
    {
        try { return Directory.EnumerateDirectories(root, pattern); }
        catch { return Array.Empty<string>(); }
    }

    internal sealed record OtdMonitor(string DeviceName, int X, int Y, int Width, int Height);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public short SpecVersion;
        public short DriverVersion;
        public short Size;
        public short DriverExtra;
        public int Fields;
        public int PositionX;
        public int PositionY;
        public int DisplayOrientation;
        public int DisplayFixedOutput;
        public short Color;
        public short Duplex;
        public short YResolution;
        public short TTOption;
        public short Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
        public short LogPixels;
        public int BitsPerPixel;
        public int Width;
        public int Height;
        public int DisplayFlags;
        public int DisplayFrequency;
    }

    [DllImport("user32.dll", EntryPoint = "EnumDisplayDevicesW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(
        string? device, int deviceNumber, ref DisplayDevice displayDevice, int flags);

    [DllImport("user32.dll", EntryPoint = "EnumDisplaySettingsW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettings(string deviceName, int modeNumber, ref DevMode mode);

    [GeneratedRegex(@"\d+\.\d+\.\d+(?:\.\d+)?")]
    private static partial Regex VersionRegex();
}
