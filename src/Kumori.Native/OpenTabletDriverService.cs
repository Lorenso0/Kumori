using System.Diagnostics;
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
    private const string OtdDaemonExe = "OpenTabletDriver.Daemon.exe";
    private const string SupportedVersion = "0.6.7";
    private static readonly object OwnedGate = new();
    private static readonly HashSet<int> OwnedProcessIds = new();

    public static OpenTabletDriverInstallation? Detect(string configuredPath = "")
    {
        var executable = CandidatePaths(configuredPath).FirstOrDefault(File.Exists);
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
        if (IsUiRunning())
        {
            return false;
        }
        if (!File.Exists(executablePath))
        {
            throw new FileNotFoundException("OpenTabletDriver executable was not found.", executablePath);
        }

        var before = OpenTabletDriverProcesses().Select(p =>
        {
            using (p) { return p.Id; }
        }).ToHashSet();
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = Path.GetDirectoryName(executablePath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        });
        if (process is not null)
        {
            TrackOwnedProcesses(before, process.Id);
            process.Dispose();
        }
        return true;
    }

    public static void CloseOwned()
    {
        int[] owned;
        lock (OwnedGate)
        {
            owned = OwnedProcessIds.ToArray();
            OwnedProcessIds.Clear();
        }
        if (owned.Length == 0)
        {
            return;
        }
        foreach (var process in OpenTabletDriverProcesses())
        {
            using (process)
            {
                if (!owned.Contains(process.Id))
                {
                    continue;
                }
                try { process.CloseMainWindow(); } catch { }
                try
                {
                    if (!process.WaitForExit(3000))
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch { }
            }
        }
    }

    public static bool IsUiRunning() =>
        OpenTabletDriverProcesses().Any(p =>
        {
            using (p)
            {
                return string.Equals(p.ProcessName, "OpenTabletDriver.UX.Wpf", StringComparison.OrdinalIgnoreCase);
            }
        });

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

    private static OpenTabletDriverInstallation Versionless(string executablePath) =>
        new(Path.GetFullPath(executablePath), "", false,
            Directory.Exists(Path.Combine(Path.GetDirectoryName(executablePath)!, "userdata")));

    private static string ReadVersion(string executablePath)
    {
        var daemon = Path.Combine(Path.GetDirectoryName(executablePath)!, OtdDaemonExe);
        if (File.Exists(daemon))
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = daemon,
                    ArgumentList = { "--version" },
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                });
                if (process is not null && process.WaitForExit(4000))
                {
                    var text = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
                    var match = VersionRegex().Match(text);
                    if (match.Success)
                    {
                        return match.Value;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "OpenTabletDriver version probe failed");
            }
        }

        var folder = Path.GetFileName(Path.GetDirectoryName(executablePath)) ?? "";
        var folderMatch = VersionRegex().Match(folder);
        return folderMatch.Success ? folderMatch.Value : "";
    }

    private static IEnumerable<Process> OpenTabletDriverProcesses() =>
        Process.GetProcesses().Where(p =>
            string.Equals(p.ProcessName, "OpenTabletDriver.UX.Wpf", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(p.ProcessName, "OpenTabletDriver.Daemon", StringComparison.OrdinalIgnoreCase));

    private static void TrackOwnedProcesses(HashSet<int> before, int directProcessId)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        var found = new HashSet<int> { directProcessId };
        while (DateTimeOffset.UtcNow < deadline)
        {
            foreach (var process in OpenTabletDriverProcesses())
            {
                using (process)
                {
                    if (!before.Contains(process.Id))
                    {
                        found.Add(process.Id);
                    }
                }
            }
            if (found.Count > 1 || IsUiRunning())
            {
                break;
            }
            Thread.Sleep(200);
        }
        lock (OwnedGate)
        {
            foreach (var id in found)
            {
                OwnedProcessIds.Add(id);
            }
        }
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string root, string pattern)
    {
        try { return Directory.EnumerateDirectories(root, pattern); }
        catch { return Array.Empty<string>(); }
    }

    [GeneratedRegex(@"\d+\.\d+\.\d+(?:\.\d+)?")]
    private static partial Regex VersionRegex();
}
