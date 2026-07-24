using System.ComponentModel;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Kumori.Tracking;
using Color = System.Windows.Media.Color;

namespace Kumori.App.Skins;

public enum SkinRecolorMode
{
    Colorize,
    Tint,
    HueSaturation,
}

public sealed class SkinElementCategory
{
    public required string Name { get; init; }
    public bool IsSubfolder { get; init; }
    public List<SkinElementEntry> Files { get; } = new();
}

public sealed class SkinElementEntry : INotifyPropertyChanged
{
    private BitmapSource? thumbnail;
    private readonly List<SkinElementEntry> resolutionVariants = [];
    private bool? hasVisiblePixels;

    public SkinElementEntry(LazerSkinFileInfo file)
    {
        File = file;
        Mode = SkinElementCategorizer.DefaultModeFor(file.Filename);
    }

    public LazerSkinFileInfo File { get; private set; }
    public string Filename => File.Filename;
    public string Hash => File.Hash;
    public bool IsImage => SkinElementCategorizer.IsImage(Filename);
    public bool IsAudio => SkinElementCategorizer.IsAudio(Filename);
    public bool IsHighResolution => SkinElementCategorizer.IsHighResolution(Filename);
    public IReadOnlyList<SkinElementEntry> ResolutionVariants => resolutionVariants;
    public IEnumerable<SkinElementEntry> PhysicalEntries => [this, .. resolutionVariants];
    public bool HasPairedResolution => resolutionVariants.Count > 0;
    public string ResolutionVariantLabel => HasPairedResolution ? "1× + 2× · edits both" : "";
    public long TotalSizeBytes => PhysicalEntries.Sum(entry => entry.File.SizeBytes);
    public bool? HasVisiblePixels
    {
        get => hasVisiblePixels;
        set
        {
            hasVisiblePixels = value;
            OnPropertyChanged(nameof(HasVisiblePixels));
            OnPropertyChanged(nameof(IsLogicallyEmpty));
        }
    }
    public bool IsLogicallyEmpty =>
        IsImage
        && PhysicalEntries.All(entry => entry.HasVisiblePixels == false);
    public SkinRecolorMode Mode { get; set; }
    public Color? TintColor { get; set; }
    public double HueShiftDegrees { get; set; }
    public double SaturationMultiplier { get; set; } = 1;
    public double LightnessMultiplier { get; set; } = 1;
    public byte[]? OriginalBytes { get; set; }
    public byte[]? OriginalPixels { get; set; }
    public int PixelWidth { get; set; }
    public int PixelHeight { get; set; }
    public int Stride { get; set; }

    public BitmapSource? Thumbnail
    {
        get => thumbnail;
        set
        {
            thumbnail = value;
            OnPropertyChanged(nameof(Thumbnail));
        }
    }

    public bool HasEdits => Mode == SkinRecolorMode.HueSaturation
        ? HueShiftDegrees != 0
          || Math.Abs(SaturationMultiplier - 1) > 0.0001
          || Math.Abs(LightnessMultiplier - 1) > 0.0001
        : TintColor is not null;

    public string EditState => HasEdits ? "Edited" : "";

    public void Reset()
    {
        foreach (var entry in PhysicalEntries)
            entry.ResetOwnState();
        RaiseStateChanged();
    }

    public void AddResolutionVariant(SkinElementEntry entry)
    {
        resolutionVariants.Add(entry);
        OnPropertyChanged(nameof(ResolutionVariants));
        OnPropertyChanged(nameof(HasPairedResolution));
        OnPropertyChanged(nameof(ResolutionVariantLabel));
        OnPropertyChanged(nameof(TotalSizeBytes));
        OnPropertyChanged(nameof(IsLogicallyEmpty));
    }

    public void SynchronizeEditsToVariants()
    {
        foreach (var variant in resolutionVariants)
        {
            variant.Mode = Mode;
            variant.TintColor = TintColor;
            variant.HueShiftDegrees = HueShiftDegrees;
            variant.SaturationMultiplier = SaturationMultiplier;
            variant.LightnessMultiplier = LightnessMultiplier;
            variant.RaiseStateChanged();
        }
    }

    public void ReplaceFile(LazerSkinFileInfo file)
    {
        File = file;
        OnPropertyChanged(nameof(Hash));
        OnPropertyChanged(nameof(File));
    }

    public void RaiseStateChanged()
    {
        OnPropertyChanged(nameof(HasEdits));
        OnPropertyChanged(nameof(EditState));
    }

    private void ResetOwnState()
    {
        TintColor = null;
        HueShiftDegrees = 0;
        SaturationMultiplier = 1;
        LightnessMultiplier = 1;
        RaiseStateChanged();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string property) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}

public static class SkinElementCategorizer
{
    public static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg"];
    public static readonly string[] AudioExtensions = [".mp3", ".wav", ".ogg"];

    private static readonly string[] orderedCategories =
    [
        "Cursor", "Hitcircles", "Numbers", "Judgements", "Sliders", "Spinner",
        "Scorebar", "Interface", "Catch", "Taiko", "Mania", "Skin Previews",
        "Sounds", "Other",
    ];

    public static bool IsImage(string filename) =>
        ImageExtensions.Contains(Path.GetExtension(filename), StringComparer.OrdinalIgnoreCase);

    public static bool IsAudio(string filename) =>
        AudioExtensions.Contains(Path.GetExtension(filename), StringComparer.OrdinalIgnoreCase);

    public static bool IsHighResolution(string filename) =>
        Path.GetFileNameWithoutExtension(filename)
            .EndsWith("@2x", StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<SkinElementCategory> Categorize(IEnumerable<LazerSkinFileInfo> files)
    {
        var roots = orderedCategories.ToDictionary(
            name => name,
            name => new SkinElementCategory { Name = name },
            StringComparer.OrdinalIgnoreCase);
        var folders = new Dictionary<string, SkinElementCategory>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files.Where(file =>
                     !file.Filename.Equals("skin.ini", StringComparison.OrdinalIgnoreCase)))
        {
            var normalized = file.Filename.Replace('\\', '/');
            var slash = normalized.IndexOf('/');
            SkinElementCategory category;
            if (slash >= 0)
            {
                var folder = normalized[..slash];
                if (!folders.TryGetValue(folder, out category!))
                {
                    category = new SkinElementCategory { Name = folder, IsSubfolder = true };
                    folders[folder] = category;
                }
            }
            else
            {
                category = roots[CategoryFor(normalized)];
            }

            category.Files.Add(new SkinElementEntry(file));
        }

        foreach (var category in roots.Values.Concat(folders.Values))
        {
            var logicalEntries = category.Files
                .GroupBy(entry => ResolutionKey(entry.Filename), StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var entries = group.ToArray();
                    var primary = entries.FirstOrDefault(entry => entry.IsHighResolution)
                        ?? entries[0];
                    foreach (var variant in entries.Where(entry => !ReferenceEquals(entry, primary)))
                        primary.AddResolutionVariant(variant);
                    return primary;
                })
                .ToList();
            logicalEntries.Sort((left, right) =>
                StringComparer.OrdinalIgnoreCase.Compare(left.Filename, right.Filename));
            category.Files.Clear();
            category.Files.AddRange(logicalEntries);
        }

        return orderedCategories.Select(name => roots[name])
            .Where(category => category.Files.Count > 0)
            .Concat(folders.Values.OrderBy(category => category.Name, StringComparer.OrdinalIgnoreCase))
            .ToArray();
    }

    public static SkinRecolorMode DefaultModeFor(string filename)
    {
        var stem = Stem(filename);
        return stem.Contains("preview") || stem.Contains("banner") || stem.Contains("thumbnail")
            ? SkinRecolorMode.HueSaturation
            : SkinRecolorMode.Colorize;
    }

    internal static string CategoryFor(string filename)
    {
        var stem = Stem(filename);
        if (IsAudio(filename)) return "Sounds";
        if (!IsImage(filename)) return "Other";
        if (stem.Contains("preview") || stem.Contains("banner") || stem.Contains("thumbnail"))
            return "Skin Previews";
        if (stem.StartsWith("cursor")) return "Cursor";
        if (stem.StartsWith("fruit") || stem == "comboburst-fruits") return "Catch";
        if (stem.StartsWith("taiko") || stem.StartsWith("pippidon")) return "Taiko";
        if (stem.StartsWith("mania") || stem.StartsWith("stage-") || stem.StartsWith("key")
            || stem.StartsWith("note") || stem is "lightingn" or "lightingl" or "comboburst-mania")
            return "Mania";
        if (stem.StartsWith("hitcircle") || stem.StartsWith("approachcircle")
            || stem.StartsWith("followpoint")) return "Hitcircles";
        if (stem.StartsWith("scorebar")) return "Scorebar";
        if (stem.StartsWith("default-") || stem.StartsWith("score-")
            || stem.StartsWith("combo-") || stem.StartsWith("scoreentry")) return "Numbers";
        if (stem.StartsWith("hit0") || stem.StartsWith("hit50") || stem.StartsWith("hit100")
            || stem.StartsWith("hit300") || stem.StartsWith("particle")
            || stem.StartsWith("lighting")) return "Judgements";
        if (stem.StartsWith("slider") || stem.StartsWith("reversearrow")) return "Sliders";
        if (stem.StartsWith("spinner")) return "Spinner";
        if (stem.StartsWith("menu") || stem.StartsWith("button") || stem.StartsWith("star")
            || stem.StartsWith("ranking") || stem.StartsWith("pause") || stem.StartsWith("fail")
            || stem.StartsWith("selection") || stem.StartsWith("mode-") || stem.StartsWith("count")
            || stem is "go" or "ready" || stem.StartsWith("play") || stem.StartsWith("inputoverlay")
            || stem.StartsWith("arrow") || stem.StartsWith("section-") || stem.StartsWith("multi-")
            || stem.StartsWith("songselect") || stem.StartsWith("comboburst")
            || stem.StartsWith("masking-")) return "Interface";
        return "Other";
    }

    private static string Stem(string filename)
    {
        var stem = Path.GetFileNameWithoutExtension(filename).ToLowerInvariant();
        if (stem.EndsWith("@2x", StringComparison.Ordinal))
            stem = stem[..^3];
        var animationSuffix = stem.LastIndexOf('-');
        if (animationSuffix > 0
            && int.TryParse(stem[(animationSuffix + 1)..], out _))
            stem = stem[..animationSuffix];
        return stem;
    }

    private static string ResolutionKey(string filename)
    {
        var normalized = filename.Replace('\\', '/');
        var extension = Path.GetExtension(normalized);
        var stem = normalized[..^extension.Length];
        if (stem.EndsWith("@2x", StringComparison.OrdinalIgnoreCase))
            stem = stem[..^3];
        return stem + extension;
    }
}
