using System.Text.Json;
using Kumori.Core.Models;

namespace Kumori.Tracking;

public sealed record LazerReplayFrame
{
    public double MapTimeMs { get; init; }
    public double X { get; init; }
    public double Y { get; init; }
    public bool LeftPressed { get; init; }
    public bool RightPressed { get; init; }
    public bool Focused { get; init; } = true;
    public bool Paused { get; init; }
    public double? MonotonicMs { get; init; }
    public long? Sequence { get; init; }
}

public interface ILazerReplayFrameSource
{
    IAsyncEnumerable<LazerReplayFrame> ReadFramesAsync(CancellationToken cancellationToken);
}

public interface ILazerReplayFrameSnapshotSource
{
    IReadOnlyList<LazerReplayFrame> ReadCurrentFramesSnapshot();
}

public interface IAttemptAwareReplayFrameSource
{
    void StartAttempt(AttemptStart start);
    void UpdateAttempt(AttemptSnapshot snapshot);
    void EndAttempt();
}

/// <summary>
/// Allows an attempt-aware source to finish consuming its native stream before
/// the capture service tears it down. This is intentionally synchronous because
/// attempt finalisation itself is ordered synchronously by the tracker.
/// </summary>
public interface IFinalizableReplayFrameSource
{
    IReadOnlyList<LazerReplayFrame> FinalizeAttemptSnapshot();
}

public static class LazerReplayFrameMapper
{
    public const int Key1Button = 0x10;
    public const int Key2Button = 0x20;
    public const int FocusedFlag = 0x01;
    public const int PausedFlag = 0x02;

    public static MovementSample ToMovementSample(LazerReplayFrame frame) => new()
    {
        MapTimeMs = frame.MapTimeMs,
        MonotonicMs = frame.MonotonicMs ?? frame.MapTimeMs,
        X = frame.X,
        Y = frame.Y,
        RawX = ClampToInt16(frame.X),
        RawY = ClampToInt16(frame.Y),
        Buttons = (frame.LeftPressed ? Key1Button : 0)
            | (frame.RightPressed ? Key2Button : 0),
        Flags = (frame.Focused ? FocusedFlag : 0)
            | (frame.Paused ? PausedFlag : 0),
        Pressure = 0,
    };

    private static short ClampToInt16(double value)
        => checked((short)Math.Clamp((int)Math.Round(value), short.MinValue, short.MaxValue));
}

public static class LazerReplayFrameJson
{
    public static bool TryParse(string json, out LazerReplayFrame frame)
    {
        frame = new LazerReplayFrame();
        try
        {
            using var doc = JsonDocument.Parse(json);
            return TryParse(doc.RootElement, out frame);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static bool TryParse(JsonElement root, out LazerReplayFrame frame)
    {
        frame = new LazerReplayFrame();
        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (GetString(root, "type") is { } type
            && !type.Equals("frame", StringComparison.OrdinalIgnoreCase)
            && !type.Equals("lazer_replay_frame", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var mapTime = GetDouble(root, "mapTimeMs")
            ?? GetDouble(root, "time")
            ?? GetDouble(root, "Time");
        if (mapTime is null)
        {
            return false;
        }

        double? x = GetDouble(root, "x") ?? GetDouble(root, "X");
        double? y = GetDouble(root, "y") ?? GetDouble(root, "Y");
        if ((x is null || y is null)
            && root.TryGetProperty("position", out var position)
            && position.ValueKind == JsonValueKind.Object)
        {
            x ??= GetDouble(position, "x") ?? GetDouble(position, "X");
            y ??= GetDouble(position, "y") ?? GetDouble(position, "Y");
        }

        if (x is null || y is null)
        {
            return false;
        }

        var actions = ReadActions(root);
        frame = new LazerReplayFrame
        {
            MapTimeMs = mapTime.Value,
            X = x.Value,
            Y = y.Value,
            LeftPressed = GetBool(root, "leftPressed")
                ?? GetBool(root, "left")
                ?? actions.Contains("LeftButton", StringComparer.OrdinalIgnoreCase),
            RightPressed = GetBool(root, "rightPressed")
                ?? GetBool(root, "right")
                ?? actions.Contains("RightButton", StringComparer.OrdinalIgnoreCase),
            Focused = GetBool(root, "focused") ?? true,
            Paused = GetBool(root, "paused") ?? false,
            MonotonicMs = GetDouble(root, "monotonicMs"),
            Sequence = GetLong(root, "sequence"),
        };
        return true;
    }

    private static HashSet<string> ReadActions(JsonElement root)
    {
        if (!root.TryGetProperty("actions", out var actions) || actions.ValueKind != JsonValueKind.Array)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        return actions.EnumerateArray()
            .Where(a => a.ValueKind == JsonValueKind.String)
            .Select(a => a.GetString() ?? "")
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string? GetString(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static bool? GetBool(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean()
            : null;

    private static long? GetLong(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v)
            && v.ValueKind == JsonValueKind.Number
            && v.TryGetInt64(out var result)
                ? result
                : null;

    private static double? GetDouble(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v)
            && v.ValueKind == JsonValueKind.Number
            && v.TryGetDouble(out var result)
                ? result
                : null;
}
