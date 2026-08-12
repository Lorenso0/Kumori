namespace Kumori.Tracking;

internal static class LazerSkinStorageRoot
{
    public static string? Find()
    {
        var defaultRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "osu");
        var configured = readConfiguredRoot(defaultRoot);
        if (isStore(configured))
            return Path.GetFullPath(configured!);
        return isStore(defaultRoot) ? Path.GetFullPath(defaultRoot) : null;
    }

    private static bool isStore(string? root) =>
        !string.IsNullOrWhiteSpace(root)
        && File.Exists(Path.Combine(root, "client.realm"))
        && Directory.Exists(Path.Combine(root, "files"));

    private static string? readConfiguredRoot(string defaultRoot)
    {
        try
        {
            var storageIni = Path.Combine(defaultRoot, "storage.ini");
            if (!File.Exists(storageIni))
                return null;
            var line = File.ReadLines(storageIni).FirstOrDefault(
                value => value.StartsWith(
                    "FullPath",
                    StringComparison.OrdinalIgnoreCase));
            var separator = line?.IndexOf('=') ?? -1;
            if (separator < 0)
                return null;
            var path = line![(separator + 1)..].Trim();
            return Path.IsPathFullyQualified(path) ? path : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
