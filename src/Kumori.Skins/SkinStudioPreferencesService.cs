using System.Text.Json;

namespace Kumori.Skins;

public sealed record SkinStudioPreferences(
    int FormatVersion = SkinStudioPreferencesService.CurrentFormatVersion,
    bool AutomaticEditBackups = true,
    int BackupRetention = 30);

public sealed class SkinStudioPreferencesService
{
    public const int CurrentFormatVersion = 1;
    public const int MinimumRetention = 1;
    public const int MaximumRetention = 200;

    private readonly string path;

    public SkinStudioPreferencesService(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        var root = Path.GetFullPath(workspaceRoot);
        path = Path.GetFullPath(Path.Combine(root, "studio-preferences.json"));
        var prefix =
            Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Skin Studio preferences escaped the isolated workspace.");
        }
    }

    public SkinStudioPreferences Load()
    {
        if (!File.Exists(path))
            return new SkinStudioPreferences();
        var preferences = JsonSerializer.Deserialize<SkinStudioPreferences>(
                              File.ReadAllText(path),
                              SkinStudioLaunchContract.JsonOptions)
                          ?? throw new InvalidDataException(
                              "Skin Studio preferences are empty.");
        return Validate(preferences);
    }

    public SkinStudioPreferences Save(SkinStudioPreferences preferences)
    {
        preferences = Validate(preferences);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".new";
        try
        {
            File.WriteAllText(
                temporary,
                JsonSerializer.Serialize(
                    preferences,
                    SkinStudioLaunchContract.JsonOptions));
            using (var stream = new FileStream(
                       temporary,
                       FileMode.Open,
                       FileAccess.ReadWrite,
                       FileShare.None))
            {
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
            return preferences;
        }
        finally
        {
            try
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
            catch
            {
            }
        }
    }

    public static SkinStudioPreferences Validate(
        SkinStudioPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        if (preferences.FormatVersion != CurrentFormatVersion)
        {
            throw new InvalidDataException(
                $"Unsupported Skin Studio preferences version "
                + $"{preferences.FormatVersion}.");
        }
        if (preferences.BackupRetention is < MinimumRetention
            or > MaximumRetention)
        {
            throw new InvalidDataException(
                $"Backup retention must be between {MinimumRetention} and "
                + $"{MaximumRetention}.");
        }
        return preferences;
    }
}
