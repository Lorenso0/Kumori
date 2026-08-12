using System.Diagnostics;
using System.Windows;
using Kumori.FarmFinder;

namespace Kumori.App.FarmFinder;

public sealed class ExternalUrlLauncher : IExternalUrlLauncher
{
    public void Open(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (!IsWebLink(uri) && !IsOsuBeatmapLink(uri)))
            throw new ArgumentException(
                "Only web links and osu! beatmap links can be opened.",
                nameof(url));
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    public void Copy(string text) => Clipboard.SetText(text);

    private static bool IsWebLink(Uri uri) =>
        uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
        uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);

    private static bool IsOsuBeatmapLink(Uri uri) =>
        uri.Scheme.Equals("osu", StringComparison.OrdinalIgnoreCase) &&
        uri.Host.Equals("b", StringComparison.OrdinalIgnoreCase) &&
        string.IsNullOrEmpty(uri.Query) &&
        string.IsNullOrEmpty(uri.Fragment) &&
        long.TryParse(uri.AbsolutePath.Trim('/'), out var beatmapId) &&
        beatmapId > 0;
}
