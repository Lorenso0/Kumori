using System.Text;
using System.Runtime.InteropServices;

namespace Kumori.SkinStudio;

internal sealed class EmbeddedWindowActivationMonitor : IDisposable
{
    private nint window;
    private nint originalWindowProcedure;
    private NativeMethods.WindowProcedure? windowProcedure;

    public EmbeddedWindowActivationMonitor(nint window = default)
    {
        this.window = window;
    }

    public bool HasKeyboardFocus
    {
        get
        {
            if (!OperatingSystem.IsWindows() || window == 0)
                return false;
            var focused = NativeMethods.GetFocusedWindow(window);
            return FocusBelongsToStudio(
                window,
                focused,
                focused != 0 && NativeMethods.IsChild(window, focused));
        }
    }

    public void Poll()
    {
        if (!OperatingSystem.IsWindows())
            return;

        if (window == 0)
        {
            window = NativeMethods.FindStudioWindow(
                NativeMethods.GetCurrentProcessId());
            if (window == 0)
                return;
        }

        ensureWindowSubclass();
    }

    public void Dispose()
    {
        if (window != 0
            && originalWindowProcedure != 0)
        {
            NativeMethods.SetWindowProcedure(
                window,
                originalWindowProcedure);
        }
        originalWindowProcedure = 0;
        windowProcedure = null;
    }

    private void ensureWindowSubclass()
    {
        if (windowProcedure is not null)
            return;

        windowProcedure = handleWindowMessage;
        originalWindowProcedure = NativeMethods.SetWindowProcedure(
            window,
            Marshal.GetFunctionPointerForDelegate(windowProcedure));
        if (originalWindowProcedure == 0)
            windowProcedure = null;
    }

    private nint handleWindowMessage(
        nint handle,
        uint message,
        nint wParam,
        nint lParam)
    {
        // WM_MOUSEACTIVATE is sent before the corresponding button-down. This
        // is the only safe point to hand focus to the re-parented SDL child;
        // doing it from WM_*BUTTONDOWN interrupts mouse capture and can leave
        // osu!'s cursor permanently pressed.
        if (IsPreButtonActivationMessage(message))
        {
            NativeMethods.SetForegroundWindow(handle);
            NativeMethods.SetFocus(handle);
        }
        return NativeMethods.CallWindowProcedure(
            originalWindowProcedure,
            handle,
            message,
            wParam,
            lParam);
    }

    internal static int WindowCandidateScore(
        string className,
        int width,
        int height,
        bool visible)
    {
        if (!visible || width < 64 || height < 64)
            return 0;
        var score = Math.Min(width * height / 1000, 100_000);
        if (className.Contains("SDL", StringComparison.OrdinalIgnoreCase))
            score += 1_000_000;
        return score;
    }

    internal static bool IsPreButtonActivationMessage(uint message) =>
        message == NativeMethods.WM_MOUSEACTIVATE;

    internal static bool FocusBelongsToStudio(
        nint studioWindow,
        nint focusedWindow,
        bool focusedWindowIsChild) =>
        studioWindow != 0
        && focusedWindow != 0
        && (studioWindow == focusedWindow || focusedWindowIsChild);

    internal static class NativeMethods
    {
        internal const uint WM_MOUSEACTIVATE = 0x0021;
        private const int gwlp_wndproc = -4;
        internal delegate bool EnumWindowsCallback(
            nint window,
            nint parameter);
        internal delegate nint WindowProcedure(
            nint window,
            uint message,
            nint wParam,
            nint lParam);

        [StructLayout(LayoutKind.Sequential)]
        internal struct Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct GuiThreadInfo
        {
            public int Size;
            public uint Flags;
            public nint Active;
            public nint Focus;
            public nint Capture;
            public nint MenuOwner;
            public nint MoveSize;
            public nint Caret;
            public Rect CaretRect;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindowVisible(nint window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetForegroundWindow(nint window);

        [DllImport("user32.dll")]
        internal static extern nint SetFocus(nint window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsChild(
            nint parent,
            nint candidate);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetGUIThreadInfo(
            uint threadId,
            ref GuiThreadInfo information);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern nint CallWindowProc(
            nint previousProcedure,
            nint window,
            uint message,
            nint wParam,
            nint lParam);

        [DllImport(
            "user32.dll",
            EntryPoint = "SetWindowLongPtrW",
            SetLastError = true)]
        private static extern nint SetWindowLongPtr64(
            nint window,
            int index,
            nint value);

        [DllImport(
            "user32.dll",
            EntryPoint = "SetWindowLongW",
            SetLastError = true)]
        private static extern int SetWindowLong32(
            nint window,
            int index,
            int value);

        [DllImport("kernel32.dll")]
        internal static extern uint GetCurrentProcessId();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumWindows(
            EnumWindowsCallback callback,
            nint parameter);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumChildWindows(
            nint parent,
            EnumWindowsCallback callback,
            nint parameter);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(
            nint window,
            out uint processId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetClientRect(
            nint window,
            out Rect bounds);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(
            nint window,
            StringBuilder className,
            int maximumCount);

        internal static nint SetWindowProcedure(
            nint window,
            nint procedure) =>
            nint.Size == 8
                ? SetWindowLongPtr64(
                    window,
                    gwlp_wndproc,
                    procedure)
                : new nint(SetWindowLong32(
                    window,
                    gwlp_wndproc,
                    procedure.ToInt32()));

        internal static nint CallWindowProcedure(
            nint previousProcedure,
            nint window,
            uint message,
            nint wParam,
            nint lParam) =>
            CallWindowProc(
                previousProcedure,
                window,
                message,
                wParam,
                lParam);

        internal static nint GetFocusedWindow(nint studioWindow)
        {
            var threadId = GetWindowThreadProcessId(
                studioWindow,
                out _);
            if (threadId == 0)
                return 0;
            var information = new GuiThreadInfo
            {
                Size = Marshal.SizeOf<GuiThreadInfo>(),
            };
            return GetGUIThreadInfo(threadId, ref information)
                ? information.Focus
                : 0;
        }

        internal static nint FindStudioWindow(uint targetProcessId)
        {
            nint best = 0;
            var bestScore = 0;
            void inspect(nint candidate)
            {
                GetWindowThreadProcessId(candidate, out var processId);
                if (processId != targetProcessId
                    || !GetClientRect(candidate, out var bounds))
                {
                    return;
                }

                var className = new StringBuilder(256);
                GetClassName(
                    candidate,
                    className,
                    className.Capacity);
                var score = WindowCandidateScore(
                    className.ToString(),
                    bounds.Right - bounds.Left,
                    bounds.Bottom - bounds.Top,
                    IsWindowVisible(candidate));
                if (score > bestScore)
                {
                    best = candidate;
                    bestScore = score;
                }
            }

            EnumWindows((topLevel, _) =>
            {
                inspect(topLevel);
                EnumChildWindows(
                    topLevel,
                    (child, _) =>
                    {
                        inspect(child);
                        return true;
                    },
                    0);
                return true;
            }, 0);
            return best;
        }
    }
}
