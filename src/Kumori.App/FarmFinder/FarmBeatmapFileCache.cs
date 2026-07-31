using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using Kumori.Core;
using Kumori.FarmFinder;
using Serilog;

namespace Kumori.App.FarmFinder;

internal sealed class FarmBeatmapFileCache
{
    private const long maximumBeatmapBytes = 8L * 1024 * 1024;
    private static readonly HttpClient sharedHttp = CreateHttpClient();
    private readonly HttpClient http;
    private readonly string cacheDirectory;
    private readonly Func<FarmBeatmap, string?> resolveLocal;
    private readonly SemaphoreSlim downloadGate = new(48, 48);
    private readonly ConcurrentDictionary<long, Lazy<Task<string?>>> downloads = new();

    public FarmBeatmapFileCache(
        string? cacheDirectory = null,
        HttpClient? httpClient = null,
        Func<FarmBeatmap, string?>? localResolver = null)
    {
        this.cacheDirectory = cacheDirectory ?? AppPaths.FarmFinderBeatmapFilesDir;
        http = httpClient ?? sharedHttp;
        resolveLocal = localResolver ?? ResolveLegacyCache;
    }

    public async Task<string?> GetAsync(
        FarmBeatmap beatmap,
        CancellationToken cancellationToken = default)
    {
        var destination = Path.Combine(cacheDirectory, $"{beatmap.BeatmapId}.osu");
        if (IsValidBeatmapFile(destination))
            return destination;

        var local = resolveLocal(beatmap);
        if (!string.IsNullOrWhiteSpace(local))
            return local;

        var download = downloads.GetOrAdd(
            beatmap.BeatmapId,
            _ => new Lazy<Task<string?>>(
                () => DownloadAsync(beatmap.BeatmapId, destination, cancellationToken),
                LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            return await download.Value.WaitAsync(cancellationToken);
        }
        finally
        {
            downloads.TryRemove(beatmap.BeatmapId, out _);
        }
    }

    private async Task<string?> DownloadAsync(
        long beatmapId,
        string destination,
        CancellationToken cancellationToken)
    {
        await downloadGate.WaitAsync(cancellationToken);
        var temporary = destination + $".{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(cacheDirectory);
            using var response = await http.GetAsync(
                $"https://mirror.hinamizawa.ai/api/osu/{beatmapId}",
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
                return null;
            if (response.Content.Headers.ContentLength is > maximumBeatmapBytes)
                return null;

            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var target = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await CopyWithLimitAsync(source, target, cancellationToken);
            }

            if (!IsValidBeatmapFile(temporary))
                return null;
            File.Move(temporary, destination, true);
            return destination;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Log.Debug(
                exception,
                "Farm Finder beatmap download failed for {BeatmapId}",
                beatmapId);
            return null;
        }
        finally
        {
            try
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
            catch (IOException)
            {
            }
            downloadGate.Release();
        }
    }

    private static async Task CopyWithLimitAsync(
        Stream source,
        Stream destination,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                return;
            total += read;
            if (total > maximumBeatmapBytes)
                throw new InvalidDataException("The beatmap file was unexpectedly large.");
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private static bool IsValidBeatmapFile(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length is <= 32 or > maximumBeatmapBytes)
                return false;
            using var reader = new StreamReader(path);
            return reader.ReadLine()?.StartsWith(
                "osu file format v",
                StringComparison.OrdinalIgnoreCase) == true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static string? ResolveLegacyCache(FarmBeatmap beatmap)
    {
        foreach (var directory in new[]
                 {
                     AppPaths.LegacyBeatmapFilesDir,
                     AppPaths.OldLegacyBeatmapFilesDir,
                 })
        {
            var path = Path.Combine(directory, $"{beatmap.BeatmapId}.osu");
            if (IsValidBeatmapFile(path))
                return path;
        }
        return null;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Kumori-FarmFinder/1.0");
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("text/plain"));
        return client;
    }
}
