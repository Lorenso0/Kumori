using Kumori.Skins;

namespace Kumori.SkinStudio;

internal sealed record StudioBeatmapImportResult(
    string BeatmapPath,
    string Hash,
    int HitObjectCount,
    IReadOnlyList<string> CopiedMedia,
    IReadOnlyList<string> MissingMedia);

internal static class StudioBeatmapImportService
{
    private const long max_beatmap_size = 16L * 1024 * 1024;
    private const long max_media_size = 256L * 1024 * 1024;

    public static StudioBeatmapImportResult Prepare(
        string sourcePath,
        string isolatedMapsRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(isolatedMapsRoot);

        var source = Path.GetFullPath(sourcePath);
        if (!Path.GetExtension(source).Equals(".osu", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Only .osu beatmap files are supported.");
        if (!File.Exists(source))
            throw new FileNotFoundException("The selected beatmap does not exist.", source);

        var sourceInfo = new FileInfo(source);
        if (sourceInfo.Length is <= 0 or > max_beatmap_size)
            throw new InvalidDataException("The beatmap must be between 1 byte and 16 MiB.");
        if (ReadDeclaredMode(source) != 0)
            throw new NotSupportedException(
                "Only osu!standard maps can use the authoritative gameplay preview.");

        osu.Game.Beatmaps.Beatmap decoded;
        try
        {
            decoded = StudioWorkingBeatmap.Decode(source);
        }
        catch (Exception ex) when (ex is not InvalidDataException)
        {
            throw new InvalidDataException("The selected file is not a valid osu! beatmap.", ex);
        }

        if (decoded.BeatmapInfo.Ruleset.OnlineID != 0)
            throw new NotSupportedException(
                "Only osu!standard maps can use the authoritative gameplay preview.");
        if (decoded.HitObjects.Count == 0)
            throw new InvalidDataException("The beatmap contains no playable hit objects.");

        var bytes = File.ReadAllBytes(source);
        var hash = SkinDraftWorkspaceService.Hash(bytes);
        var mapsRoot = Path.GetFullPath(isolatedMapsRoot);
        Directory.CreateDirectory(mapsRoot);

        var destination = GetContainedPath(mapsRoot, hash);
        var isolatedBeatmap = Path.Combine(destination, "preview.osu");
        if (!Directory.Exists(destination))
            materialize();

        var copied = new List<string>();
        var missing = new List<string>();
        classifyMedia(decoded.Metadata.AudioFile, destination, copied, missing);
        classifyMedia(decoded.Metadata.BackgroundFile, destination, copied, missing);

        return new StudioBeatmapImportResult(
            isolatedBeatmap,
            hash,
            decoded.HitObjects.Count,
            copied,
            missing);

        void materialize()
        {
            var temporary = GetContainedPath(
                mapsRoot,
                $".{hash}.{Guid.NewGuid():N}.new");
            Directory.CreateDirectory(temporary);
            try
            {
                File.Copy(source, Path.Combine(temporary, "preview.osu"), false);
                copyMedia(decoded.Metadata.AudioFile, temporary);
                copyMedia(decoded.Metadata.BackgroundFile, temporary);

                try
                {
                    Directory.Move(temporary, destination);
                }
                catch (IOException) when (Directory.Exists(destination))
                {
                    // Another import completed the same content-addressed map.
                }
            }
            finally
            {
                if (Directory.Exists(temporary))
                    Directory.Delete(temporary, true);
            }
        }

        void copyMedia(string? relativePath, string temporary)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
                return;

            var sourceMedia = ResolveReferencedFile(
                Path.GetDirectoryName(source)!,
                relativePath);
            if (!File.Exists(sourceMedia))
                return;

            var mediaInfo = new FileInfo(sourceMedia);
            if (mediaInfo.Length > max_media_size)
                throw new InvalidDataException(
                    $"Referenced media “{relativePath}” exceeds 256 MiB.");

            var target = GetContainedPath(
                temporary,
                NormalizeRelativePath(relativePath));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(sourceMedia, target, false);
        }
    }

    private static void classifyMedia(
        string? relativePath,
        string destination,
        ICollection<string> copied,
        ICollection<string> missing)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return;

        var normalized = NormalizeRelativePath(relativePath);
        var target = GetContainedPath(destination, normalized);
        (File.Exists(target) ? copied : missing).Add(normalized);
    }

    private static string ResolveReferencedFile(string sourceRoot, string relativePath)
    {
        var normalized = NormalizeRelativePath(relativePath);
        return GetContainedPath(Path.GetFullPath(sourceRoot), normalized);
    }

    internal static int ReadDeclaredMode(string path)
    {
        var inGeneral = false;
        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                inGeneral = line.Equals(
                    "[General]",
                    StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inGeneral || line.Length == 0 || line.StartsWith("//"))
                continue;

            var separator = line.IndexOf(':');
            if (separator < 0
                || !line[..separator].Trim().Equals(
                    "Mode",
                    StringComparison.OrdinalIgnoreCase))
                continue;

            if (!int.TryParse(line[(separator + 1)..].Trim(), out var mode)
                || mode is < 0 or > 3)
                throw new InvalidDataException(
                    "The beatmap has an invalid ruleset mode.");
            return mode;
        }

        // The osu! file format defaults an omitted Mode field to osu!standard.
        return 0;
    }

    private static string NormalizeRelativePath(string relativePath)
    {
        var normalized = relativePath
            .Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar)
            .Trim();
        if (normalized.Length == 0 || Path.IsPathRooted(normalized))
            throw new InvalidDataException("Beatmap media paths must be relative.");
        return normalized;
    }

    private static string GetContainedPath(string root, string relativePath)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root))
                             + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(normalizedRoot, relativePath));
        if (!candidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"Beatmap media path escapes its allowed directory: {relativePath}");
        return candidate;
    }
}
