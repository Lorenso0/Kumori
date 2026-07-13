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
        var targetMoved = false;
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

            MoveWithRetry(target, backup);
            targetMoved = true;
            MoveWithRetry(package, target);
            if (!string.Equals(ComputeSha256(target), actualHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The installed update failed verification.");
            }

            TryDelete(Path.Combine(AppPaths.RuntimeDir, "update-error.txt"));
            StartKumori(target, showChangelog: true);
            TryDelete(backup);
        }
        catch (Exception ex)
        {
            if (targetMoved)
            {
                TryDelete(target);
                try { MoveWithRetry(backup, target); } catch { }
            }
            if (File.Exists(target))
            {
                try
                {
                    Directory.CreateDirectory(AppPaths.RuntimeDir);
                    File.WriteAllText(
                        Path.Combine(AppPaths.RuntimeDir, "update-error.txt"),
                        $"Kumori could not finish installing the update and restored the previous version.\n\n{ex.Message}");
                }
                catch { }
                try { StartKumori(target, showChangelog: false); } catch { }
            }
        }
        finally
        {
            TryDelete(package);
        }
    }

    private static void StartKumori(string target, bool showChangelog)
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
        _ = Process.Start(start) ?? throw new InvalidOperationException("The updated Kumori executable could not be started.");
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

    private static void MoveWithRetry(string source, string destination)
    {
        const int attempts = 20;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(source, destination);
                return;
            }
            catch (IOException) when (attempt < attempts)
            {
                Thread.Sleep(250);
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
