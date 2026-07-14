using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Kumori.Native;

public static class OsuProcessDetector
{
    private static readonly HashSet<string> ProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "osu!",
        "osu",
        "osu.Desktop",
        "osulazer",
    };

    public static bool IsRunning()
    {
        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch
        {
            return false;
        }

        var found = false;
        foreach (var process in processes)
        {
            try
            {
                if (!found && ProcessNames.Contains(process.ProcessName) && !process.HasExited)
                    found = true;
            }
            catch
            {
                // The process can exit or deny access while it is inspected.
            }
            finally
            {
                process.Dispose();
            }
        }
        return found;
    }

    /// <summary>Returns identities for the currently running osu! client processes.</summary>
    public static IReadOnlySet<int> RunningProcessIds()
    {
        var result = new HashSet<int>();
        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch
        {
            return result;
        }

        foreach (var process in processes)
        {
            try
            {
                if (ProcessNames.Contains(process.ProcessName) && !process.HasExited)
                {
                    result.Add(process.Id);
                }
            }
            catch
            {
                // The process can exit or deny access while it is inspected.
            }
            finally
            {
                process.Dispose();
            }
        }
        return result;
    }

    /// <summary>
    /// Suspends every running osu! client as one all-or-nothing lease. Disposing
    /// the lease resumes every process, including when display switching fails.
    /// </summary>
    public static OsuProcessSuspension? TrySuspendRunning()
    {
        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch
        {
            return null;
        }

        var candidates = new List<Process>();
        foreach (var process in processes)
        {
            try
            {
                if (ProcessNames.Contains(process.ProcessName) && !process.HasExited)
                {
                    candidates.Add(process);
                    continue;
                }
            }
            catch
            {
                // A process can exit while the snapshot is being filtered.
            }
            process.Dispose();
        }

        var suspended = new List<Process>();
        var failed = false;
        foreach (var process in candidates)
        {
            try
            {
                if (process.HasExited)
                {
                    process.Dispose();
                    continue;
                }

                if (NativeMethods.NtSuspendProcess(process.Handle) != 0)
                {
                    failed = true;
                    process.Dispose();
                    break;
                }
                suspended.Add(process);
            }
            catch
            {
                failed = true;
                process.Dispose();
                break;
            }
        }

        if (failed || suspended.Count == 0)
        {
            foreach (var process in candidates.Where(process => !suspended.Contains(process)))
            {
                process.Dispose();
            }
            ResumeAndDispose(suspended);
            return null;
        }

        return new OsuProcessSuspension(suspended);
    }

    public sealed class OsuProcessSuspension : IDisposable
    {
        private List<Process>? processes;

        internal OsuProcessSuspension(List<Process> processes) => this.processes = processes;

        public void Dispose()
        {
            var suspended = Interlocked.Exchange(ref processes, null);
            if (suspended is not null)
            {
                ResumeAndDispose(suspended);
            }
        }
    }

    private static void ResumeAndDispose(IEnumerable<Process> processes)
    {
        foreach (var process in processes)
        {
            try
            {
                if (!process.HasExited)
                    _ = NativeMethods.NtResumeProcess(process.Handle);
            }
            catch
            {
                // The process may have closed while the monitor was switching.
            }
            finally
            {
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
