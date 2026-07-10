using System.Runtime.InteropServices;

namespace Kumori.Native;

/// <summary>
/// Applies the dark (immersive) title bar to a window. Must be called with
/// the window handle before/at first render — for WPF, from OnSourceInitialized.
/// </summary>
public static class DarkTitleBar
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_LEGACY = 19; // pre-20H1 builds
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_TEXT_COLOR = 36;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd, int attribute, ref int value, int size);

    public static void Apply(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }
        int on = 1;
        if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int)) != 0)
        {
            _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_LEGACY, ref on, sizeof(int));
        }

        int black = 0x000000;
        int white = 0xFFFFFF;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref black, sizeof(int));
        _ = DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref white, sizeof(int));
    }
}
