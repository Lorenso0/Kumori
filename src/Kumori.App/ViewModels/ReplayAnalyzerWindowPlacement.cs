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

    public static async Task<bool> CenterNearOwnerAsync(
        Process process,
        Window owner,
        CancellationToken cancellationToken = default,
        bool activate = false)
    {
        var target = TargetBounds(owner);
        try
        {
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
                    uint flags = SwpNoZOrder | SwpShowWindow;
                    if (!activate)
                        flags |= SwpNoActivate;
                    var positioned = SetWindowPos(
                        handle,
                        IntPtr.Zero,
                        target.X,
                        target.Y,
                        target.Width,
                        target.Height,
                        flags);
                    if (!positioned)
                    {
                        Log.Debug("Could not position Replay Analyzer window. Win32 error {Error}", Marshal.GetLastWin32Error());
                    }
                    if (!activate)
                        return positioned;

                    _ = ShowWindow(handle, SwRestore);
                    bool focused = SetForegroundWindow(handle);
                    if (!focused)
                        Log.Debug("Windows did not grant foreground focus to Replay Analyzer");
                    return positioned || focused;
                }

                await Task.Delay(100, cancellationToken);
            }
        }
        catch (Exception exception) when (exception is ObjectDisposedException or InvalidOperationException)
        {
            // Output capture owns and disposes the process after it exits.
            // Process.HasExited and MainWindowHandle can throw either exception
            // once the native process association has been released.
            return false;
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

    private const int SwRestore = 9;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

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
