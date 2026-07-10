using System.Diagnostics;

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
}
