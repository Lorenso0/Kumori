namespace Kumori.ReplayViewer;

internal static class NativeViewerLog
{
    private static readonly object Sync = new();

    public static string LogPath
    {
        get
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "Kumori", "logs", "viewer", $"native-viewer-{DateTimeOffset.Now:yyyyMMdd}.log");
        }
    }

    public static void Write(string message)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                PruneOldLogs();
                File.AppendAllText(LogPath, $"[{DateTimeOffset.Now:O}] native {message}{Environment.NewLine}");
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

            var cutoff = DateTimeOffset.Now.UtcDateTime.AddDays(-3);
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
