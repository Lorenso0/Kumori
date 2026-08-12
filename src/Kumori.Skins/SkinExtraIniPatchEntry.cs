namespace Kumori.Skins;

public sealed record SkinExtraIniPatchEntry(
    string Section,
    string Key,
    string? Value,
    int? ManiaKeys = null);
