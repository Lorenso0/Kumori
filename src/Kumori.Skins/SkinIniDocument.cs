using System.Text;

namespace Kumori.Skins;

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

public enum SkinIniVisualGroup
{
    Identity,
    Animation,
    Slider,
    Combo,
    Cursor,
    HitObjects,
    Spinner,
    Interface,
    Fonts,
    Catch,
}

public enum SkinIniPreviewKind
{
    Information,
    ComboPalette,
    Slider,
    Cursor,
    HitObjects,
    Spinner,
    Interface,
    Catch,
}

public sealed record SkinIniRichMetadata(
    SkinIniVisualGroup Group,
    SkinIniPreviewKind Preview,
    string Help,
    IReadOnlyList<string> Affects);

public sealed record SkinIniSectionInstance(
    string Name,
    int Occurrence,
    int? ManiaKeys,
    IReadOnlyDictionary<string, string> Values);

/// <summary>
/// Presentation-only metadata for Form mode. It deliberately sits beside the
/// schema so its ordering and Raw-mode serialization never change.
/// </summary>
public static class SkinIniRichEditor
{
    public static SkinIniRichMetadata Describe(SkinIniKeyDefinition definition)
    {
        var (section, key) = (definition.Section, definition.Key);
        if (section.Equals("Colours", StringComparison.OrdinalIgnoreCase))
        {
            if (key.StartsWith("Combo", StringComparison.OrdinalIgnoreCase))
                return New(SkinIniVisualGroup.Combo, SkinIniPreviewKind.ComboPalette,
                    "Controls the repeating hit-object colour sequence.", "Hit circles", "Approach circles", "Slider ball");
            if (key.StartsWith("Slider", StringComparison.OrdinalIgnoreCase))
                return New(SkinIniVisualGroup.Slider, SkinIniPreviewKind.Slider,
                    "Updates the rendered slider body and ball in the live preview.", "Slider body", "Slider border", "Slider ball");
            if (key.StartsWith("SongSelect", StringComparison.OrdinalIgnoreCase) || key is "InputOverlayText" or "MenuGlow")
                return New(SkinIniVisualGroup.Interface, SkinIniPreviewKind.Interface,
                    "Applies to osu!'s interface rather than the playfield.", "HUD", "Song select");
            if (key.StartsWith("Spinner", StringComparison.OrdinalIgnoreCase) || key == "StarBreakAdditive")
                return New(SkinIniVisualGroup.Spinner, SkinIniPreviewKind.Spinner,
                    "Applies to spinner or break-time presentation.", "Spinner", "Break effects");
        }

        if (section.Equals("CatchTheBeat", StringComparison.OrdinalIgnoreCase))
            return New(SkinIniVisualGroup.Catch, SkinIniPreviewKind.Catch,
                "Applies to Catch the Beat hyperdash effects.", "Catch", "Hyperdash");
        if (section.Equals("Fonts", StringComparison.OrdinalIgnoreCase))
            return New(SkinIniVisualGroup.Fonts, SkinIniPreviewKind.HitObjects,
                "Changes the number assets used by hit objects or score displays.", "Hit-object numbers", "Score");

        return key switch
        {
            "AnimationFramerate" => New(SkinIniVisualGroup.Animation, SkinIniPreviewKind.Information,
                "Sets the frame rate for animated skin elements.", "Animated assets"),
            "AllowSliderBallTint" or "SliderBallFlip" => New(SkinIniVisualGroup.Slider, SkinIniPreviewKind.Slider,
                "Changes slider behaviour in the live slider preview.", "Slider ball", "Reverse arrows"),
            "ComboBurstRandom" or "CustomComboBurstSounds" => New(SkinIniVisualGroup.Combo, SkinIniPreviewKind.ComboPalette,
                "Changes combo progression feedback.", "Combo colours", "Combo bursts"),
            "CursorCentre" or "CursorExpand" or "CursorRotate" or "CursorTrailRotate" => New(SkinIniVisualGroup.Cursor, SkinIniPreviewKind.Cursor,
                "Changes how the cursor and cursor trail behave.", "Cursor", "Cursor trail"),
            "HitCircleOverlayAboveNumber" or "LayeredHitSounds" => New(SkinIniVisualGroup.HitObjects, SkinIniPreviewKind.HitObjects,
                "Changes how hit objects are layered or sounded.", "Hit circles", "Numbers", "Overlay"),
            "SpinnerFadePlayfield" or "SpinnerFrequencyModulate" or "SpinnerNoBlink" => New(SkinIniVisualGroup.Spinner, SkinIniPreviewKind.Spinner,
                "Changes spinner presentation or feedback.", "Spinner"),
            _ => New(SkinIniVisualGroup.Identity, SkinIniPreviewKind.Information,
                "General skin information.", "Skin metadata"),
        };
    }

    public static string DisplayName(SkinIniVisualGroup group) => group switch
    {
        SkinIniVisualGroup.HitObjects => "Hit objects",
        SkinIniVisualGroup.Combo => "Combo & hit colours",
        SkinIniVisualGroup.Interface => "Interface colours",
        SkinIniVisualGroup.Catch => "Catch the Beat",
        _ => group.ToString(),
    };

    private static SkinIniRichMetadata New(
        SkinIniVisualGroup group,
        SkinIniPreviewKind preview,
        string help,
        params string[] affects) => new(group, preview, help, affects);
}

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

    public IReadOnlyList<SkinIniSectionInstance> GetSections(string section)
    {
        var result = new List<SkinIniSectionInstance>();
        foreach (var header in FindSections(section))
        {
            var values = ReadSectionValues(header);
            int? maniaKeys = null;
            if (section.Equals("Mania", StringComparison.OrdinalIgnoreCase)
                && values.TryGetValue("Keys", out var rawKeys)
                && int.TryParse(rawKeys, out var parsedKeys))
                maniaKeys = parsedKeys;
            result.Add(new SkinIniSectionInstance(
                section,
                result.Count,
                maniaKeys,
                values));
        }
        return result;
    }

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

    /// <summary>
    /// Sets a value in the Mania section for a specific key count. Unlike the
    /// regular editor index this safely addresses repeated [Mania] sections.
    /// </summary>
    public void SetManiaValue(int keys, string key, string value)
    {
        var header = FindManiaSection(keys);
        if (header < 0)
        {
            if (lines.Count > 0 && lines[^1].Length > 0)
                lines.Add("");
            lines.Add("[Mania]");
            lines.Add($"Keys: {keys}");
            if (!key.Equals("Keys", StringComparison.OrdinalIgnoreCase))
                lines.Add($"{key}: {value}");
            Reindex();
            return;
        }
        SetValueInSection(header, key, value);
    }

    public void RemoveManiaValue(int keys, string key)
    {
        var header = FindManiaSection(keys);
        if (header < 0) return;
        var line = FindKeyInSection(header, key);
        if (line < 0) return;
        lines.RemoveAt(line);
        Reindex();
    }

    public bool RemoveManiaSection(int keys)
    {
        var header = FindManiaSection(keys);
        if (header < 0)
            return false;
        var end = lines.Count;
        for (var line = header + 1; line < lines.Count; line++)
        {
            var trimmed = lines[line].Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                end = line;
                break;
            }
        }
        lines.RemoveRange(header, end - header);
        if (header > 0
            && header <= lines.Count
            && lines[header - 1].Length == 0
            && (header == lines.Count || lines[header].Length == 0))
        {
            lines.RemoveAt(header - 1);
        }
        Reindex();
        return true;
    }

    public bool AddManiaSection(int keys)
    {
        if (keys is < 1 or > 18)
            throw new ArgumentOutOfRangeException(
                nameof(keys),
                "Mania key count must be between 1 and 18.");
        if (FindManiaSection(keys) >= 0)
            return false;
        SetManiaValue(keys, "Keys", keys.ToString());
        SetManiaValue(keys, "ColumnStart", "136");
        SetManiaValue(keys, "HitPosition", "402");
        SetManiaValue(keys, "ScorePosition", "325");
        SetManiaValue(keys, "ComboPosition", "111");
        SetManiaValue(
            keys,
            "ColumnWidth",
            string.Join(',', Enumerable.Repeat("30", keys)));
        SetManiaValue(
            keys,
            "ColumnLineWidth",
            string.Join(',', Enumerable.Repeat("2", keys + 1)));
        return true;
    }

    public void ApplyPatch(IEnumerable<SkinExtraIniPatchEntry> patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        foreach (var entry in patch)
        {
            if (entry.Section.Equals("Mania", StringComparison.OrdinalIgnoreCase))
            {
                if (entry.ManiaKeys is not int maniaKeys)
                    continue;
                if (entry.Value is null)
                    RemoveManiaValue(maniaKeys, entry.Key);
                else
                    SetManiaValue(maniaKeys, entry.Key, entry.Value);
            }
            else if (entry.Value is null)
            {
                RemoveValue(entry.Section, entry.Key);
            }
            else
            {
                SetValue(entry.Section, entry.Key, entry.Value);
            }
        }
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
            // osu!'s legacy decoder assigns every value as it is encountered,
            // so a repeated key (including one in a repeated section) is
            // resolved using its last occurrence.
            keys[line[..colon].Trim()] = lineIndex;
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

    private IReadOnlyList<int> FindSections(string section)
    {
        var result = new List<int>();
        for (var line = 0; line < lines.Count; line++)
            if (lines[line].Trim().Equals($"[{section}]", StringComparison.OrdinalIgnoreCase))
                result.Add(line);
        return result;
    }

    private int FindManiaSection(int keys)
    {
        foreach (var header in FindSections("Mania"))
        {
            var values = ReadSectionValues(header);
            if (values.TryGetValue("Keys", out var raw)
                && int.TryParse(raw, out var parsed)
                && parsed == keys)
                return header;
        }
        return -1;
    }

    private Dictionary<string, string> ReadSectionValues(int header)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var line = header + 1; line < lines.Count; line++)
        {
            var trimmed = lines[line].Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']')) break;
            if (trimmed.Length == 0 || trimmed.StartsWith("//")) continue;
            var colon = trimmed.IndexOf(':');
            if (colon <= 0) continue;
            result[trimmed[..colon].Trim()] = trimmed[(colon + 1)..].Trim();
        }
        return result;
    }

    private int FindKeyInSection(int header, string key)
    {
        var result = -1;
        for (var line = header + 1; line < lines.Count; line++)
        {
            var trimmed = lines[line].Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']')) break;
            var colon = trimmed.IndexOf(':');
            if (colon > 0 && trimmed[..colon].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                result = line;
        }
        return result;
    }

    private void SetValueInSection(int header, string key, string value)
    {
        var existingLine = FindKeyInSection(header, key);
        if (existingLine >= 0)
        {
            var existing = lines[existingLine];
            var indentation = existing[..(existing.Length - existing.TrimStart().Length)];
            lines[existingLine] = $"{indentation}{key}: {value}";
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
