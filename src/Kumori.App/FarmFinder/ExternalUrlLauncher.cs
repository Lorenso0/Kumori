using System.Diagnostics;
using System.Windows;
using Kumori.FarmFinder;

namespace Kumori.App.FarmFinder;

public sealed class ExternalUrlLauncher : IExternalUrlLauncher
{
    public void Open(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            throw new ArgumentException("Only HTTP and HTTPS links can be opened.", nameof(url));
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    public void Copy(string text) => Clipboard.SetText(text);
}
