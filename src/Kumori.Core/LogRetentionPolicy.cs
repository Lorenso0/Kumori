using System.Text;
using System.Text.Json;

namespace Kumori.Core;

/// <summary>Shared bounds for every diagnostic file stored below Kumori/logs.</summary>
public static class LogRetentionPolicy
{
    private static readonly object AppendGate = new();

    public static int NormalizeDays(int days) => Math.Clamp(days, 1, 3650);

    /// <summary>Reads the configured retention without creating or changing the settings file.</summary>
    public static int ReadConfiguredDays(string? settingsFile = null)
    {
        settingsFile ??= AppPaths.SettingsFile;
        try
        {
            if (!File.Exists(settingsFile))
                return AppPaths.DefaultLogRetentionDays;

            using var document = JsonDocument.Parse(File.ReadAllText(settingsFile));
            if (!document.RootElement.TryGetProperty("Developer", out var developer))
                return AppPaths.DefaultLogRetentionDays;

            // Accept the short-lived cache-specific name so development settings
            // written before the unified policy are migrated without data loss.
            foreach (var property in new[] { "LogRetentionDays", "CacheActivityLogRotationDays" })
            {
                if (developer.TryGetProperty(property, out var value) && value.TryGetInt32(out var days))
                    return NormalizeDays(days);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
        }

        return AppPaths.DefaultLogRetentionDays;
    }

    /// <summary>Appends to a plain-text log while keeping its active file size bounded.</summary>
    public static void AppendWithSizeRotation(
        string path,
        string text,
        long maxBytes = AppPaths.MaxLogFileBytes,
        int? maxAgeDays = null,
        DateTimeOffset? now = null)
    {
        lock (AppendGate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var incomingBytes = Encoding.UTF8.GetByteCount(text);
            var timestamp = now ?? DateTimeOffset.Now;
            var rotateForAge = File.Exists(path)
                && maxAgeDays is { } days
                && File.GetCreationTimeUtc(path) < timestamp.UtcDateTime.AddDays(-NormalizeDays(days));
            var rotateForSize = File.Exists(path)
                && new FileInfo(path).Length + incomingBytes > Math.Max(1, maxBytes);
            if (rotateForSize || rotateForAge)
            {
                var directory = Path.GetDirectoryName(path)!;
                var stem = Path.GetFileNameWithoutExtension(path);
                var extension = Path.GetExtension(path);
                var archive = Path.Combine(directory, $"{stem}-{timestamp:yyyyMMdd-HHmmssfff}{extension}");
                for (var suffix = 1; File.Exists(archive); suffix++)
                    archive = Path.Combine(directory, $"{stem}-{timestamp:yyyyMMdd-HHmmssfff}-{suffix}{extension}");
                File.Move(path, archive);
            }
            File.AppendAllText(path, text);
        }
    }
}
