using System.Text.Json;

namespace Kumori.Core;

/// <summary>Records files materialised in Kumori-owned caches and runtime payload stores.</summary>
public static class CacheActivityLog
{
    private const long MaxLogBytes = 5L * 1024 * 1024;
    private static readonly object Gate = new();

    public static void RecordAddition(string path, string source, string? logFile = null)
    {
        try
        {
            logFile ??= AppPaths.CacheActivityLog;
            var fullPath = Path.GetFullPath(path);
            var bytes = File.Exists(fullPath) ? new FileInfo(fullPath).Length : (long?)null;
            var line = JsonSerializer.Serialize(new
            {
                timestamp_utc = DateTimeOffset.UtcNow,
                source,
                path = fullPath,
                bytes,
            }) + Environment.NewLine;

            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(logFile)!);
                RotateIfNeeded(logFile);
                File.AppendAllText(logFile, line);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Cache writes must never fail because their diagnostic log is unavailable.
        }
    }

    private static void RotateIfNeeded(string logFile)
    {
        if (!File.Exists(logFile) || new FileInfo(logFile).Length < MaxLogBytes)
        {
            return;
        }

        File.Move(logFile, logFile + ".1", overwrite: true);
    }
}
