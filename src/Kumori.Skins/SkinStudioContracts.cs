using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kumori.Skins;

public sealed record SkinStudioLaunchContract
{
    public const int CurrentVersion = 1;

    [JsonPropertyName("contract_version")]
    public int ContractVersion { get; init; } = CurrentVersion;

    [JsonPropertyName("workspace_path")]
    public required string WorkspacePath { get; init; }

    [JsonPropertyName("draft_id")]
    public Guid? DraftId { get; init; }

    [JsonPropertyName("source_skin_path")]
    public string? SourceSkinPath { get; init; }

    [JsonPropertyName("player_root")]
    public string? PlayerRoot { get; init; }

    [JsonPropertyName("theme_id")]
    public string ThemeId { get; init; } = "dark";

    [JsonPropertyName("custom_theme")]
    public IReadOnlyDictionary<string, string> CustomTheme { get; init; } =
        new Dictionary<string, string>();

    [JsonPropertyName("live_sync_enabled")]
    public bool LiveSyncEnabled { get; init; }

    [JsonPropertyName("reload_pipe_name")]
    public string? ReloadPipeName { get; init; }

    public SkinStudioLaunchContract Normalize()
    {
        if (ContractVersion != CurrentVersion)
            throw new InvalidDataException(
                $"Unsupported Skin Studio contract version {ContractVersion}; expected {CurrentVersion}.");
        if (string.IsNullOrWhiteSpace(WorkspacePath))
            throw new InvalidDataException("Skin Studio workspace path is required.");

        var workspace = Path.GetFullPath(WorkspacePath);
        var playerRoot = string.IsNullOrWhiteSpace(PlayerRoot)
            ? null
            : Path.GetFullPath(PlayerRoot);
        var reloadPipe = string.IsNullOrWhiteSpace(ReloadPipeName)
            ? null
            : ReloadPipeName.Trim();
        if (reloadPipe is not null
            && (reloadPipe.Length > 128
                || !reloadPipe.StartsWith("kumori-skin-reload-", StringComparison.Ordinal)
                || reloadPipe.Any(character =>
                    !char.IsAsciiLetterOrDigit(character) && character != '-')))
        {
            throw new InvalidDataException(
                "The Skin Studio reload pipe name is invalid.");
        }
        if (playerRoot is not null && PathsOverlap(workspace, playerRoot))
        {
            throw new InvalidDataException(
                "The isolated Skin Studio workspace must not overlap the player's osu!lazer root.");
        }

        return this with
        {
            WorkspacePath = workspace,
            SourceSkinPath = string.IsNullOrWhiteSpace(SourceSkinPath)
                ? null
                : Path.GetFullPath(SourceSkinPath),
            PlayerRoot = playerRoot,
            ReloadPipeName = reloadPipe,
            ThemeId = string.IsNullOrWhiteSpace(ThemeId) ? "dark" : ThemeId.Trim(),
            CustomTheme = new Dictionary<string, string>(
                CustomTheme,
                StringComparer.OrdinalIgnoreCase),
        };
    }

    public static SkinStudioLaunchContract Load(string path)
    {
        using var stream = File.OpenRead(path);
        var contract = JsonSerializer.Deserialize<SkinStudioLaunchContract>(stream, JsonOptions)
            ?? throw new InvalidDataException("Skin Studio launch contract was empty.");
        return contract.Normalize();
    }

    public void Save(string path)
    {
        var normalized = Normalize();
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporary = fullPath + ".new";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(normalized, JsonOptions));
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };

    private static bool PathsOverlap(string first, string second)
    {
        var firstWithSeparator = Path.TrimEndingDirectorySeparator(first)
                                 + Path.DirectorySeparatorChar;
        var secondWithSeparator = Path.TrimEndingDirectorySeparator(second)
                                  + Path.DirectorySeparatorChar;
        return firstWithSeparator.StartsWith(
                   secondWithSeparator,
                   StringComparison.OrdinalIgnoreCase)
               || secondWithSeparator.StartsWith(
                   firstWithSeparator,
                   StringComparison.OrdinalIgnoreCase);
    }
}

public static class SkinStudioPaths
{
    public static string DefaultWorkspace =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Kumori",
            "skins",
            "studio");

    public static string ContractsDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Kumori",
            "runtime",
            "skin-studio-contracts");
}

public static class SkinStudioWriteBoundary
{
    public static void AssertNormalRootsAreIsolated(
        string? playerRoot,
        params string[] normalWriteRoots)
    {
        if (string.IsNullOrWhiteSpace(playerRoot))
            return;
        ArgumentNullException.ThrowIfNull(normalWriteRoots);
        var player = Path.GetFullPath(playerRoot);
        foreach (var root in normalWriteRoots)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(root);
            var writeRoot = Path.GetFullPath(root);
            if (Overlaps(player, writeRoot))
            {
                throw new InvalidDataException(
                    $"Normal Skin Studio storage '{writeRoot}' must not overlap "
                    + $"the detected osu!lazer root '{player}'.");
            }
        }
    }

    public static bool IsNormalWriteAllowed(
        string? playerRoot,
        string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return string.IsNullOrWhiteSpace(playerRoot)
               || !Overlaps(
                   Path.GetFullPath(playerRoot),
                   Path.GetFullPath(path));
    }

    private static bool Overlaps(string first, string second)
    {
        var firstWithSeparator = Path.TrimEndingDirectorySeparator(first)
                                 + Path.DirectorySeparatorChar;
        var secondWithSeparator = Path.TrimEndingDirectorySeparator(second)
                                  + Path.DirectorySeparatorChar;
        return firstWithSeparator.StartsWith(
                   secondWithSeparator,
                   StringComparison.OrdinalIgnoreCase)
               || secondWithSeparator.StartsWith(
                   firstWithSeparator,
                   StringComparison.OrdinalIgnoreCase);
    }
}
