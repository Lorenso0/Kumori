using System.Text;

namespace Kumori.App.Skins;

public enum SkinIniValueType
{
    Boolean,
    Integer,
    Text,
    Rgb,
    Rgba,
}

public sealed record SkinIniKeyDefinition(
    string Section,
    string Key,
    string Label,
    SkinIniValueType Type,
    string DefaultValue);

/// <summary>Line-preserving skin.ini parser/editor, including unknown and repeated sections.</summary>
public sealed class SkinIniDocument
{
    private readonly Dictionary<string, Dictionary<string, int>> index =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> lines;
    private readonly Encoding encoding;
    private readonly string newline;
    private readonly bool endedWithNewline;

    private SkinIniDocument(
        List<string> lines,
        Encoding encoding,
        string newline,
        bool endedWithNewline)
    {
        this.lines = lines;
        this.encoding = encoding;
        this.newline = newline;
        this.endedWithNewline = endedWithNewline;
        Reindex();
    }

    public static SkinIniDocument Parse(byte[] bytes)
    {
        var (text, encoding) = Decode(bytes);
        return ParseText(text, encoding);
    }

    public static SkinIniDocument ParseText(string text) =>
        ParseText(text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

    public static SkinIniDocument Create(string name, string creator) =>
        ParseText(
            $"[General]\r\nName: {name}\r\nAuthor: {creator}\r\nVersion: latest\r\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

    public string ToText()
    {
        var text = string.Join(newline, lines);
        return endedWithNewline || lines.Count == 0 ? text + newline : text;
    }

    public byte[] ToBytes() => Encode(ToText(), encoding);

    public SkinIniDocument WithText(string text)
    {
        var normalized = text.Replace("\r\n", "\n");
        var ended = normalized.EndsWith('\n');
        var replacementLines = normalized.Split('\n').ToList();
        if (ended && replacementLines.Count > 0 && replacementLines[^1].Length == 0)
            replacementLines.RemoveAt(replacementLines.Count - 1);
        return new SkinIniDocument(replacementLines, encoding, newline, ended);
    }

    public string? GetValue(string section, string key)
    {
        if (!index.TryGetValue(section, out var keys)
            || !keys.TryGetValue(key, out var lineIndex))
            return null;
        var colon = lines[lineIndex].IndexOf(':');
        return colon < 0 ? "" : lines[lineIndex][(colon + 1)..].Trim();
    }

    public bool HasValue(string section, string key) =>
        index.TryGetValue(section, out var keys) && keys.ContainsKey(key);

    public void SetValue(string section, string key, string value)
    {
        if (index.TryGetValue(section, out var keys)
            && keys.TryGetValue(key, out var lineIndex))
        {
            var existing = lines[lineIndex];
            var indentation = existing[..(existing.Length - existing.TrimStart().Length)];
            lines[lineIndex] = $"{indentation}{key}: {value}";
            Reindex();
            return;
        }

        var header = FindSection(section);
        if (header < 0)
        {
            if (lines.Count > 0 && lines[^1].Length > 0)
                lines.Add("");
            lines.Add($"[{section}]");
            lines.Add($"{key}: {value}");
        }
        else
        {
            var insertion = lines.Count;
            for (var line = header + 1; line < lines.Count; line++)
            {
                var trimmed = lines[line].Trim();
                if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
                {
                    insertion = line;
                    break;
                }
            }

            lines.Insert(insertion, $"{key}: {value}");
        }

        Reindex();
    }

    public void RemoveValue(string section, string key)
    {
        if (!index.TryGetValue(section, out var keys)
            || !keys.TryGetValue(key, out var lineIndex))
            return;
        lines.RemoveAt(lineIndex);
        Reindex();
    }

    public static bool TryValidate(SkinIniKeyDefinition definition, string value, out string error)
    {
        error = "";
        switch (definition.Type)
        {
            case SkinIniValueType.Boolean when value is not ("0" or "1"):
                error = "Use 0 or 1.";
                return false;
            case SkinIniValueType.Integer when !int.TryParse(value, out _):
                error = "Enter a whole number.";
                return false;
            case SkinIniValueType.Rgb:
                return ValidateColor(value, 3, out error);
            case SkinIniValueType.Rgba:
                return ValidateColor(value, 4, out error);
            default:
                return true;
        }
    }

    private static bool ValidateColor(string value, int channels, out string error)
    {
        var parts = value.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != channels
            || parts.Any(part => !byte.TryParse(part, out _)))
        {
            error = $"Enter {channels} comma-separated values from 0 to 255.";
            return false;
        }

        error = "";
        return true;
    }

    private void Reindex()
    {
        index.Clear();
        string? section = null;
        for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            var line = lines[lineIndex].Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim();
                continue;
            }

            if (section is null || section.Equals("Mania", StringComparison.OrdinalIgnoreCase)
                || line.Length == 0 || line.StartsWith("//"))
                continue;
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            if (!index.TryGetValue(section, out var keys))
                index[section] = keys = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            keys.TryAdd(line[..colon].Trim(), lineIndex);
        }
    }

    private int FindSection(string section)
    {
        for (var line = 0; line < lines.Count; line++)
        {
            if (lines[line].Trim().Equals($"[{section}]", StringComparison.OrdinalIgnoreCase))
                return line;
        }

        return -1;
    }

    private static SkinIniDocument ParseText(string text, Encoding encoding)
    {
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n"
            : text.Contains('\n') ? "\n"
            : Environment.NewLine;
        var ended = text.EndsWith("\r\n", StringComparison.Ordinal)
            || text.EndsWith('\n');
        var normalized = text.Replace("\r\n", "\n");
        var lines = normalized.Split('\n').ToList();
        if (ended && lines.Count > 0 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);
        return new SkinIniDocument(lines, encoding, newline, ended);
    }

    private static (string Text, Encoding Encoding) Decode(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
        {
            var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
            return (encoding.GetString(bytes.AsSpan(3)), encoding);
        }

        try
        {
            var utf8 = new UTF8Encoding(false, throwOnInvalidBytes: true);
            return (utf8.GetString(bytes), utf8);
        }
        catch (DecoderFallbackException)
        {
            return (Encoding.Latin1.GetString(bytes), Encoding.Latin1);
        }
    }

    private static byte[] Encode(string text, Encoding encoding)
    {
        var body = encoding.GetBytes(text);
        var preamble = encoding.GetPreamble();
        if (preamble.Length == 0)
            return body;
        var result = new byte[preamble.Length + body.Length];
        preamble.CopyTo(result, 0);
        body.CopyTo(result, preamble.Length);
        return result;
    }
}

public static class SkinIniSchema
{
    public static readonly IReadOnlyList<SkinIniKeyDefinition> General =
    [
        new("General", "Name", "Skin name", SkinIniValueType.Text, "Unknown"),
        new("General", "Author", "Skin author", SkinIniValueType.Text, ""),
        new("General", "Version", "Skin version", SkinIniValueType.Text, "latest"),
        new("General", "AnimationFramerate", "Animation framerate", SkinIniValueType.Integer, "-1"),
        new("General", "AllowSliderBallTint", "Tint slider ball with combo colour", SkinIniValueType.Boolean, "0"),
        new("General", "ComboBurstRandom", "Random combo burst order", SkinIniValueType.Boolean, "0"),
        new("General", "CursorCentre", "Cursor origin at centre", SkinIniValueType.Boolean, "1"),
        new("General", "CursorExpand", "Cursor expands on click", SkinIniValueType.Boolean, "1"),
        new("General", "CursorRotate", "Cursor rotates", SkinIniValueType.Boolean, "1"),
        new("General", "CursorTrailRotate", "Cursor trail rotates", SkinIniValueType.Boolean, "1"),
        new("General", "CustomComboBurstSounds", "Combo burst trigger counts", SkinIniValueType.Text, ""),
        new("General", "HitCircleOverlayAboveNumber", "Overlay above hitcircle number", SkinIniValueType.Boolean, "1"),
        new("General", "LayeredHitSounds", "Always play hitnormal", SkinIniValueType.Boolean, "1"),
        new("General", "SliderBallFlip", "Flip slider ball when reversed", SkinIniValueType.Boolean, "1"),
        new("General", "SpinnerFadePlayfield", "Black bars during spinners", SkinIniValueType.Boolean, "0"),
        new("General", "SpinnerFrequencyModulate", "Pitch up spinner sound", SkinIniValueType.Boolean, "1"),
        new("General", "SpinnerNoBlink", "Keep highest metre bar visible", SkinIniValueType.Boolean, "0"),
    ];

    public static readonly IReadOnlyList<SkinIniKeyDefinition> Colours =
    [
        new("Colours", "Combo1", "Combo colour 1", SkinIniValueType.Rgb, "255,192,0"),
        new("Colours", "Combo2", "Combo colour 2", SkinIniValueType.Rgb, "0,202,0"),
        new("Colours", "Combo3", "Combo colour 3", SkinIniValueType.Rgb, "18,124,255"),
        new("Colours", "Combo4", "Combo colour 4", SkinIniValueType.Rgb, "242,24,57"),
        new("Colours", "Combo5", "Combo colour 5", SkinIniValueType.Rgb, ""),
        new("Colours", "Combo6", "Combo colour 6", SkinIniValueType.Rgb, ""),
        new("Colours", "Combo7", "Combo colour 7", SkinIniValueType.Rgb, ""),
        new("Colours", "Combo8", "Combo colour 8", SkinIniValueType.Rgb, ""),
        new("Colours", "InputOverlayText", "Input overlay number colour", SkinIniValueType.Rgb, "0,0,0"),
        new("Colours", "MenuGlow", "Menu spectrum colour", SkinIniValueType.Rgb, "0,78,155"),
        new("Colours", "SliderBall", "Slider ball colour", SkinIniValueType.Rgb, "2,170,255"),
        new("Colours", "SliderBorder", "Slider border colour", SkinIniValueType.Rgb, "255,255,255"),
        new("Colours", "SliderTrackOverride", "Slider body override colour", SkinIniValueType.Rgb, ""),
        new("Colours", "SongSelectActiveText", "Song select active text", SkinIniValueType.Rgb, "0,0,0"),
        new("Colours", "SongSelectInactiveText", "Song select inactive text", SkinIniValueType.Rgb, "255,255,255"),
        new("Colours", "SpinnerBackground", "Spinner background tint", SkinIniValueType.Rgb, "100,100,100"),
        new("Colours", "StarBreakAdditive", "Break particle colour", SkinIniValueType.Rgb, "255,182,193"),
    ];

    public static readonly IReadOnlyList<SkinIniKeyDefinition> Fonts =
    [
        new("Fonts", "HitCirclePrefix", "Hitcircle number prefix", SkinIniValueType.Text, "default"),
        new("Fonts", "HitCircleOverlap", "Hitcircle number overlap", SkinIniValueType.Integer, "-2"),
        new("Fonts", "ScorePrefix", "Score number prefix", SkinIniValueType.Text, "score"),
        new("Fonts", "ScoreOverlap", "Score number overlap", SkinIniValueType.Integer, "0"),
        new("Fonts", "ComboPrefix", "Combo number prefix", SkinIniValueType.Text, "score"),
        new("Fonts", "ComboOverlap", "Combo number overlap", SkinIniValueType.Integer, "0"),
    ];

    public static readonly IReadOnlyList<SkinIniKeyDefinition> CatchTheBeat =
    [
        new("CatchTheBeat", "HyperDash", "Hyperdash catcher colour", SkinIniValueType.Rgb, "255,0,0"),
        new("CatchTheBeat", "HyperDashFruit", "Hyperdash fruit colour", SkinIniValueType.Rgb, ""),
        new("CatchTheBeat", "HyperDashAfterImage", "Hyperdash after-image colour", SkinIniValueType.Rgb, ""),
    ];

    public static IEnumerable<(string Section, IReadOnlyList<SkinIniKeyDefinition> Keys)> Sections()
    {
        yield return ("General", General);
        yield return ("Colours", Colours);
        yield return ("Fonts", Fonts);
        yield return ("CatchTheBeat", CatchTheBeat);
    }
}
