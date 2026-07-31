using System.Security.Cryptography;
using Kumori.Core;
using Kumori.Storage;

try
{
    var options = ParseArguments(args);
    var progress = new ConsoleProgress();
    var publisher = new FarmFinderCachePublisher();
    Console.WriteLine($"Source: {options.SourceDatabase}");
    var result = publisher.Publish(
        new FarmFinderCachePublishOptions(
            options.SourceDatabase,
            options.OutputRoot,
            options.BaseUri,
            options.MinimumAppVersion),
        progress);

    if (options.DeployDirectory is not null)
        Deploy(result, options.DeployDirectory);

    Console.WriteLine();
    Console.WriteLine("Ready:");
    Console.WriteLine($"  Database: {result.DatabasePath}");
    Console.WriteLine($"  Manifest: {result.ManifestPath}");
    Console.WriteLine($"  SHA-256:  {result.Sha256}");
    if (options.DeployDirectory is null)
        Console.WriteLine("Upload the database first, then manifest.json.");
    else
        Console.WriteLine($"Published to: {options.DeployDirectory}");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Farm Finder cache was not published: {exception.Message}");
    return 1;
}

static PublisherArguments ParseArguments(string[] args)
{
    var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var index = 0; index < args.Length; index += 2)
    {
        if (index + 1 >= args.Length ||
            !args[index].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException(
                "Arguments must be supplied as --name value pairs.");
        values[args[index][2..]] = args[index + 1];
    }

    if (!values.TryGetValue("base-url", out var baseUrl) ||
        !Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
        throw new ArgumentException(
            "Provide --base-url https://your-server.example/farm-finder/.");

    var desktop = Environment.GetFolderPath(
        Environment.SpecialFolder.DesktopDirectory);
    var sourceDatabase = values.TryGetValue("source", out var configuredSource)
        ? Path.GetFullPath(configuredSource)
        : FindBestDefaultSource();
    return new PublisherArguments(
        baseUri,
        sourceDatabase,
        Path.GetFullPath(values.GetValueOrDefault(
            "output-root",
            Path.Combine(desktop, "farm-finder-publish"))),
        values.GetValueOrDefault("minimum-app-version"),
        values.TryGetValue("deploy-directory", out var deployDirectory)
            ? Path.GetFullPath(deployDirectory)
            : null);
}

static string FindBestDefaultSource()
{
    var candidates = new[]
        {
            AppPaths.FarmFinderDatabase,
            AppPaths.FarmFinderDatabase + ".old",
            AppPaths.FarmFinderDatabase + ".previous",
        }
        .Where(File.Exists)
        .Select(path => new FileInfo(path))
        .OrderByDescending(file => file.Length)
        .ThenByDescending(file => file.LastWriteTimeUtc)
        .ToArray();
    return candidates.Length == 0
        ? AppPaths.FarmFinderDatabase
        : candidates[0].FullName;
}

static void Deploy(
    FarmFinderCachePublishResult result,
    string deployDirectory)
{
    Directory.CreateDirectory(deployDirectory);
    var databaseDestination = Path.Combine(
        deployDirectory,
        Path.GetFileName(result.DatabasePath));
    if (File.Exists(databaseDestination))
    {
        using var existing = File.OpenRead(databaseDestination);
        var existingHash =
            Convert.ToHexStringLower(SHA256.HashData(existing));
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(existingHash),
                Convert.FromHexString(result.Sha256)))
            throw new IOException(
                $"A different file already exists at {databaseDestination}.");
    }
    else
    {
        CopyAtomically(
            result.DatabasePath,
            databaseDestination,
            overwrite: false);
    }

    // The manifest is deliberately deployed last; this is the release switch.
    var manifestDestination = Path.Combine(deployDirectory, "manifest.json");
    CopyAtomically(result.ManifestPath, manifestDestination, overwrite: true);
}

static void CopyAtomically(string source, string destination, bool overwrite)
{
    var staging = destination + $".uploading-{Guid.NewGuid():N}";
    try
    {
        File.Copy(source, staging);
        File.Move(staging, destination, overwrite);
    }
    finally
    {
        if (File.Exists(staging))
            File.Delete(staging);
    }
}

internal sealed record PublisherArguments(
    Uri BaseUri,
    string SourceDatabase,
    string OutputRoot,
    string? MinimumAppVersion,
    string? DeployDirectory);

internal sealed class ConsoleProgress : IProgress<string>
{
    public void Report(string value) => Console.WriteLine(value);
}
