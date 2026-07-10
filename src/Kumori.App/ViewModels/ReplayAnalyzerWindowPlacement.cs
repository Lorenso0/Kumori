using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Serilog;

namespace Kumori.App.ViewModels;

internal static class ReplayAnalyzerWindowPlacement
{
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;

    public static async Task<bool> CenterNearOwnerAsync(Process process, Window owner, CancellationToken cancellationToken = default)
    {
        var target = TargetBounds(owner);
        for (var attempt = 0; attempt < 90; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                return false;
            }

            process.Refresh();
            var handle = process.MainWindowHandle;
            if (handle != IntPtr.Zero)
            {
                var ok = SetWindowPos(
                    handle,
                    IntPtr.Zero,
                    target.X,
                    target.Y,
                    target.Width,
                    target.Height,
                    SwpNoZOrder | SwpNoActivate | SwpShowWindow);
                if (!ok)
                {
                    Log.Debug("Could not position Replay Analyzer window. Win32 error {Error}", Marshal.GetLastWin32Error());
                }
                return ok;
            }

            await Task.Delay(100, cancellationToken);
        }

        return false;
    }

    private static TargetRect TargetBounds(Window owner)
    {
        var ownerHandle = new WindowInteropHelper(owner).Handle;
        if (ownerHandle != IntPtr.Zero && GetWindowRect(ownerHandle, out var rect))
        {
            var ownerWidth = Math.Max(1, rect.Right - rect.Left);
            var ownerHeight = Math.Max(1, rect.Bottom - rect.Top);
            return InsetOwnerBounds(rect.Left, rect.Top, ownerWidth, ownerHeight);
        }

        var scale = PresentationSource.FromVisual(owner)?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
        var left = (int)Math.Round(owner.Left * scale.M11);
        var top = (int)Math.Round(owner.Top * scale.M22);
        var width = (int)Math.Round(Math.Max(owner.ActualWidth, owner.Width) * scale.M11);
        var height = (int)Math.Round(Math.Max(owner.ActualHeight, owner.Height) * scale.M22);
        return InsetOwnerBounds(left, top, width, height);
    }

    private static TargetRect InsetOwnerBounds(int ownerLeft, int ownerTop, int ownerWidth, int ownerHeight)
    {
        var insetX = (int)Math.Clamp(ownerWidth * 0.045, 48, 96);
        var insetY = (int)Math.Clamp(ownerHeight * 0.055, 42, 86);
        var width = Math.Max(760, ownerWidth - insetX * 2);
        var height = Math.Max(540, ownerHeight - insetY * 2);

        width = Math.Min(width, ownerWidth);
        height = Math.Min(height, ownerHeight);

        return new TargetRect(
            ownerLeft + (ownerWidth - width) / 2,
            ownerTop + (ownerHeight - height) / 2,
            width,
            height);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeRect
    {
        public readonly int Left;
        public readonly int Top;
        public readonly int Right;
        public readonly int Bottom;
    }

    private readonly record struct TargetRect(int X, int Y, int Width, int Height);
}
