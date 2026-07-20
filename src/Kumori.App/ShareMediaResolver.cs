using System.IO;
using Kumori.App.ViewModels;
using Kumori.Core.Models;
using Kumori.Storage;
using Kumori.Tracking;

namespace Kumori.App;

internal sealed record ResolvedShareMedia(
    IReadOnlyList<ShareMediaFile> Files,
    IReadOnlyList<string> OptionalOmissions);

internal static class ShareMediaResolver
{
    public static IReadOnlyList<string> FindLocalAssetCandidates(SharedPlayV1 play)
    {
        var summary = new AttemptSummary
        {
            Artist = play.Map.Artist,
            Title = play.Map.Title,
            Difficulty = play.Map.Difficulty,
            Mapper = play.Map.Mapper,
            OsuBeatmapId = play.Map.BeatmapId,
            BeatmapSetId = play.Map.SetId,
            Checksum = play.Map.Checksum,
        };
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        LazerBeatmapAssets? lazer = LazerStorage.ResolveBeatmapAssets(
            summary.OsuBeatmapId,
            summary.BeatmapSetId,
            summary.Difficulty);
        if (lazer is not null)
        {
            candidates.Add(lazer.BeatmapPath);
            foreach (string path in lazer.Files.Values)
                if (File.Exists(path))
                    candidates.Add(path);
        }

        string? beatmap = BeatmapArtworkResolver.ResolveBeatmapFile(summary);
        if (!string.IsNullOrWhiteSpace(beatmap) && File.Exists(beatmap))
            candidates.Add(beatmap);
        string? mediaDirectory = BeatmapArtworkResolver.ResolveMediaDirectory(summary);
        if (!string.IsNullOrWhiteSpace(mediaDirectory) && Directory.Exists(mediaDirectory))
        {
            foreach (string path in Directory.EnumerateFiles(mediaDirectory).Take(4096))
                candidates.Add(path);
        }
        return candidates.ToArray();
    }

    public static ResolvedShareMedia Resolve(AttemptDetails details)
    {
        string? beatmapPath = !string.IsNullOrWhiteSpace(details.LocalBeatmapPath) && File.Exists(details.LocalBeatmapPath)
            ? details.LocalBeatmapPath
            : null;
        LazerBeatmapAssets? lazer = beatmapPath is null
            ? LazerStorage.ResolveBeatmapAssets(
                details.Summary.OsuBeatmapId,
                details.Summary.BeatmapSetId,
                details.Summary.Difficulty)
            : null;
        beatmapPath ??= lazer?.BeatmapPath ?? BeatmapArtworkResolver.ResolveBeatmapFile(details.Summary);
        if (string.IsNullOrWhiteSpace(beatmapPath) || !File.Exists(beatmapPath))
            throw new InvalidOperationException("Kumori could not find the .osu beatmap file for this play.");

        var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string logical, string path) in details.LocalMediaPaths)
        {
            if (File.Exists(path))
                sources[Path.GetFileName(logical.Replace('\\', '/'))] = path;
        }
        if (lazer?.Files is { } lazerFiles)
        {
            foreach ((string logical, string path) in lazerFiles)
            {
                if (File.Exists(path))
                    sources[Path.GetFileName(logical.Replace('\\', '/'))] = path;
            }
        }
        foreach (string? directory in new[]
                 {
                     details.LocalMediaDirectory,
                     BeatmapArtworkResolver.ResolveMediaDirectory(details.Summary),
                     Path.GetDirectoryName(beatmapPath),
                 })
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                continue;
            foreach (string path in Directory.EnumerateFiles(directory))
                sources.TryAdd(Path.GetFileName(path), path);
        }

        BeatmapReferences references = ParseBeatmap(beatmapPath);
        string audioPath = ResolveNamed(sources, references.Audio)
            ?? throw new InvalidOperationException(
                $"Kumori could not find the required beatmap audio '{references.Audio}'.");
        var files = new List<ShareMediaFile>
        {
            new(PortableBeatmapName(beatmapPath, details), "beatmap", beatmapPath),
            new(references.Audio, "audio", audioPath),
        };
        var omissions = new List<string>();

        if (!string.IsNullOrWhiteSpace(references.Background))
        {
            string? background = ResolveNamed(sources, references.Background);
            if (background is null)
                omissions.Add($"Background: {references.Background}");
            else
                files.Add(new ShareMediaFile(references.Background, "background", background));
        }
        foreach (string sample in references.Samples)
        {
            string? samplePath = ResolveNamed(sources, sample);
            if (samplePath is null)
                omissions.Add($"Custom sample: {sample}");
            else
                files.Add(new ShareMediaFile(sample, "sample", samplePath));
        }
        return new ResolvedShareMedia(files, omissions);
    }

    internal static string PortableBeatmapName(string physicalPath, AttemptDetails details)
    {
        string physicalName = Path.GetFileName(physicalPath);
        if (physicalName.EndsWith(".osu", StringComparison.OrdinalIgnoreCase))
            return physicalName;
        string raw =
            $"{details.Summary.Artist} - {details.Summary.Title} ({details.Mapper}) [{details.Summary.Difficulty}].osu";
        char[] invalid = Path.GetInvalidFileNameChars();
        string safe = new(raw.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        safe = string.Join(" ", safe.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim();
        return safe.Length <= 220 ? safe : safe[..216].TrimEnd() + ".osu";
    }

    private static string? ResolveNamed(IReadOnlyDictionary<string, string> sources, string name)
    {
        string safe = Path.GetFileName(name.Replace('\\', '/'));
        return sources.TryGetValue(safe, out string? path) && File.Exists(path) ? path : null;
    }

    private static BeatmapReferences ParseBeatmap(string path)
    {
        string section = "";
        string audio = "";
        string background = "";
        var samples = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in File.ReadLines(path))
        {
            string line = raw.Trim();
            if (line.StartsWith('['))
            {
                section = line;
                continue;
            }
            if (section == "[General]" && line.StartsWith("AudioFilename:", StringComparison.OrdinalIgnoreCase))
            {
                audio = SafeName(line.Split(':', 2)[1].Trim());
            }
            else if (section == "[Events]"
                     && (line.StartsWith("0,", StringComparison.Ordinal)
                         || line.StartsWith("Background,", StringComparison.OrdinalIgnoreCase)))
            {
                string[] parts = line.Split(',');
                if (parts.Length >= 3)
                    background = SafeName(parts[2].Trim().Trim('"'));
            }
            else if (section == "[HitObjects]")
            {
                string[] fields = line.Split(',');
                if (fields.Length == 0)
                    continue;
                string[] tail = fields[^1].Split(':');
                if (tail.Length >= 5 && !string.IsNullOrWhiteSpace(tail[4]))
                {
                    string sample = SafeName(tail[4]);
                    if (sample.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)
                        || sample.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase)
                        || sample.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
                        samples.Add(sample);
                }
            }
        }
        if (string.IsNullOrWhiteSpace(audio))
            throw new InvalidDataException("The selected .osu file does not declare an audio file.");
        return new BeatmapReferences(audio, background, samples.ToArray());
    }

    private static string SafeName(string value)
    {
        string result = Path.GetFileName(value.Replace('\\', '/')).Trim();
        if (string.IsNullOrWhiteSpace(result) || result.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidDataException($"Beatmap media name '{value}' is not safe.");
        return result;
    }

    private sealed record BeatmapReferences(
        string Audio,
        string Background,
        IReadOnlyList<string> Samples);
}
