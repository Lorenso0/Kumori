using System.Globalization;
using System.Security.Cryptography;
using Kumori.Gameplay;
using osu.Game.Beatmaps;
using osu.Game.Replays;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets.Osu.Mods;
using osu.Game.Rulesets.Osu.Replays;
using osu.Game.Rulesets.Scoring;
using osu.Game.Scoring;
using osu.Game.Scoring.Legacy;

namespace Kumori.ReplayViewer;

/// <summary>
/// Decodes one user-selected .osr into an in-memory comparison. Nothing in
/// this path calls Kumori storage or writes back to the viewer contract.
/// </summary>
internal static class OsrComparisonImporter
{
    internal const long EphemeralAttemptId = long.MinValue;
    private const long maximum_file_size = 64 * 1024 * 1024;

    private static readonly HashSet<string> replayAlignmentMods = new(StringComparer.OrdinalIgnoreCase)
    {
        "DT", "NC", "HT", "DC", "WU", "WD", "AS", "BPM",
        "EZ", "HR", "DA", "MR", "RD", "TP",
        "DP", "MG", "RP", "TR", "WG",
    };

    public static ComparisonContract Import(string path, string beatmapPath, AttemptContract primary)
    {
        var file = new FileInfo(path);
        if (!file.Exists)
            throw new FileNotFoundException("The selected replay no longer exists.", path);
        if (!file.Extension.Equals(".osr", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Select an osu! replay file with the .osr extension.");
        if (file.Length is <= 0 or > maximum_file_size)
            throw new InvalidDataException("The selected replay is empty or unexpectedly large.");

        using var stream = file.OpenRead();
        var decoder = new ComparisonScoreDecoder(beatmapPath);
        Score decoded = decoder.Parse(stream);

        string localBeatmapHash = beatmapHash(beatmapPath);
        if (string.IsNullOrWhiteSpace(decoder.ReplayBeatmapHash)
            || !decoder.ReplayBeatmapHash.Equals(localBeatmapHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("That replay belongs to a different map or difficulty.");
        }

        validateMods(primary, decoded.ScoreInfo.Mods, beatmapPath);

        OsuReplayFrame[] frames = decoded.Replay.Frames
            .OfType<OsuReplayFrame>()
            .Where(frame => double.IsFinite(frame.Time)
                            && float.IsFinite(frame.Position.X)
                            && float.IsFinite(frame.Position.Y))
            .OrderBy(frame => frame.Time)
            .ToArray();
        if (frames.Length < 2)
            throw new InvalidDataException("The replay contains no usable osu!standard cursor frames.");

        var mods = decoded.ScoreInfo.Mods.ToArray();
        string modsKey = mods.Length == 0 ? "NM" : string.Concat(mods.Select(mod => mod.Acronym));
        ScoreInfo score = decoded.ScoreInfo;

        return new ComparisonContract
        {
            AttemptId = EphemeralAttemptId,
            Ephemeral = true,
            SourceName = file.Name,
            StartedAt = score.Date.ToString("O", CultureInfo.InvariantCulture),
            Outcome = "imported .osr",
            ModsKey = modsKey,
            Accuracy = score.Accuracy * 100,
            Score = score.TotalScore,
            Combo = score.MaxCombo,
            MaxCombo = primary.MaxCombo,
            N300 = count(score, HitResult.Great),
            N100 = count(score, HitResult.Ok),
            N50 = count(score, HitResult.Meh),
            Misses = count(score, HitResult.Miss),
            Samples = frames.Select(frame => new MovementSample
            {
                MapTimeMs = frame.Time,
                MonotonicMs = frame.Time,
                X = frame.Position.X,
                Y = frame.Position.Y,
                Buttons = buttons(frame),
            }).ToList(),
        };
    }

    private static void validateMods(
        AttemptContract primary,
        IReadOnlyList<Mod> imported,
        string beatmapPath)
    {
        Mod[] primaryResolved = LazerReplayAdapter.CreateCapturedMods(
            primary,
            BpmAdjustBeatmap.Decode(beatmapPath));
        HashSet<string> primaryMods = primaryResolved
            .Select(mod => mod.Acronym)
            .Where(replayAlignmentMods.Contains)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> importedMods = imported
            .Select(mod => mod.Acronym)
            .Where(replayAlignmentMods.Contains)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!primaryMods.SetEquals(importedMods))
        {
            string expected = primaryMods.Count == 0 ? "NM" : string.Join("+", primaryMods.Order());
            string actual = importedMods.Count == 0 ? "NM" : string.Join("+", importedMods.Order());
            throw new InvalidDataException($"Gameplay-adjusting mods do not match (viewer: {expected}; replay: {actual}).");
        }

        ModRateAdjust? importedRate = imported.OfType<ModRateAdjust>().FirstOrDefault();
        if (importedRate != null
            && primary.ClockRate > 0
            && Math.Abs(importedRate.SpeedChange.Value - primary.ClockRate) > 0.001)
        {
            throw new InvalidDataException(
                $"Playback rates do not match (viewer: {primary.ClockRate:0.###}x; replay: {importedRate.SpeedChange.Value:0.###}x).");
        }

        OsuModDifficultyAdjust? primaryDifficulty = primaryResolved.OfType<OsuModDifficultyAdjust>().FirstOrDefault();
        OsuModDifficultyAdjust? importedDifficulty = imported.OfType<OsuModDifficultyAdjust>().FirstOrDefault();
        if (primaryDifficulty is not null && importedDifficulty is not null)
        {
            validateDifficultySetting("CS", primaryDifficulty.CircleSize.Value, importedDifficulty.CircleSize.Value);
            validateDifficultySetting("AR", primaryDifficulty.ApproachRate.Value, importedDifficulty.ApproachRate.Value);
            validateDifficultySetting("OD", primaryDifficulty.OverallDifficulty.Value, importedDifficulty.OverallDifficulty.Value);
            validateDifficultySetting("HP", primaryDifficulty.DrainRate.Value, importedDifficulty.DrainRate.Value);
        }
    }

    private static void validateDifficultySetting(string name, double? primary, double? imported)
    {
        if (primary is null && imported is null)
            return;
        if (primary is null || imported is null || Math.Abs(primary.Value - imported.Value) > 0.001)
        {
            throw new InvalidDataException(
                $"Difficulty Adjust {name} does not match (viewer: {format(primary)}; replay: {format(imported)}).");
        }

        static string format(double? value) => value?.ToString("0.###", CultureInfo.InvariantCulture) ?? "default";
    }

    private static int buttons(OsuReplayFrame frame)
        => (frame.Actions.Contains(OsuAction.LeftButton) ? 0x10 : 0)
           | (frame.Actions.Contains(OsuAction.RightButton) ? 0x20 : 0);

    private static int count(ScoreInfo score, HitResult result)
        => score.Statistics.GetValueOrDefault(result);

    private static string beatmapHash(string path)
    {
        using Stream stream = File.OpenRead(path);
        return Convert.ToHexString(MD5.HashData(stream)).ToLowerInvariant();
    }

    private sealed class ComparisonScoreDecoder(string beatmapPath) : LegacyScoreDecoder
    {
        private readonly WorkingBeatmap workingBeatmap = new FlatWorkingBeatmap(beatmapPath);

        public string ReplayBeatmapHash { get; private set; } = "";

        protected override Ruleset GetRuleset(int rulesetId)
            => rulesetId == 0
                ? new OsuRuleset()
                : throw new InvalidDataException("That replay is not an osu!standard replay.");

        protected override WorkingBeatmap GetBeatmap(string md5Hash)
        {
            ReplayBeatmapHash = md5Hash;
            return workingBeatmap;
        }
    }
}
