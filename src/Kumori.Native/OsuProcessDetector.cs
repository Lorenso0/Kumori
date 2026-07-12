using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Kumori.Native;

public static class OsuProcessDetector
{
    private static readonly string[] ProcessNames =
    {
        "osu!",
        "osu",
        "osu.Desktop",
        "osulazer",
    };

    public static bool IsRunning()
    {
        foreach (var name in ProcessNames)
        {
            try
            {
                var processes = Process.GetProcessesByName(name);
                var running = processes.Length > 0;
                foreach (var process in processes)
                {
                    process.Dispose();
                }
                if (running)
                {
                    return true;
                }
            }
            catch
            {
            }
        }
        return false;
    }

    /// <summary>Returns identities for the currently running osu! client processes.</summary>
    public static IReadOnlySet<int> RunningProcessIds()
    {
        var result = new HashSet<int>();
        foreach (var name in ProcessNames)
        {
            try
            {
                foreach (var process in Process.GetProcessesByName(name))
                {
                    try
                    {
                        if (!process.HasExited)
                        {
                            result.Add(process.Id);
                        }
                    }
                    catch
                    {
                        // The process can exit while it is being inspected.
                    }
                    finally
                    {
                        process.Dispose();
                    }
                }
            }
            catch
            {
            }
        }
        return result;
    }

    /// <summary>
    /// Stops the current osu! process(es) and returns the executable paths needed
    /// to launch the same client again. Nothing is stopped if the executable path
    /// cannot be determined, so the caller never leaves the user unable to reopen osu!.
    /// </summary>
    public static IReadOnlyList<string> StopAndCaptureLaunchPaths()
    {
        var processes = ProcessNames
            .SelectMany(Process.GetProcessesByName)
            .GroupBy(process => process.Id)
            .Select(group => group.First())
            .ToArray();
        var launchPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var process in processes)
            {
                try
                {
                    var path = process.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                    {
                        launchPaths.Add(path);
                    }
                }
                catch
                {
                    // Access to another user's process is denied; do not stop it.
                }
            }

            if (launchPaths.Count == 0)
            {
                return [];
            }

            foreach (var process in processes)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        process.WaitForExit(5_000);
                    }
                }
                catch
                {
                    // The process may have closed while the display was enumerated.
                }
            }

            return launchPaths.ToArray();
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    public static void Launch(IEnumerable<string> executablePaths)
    {
        foreach (var executablePath in executablePaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = executablePath,
                WorkingDirectory = Path.GetDirectoryName(executablePath)!,
                UseShellExecute = true,
            });
        }
    }

    /// <summary>
    /// Suspends the osu! processes currently owned by this user. The returned
    /// lease always resumes every process it successfully suspended when disposed.
    /// </summary>
    public static OsuProcessSuspension? TrySuspendRunning()
    {
        var suspended = new List<Process>();
        foreach (var process in ProcessNames.SelectMany(Process.GetProcessesByName).GroupBy(p => p.Id).Select(g => g.First()))
        {
            try
            {
                if (!process.HasExited && NativeMethods.NtSuspendProcess(process.Handle) == 0)
                {
                    suspended.Add(process);
                    continue;
                }
            }
            catch
            {
            }
            process.Dispose();
        }

        if (suspended.Count == 0)
        {
            return null;
        }

        return new OsuProcessSuspension(suspended);
    }

    public sealed class OsuProcessSuspension : IDisposable
    {
        private List<Process>? _processes;

        internal OsuProcessSuspension(List<Process> processes) => _processes = processes;

        public void Dispose()
        {
            var processes = Interlocked.Exchange(ref _processes, null);
            if (processes is null)
            {
                return;
            }

            foreach (var process in processes)
            {
                try { _ = NativeMethods.NtResumeProcess(process.Handle); } catch { }
                process.Dispose();
            }
        }
    }

    private static class NativeMethods
    {
        [DllImport("ntdll.dll")]
        internal static extern int NtSuspendProcess(IntPtr processHandle);

        [DllImport("ntdll.dll")]
        internal static extern int NtResumeProcess(IntPtr processHandle);
    }
}
