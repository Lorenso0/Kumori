using System.Text.Json;

namespace Kumori.Core;

/// <summary>Records files materialised in Kumori-owned caches and runtime payload stores.</summary>
public static class CacheActivityLog
{
    private const long MaxLogBytes = 5L * 1024 * 1024;
    private const int DefaultRotationDays = 30;
    private static readonly object Gate = new();
    private static int rotationDays = DefaultRotationDays;

    /// <summary>Sets the age limit for the active cache-additions log.</summary>
    public static void ConfigureRotationDays(int days) =>
        Volatile.Write(ref rotationDays, Math.Clamp(days, 1, 3650));

    public static void RecordAddition(
        string path,
        string source,
        string? logFile = null,
        string? reason = null,
        long? beatmapId = null,
        long? beatmapSetId = null,
        string? cacheKey = null)
    {
        try
        {
            logFile ??= AppPaths.CacheActivityLog;
            var fullPath = Path.GetFullPath(path);
            var bytes = File.Exists(fullPath) ? new FileInfo(fullPath).Length : (long?)null;
            var line = JsonSerializer.Serialize(new
            {
                timestamp_utc = DateTimeOffset.UtcNow,
                category = beatmapId is null && cacheKey is null ? "file" : "beatmap",
                source,
                reason,
                beatmap_id = beatmapId,
                beatmap_set_id = beatmapSetId,
                cache_key = cacheKey,
                path = fullPath,
                file_name = Path.GetFileName(fullPath),
                bytes,
            }) + Environment.NewLine;

            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(logFile)!);
                RotateIfNeeded(logFile, DateTimeOffset.UtcNow);
                File.AppendAllText(logFile, line);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Cache writes must never fail because their diagnostic log is unavailable.
        }
    }

    public static IReadOnlyList<CacheActivityEntry> ReadRecent(int maximum = 50, string? logFile = null)
    {
        logFile ??= AppPaths.CacheActivityLog;
        if (maximum <= 0 || !File.Exists(logFile))
            return [];

        try
        {
            lock (Gate)
            {
                var files = new[] { logFile + ".1", logFile }
                    .Where(File.Exists);
                return files
                    .SelectMany(file => File.ReadLines(file)
                        .TakeLast(Math.Max(maximum * 4, maximum)))
                    .Select(Parse)
                    .Where(entry => entry is not null)
                    .OrderByDescending(entry => entry!.TimestampUtc)
                    .Take(maximum)
                    .Cast<CacheActivityEntry>()
                    .ToArray();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static CacheActivityEntry? Parse(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            return new CacheActivityEntry(
                root.TryGetProperty("timestamp_utc", out var timestamp)
                && timestamp.TryGetDateTimeOffset(out var parsedTimestamp)
                    ? parsedTimestamp
                    : DateTimeOffset.MinValue,
                String(root, "category") ?? "file",
                String(root, "source") ?? "unknown",
                String(root, "reason"),
                Long(root, "beatmap_id"),
                Long(root, "beatmap_set_id"),
                String(root, "cache_key"),
                String(root, "path") ?? string.Empty,
                String(root, "file_name") ?? Path.GetFileName(String(root, "path") ?? string.Empty),
                Long(root, "bytes"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? String(JsonElement root, string property)
        => root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? Long(JsonElement root, string property)
        => root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
           && value.TryGetInt64(out var parsed)
            ? parsed
            : null;

    private static void RotateIfNeeded(string logFile, DateTimeOffset nowUtc)
    {
        if (!File.Exists(logFile))
        {
            return;
        }

        var logInfo = new FileInfo(logFile);
        var firstEntry = logInfo.Length < MaxLogBytes
            ? File.ReadLines(logFile).Select(Parse).FirstOrDefault(entry => entry is not null)
            : null;
        var ageLimit = TimeSpan.FromDays(Volatile.Read(ref rotationDays));
        var isExpired = firstEntry is not null && nowUtc - firstEntry.TimestampUtc >= ageLimit;
        if (logInfo.Length >= MaxLogBytes || isExpired)
        {
            File.Move(logFile, logFile + ".1", overwrite: true);
        }
    }
}

public sealed record CacheActivityEntry(
    DateTimeOffset TimestampUtc,
    string Category,
    string Source,
    string? Reason,
    long? BeatmapId,
    long? BeatmapSetId,
    string? CacheKey,
    string Path,
    string FileName,
    long? Bytes);
