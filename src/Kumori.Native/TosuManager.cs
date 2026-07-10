using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using Kumori.Core;
using Serilog;

namespace Kumori.Native;

public sealed record TosuInstallResult(string Version, string ExecutablePath, bool InstalledOrUpdated);

public static class TosuManager
{
    public const string SourceUrl = "https://github.com/tosuapp/tosu";
    public const string ReleasesUrl = "https://github.com/tosuapp/tosu/releases";
    private const string LatestReleaseUrl = "https://api.github.com/repos/tosuapp/tosu/releases/latest";
    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(24);
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromMinutes(3),
    };
    private static readonly object ProcessGate = new();
    private static Process? _ownedProcess;

    static TosuManager()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("Kumori");
    }

    public static async Task<TosuInstallResult> EnsureInstalledAsync(
        bool forceCheck = false,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(AppPaths.TosuDir);
        var local = ReadLocalVersion();
        if (File.Exists(AppPaths.TosuExecutable) && !forceCheck && RecentUpdateCheck())
        {
            TryVerifyWindowsSignature(AppPaths.TosuExecutable);
            EnsureEnvironment();
            return new TosuInstallResult(local, AppPaths.TosuExecutable, false);
        }

        var release = await GetLatestReleaseAsync(cancellationToken).ConfigureAwait(false);
        if (File.Exists(AppPaths.TosuExecutable) && string.Equals(local, release.Version, StringComparison.OrdinalIgnoreCase))
        {
            TryVerifyWindowsSignature(AppPaths.TosuExecutable);
            MarkUpdateChecked(release.Version);
            EnsureEnvironment();
            return new TosuInstallResult(release.Version, AppPaths.TosuExecutable, false);
        }

        var downloadPath = Path.Combine(AppPaths.TosuDir, "tosu.download");
        await DownloadAsync(release.Url, downloadPath, cancellationToken).ConfigureAwait(false);
        Log.Information("Downloaded tosu {Version} ({Bytes} bytes)", release.Version, new FileInfo(downloadPath).Length);

        var candidatePath = downloadPath;
        if (release.Url.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            candidatePath = Path.Combine(AppPaths.TosuDir, "tosu.archive.exe");
            ExtractExecutable(downloadPath, candidatePath);
        }

        ValidateExecutable(candidatePath);
        TryVerifyWindowsSignature(candidatePath);

        var tempExe = Path.Combine(AppPaths.TosuDir, "tosu.new.exe");
        File.Copy(candidatePath, tempExe, overwrite: true);
        StripZoneIdentifier(tempExe);
        File.Move(tempExe, AppPaths.TosuExecutable, overwrite: true);
        File.WriteAllText(AppPaths.TosuVersionFile, release.Version);
        SafeDelete(downloadPath);
        SafeDelete(Path.Combine(AppPaths.TosuDir, "tosu.archive.exe"));
        SafeDelete(Path.Combine(AppPaths.TosuDir, "tosu-kumori.exe"));
        SafeDelete(Path.Combine(AppPaths.TosuDir, "tosu-kumori.download"));
        SafeDelete(Path.Combine(AppPaths.TosuDir, "tosu-kumori.archive.exe"));
        SafeDelete(Path.Combine(AppPaths.TosuDir, "tosu-kumori.new.exe"));
        MarkUpdateChecked(release.Version);
        EnsureEnvironment();
        Log.Information("Installed tosu {Version}", release.Version);
        return new TosuInstallResult(release.Version, AppPaths.TosuExecutable, true);
    }

    public static async Task<Process> EnsureInstalledAndLaunchAsync(
        bool forceCheck = false,
        CancellationToken cancellationToken = default)
    {
        var install = await EnsureInstalledAsync(forceCheck, cancellationToken).ConfigureAwait(false);
        EnsureEnvironment();

        lock (ProcessGate)
        {
            if (_ownedProcess is { HasExited: false })
            {
                return _ownedProcess;
            }
            _ownedProcess?.Dispose();
            _ownedProcess = null;

            var running = FindManagedProcess();
            if (running is not null)
            {
                _ownedProcess = running;
                return _ownedProcess;
            }

            _ownedProcess = Process.Start(new ProcessStartInfo
            {
                FileName = install.ExecutablePath,
                WorkingDirectory = AppPaths.TosuDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            }) ?? throw new InvalidOperationException("tosu did not start.");
            return _ownedProcess;
        }
    }

    public static void CloseOwned()
    {
        Process? process;
        lock (ProcessGate)
        {
            process = _ownedProcess;
            _ownedProcess = null;
        }

        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                try { process.CloseMainWindow(); } catch { }
                if (!process.WaitForExit(2_000))
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(2_000);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to close owned tosu process");
        }
        finally
        {
            process.Dispose();
        }
    }

    public static void EnsureEnvironment()
    {
        Directory.CreateDirectory(AppPaths.TosuDir);
        Directory.CreateDirectory(AppPaths.TosuLogDir);
        MoveToolLocalLogs();
        var lines = File.Exists(AppPaths.TosuEnvFile)
            ? File.ReadAllLines(AppPaths.TosuEnvFile).ToList()
            : new List<string>();
        Upsert("OPEN_DASHBOARD_ON_STARTUP", "false");
        Upsert("ENABLE_INGAME_OVERLAY", "false");
        Upsert("INGAME_OVERLAY_KEYBIND", "");
        Upsert("SERVER_IP", "127.0.0.1");
        Upsert("SERVER_PORT", "24051");
        File.WriteAllLines(AppPaths.TosuEnvFile, lines);

        void Upsert(string key, string value)
        {
            var index = lines.FindIndex(line =>
                string.Equals(line.Split('=', 2)[0].Trim(), key, StringComparison.OrdinalIgnoreCase));
            var replacement = $"{key}={value}";
            if (index >= 0)
            {
                lines[index] = replacement;
            }
            else
            {
                lines.Add(replacement);
            }
        }
    }

    private static void MoveToolLocalLogs()
    {
        var source = Path.Combine(AppPaths.TosuDir, "logs");
        if (!Directory.Exists(source))
        {
            return;
        }

        Directory.CreateDirectory(AppPaths.TosuLogDir);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).ToArray())
        {
            try
            {
                var relative = Path.GetRelativePath(source, file);
                var destination = Path.Combine(AppPaths.TosuLogDir, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                if (!File.Exists(destination))
                {
                    File.Move(file, destination);
                }
                else
                {
                    File.Delete(file);
                }
            }
            catch
            {
            }
        }

        try { Directory.Delete(source, recursive: true); } catch { }
    }

    private static async Task<(string Version, string Url)> GetLatestReleaseAsync(CancellationToken cancellationToken)
    {
        using var response = await Http.GetAsync(LatestReleaseUrl, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = doc.RootElement;
        var version = root.TryGetProperty("tag_name", out var tag)
            ? (tag.GetString() ?? "").TrimStart('v', 'V')
            : "";
        if (string.IsNullOrWhiteSpace(version))
        {
            throw new InvalidOperationException("The latest tosu release did not include a version tag.");
        }

        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("The latest tosu release has no downloadable assets.");
        }

        var compatible = new List<(bool NonX64, string Name, string Url)>();
        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var nameElement)
                ? nameElement.GetString() ?? ""
                : "";
            var lower = name.ToLowerInvariant();
            var windows = lower.Contains("windows") || lower.Contains("win") || lower == "tosu.exe";
            var x64 = new[] { "x64", "x86_64", "amd64", "win64", "64-bit" }.Any(lower.Contains);
            var wrongArch = new[] { "arm", "aarch", "i386", "i686", "32-bit" }.Any(lower.Contains);
            var compatibleExtension = lower.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                                      lower.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
            if (!windows || wrongArch || !compatibleExtension)
            {
                continue;
            }
            if (!asset.TryGetProperty("browser_download_url", out var urlElement))
            {
                continue;
            }
            var url = urlElement.GetString();
            if (!string.IsNullOrWhiteSpace(url))
            {
                compatible.Add((!x64, lower, url));
            }
        }

        var selected = compatible
            .OrderBy(c => c.NonX64)
            .ThenBy(c => c.Name == "tosu.exe" ? 0 : 1)
            .ThenBy(c => c.Name)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(selected.Url))
        {
            throw new InvalidOperationException("The latest tosu release has no compatible Windows asset.");
        }
        return (version, selected.Url);
    }

    private static async Task DownloadAsync(string url, string destination, CancellationToken cancellationToken)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = File.Create(destination);
        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
    }

    private static void ExtractExecutable(string archivePath, string destination)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var entry = archive.Entries.FirstOrDefault(e =>
            string.Equals(Path.GetFileName(e.FullName.Replace('\\', '/')), "tosu.exe", StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            throw new InvalidOperationException("Downloaded tosu archive did not contain tosu.exe.");
        }
        entry.ExtractToFile(destination, overwrite: true);
    }

    private static void ValidateExecutable(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[2];
        if (stream.Read(header) != 2 || header[0] != (byte)'M' || header[1] != (byte)'Z')
        {
            throw new InvalidOperationException("Downloaded tosu executable failed validation.");
        }
    }

    private static bool TryVerifyWindowsSignature(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }
        var command = "$signature=Get-AuthenticodeSignature -LiteralPath $env:KUMORI_TOSU_VERIFY_PATH;$signature.Status.ToString()";
        var start = new ProcessStartInfo
        {
            FileName = "powershell",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add(command);
        start.Environment["KUMORI_TOSU_VERIFY_PATH"] = path;
        using var process = Process.Start(start);
        if (process is null)
        {
            Log.Warning("Could not start PowerShell for tosu signature validation.");
            return false;
        }
        if (!process.WaitForExit(30_000))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            Log.Warning("tosu signature validation timed out.");
            return false;
        }
        var stdout = process.StandardOutput.ReadToEnd().Trim();
        var stderr = process.StandardError.ReadToEnd().Trim();
        if (process.ExitCode != 0 || !string.Equals(stdout, "Valid", StringComparison.OrdinalIgnoreCase))
        {
            Log.Warning(
                "tosu signature validation did not report Valid: {Status}",
                stdout.NullIfEmpty() ?? stderr.NullIfEmpty() ?? "unknown status");
            return false;
        }
        return true;
    }

    private static Process? FindManagedProcess()
    {
        var target = Path.GetFullPath(AppPaths.TosuExecutable);
        foreach (var process in Process.GetProcessesByName("tosu"))
        {
            try
            {
                if (string.Equals(process.MainModule?.FileName, target, StringComparison.OrdinalIgnoreCase))
                {
                    return process;
                }
            }
            catch
            {
                process.Dispose();
            }
        }
        return null;
    }

    private static string ReadLocalVersion()
    {
        try
        {
            return File.Exists(AppPaths.TosuVersionFile)
                ? File.ReadAllText(AppPaths.TosuVersionFile).Trim()
                : "";
        }
        catch
        {
            return "";
        }
    }

    private static bool RecentUpdateCheck()
    {
        try
        {
            // The v2 model does not need to expose this implementation detail;
            // file time gives us the same cheap 24h throttle for managed tosu.
            return File.Exists(AppPaths.TosuVersionFile) &&
                   DateTimeOffset.UtcNow - File.GetLastWriteTimeUtc(AppPaths.TosuVersionFile) < UpdateCheckInterval;
        }
        catch
        {
            return false;
        }
    }

    private static void MarkUpdateChecked(string version)
    {
        Directory.CreateDirectory(AppPaths.TosuDir);
        File.WriteAllText(AppPaths.TosuVersionFile, version);
    }

    private static void StripZoneIdentifier(string path)
    {
        try { File.Delete(path + ":Zone.Identifier"); } catch { }
    }

    private static void SafeDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    private static string? NullIfEmpty(this string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
