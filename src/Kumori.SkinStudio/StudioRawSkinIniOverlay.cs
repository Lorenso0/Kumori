using Kumori.Skins;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Game.Graphics.Containers;
using osu.Game.Graphics.UserInterface;
using osuTK;
using osuTK.Graphics;

namespace Kumori.SkinStudio;

internal partial class StudioRawSkinIniOverlay : CompositeDrawable
{
    private const string delete_marker = "<delete>";
    private readonly FillFlowContainer linesFlow;
    private SpriteText validation = null!;
    private readonly List<OsuTextBox> lines = [];
    private SkinIniDocument? document;
    private Func<byte[], bool>? commit;
    private Action<byte[]>? switchToStructured;
    private bool endedWithNewline;
    private bool controlsAdded;

    public StudioRawSkinIniOverlay()
    {
        RelativeSizeAxes = Axes.Both;
        Depth = -91;
        InternalChildren =
        [
            new Box
            {
                RelativeSizeAxes = Axes.Both,
                Colour = Colour4.Black.Opacity(0.78f),
            },
            new Container
            {
                RelativeSizeAxes = Axes.Both,
                Padding = new MarginPadding
                {
                    Horizontal = 90,
                    Vertical = 52,
                },
                Child = new Container
                {
                    RelativeSizeAxes = Axes.Both,
                    Masking = true,
                    CornerRadius = 12,
                    Children =
                    [
                        new Box
                        {
                            RelativeSizeAxes = Axes.Both,
                            Colour = Colour4.FromHex("#1B1925"),
                        },
                        new OsuScrollContainer(Direction.Vertical)
                        {
                            RelativeSizeAxes = Axes.Both,
                            ScrollbarVisible = true,
                            Child = linesFlow = new FillFlowContainer
                            {
                                RelativeSizeAxes = Axes.X,
                                AutoSizeAxes = Axes.Y,
                                Direction = FillDirection.Vertical,
                                Spacing = new Vector2(0, 7),
                                Padding = new MarginPadding(26),
                            },
                        },
                    ],
                },
            },
        ];
        Hide();
    }

    public void Present(
        byte[] skinIni,
        Func<byte[], bool> commit,
        Action<byte[]>? switchToStructured = null)
    {
        document = SkinIniDocument.Parse(skinIni);
        this.commit = commit;
        this.switchToStructured = switchToStructured;
        var text = document.ToText();
        endedWithNewline = text.EndsWith('\n');
        var normalized = text.Replace("\r\n", "\n");
        var sourceLines = normalized.Split('\n').ToList();
        if (endedWithNewline && sourceLines.Count > 0 && sourceLines[^1].Length == 0)
            sourceLines.RemoveAt(sourceLines.Count - 1);
        rebuildEditor(sourceLines);
        Show();
    }

    private void rebuildEditor(IReadOnlyList<string> sourceLines)
    {
        lines.Clear();
        linesFlow.Clear();
        controlsAdded = false;
        linesFlow.Add(label("RAW SKIN.INI", 22, Colour4.FromHex("#FFB7D5"), true));
        linesFlow.Add(label(
            $"Edit any original line. Use \u2191, \u2193, +, or \u00D7 to reorder, insert, or remove it; {delete_marker} is also accepted. Original encoding and line endings are retained.",
            11,
            Colour4.FromHex("#C6A8BA")));
        foreach (var line in sourceLines)
            addLine(line);
        validation = label("", 11, Colour4.FromHex("#FF8EAF"));
        linesFlow.Add(validation);
        linesFlow.Add(new StudioActionButton("Add raw line", () => addLine("")));
        linesFlow.Add(new StudioActionButton(
            "Switch to structured editor (keep unsaved edits)",
            switchMode,
            enabled: switchToStructured is not null));
        linesFlow.Add(new StudioActionButton("Save raw skin.ini", save, accent: true));
        linesFlow.Add(new StudioActionButton("Cancel", Hide));
        controlsAdded = true;
    }

    private void addLine(string value)
    {
        var textBox = new OsuTextBox
        {
            RelativeSizeAxes = Axes.X,
            Height = 34,
            LengthLimit = 10_000,
            PlaceholderText = "Blank line",
        };
        textBox.Current.Value = value;
        lines.Add(textBox);
        var row = new GridContainer
        {
            RelativeSizeAxes = Axes.X,
            Height = 34,
            ColumnDimensions =
            [
                new Dimension(),
                new Dimension(GridSizeMode.Absolute, 48),
                new Dimension(GridSizeMode.Absolute, 48),
                new Dimension(GridSizeMode.Absolute, 48),
                new Dimension(GridSizeMode.Absolute, 48),
            ],
            RowDimensions =
            [
                new Dimension(GridSizeMode.Absolute, 34),
            ],
            Content = new[]
            {
                new Drawable[]
                {
                    textBox,
                    lineButton("\u2191", () => moveLine(textBox, -1)),
                    lineButton("\u2193", () => moveLine(textBox, 1)),
                    lineButton("+", () => insertLineAfter(textBox)),
                    lineButton("\u00D7", () => removeLine(textBox)),
                },
            },
        };
        if (controlsAdded)
            linesFlow.Insert(linesFlow.Count - 4, row);
        else
            linesFlow.Add(row);
    }

    private static StudioActionButton lineButton(string text, Action action) =>
        new(text, action)
        {
            Height = 34,
        };

    private void moveLine(OsuTextBox line, int delta)
    {
        var index = lines.IndexOf(line);
        if (index < 0)
            return;
        var values = MoveRawLine(
            lines.Select(item => item.Current.Value),
            index,
            delta);
        Schedule(() => rebuildEditor(values));
    }

    private void insertLineAfter(OsuTextBox line)
    {
        var index = lines.IndexOf(line);
        if (index < 0)
            return;
        var values = InsertRawLine(
            lines.Select(item => item.Current.Value),
            index + 1,
            "");
        Schedule(() => rebuildEditor(values));
    }

    private void removeLine(OsuTextBox line)
    {
        var index = lines.IndexOf(line);
        if (index < 0)
            return;
        var values = RemoveRawLine(
            lines.Select(item => item.Current.Value),
            index);
        Schedule(() => rebuildEditor(values));
    }

    private void save()
    {
        if (!tryCompose(out var bytes))
            return;
        if (commit?.Invoke(bytes) == true)
            Hide();
    }

    private void switchMode()
    {
        if (switchToStructured is null || !tryCompose(out var bytes))
            return;
        Hide();
        switchToStructured(bytes);
    }

    private bool tryCompose(out byte[] bytes)
    {
        bytes = [];
        if (document is null)
            return false;
        try
        {
            var raw = ComposeRawText(
                lines.Select(line => line.Current.Value),
                endedWithNewline);
            var updated = document.WithText(raw);
            bytes = updated.ToBytes();
            SkinIniDocument.Parse(bytes);
            validation.Text = "";
            return true;
        }
        catch (Exception ex)
        {
            validation.Text = $"Raw skin.ini is invalid: {ex.Message}";
            return false;
        }
    }

    internal static string ComposeRawText(
        IEnumerable<string> lines,
        bool endedWithNewline)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var raw = string.Join(
            '\n',
            lines.Where(line => !line.Trim().Equals(
                delete_marker,
                StringComparison.OrdinalIgnoreCase)));
        return endedWithNewline ? raw + "\n" : raw;
    }

    internal static IReadOnlyList<string> MoveRawLine(
        IEnumerable<string> source,
        int index,
        int delta)
    {
        ArgumentNullException.ThrowIfNull(source);
        var result = source.ToList();
        var target = index + delta;
        if (index < 0 || index >= result.Count
            || target < 0 || target >= result.Count)
        {
            return result;
        }
        (result[index], result[target]) = (result[target], result[index]);
        return result;
    }

    internal static IReadOnlyList<string> InsertRawLine(
        IEnumerable<string> source,
        int index,
        string value)
    {
        ArgumentNullException.ThrowIfNull(source);
        var result = source.ToList();
        result.Insert(Math.Clamp(index, 0, result.Count), value);
        return result;
    }

    internal static IReadOnlyList<string> RemoveRawLine(
        IEnumerable<string> source,
        int index)
    {
        ArgumentNullException.ThrowIfNull(source);
        var result = source.ToList();
        if (index >= 0 && index < result.Count)
            result.RemoveAt(index);
        return result;
    }

    internal IReadOnlyList<string> AcceptanceLines =>
        lines.Select(line => line.Current.Value).ToArray();

    internal string AcceptanceValidation => validation.Text.ToString();

    internal void SetAcceptanceLines(IEnumerable<string> values) =>
        rebuildEditor(values.ToArray());

    internal void SaveAcceptance() => save();

    private static SpriteText label(
        string text,
        float size,
        Colour4 colour,
        bool bold = false) => new()
        {
            Text = text,
            Font = FontUsage.Default.With(size: size, weight: bold ? "Bold" : "Regular"),
            Colour = colour,
        };
}
