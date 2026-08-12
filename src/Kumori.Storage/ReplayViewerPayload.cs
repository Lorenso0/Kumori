using System.IO.Compression;
using Kumori.Core;
using Serilog;

namespace Kumori.Storage;

/// <summary>
/// Materialises the replay viewer embedded in a single-file Kumori publish.
/// Development builds do not carry the resource and continue to use the
/// adjacent viewer output instead.
/// </summary>
internal static class ReplayViewerPayload
{
    private const string ResourceName = "Kumori.ReplayViewer.Bundle.zip";
    private static readonly object ExtractionGate = new();

    public static string? TryEnsureExtracted()
    {
        var nativeToolsViewer = NativeToolsPayload.TryEnsureReplayViewerExtracted();
        if (nativeToolsViewer is not null)
            return nativeToolsViewer;

        var assembly = System.Reflection.Assembly.GetEntryAssembly() ?? typeof(ReplayViewerPayload).Assembly;
        if (assembly.GetManifestResourceInfo(ResourceName) is null)
        {
            return null;
        }

        var versionDirectory = Path.Combine(
            AppPaths.ViewerRuntimeDir,
            assembly.ManifestModule.ModuleVersionId.ToString("N"));
        var executable = Path.Combine(versionDirectory, "Kumori.ReplayViewer.exe");
        if (File.Exists(executable))
        {
            return executable;
        }

        lock (ExtractionGate)
        {
            if (File.Exists(executable))
            {
                return executable;
            }

            var temporaryDirectory = versionDirectory + ".extract-" + Guid.NewGuid().ToString("N");
            try
            {
                Directory.CreateDirectory(AppPaths.ViewerRuntimeDir);
                using var stream = assembly.GetManifestResourceStream(ResourceName)
                    ?? throw new InvalidOperationException("The embedded replay viewer payload is unavailable.");
                ZipFile.ExtractToDirectory(stream, temporaryDirectory, overwriteFiles: true);

                InstallExtractedPayload(temporaryDirectory, versionDirectory);
                foreach (var added in Directory.EnumerateFiles(versionDirectory, "*", SearchOption.AllDirectories))
                {
                    CacheActivityLog.RecordAddition(added, "embedded-replay-viewer");
                }
                PruneOtherVersions(versionDirectory);

                return File.Exists(executable) ? executable : null;
            }
            catch (Exception ex)
            {
                try
                {
                    if (Directory.Exists(temporaryDirectory))
                    {
                        Directory.Delete(temporaryDirectory, recursive: true);
                    }
                }
                catch
                {
                }

                Log.Warning(ex, "Could not extract the embedded replay viewer");
                return null;
            }
        }
    }

    internal static void InstallExtractedPayload(string temporaryDirectory, string versionDirectory)
    {
        var temporaryExecutable = Path.Combine(temporaryDirectory, "Kumori.ReplayViewer.exe");
        if (!File.Exists(temporaryExecutable))
        {
            throw new InvalidDataException(
                "The embedded replay viewer payload does not contain Kumori.ReplayViewer.exe.");
        }

        if (Directory.Exists(versionDirectory))
        {
            // An interrupted extraction can leave the version folder in place
            // without its executable. Replace that incomplete folder instead of
            // throwing away the newly validated payload forever.
            Directory.Delete(versionDirectory, recursive: true);
        }
        Directory.Move(temporaryDirectory, versionDirectory);
    }

    private static void PruneOtherVersions(string currentDirectory)
    {
        foreach (var directory in Directory.EnumerateDirectories(AppPaths.ViewerRuntimeDir))
        {
            if (string.Equals(directory, currentDirectory, StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(directory).Contains(".extract-", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
