using System.IO.Compression;
using Kumori.Core;
using Serilog;

namespace Kumori.Storage;

/// <summary>
/// Atomically extracts the versioned native-tools bundle shared by Replay
/// Viewer and the lazer-native Skin Studio.
/// </summary>
public static class NativeToolsPayload
{
    private const string resource_name = "Kumori.NativeTools.Bundle.zip";
    private static readonly object extraction_gate = new();

    public static string? TryEnsureReplayViewerExtracted() =>
        tryEnsureExtracted("Kumori.ReplayViewer.exe");

    public static string? TryEnsureSkinStudioExtracted() =>
        tryEnsureExtracted("Kumori.SkinStudio.exe");

    private static string? tryEnsureExtracted(string executableName)
    {
        var assembly = System.Reflection.Assembly.GetEntryAssembly()
                       ?? typeof(NativeToolsPayload).Assembly;
        if (assembly.GetManifestResourceInfo(resource_name) is null)
            return null;

        var versionDirectory = Path.Combine(
            AppPaths.NativeToolsRuntimeDir,
            assembly.ManifestModule.ModuleVersionId.ToString("N"));
        var executable = Path.Combine(versionDirectory, executableName);
        if (File.Exists(executable))
            return executable;

        lock (extraction_gate)
        {
            if (File.Exists(executable))
                return executable;

            var temporary = versionDirectory + ".extract-" + Guid.NewGuid().ToString("N");
            try
            {
                Directory.CreateDirectory(AppPaths.NativeToolsRuntimeDir);
                using var stream = assembly.GetManifestResourceStream(resource_name)
                    ?? throw new InvalidOperationException(
                        "The embedded native-tools payload is unavailable.");
                ZipFile.ExtractToDirectory(stream, temporary, overwriteFiles: true);
                InstallExtractedPayload(temporary, versionDirectory);
                foreach (var added in Directory.EnumerateFiles(
                             versionDirectory,
                             "*",
                             SearchOption.AllDirectories))
                {
                    CacheActivityLog.RecordAddition(added, "embedded-native-tools");
                }
                pruneOtherVersions(versionDirectory);
                return File.Exists(executable) ? executable : null;
            }
            catch (Exception ex)
            {
                try
                {
                    if (Directory.Exists(temporary))
                        Directory.Delete(temporary, recursive: true);
                }
                catch
                {
                }
                Log.Warning(ex, "Could not extract the embedded native-tools bundle");
                return null;
            }
        }
    }

    internal static void InstallExtractedPayload(
        string temporaryDirectory,
        string versionDirectory)
    {
        foreach (var executable in new[]
                 {
                     "Kumori.ReplayViewer.exe",
                     "Kumori.SkinStudio.exe",
                 })
        {
            if (!File.Exists(Path.Combine(temporaryDirectory, executable)))
            {
                throw new InvalidDataException(
                    $"The native-tools payload does not contain {executable}.");
            }
        }

        if (Directory.Exists(versionDirectory))
            Directory.Delete(versionDirectory, recursive: true);
        Directory.Move(temporaryDirectory, versionDirectory);
    }

    private static void pruneOtherVersions(string currentDirectory)
    {
        foreach (var directory in Directory.EnumerateDirectories(
                     AppPaths.NativeToolsRuntimeDir))
        {
            if (string.Equals(
                    directory,
                    currentDirectory,
                    StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(directory).Contains(
                    ".extract-",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
