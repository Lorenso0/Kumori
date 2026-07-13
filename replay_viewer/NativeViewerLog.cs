using Kumori.Core;

namespace Kumori.ReplayViewer;

internal static class NativeViewerLog
{
    private static readonly object Sync = new();

    public static string LogPath
    {
        get
        {
            return AppPaths.ViewerLogFile;
        }
    }

    public static void Write(string message)
    {
        try
        {
            lock (Sync)
            {
                PruneOldLogs();
                LogRetentionPolicy.AppendWithSizeRotation(
                    LogPath,
                    $"[{DateTimeOffset.Now:O}] native {message}{Environment.NewLine}",
                    maxAgeDays: LogRetentionPolicy.ReadConfiguredDays());
            }
        }
        catch
        {
            // Last-chance diagnostics should never affect replay playback.
        }
    }

    public static void Error(Exception ex, string message)
        => Write($"{message}{Environment.NewLine}{ex}");

    private static void PruneOldLogs()
    {
        try
        {
            var directory = Path.GetDirectoryName(LogPath);
            if (directory is null || !Directory.Exists(directory))
            {
                return;
            }

            var cutoff = DateTimeOffset.Now.UtcDateTime.AddDays(-LogRetentionPolicy.ReadConfiguredDays());
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff)
                    {
                        File.Delete(file);
                    }
                }
                catch
                {
                }
            }
        }
        catch
        {
        }
    }
}
