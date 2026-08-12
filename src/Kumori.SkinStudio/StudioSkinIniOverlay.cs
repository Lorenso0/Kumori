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

internal partial class StudioSkinIniOverlay : CompositeDrawable
{
    private readonly FillFlowContainer fields;
    private SpriteText validation = null!;
    private readonly List<EditorEntry> entries = [];
    private SkinIniDocument? document;
    private Func<byte[], bool>? commit;
    private Action<byte[]>? switchToRaw;
    private Action<string>? focusContext;

    public StudioSkinIniOverlay()
    {
        RelativeSizeAxes = Axes.Both;
        Depth = -90;
        InternalChildren =
        [
            new Box
            {
                RelativeSizeAxes = Axes.Both,
                Colour = Colour4.Black.Opacity(0.76f),
            },
            new Container
            {
                RelativeSizeAxes = Axes.Both,
                Padding = new MarginPadding
                {
                    Horizontal = 110,
                    Vertical = 64,
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
                            Child = fields = new FillFlowContainer
                            {
                                RelativeSizeAxes = Axes.X,
                                AutoSizeAxes = Axes.Y,
                                Direction = FillDirection.Vertical,
                                Spacing = new Vector2(0, 10),
                                Padding = new MarginPadding(28),
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
        Action<byte[]>? switchToRaw = null,
        Action<string>? focusContext = null)
    {
        document = SkinIniDocument.Parse(skinIni);
        this.commit = commit;
        this.switchToRaw = switchToRaw;
        this.focusContext = focusContext;
        entries.Clear();
        fields.Clear();
        fields.Add(new SpriteText
        {
            Text = "STRUCTURED SKIN.INI",
            Font = FontUsage.Default.With(size: 22, weight: "Bold"),
            Colour = Colour4.FromHex("#FFB7D5"),
        });
        fields.Add(new SpriteText
        {
            Text = "Known values are validated here. Comments, unknown keys, ordering, encoding, and line endings remain untouched.",
            Font = FontUsage.Default.With(size: 12),
            Colour = Colour4.FromHex("#C6A8BA"),
        });
        var maniaKeysInput = new OsuTextBox
        {
            RelativeSizeAxes = Axes.X,
            Height = 38,
            PlaceholderText = "New Mania key count (1–18)",
            LengthLimit = 2,
        };
        fields.Add(maniaKeysInput);
        fields.Add(new StudioActionButton(
            "Add Mania section",
            () => addManiaSection(maniaKeysInput.Current.Value)));

        foreach (var (section, definitions) in SkinIniSchema.Sections())
        {
            addSectionHeading(section);
            foreach (var definition in definitions)
            {
                var original = document.GetValue(section, definition.Key);
                addEntry(section, definition, original);
            }
        }
        foreach (var mania in document.GetSections("Mania"))
        {
            if (mania.ManiaKeys is not int maniaKeys)
                continue;
            addSectionHeading(
                $"Mania {maniaKeys}K (section {mania.Occurrence + 1})");
            fields.Add(new StudioActionButton(
                $"Remove Mania {maniaKeys}K section",
                () => removeManiaSection(maniaKeys)));
            foreach (var (key, value) in mania.Values.Where(pair =>
                         !pair.Key.Equals("Keys", StringComparison.OrdinalIgnoreCase)))
            {
                addEntry(
                    "Mania",
                    ManiaDefinition(key, value),
                    value,
                    mania.ManiaKeys);
            }
        }

        validation = new SpriteText
        {
            Text = "",
            Font = FontUsage.Default.With(size: 11),
            Colour = Colour4.FromHex("#FF8EAF"),
        };
        fields.Add(validation);
        fields.Add(new StudioActionButton(
            "Switch to raw editor (keep unsaved edits)",
            switchMode,
            enabled: switchToRaw is not null));
        fields.Add(new StudioActionButton("Save structured skin.ini", save, accent: true));
        fields.Add(new StudioActionButton("Cancel", Hide));
        Show();
    }

    private void save()
    {
        if (!applyEntriesToDocument() || document is null)
            return;
        if (commit?.Invoke(document.ToBytes()) == true)
            Hide();
    }

    private void switchMode()
    {
        if (!applyEntriesToDocument() || document is null || switchToRaw is null)
            return;
        var bytes = document.ToBytes();
        Hide();
        switchToRaw(bytes);
    }

    private bool applyEntriesToDocument()
    {
        if (document is null)
            return false;
        foreach (var entry in entries)
        {
            var value = entry.TextBox.Current.Value.Trim();
            if (value.Length > 0
                && !SkinIniDocument.TryValidate(entry.Definition, value, out var error))
            {
                validation.Text = $"{entry.Section} / {entry.Definition.Key}: {error}";
                return false;
            }
        }

        foreach (var entry in entries)
        {
            var value = entry.TextBox.Current.Value.Trim();
            if (value.Equals(entry.OriginalValue, StringComparison.Ordinal))
                continue;
            if (entry.ManiaKeys is { } maniaKeys)
            {
                if (value.Length == 0)
                    document.RemoveManiaValue(maniaKeys, entry.Definition.Key);
                else
                    document.SetManiaValue(maniaKeys, entry.Definition.Key, value);
            }
            else if (value.Length == 0)
                document.RemoveValue(entry.Section, entry.Definition.Key);
            else
                document.SetValue(entry.Section, entry.Definition.Key, value);
        }
        return true;
    }

    private void addManiaSection(string rawKeys)
    {
        if (!applyEntriesToDocument() || document is null || commit is null)
            return;
        if (!int.TryParse(rawKeys.Trim(), out var keys)
            || keys is < 1 or > 18)
        {
            validation.Text = "Mania key count must be between 1 and 18.";
            return;
        }
        if (document.GetSections("Mania").Any(section =>
                section.ManiaKeys == keys))
        {
            validation.Text = $"A Mania {keys}K section already exists.";
            return;
        }
        document.AddManiaSection(keys);
        Present(document.ToBytes(), commit, switchToRaw, focusContext);
    }

    private void removeManiaSection(int keys)
    {
        if (!applyEntriesToDocument() || document is null || commit is null)
            return;
        if (!document.RemoveManiaSection(keys))
        {
            validation.Text = $"The Mania {keys}K section no longer exists.";
            return;
        }
        Present(document.ToBytes(), commit, switchToRaw, focusContext);
    }

    private void addSectionHeading(string section)
    {
        fields.Add(new SpriteText
        {
            Text = section.ToUpperInvariant(),
            Margin = new MarginPadding { Top = 12 },
            Font = FontUsage.Default.With(size: 16, weight: "Bold"),
            Colour = Colour4.FromHex("#F3AFCF"),
        });
    }

    private void addEntry(
        string section,
        SkinIniKeyDefinition definition,
        string? original,
        int? maniaKeys = null)
    {
        var textBox = new OsuTextBox
        {
            RelativeSizeAxes = Axes.X,
            Height = 38,
            PlaceholderText = $"Default: {definition.DefaultValue}",
            LengthLimit = 2000,
        };
        textBox.Current.Value = original ?? "";
        entries.Add(new EditorEntry(
            section,
            definition,
            original,
            textBox,
            maniaKeys));
        var children = new List<Drawable>
        {
            new SpriteText
            {
                Text = $"{definition.Key} — {definition.Label}",
                Font = FontUsage.Default.With(size: 11, weight: "SemiBold"),
                Colour = Colour4.White,
            },
            textBox,
        };
        var context = ContextComponent(section, definition.Key, original);
        if (context is not null && focusContext is not null)
        {
            children.Add(new StudioActionButton(
                $"Show {context} in workbench",
                () =>
                {
                    Hide();
                    focusContext(context);
                }));
        }
        fields.Add(new FillFlowContainer
        {
            RelativeSizeAxes = Axes.X,
            AutoSizeAxes = Axes.Y,
            Direction = FillDirection.Vertical,
            Spacing = new Vector2(0, 4),
            Children = children,
        });
    }

    internal static string? ContextComponent(
        string section,
        string key,
        string? value)
    {
        if (section.Equals("Mania", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(value)
            && (key.StartsWith("NoteImage", StringComparison.OrdinalIgnoreCase)
                || key.StartsWith("KeyImage", StringComparison.OrdinalIgnoreCase)
                || key.StartsWith("Stage", StringComparison.OrdinalIgnoreCase)
                || key.StartsWith("Lighting", StringComparison.OrdinalIgnoreCase)
                || key.StartsWith("Hit", StringComparison.OrdinalIgnoreCase)))
        {
            return SkinDraftAssetService.ComponentName(
                Path.HasExtension(value) ? value : value + ".png");
        }

        return (section.ToLowerInvariant(), key.ToLowerInvariant()) switch
        {
            ("general", "cursorrotate")
                or ("general", "cursorexpand")
                or ("general", "cursorcentre") => "cursor",
            ("general", "comboburstsounds") => "comboburst",
            ("general", "spinnerfrequencymodulate")
                or ("general", "spinnerfadeplayfield") => "spinner-circle",
            ("colours", "sliderborder")
                or ("colours", "slidertrackoverride") => "sliderb",
            ("fonts", "hitcircleprefix") => "default-0",
            ("fonts", "scoreprefix") => "score-0",
            ("fonts", "comboprefix") => "combo-0",
            ("catchthebeat", "hyperdash") => "fruit-catcher-hyper",
            _ when section.Equals("Colours", StringComparison.OrdinalIgnoreCase)
                   && key.StartsWith("Combo", StringComparison.OrdinalIgnoreCase)
                => "hitcircle",
            _ => null,
        };
    }

    internal static SkinIniKeyDefinition ManiaDefinition(
        string key,
        string value)
    {
        var type = int.TryParse(value, out _)
            ? SkinIniValueType.Integer
            : looksLikeRgb(value)
                ? SkinIniValueType.Rgb
                : SkinIniValueType.Text;
        return new SkinIniKeyDefinition(
            "Mania",
            key,
            key,
            type,
            value);
    }

    internal void SetAcceptanceValue(
        string section,
        string key,
        string value)
    {
        var entry = entries.FirstOrDefault(candidate =>
            candidate.Section.Equals(
                section,
                StringComparison.OrdinalIgnoreCase)
            && candidate.Definition.Key.Equals(
                key,
                StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"Structured skin.ini field {section}/{key} is unavailable.");
        entry.TextBox.Current.Value = value;
    }

    internal void SaveAcceptance() => save();

    internal void FocusAcceptanceContext(string section, string key)
    {
        var entry = entries.FirstOrDefault(candidate =>
            candidate.Section.Equals(
                section,
                StringComparison.OrdinalIgnoreCase)
            && candidate.Definition.Key.Equals(
                key,
                StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"Structured skin.ini field {section}/{key} is unavailable.");
        var component = ContextComponent(
            entry.Section,
            entry.Definition.Key,
            entry.OriginalValue)
            ?? throw new InvalidOperationException(
                $"Structured skin.ini field {section}/{key} has no context.");
        Hide();
        focusContext?.Invoke(component);
    }

    private static bool looksLikeRgb(string value)
    {
        var parts = value.Split(',', StringSplitOptions.TrimEntries);
        return parts.Length == 3 && parts.All(part => byte.TryParse(part, out _));
    }

    private sealed record EditorEntry(
        string Section,
        SkinIniKeyDefinition Definition,
        string? OriginalValue,
        OsuTextBox TextBox,
        int? ManiaKeys);
}
