using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Net.WebSockets;
using System.Security.Cryptography;
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
    private const long MaxDownloadBytes = 250L * 1024 * 1024;
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromMinutes(3),
    };
    private static readonly object ProcessGate = new();
    private static readonly object RestartTaskGate = new();
    private static readonly SemaphoreSlim InstallGate = new(1, 1);
    private static readonly SemaphoreSlim RestartGate = new(1, 1);
    private static Process? _ownedProcess;
    private static Task<Process>? _restartTask;

    static TosuManager()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("Kumori");
    }

    public static async Task<TosuInstallResult> EnsureInstalledAsync(
        bool forceCheck = false,
        CancellationToken cancellationToken = default)
    {
        await InstallGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await EnsureInstalledCoreAsync(forceCheck, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            InstallGate.Release();
        }
    }

    public static async Task<TosuInstallResult> CheckForUpdatesAsync(
        CancellationToken cancellationToken = default)
    {
        return await EnsureInstalledAsync(forceCheck: true, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<TosuInstallResult> EnsureInstalledCoreAsync(
        bool forceCheck,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(AppPaths.TosuDir);
        var local = ReadLocalVersion();
        if (File.Exists(AppPaths.TosuExecutable) && !forceCheck && RecentUpdateCheck() && InstalledDigestIsValid(cancellationToken))
        {
            EnsureEnvironment();
            return new TosuInstallResult(local, AppPaths.TosuExecutable, false);
        }

        var release = await GetLatestReleaseAsync(cancellationToken).ConfigureAwait(false);
        if (File.Exists(AppPaths.TosuExecutable) &&
            string.Equals(local, release.Version, StringComparison.OrdinalIgnoreCase) &&
            InstalledDigestIsValid(cancellationToken))
        {
            MarkUpdateChecked(release.Version);
            EnsureEnvironment();
            return new TosuInstallResult(release.Version, AppPaths.TosuExecutable, false);
        }

        var downloadPath = Path.Combine(AppPaths.TosuDir, "tosu.download");
        await DownloadAsync(release.Url, downloadPath, cancellationToken).ConfigureAwait(false);
        if (!VerifyDigest(downloadPath, release.Digest, cancellationToken))
        {
            throw new InvalidDataException("Downloaded tosu asset did not match GitHub's SHA-256 digest.");
        }
        Log.Information("Downloaded tosu {Version} ({Bytes} bytes)", release.Version, new FileInfo(downloadPath).Length);

        var candidatePath = downloadPath;
        if (release.Url.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            candidatePath = Path.Combine(AppPaths.TosuDir, "tosu.archive.exe");
            ExtractExecutable(downloadPath, candidatePath, cancellationToken);
        }

        ValidateExecutable(candidatePath);
        ValidateWindowsSignaturePolicy(candidatePath);

        var tempExe = Path.Combine(AppPaths.TosuDir, "tosu.new.exe");
        File.Copy(candidatePath, tempExe, overwrite: true);
        StripZoneIdentifier(tempExe);
        File.Move(tempExe, AppPaths.TosuExecutable, overwrite: true);
        File.WriteAllText(AppPaths.TosuVersionFile, release.Version);
        File.WriteAllText(AppPaths.TosuDigestFile, ComputeDigest(AppPaths.TosuExecutable, cancellationToken));
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
        return LaunchInstalledProcess(install.ExecutablePath);
    }

    private static Process LaunchInstalledProcess(string executablePath)
    {
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
                FileName = executablePath,
                WorkingDirectory = AppPaths.TosuDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            }) ?? throw new InvalidOperationException("tosu did not start.");
            return _ownedProcess;
        }
    }

    public static Process LaunchInstalled()
    {
        if (!File.Exists(AppPaths.TosuExecutable))
            throw new FileNotFoundException("Managed tosu is not installed.", AppPaths.TosuExecutable);
        return LaunchInstalledProcess(AppPaths.TosuExecutable);
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
        MoveToolLocalLogs();
    }

    /// <summary>
    /// Restarts Kumori's managed tosu process and returns the replacement process.
    /// Concurrent recovery notifications join the same mandatory restart instead
    /// of repeatedly tearing down an already-recovering tracker.
    /// </summary>
    public static Task<Process> RestartAsync(
        CancellationToken cancellationToken = default,
        Func<Func<Process>, Process>? processTransition = null,
        string reason = "replay-based result recovery")
    {
        Task<Process> restart;
        lock (RestartTaskGate)
        {
            if (_restartTask is { IsCompleted: false } active)
            {
                restart = active;
            }
            else
            {
                restart = RestartCoreAsync(cancellationToken, processTransition, reason);
                _restartTask = restart;
                _ = restart.ContinueWith(
                    completed =>
                    {
                        lock (RestartTaskGate)
                        {
                            if (ReferenceEquals(_restartTask, completed))
                                _restartTask = null;
                        }
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }

        return cancellationToken.CanBeCanceled
            ? restart.WaitAsync(cancellationToken)
            : restart;
    }

    private static async Task<Process> RestartCoreAsync(
        CancellationToken cancellationToken,
        Func<Func<Process>, Process>? processTransition,
        string reason)
    {
        await RestartGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Log.Warning("Restarting tosu after {Reason}", reason);
            Exception? firstFailure = null;
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Process ReplaceProcess()
                {
                    CloseOwnedForRestart();
                    // Once close begins, always finish relaunch. This brief
                    // transition is intentionally synchronous so the app can
                    // exclude attempt start without holding that boundary over
                    // the websocket readiness wait.
                    Thread.Sleep(100);
                    return File.Exists(AppPaths.TosuExecutable)
                        ? LaunchInstalledProcess(AppPaths.TosuExecutable)
                        : throw new FileNotFoundException("Managed tosu is not installed.", AppPaths.TosuExecutable);
                }

                var process = processTransition is null
                    ? ReplaceProcess()
                    : processTransition(ReplaceProcess);
                try
                {
                    await WaitForWebSocketAsync(process, cancellationToken).ConfigureAwait(false);
                    Log.Information("tosu restarted successfully with process id {ProcessId} and a ready websocket", process.Id);
                    return process;
                }
                catch (Exception ex) when (attempt == 1 && ex is not OperationCanceledException)
                {
                    firstFailure = ex;
                    Log.Warning(ex, "tosu restart process was not ready; retrying once");
                }
            }
            throw new InvalidOperationException("tosu did not expose its websocket after two restart attempts.", firstFailure);
        }
        finally
        {
            RestartGate.Release();
        }
    }

    private static void CloseOwnedForRestart()
    {
        Process? process;
        lock (ProcessGate)
        {
            process = _ownedProcess;
            _ownedProcess = null;
        }
        // A previous Kumori process may have terminated without taking its
        // managed tosu child with it. Never adopt that stale process for a new
        // osu! client: its cached memory pointers belong to the earlier launch.
        process ??= FindManagedProcess();
        if (process is null)
            return;

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(1_000);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to stop the previous managed tosu process");
        }
        finally
        {
            process.Dispose();
        }
    }

    private static async Task WaitForWebSocketAsync(Process process, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        Exception? lastError = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
                throw new InvalidOperationException($"tosu exited during restart with code {process.ExitCode}.");

            using var socket = new ClientWebSocket();
            using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            probeCts.CancelAfter(TimeSpan.FromSeconds(1));
            try
            {
                await socket.ConnectAsync(new Uri("ws://127.0.0.1:24051/websocket/v2"), probeCts.Token).ConfigureAwait(false);
                if (socket.State == WebSocketState.Open)
                {
                    socket.Abort();
                    return;
                }
            }
            catch (Exception ex) when ((ex is WebSocketException or OperationCanceledException) && !cancellationToken.IsCancellationRequested)
            {
                lastError = ex;
            }
            await Task.Delay(300, cancellationToken).ConfigureAwait(false);
        }
        throw new TimeoutException("tosu process started but its websocket did not become ready within 20 seconds.", lastError);
    }

    public static void EnsureEnvironment()
    {
        Directory.CreateDirectory(AppPaths.TosuDir);
        Directory.CreateDirectory(AppPaths.TosuLogDir);
        var lines = File.Exists(AppPaths.TosuEnvFile)
            ? File.ReadAllLines(AppPaths.TosuEnvFile).ToList()
            : new List<string>();
        var original = lines.ToArray();
        Upsert("OPEN_DASHBOARD_ON_STARTUP", "false");
        Upsert("ENABLE_INGAME_OVERLAY", "false");
        Upsert("ENABLE_KEY_OVERLAY", "false");
        Upsert("ENABLE_AUTOUPDATE", "false");
        Upsert("INGAME_OVERLAY_KEYBIND", "");
        Upsert("SERVER_IP", "127.0.0.1");
        Upsert("SERVER_PORT", "24051");
        if (!original.SequenceEqual(lines, StringComparer.Ordinal))
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

    private static async Task<(string Version, string Url, string Digest)> GetLatestReleaseAsync(CancellationToken cancellationToken)
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

        var compatible = new List<(bool NonX64, string Name, string Url, string Digest)>();
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
            var digest = asset.TryGetProperty("digest", out var digestElement)
                ? digestElement.GetString() ?? ""
                : "";
            if (!string.IsNullOrWhiteSpace(url) && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            {
                compatible.Add((!x64, lower, url, digest));
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
        return (version, selected.Url, selected.Digest);
    }

    private static async Task DownloadAsync(string url, string destination, CancellationToken cancellationToken)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaxDownloadBytes)
        {
            throw new InvalidDataException("tosu download exceeds the 250 MB safety limit.");
        }
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = File.Create(destination);
        var buffer = new byte[1024 * 1024];
        long total = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
            if (total > MaxDownloadBytes)
            {
                throw new InvalidDataException("tosu download exceeds the 250 MB safety limit.");
            }
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    private static void ExtractExecutable(
        string archivePath,
        string destination,
        CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var entry = archive.Entries.FirstOrDefault(e =>
            string.Equals(Path.GetFileName(e.FullName.Replace('\\', '/')), "tosu.exe", StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            throw new InvalidOperationException("Downloaded tosu archive did not contain tosu.exe.");
        }
        if (entry.Length is <= 0 or > MaxDownloadBytes)
        {
            throw new InvalidDataException("Downloaded tosu executable exceeds the extraction size limit.");
        }

        var temporary = $"{destination}.{Environment.ProcessId}.extracting";
        try
        {
            using var input = entry.Open();
            using (var output = new FileStream(
                       temporary,
                       FileMode.Create,
                       FileAccess.Write,
                       FileShare.None,
                       64 * 1024,
                       FileOptions.SequentialScan))
            {
                var buffer = GC.AllocateUninitializedArray<byte>(64 * 1024);
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    output.Write(buffer, 0, read);
                }
                cancellationToken.ThrowIfCancellationRequested();
                output.Flush(flushToDisk: false);
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
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

    private static bool InstalledDigestIsValid(CancellationToken cancellationToken)
    {
        try
        {
            return File.Exists(AppPaths.TosuDigestFile) &&
                   VerifyDigest(AppPaths.TosuExecutable, File.ReadAllText(AppPaths.TosuDigestFile), cancellationToken);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool VerifyDigest(
        string path,
        string expectedDigest,
        CancellationToken cancellationToken = default)
    {
        if (!expectedDigest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) return false;
        var expected = expectedDigest["sha256:".Length..].Trim();
        if (expected.Length != 64) return false;
        var actual = ComputeDigest(path, cancellationToken)["sha256:".Length..];
        return actual.Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    private static string ComputeDigest(string path, CancellationToken cancellationToken = default)
    {
        using var stream = File.OpenRead(path);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = GC.AllocateUninitializedArray<byte>(64 * 1024);
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, read);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return "sha256:" + Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void ValidateWindowsSignaturePolicy(string path)
    {
        var status = GetWindowsSignatureStatus(path);
        if (status.Equals("Valid", StringComparison.OrdinalIgnoreCase))
        {
            Log.Information("tosu Authenticode signature is valid");
            return;
        }

        // Official tosu releases are not consistently Authenticode-signed. A
        // missing signature therefore uses the already-mandatory GitHub SHA-256
        // digest policy. An explicitly invalid or untrusted signature is a
        // stronger signal and must never be installed.
        if (status.Equals("NotSigned", StringComparison.OrdinalIgnoreCase) ||
            status.Equals("Unavailable", StringComparison.OrdinalIgnoreCase))
        {
            Log.Warning(
                "tosu Authenticode status is {Status}; continuing under the verified GitHub SHA-256 digest policy",
                status);
            return;
        }

        throw new InvalidDataException($"Downloaded tosu executable has unacceptable Authenticode status: {status}.");
    }

    private static string GetWindowsSignatureStatus(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return "Unavailable";
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
            return "Unavailable";
        }
        if (!process.WaitForExit(30_000))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            Log.Warning("tosu signature validation timed out.");
            return "Unavailable";
        }
        var stdout = process.StandardOutput.ReadToEnd().Trim();
        var stderr = process.StandardError.ReadToEnd().Trim();
        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout))
        {
            Log.Warning(
                "tosu signature validation did not report Valid: {Status}",
                stdout.NullIfEmpty() ?? stderr.NullIfEmpty() ?? "unknown status");
            return "Unavailable";
        }
        return stdout;
    }

    private static Process? FindManagedProcess()
    {
        var target = Path.GetFullPath(AppPaths.TosuExecutable);
        Process? match = null;
        foreach (var process in Process.GetProcessesByName("tosu"))
        {
            try
            {
                if (match is null && string.Equals(process.MainModule?.FileName, target, StringComparison.OrdinalIgnoreCase))
                {
                    match = process;
                }
                else
                {
                    process.Dispose();
                }
            }
            catch
            {
                process.Dispose();
            }
        }
        return match;
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
