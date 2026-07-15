using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Kumori.Core;

namespace Kumori.App;

internal sealed record KumoriUpdateDownloadProgress(long BytesReceived, long? TotalBytes)
{
    public int? Percentage => TotalBytes is > 0
        ? (int)Math.Clamp(BytesReceived * 100 / TotalBytes.Value, 0, 100)
        : null;
}

internal sealed record StagedKumoriUpdate(string Version, string TargetPath, string PackagePath, string Sha256);

internal sealed partial class KumoriUpdateInstaller
{
    private const string ApplyUpdateArgument = "--apply-update";
    private const string UpdateHealthArgument = "--update-health-token";
    internal static readonly TimeSpan UpdatedStartupTimeout = TimeSpan.FromMinutes(10);
    private static readonly HttpClient SharedHttp = CreateHttpClient();
    private readonly HttpClient http;

    public KumoriUpdateInstaller(HttpClient? http = null)
    {
        this.http = http ?? SharedHttp;
    }

    public static bool IsSupportedInstallation
    {
        get
        {
            var processPath = Environment.ProcessPath;
            return !string.IsNullOrWhiteSpace(processPath) &&
                   string.Equals(Path.GetExtension(processPath), ".exe", StringComparison.OrdinalIgnoreCase) &&
                   !File.Exists(Path.ChangeExtension(processPath, ".deps.json"));
        }
    }

    public async Task<StagedKumoriUpdate> StageAsync(
        KumoriUpdateResult update,
        IProgress<KumoriUpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsSupportedInstallation)
        {
            throw new InvalidOperationException("Automatic installation is only available in a published single-file Kumori build.");
        }
        if (update.ExecutableAsset is null || !update.CanAutoInstall)
        {
            throw new InvalidOperationException("This release does not include a verifiable Kumori.exe download.");
        }

        var targetPath = Path.GetFullPath(Environment.ProcessPath!);
        var targetDirectory = Path.GetDirectoryName(targetPath)
            ?? throw new InvalidOperationException("The Kumori installation directory could not be determined.");
        var packagePath = Path.Combine(targetDirectory, $".Kumori.update-{Guid.NewGuid():N}.exe");

        try
        {
            var expectedHash = await ResolveExpectedHashAsync(update, cancellationToken).ConfigureAwait(false);
            await DownloadAsync(update.ExecutableAsset, packagePath, progress, cancellationToken).ConfigureAwait(false);
            var actualHash = await ComputeSha256Async(packagePath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The downloaded update failed SHA-256 verification and was discarded.");
            }

            return new StagedKumoriUpdate(update.LatestTag, targetPath, packagePath, actualHash);
        }
        catch
        {
            TryDelete(packagePath);
            throw;
        }
    }

    public static void LaunchUpdater(StagedKumoriUpdate update)
    {
        var currentExecutable = Environment.ProcessPath
            ?? throw new InvalidOperationException("The current Kumori executable could not be determined.");
        var updaterDirectory = Path.Combine(AppPaths.RuntimeDir, "updates");
        Directory.CreateDirectory(updaterDirectory);
        var updaterPath = Path.Combine(updaterDirectory, $"Kumori.Updater-{Guid.NewGuid():N}.exe");
        File.Copy(currentExecutable, updaterPath, overwrite: false);

        var start = new ProcessStartInfo(updaterPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(update.TargetPath)!,
        };
        start.ArgumentList.Add(ApplyUpdateArgument);
        start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add(update.TargetPath);
        start.ArgumentList.Add(update.PackagePath);
        start.ArgumentList.Add(update.Sha256);
        _ = Process.Start(start) ?? throw new InvalidOperationException("The Kumori updater could not be started.");
    }

    public static void Discard(StagedKumoriUpdate update) => TryDelete(update.PackagePath);

    public static bool TryRunUpdater(IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 0 || !string.Equals(arguments[0], ApplyUpdateArgument, StringComparison.Ordinal))
        {
            return false;
        }

        if (arguments.Count != 5 ||
            !int.TryParse(arguments[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var processId))
        {
            return true;
        }

        ApplyUpdate(processId, arguments[2], arguments[3], arguments[4]);
        return true;
    }

    /// <summary>
    /// Completes the updater's health handshake. This is intentionally called
    /// only after the shell, database, and configured tracking runtime have
    /// initialized successfully.
    /// </summary>
    public static void SignalHealthy(IReadOnlyList<string> arguments) =>
        SignalHealthy(arguments, AppPaths.RuntimeDir);

    internal static bool SignalHealthy(IReadOnlyList<string> arguments, string runtimeDirectory)
    {
        var token = ParseHealthToken(arguments);
        if (token is null)
            return false;

        Directory.CreateDirectory(runtimeDirectory);
        var marker = HealthMarkerPath(runtimeDirectory, token);
        var temporary = marker + ".new";
        try
        {
            File.WriteAllText(temporary, DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            File.Move(temporary, marker, overwrite: true);
            return true;
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    internal static string? ParseHealthToken(IReadOnlyList<string> arguments)
    {
        for (var index = 0; index + 1 < arguments.Count; index++)
        {
            if (!string.Equals(arguments[index], UpdateHealthArgument, StringComparison.Ordinal))
                continue;
            return Guid.TryParseExact(arguments[index + 1], "N", out var token)
                ? token.ToString("N")
                : null;
        }
        return null;
    }

    public static void CleanupStaleFiles()
    {
        var updaterDirectory = Path.Combine(AppPaths.RuntimeDir, "updates");
        if (!Directory.Exists(updaterDirectory))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(updaterDirectory, "Kumori.Updater-*.exe"))
        {
            TryDelete(path);
        }
    }

    public static string? ConsumeFailure()
    {
        var path = Path.Combine(AppPaths.RuntimeDir, "update-error.txt");
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }
            var message = File.ReadAllText(path);
            File.Delete(path);
            return string.IsNullOrWhiteSpace(message) ? null : message.Trim();
        }
        catch
        {
            return null;
        }
    }

    internal static string ParseSha256(string text)
    {
        var match = Sha256Regex().Match(text ?? string.Empty);
        if (!match.Success)
        {
            throw new InvalidDataException("The release checksum is not a valid SHA-256 value.");
        }
        return match.Value.ToLowerInvariant();
    }

    private async Task<string> ResolveExpectedHashAsync(KumoriUpdateResult update, CancellationToken cancellationToken)
    {
        string? assetDigest = update.ExecutableAsset?.Digest;
        string? digestHash = assetDigest?.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) == true
            ? ParseSha256(assetDigest[7..])
            : null;

        if (update.ChecksumAsset is null)
        {
            return digestHash ?? throw new InvalidDataException("The release did not provide a SHA-256 checksum.");
        }

        ValidateDownloadUrl(update.ChecksumAsset.DownloadUrl);
        using var response = await http.GetAsync(update.ChecksumAsset.DownloadUrl, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        ValidateDownloadUrl(response.RequestMessage?.RequestUri?.AbsoluteUri ?? update.ChecksumAsset.DownloadUrl);
        var checksumText = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (checksumText.Length > 4096)
        {
            throw new InvalidDataException("The release checksum file is unexpectedly large.");
        }
        var checksumHash = ParseSha256(checksumText);
        if (digestHash is not null && !string.Equals(digestHash, checksumHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The release asset digest and checksum file do not match.");
        }
        return checksumHash;
    }

    private async Task DownloadAsync(
        KumoriReleaseAsset asset,
        string destination,
        IProgress<KumoriUpdateDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        ValidateDownloadUrl(asset.DownloadUrl);
        using var response = await http.GetAsync(asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        ValidateDownloadUrl(response.RequestMessage?.RequestUri?.AbsoluteUri ?? asset.DownloadUrl);
        var contentLength = response.Content.Headers.ContentLength ?? asset.Size;
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            1024 * 128,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[1024 * 128];
        long received = 0;
        progress?.Report(new KumoriUpdateDownloadProgress(0, contentLength));
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            received += read;
            progress?.Report(new KumoriUpdateDownloadProgress(received, contentLength));
        }
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);

        if (asset.Size is > 0 && received != asset.Size.Value)
        {
            throw new InvalidDataException($"The downloaded update size was {received} bytes; expected {asset.Size.Value} bytes.");
        }
    }

    private static void ApplyUpdate(int processId, string targetArgument, string packageArgument, string expectedHash)
    {
        var target = Path.GetFullPath(targetArgument);
        var package = Path.GetFullPath(packageArgument);
        var backup = Path.Combine(Path.GetDirectoryName(target)!, $".Kumori.previous-{Guid.NewGuid():N}.exe");
        var healthToken = Guid.NewGuid().ToString("N");
        var healthMarker = HealthMarkerPath(AppPaths.RuntimeDir, healthToken);
        var backupCreated = false;
        Process? updatedProcess = null;
        try
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (!process.WaitForExit((int)TimeSpan.FromMinutes(2).TotalMilliseconds))
                {
                    throw new TimeoutException("Kumori did not close in time for the update to be installed.");
                }
            }
            catch (ArgumentException)
            {
                // The application already exited.
            }

            var actualHash = ComputeSha256(package);
            if (!string.Equals(actualHash, ParseSha256(expectedHash), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The staged update changed after verification.");
            }

            CopyWithRetry(target, backup);
            backupCreated = true;
            MoveWithRetry(package, target, overwrite: true);
            if (!string.Equals(ComputeSha256(target), actualHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The installed update failed verification.");
            }

            TryDelete(Path.Combine(AppPaths.RuntimeDir, "update-error.txt"));
            TryDelete(healthMarker);
            updatedProcess = StartKumori(target, showChangelog: true, healthToken);
            WaitForHealthyStartup(updatedProcess, healthMarker);
            updatedProcess.Dispose();
            updatedProcess = null;
            TryDelete(backup);
        }
        catch (Exception ex)
        {
            StopUpdatedProcess(updatedProcess);
            updatedProcess?.Dispose();
            updatedProcess = null;

            Exception? restoreFailure = null;
            if (backupCreated)
            {
                try
                {
                    RestoreBackup(backup, target);
                }
                catch (Exception recoveryException)
                {
                    restoreFailure = recoveryException;
                }
            }

            var launchPath = restoreFailure is null && File.Exists(target)
                ? target
                : File.Exists(backup) ? backup : null;
            var recoveryDetail = restoreFailure is null
                ? "The previous version was restored."
                : $"Automatic recovery could not replace Kumori.exe. The previous executable was retained at '{backup}'.\n\nRecovery error: {restoreFailure.Message}";
            try
            {
                Directory.CreateDirectory(AppPaths.RuntimeDir);
                File.WriteAllText(
                    Path.Combine(AppPaths.RuntimeDir, "update-error.txt"),
                    $"Kumori could not finish installing the update. {recoveryDetail}\n\nUpdate error: {ex.Message}");
            }
            catch { }

            if (launchPath is not null)
            {
                try { StartKumori(launchPath, showChangelog: false, healthToken: null).Dispose(); } catch { }
            }
        }
        finally
        {
            TryDelete(healthMarker);
            TryDelete(package);
        }
    }

    private static Process StartKumori(string target, bool showChangelog, string? healthToken)
    {
        var start = new ProcessStartInfo(target)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(target)!,
        };
        if (showChangelog)
        {
            start.ArgumentList.Add("--show-changelog");
        }
        if (healthToken is not null)
        {
            start.ArgumentList.Add(UpdateHealthArgument);
            start.ArgumentList.Add(healthToken);
        }
        return Process.Start(start) ?? throw new InvalidOperationException("The updated Kumori executable could not be started.");
    }

    private static void WaitForHealthyStartup(Process process, string marker)
    {
        WaitForHealthyStartup(
            () => File.Exists(marker),
            () => process.HasExited,
            () => process.ExitCode,
            UpdatedStartupTimeout,
            Stopwatch.GetTimestamp,
            Stopwatch.GetElapsedTime,
            Thread.Sleep);
    }

    internal static void WaitForHealthyStartup(
        Func<bool> markerExists,
        Func<bool> hasExited,
        Func<int> getExitCode,
        TimeSpan timeout,
        Func<long> getTimestamp,
        Func<long, TimeSpan> getElapsedTime,
        Action<TimeSpan> delay)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        var startedAt = getTimestamp();
        while (getElapsedTime(startedAt) < timeout)
        {
            if (markerExists())
                return;
            if (hasExited())
                throw new InvalidOperationException($"The updated Kumori process exited with code {getExitCode()} before startup completed.");
            delay(TimeSpan.FromMilliseconds(250));
        }

        // Give a marker written exactly on the deadline one final chance before
        // rolling back an otherwise healthy update.
        if (markerExists())
            return;
        if (hasExited())
            throw new InvalidOperationException($"The updated Kumori process exited with code {getExitCode()} before startup completed.");
        throw new TimeoutException("The updated Kumori process did not confirm a healthy startup in time.");
    }

    private static void StopUpdatedProcess(Process? process)
    {
        if (process is null)
            return;
        try
        {
            if (process.HasExited)
                return;
            process.Kill(entireProcessTree: true);
            process.WaitForExit((int)TimeSpan.FromSeconds(10).TotalMilliseconds);
        }
        catch
        {
            // The subsequent atomic restore will fail safely while the target
            // remains locked; the retained backup is never deleted in that case.
        }
    }

    private static string HealthMarkerPath(string runtimeDirectory, string token) =>
        Path.Combine(runtimeDirectory, $"update-health-{token}.ready");

    internal static void RestoreBackup(string backup, string target)
    {
        if (!File.Exists(backup))
            throw new FileNotFoundException("The previous Kumori executable is unavailable.", backup);

        var temporary = Path.Combine(
            Path.GetDirectoryName(target)!,
            $".Kumori.restore-{Guid.NewGuid():N}.exe");
        try
        {
            CopyWithRetry(backup, temporary);
            // Keep the current target in place until a complete replacement is
            // ready. A failed copy therefore cannot leave Kumori.exe missing.
            MoveWithRetry(temporary, target, overwrite: true);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 128, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string ComputeSha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 128, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void ValidateDownloadUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidDataException("The release asset did not use a secure HTTPS download URL.");
        }
    }

    private static void MoveWithRetry(string source, string destination, bool overwrite = false)
    {
        const int attempts = 20;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(source, destination, overwrite);
                return;
            }
            catch (IOException) when (attempt < attempts)
            {
                Thread.Sleep(250);
            }
        }
    }

    private static void CopyWithRetry(string source, string destination)
    {
        const int attempts = 20;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Copy(source, destination, overwrite: false);
                return;
            }
            catch (IOException) when (attempt < attempts)
            {
                TryDelete(destination);
                Thread.Sleep(250);
            }
            catch
            {
                TryDelete(destination);
                throw;
            }
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Kumori");
        return client;
    }

    [GeneratedRegex("(?<![A-Fa-f0-9])[A-Fa-f0-9]{64}(?![A-Fa-f0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Regex();
}
