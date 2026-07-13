using System.IO;
using System.Text.Json;
using System.Windows;

namespace Kumori.App;

internal sealed record ChangelogRelease
{
    public string Version { get; init; } = string.Empty;
    public string Date { get; init; } = string.Empty;
    public IReadOnlyList<string> Major { get; init; } = [];
    public IReadOnlyList<string> Features { get; init; } = [];
    public IReadOnlyList<string> Improvements { get; init; } = [];
    public IReadOnlyList<string> Fixes { get; init; } = [];
}

internal static class ChangelogService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static IReadOnlyList<ChangelogRelease> LoadBundled()
    {
        var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/KumoriChangelog.json"))
            ?? throw new FileNotFoundException("The bundled Kumori changelog could not be found.");
        using (resource.Stream)
        using (var reader = new StreamReader(resource.Stream))
        {
            return Parse(reader.ReadToEnd());
        }
    }

    internal static IReadOnlyList<ChangelogRelease> Parse(string json)
    {
        var releases = JsonSerializer.Deserialize<List<ChangelogRelease>>(json, JsonOptions)
            ?? throw new InvalidDataException("The Kumori changelog is empty.");
        if (releases.Any(release => string.IsNullOrWhiteSpace(release.Version)))
            throw new InvalidDataException("Every changelog entry must have a version.");
        return releases;
    }
}
