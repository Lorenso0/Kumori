using System.Globalization;
using System.Text.Json;

namespace Kumori.Gameplay;

// Canonical implementation and verified commit:
// docs/BPM_ADJUST_UPSTREAM.md
public enum BpmAdjustAudioMode
{
    PreservePitch = 0,
    AdjustPitch = 1,
    Nightcore = 2,
}

public readonly record struct BpmAdjustSettings(
    double? TargetBpm,
    BpmAdjustAudioMode AudioMode,
    bool ScaleMapStatsWithBpm)
{
    public static BpmAdjustSettings Default => new(null, BpmAdjustAudioMode.PreservePitch, true);

    public static BpmAdjustSettings Parse(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson))
            return Default;

        try
        {
            using var document = JsonDocument.Parse(settingsJson);
            return Parse(document.RootElement);
        }
        catch (JsonException)
        {
            return Default;
        }
    }

    public static BpmAdjustSettings Parse(JsonElement settings)
    {
        if (settings.ValueKind != JsonValueKind.Object)
            return Default;

        double? targetBpm = tryNumber(settings, "target_bpm");
        if (targetBpm is not > 0 || !double.IsFinite(targetBpm.Value))
            targetBpm = null;

        BpmAdjustAudioMode audioMode = BpmAdjustAudioMode.PreservePitch;
        if (settings.TryGetProperty("audio_mode", out JsonElement audio))
            audioMode = parseAudioMode(audio);

        bool scaleStats = true;
        if (settings.TryGetProperty("scale_map_stats_with_bpm", out JsonElement scale))
            scaleStats = parseBoolean(scale, true);

        return new BpmAdjustSettings(targetBpm, audioMode, scaleStats);
    }

    public double ClockRate(double sourceBpm)
    {
        if (TargetBpm is not > 0 || sourceBpm <= 0 || !double.IsFinite(sourceBpm))
            return 1;

        double rate = TargetBpm.Value / sourceBpm;
        return rate > 0 && double.IsFinite(rate) ? rate : 1;
    }

    private static double? tryNumber(JsonElement settings, string key)
    {
        if (!settings.TryGetProperty(key, out JsonElement value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out double number) => number,
            JsonValueKind.String when double.TryParse(
                value.GetString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double number) => number,
            _ => null,
        };
    }

    private static BpmAdjustAudioMode parseAudioMode(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int numeric))
            return enumValue(numeric);

        if (value.ValueKind != JsonValueKind.String)
            return BpmAdjustAudioMode.PreservePitch;

        string text = value.GetString()?.Trim() ?? string.Empty;
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out numeric))
            return enumValue(numeric);

        string normalized = new(text.Where(char.IsLetterOrDigit)
                                    .Select(char.ToLowerInvariant)
                                    .ToArray());
        return normalized switch
        {
            "preservepitch" => BpmAdjustAudioMode.PreservePitch,
            "adjustpitch" => BpmAdjustAudioMode.AdjustPitch,
            "nightcore" => BpmAdjustAudioMode.Nightcore,
            _ => BpmAdjustAudioMode.PreservePitch,
        };
    }

    private static BpmAdjustAudioMode enumValue(int value)
        => Enum.IsDefined(typeof(BpmAdjustAudioMode), value)
            ? (BpmAdjustAudioMode)value
            : BpmAdjustAudioMode.PreservePitch;

    private static bool parseBoolean(JsonElement value, bool fallback) => value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number when value.TryGetInt32(out int number) => number != 0,
        JsonValueKind.String when bool.TryParse(value.GetString(), out bool boolean) => boolean,
        JsonValueKind.String when int.TryParse(
            value.GetString(),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out int number) => number != 0,
        _ => fallback,
    };
}
