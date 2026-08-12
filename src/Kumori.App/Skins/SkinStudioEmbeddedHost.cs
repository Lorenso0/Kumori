using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Kumori.App.Skins;

public sealed class SkinStudioEmbeddedHost : HwndHost
{
    private const int embedded_start_timeout_seconds = 45;

    private TaskCompletionSource<nint> hostReady =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private Process? process;
    private nint hostWindow;
    private nint studioWindow;
    private uint hostWindowThread;
    private uint studioWindowThread;
    private bool inputQueuesAttached;
    private int generation;
    private string standardError = "";

    public event EventHandler<SkinStudioProcessExitedEventArgs>? StudioExited;

    public bool IsStudioRunning =>
        process is { HasExited: false }
        && studioWindow != 0;

    public int? StudioProcessId =>
        process is { HasExited: false } running ? running.Id : null;

    public SkinStudioEmbeddedHost()
    {
        Focusable = true;
    }

    public Task StartAsync(
        string executablePath,
        string contractPath,
        CancellationToken cancellationToken = default) =>
        startAsync(executablePath, contractPath, rendererOnly: false, cancellationToken);

    public Task StartRendererAsync(
        string executablePath,
        string rendererContractPath,
        CancellationToken cancellationToken = default) =>
        startAsync(executablePath, rendererContractPath, rendererOnly: true, cancellationToken);

    private async Task startAsync(
        string executablePath,
        string contractPath,
        bool rendererOnly,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(contractPath);
        var executable = Path.GetFullPath(executablePath);
        var contract = Path.GetFullPath(contractPath);
        if (!File.Exists(executable))
            throw new FileNotFoundException("The native Skin Studio executable was not found.", executable);
        if (!File.Exists(contract))
            throw new FileNotFoundException("The Skin Studio launch contract was not found.", contract);

        await lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            await stopCoreAsync();
            var container = await hostReady.Task.WaitAsync(
                TimeSpan.FromSeconds(embedded_start_timeout_seconds),
                cancellationToken);
            var currentGeneration = ++generation;
            var session = Guid.NewGuid().ToString("N");
            var startInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetDirectoryName(executable)!,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.ArgumentList.Add(
                rendererOnly ? "--renderer-contract" : "--contract");
            startInfo.ArgumentList.Add(contract);
            startInfo.ArgumentList.Add("--embedded");
            startInfo.ArgumentList.Add("--embedded-session");
            startInfo.ArgumentList.Add(session);

            var launched = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true,
            };
            launched.Exited += processExited;
            if (!launched.Start())
                throw new InvalidOperationException("The native Skin Studio process did not start.");

            process = launched;
            var embeddedReady = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _ = monitorStandardOutputAsync(
                launched,
                session,
                currentGeneration,
                embeddedReady);
            _ = captureStandardErrorAsync(launched, currentGeneration);

            var window = await Task.Run(
                () => waitForStudioWindow(launched, cancellationToken),
                cancellationToken);
            await embeddedReady.Task.WaitAsync(
                TimeSpan.FromSeconds(embedded_start_timeout_seconds),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (currentGeneration != generation || launched.HasExited)
            {
                throw new InvalidOperationException(
                    $"The native Skin Studio exited during embedded startup with code "
                    + $"{(launched.HasExited ? launched.ExitCode : -1)}.");
            }

            await Dispatcher.InvokeAsync(
                () => attachStudioWindow(container, window),
                DispatcherPriority.Send,
                cancellationToken);
        }
        catch
        {
            await stopCoreAsync();
            throw;
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async Task StopAsync()
    {
        await lifecycleGate.WaitAsync();
        try
        {
            await stopCoreAsync();
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public void FocusStudio()
    {
        if (studioWindow == 0)
            return;
        // Re-focusing an already active native child during WM_*BUTTONDOWN can
        // interrupt its mouse capture. Only perform the cross-process focus
        // handoff when focus actually moved away from the Studio.
        if (NativeMethods.GetFocus() == studioWindow)
            return;
        if (!IsKeyboardFocusWithin)
            Keyboard.Focus(this);
        attachInputQueues();
        grantStudioForegroundPermission();
        NativeMethods.SetForegroundWindow(studioWindow);
        NativeMethods.SetActiveWindow(studioWindow);
        NativeMethods.SetFocus(studioWindow);
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        if (hostReady.Task.IsCompleted)
        {
            hostReady = new TaskCompletionSource<nint>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }
        hostWindow = NativeMethods.CreateWindowEx(
            NativeMethods.WS_EX_NOPARENTNOTIFY,
            "static",
            "",
            (uint)(NativeMethods.WS_CHILD
                   | NativeMethods.WS_VISIBLE
                   | NativeMethods.WS_CLIPCHILDREN
                   | NativeMethods.WS_CLIPSIBLINGS),
            0,
            0,
            1,
            1,
            hwndParent.Handle,
            0,
            0,
            0);
        if (hostWindow == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the Skin Studio host window.");

        hostReady.TrySetResult(hostWindow);
        return new HandleRef(this, hostWindow);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        stopCoreSynchronously();
        if (hwnd.Handle != 0)
            NativeMethods.DestroyWindow(hwnd.Handle);
        hostWindow = 0;
        hostReady = new TaskCompletionSource<nint>(
            TaskCreationOptions.RunContinuationsAsynchronously);
    }

    protected override void OnWindowPositionChanged(Rect rcBoundingBox)
    {
        base.OnWindowPositionChanged(rcBoundingBox);
        resizeStudioWindow();
    }

    protected override void OnGotKeyboardFocus(
        KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        FocusStudio();
    }

    protected override bool TabIntoCore(TraversalRequest request)
    {
        FocusStudio();
        return studioWindow != 0;
    }

    protected override nint WndProc(
        nint hwnd,
        int message,
        nint wParam,
        nint lParam,
        ref bool handled)
    {
        if (ShouldResizeForMessage(message))
            resizeStudioWindow();
        else if (message == NativeMethods.WM_SETFOCUS)
            FocusStudio();
        return base.WndProc(hwnd, message, wParam, lParam, ref handled);
    }

    private async Task captureStandardErrorAsync(Process launched, int currentGeneration)
    {
        try
        {
            var error = await launched.StandardError.ReadToEndAsync();
            if (currentGeneration == generation)
                standardError = error.Trim();
        }
        catch
        {
        }
    }

    private async Task monitorStandardOutputAsync(
        Process launched,
        string session,
        int currentGeneration,
        TaskCompletionSource embeddedReady)
    {
        try
        {
            while (await launched.StandardOutput.ReadLineAsync() is { } line)
            {
                if (IsEmbeddedReadyMessage(line, session))
                    embeddedReady.TrySetResult();
            }
            if (!embeddedReady.Task.IsCompleted
                && currentGeneration == generation)
            {
                embeddedReady.TrySetException(new InvalidOperationException(
                    "The native Studio stopped before its workbench was ready."));
            }
        }
        catch (Exception ex)
        {
            if (currentGeneration == generation)
                embeddedReady.TrySetException(ex);
        }
    }

    private static nint waitForStudioWindow(
        Process launched,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(embedded_start_timeout_seconds);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            launched.Refresh();
            if (launched.HasExited)
            {
                throw new InvalidOperationException(
                    $"The native Skin Studio exited during startup with code {launched.ExitCode}.");
            }

            var handle = NativeMethods.FindTopLevelWindow(launched.Id);
            if (handle != 0)
                return handle;
            Thread.Sleep(50);
        }

        throw new TimeoutException(
            $"The native Skin Studio did not create an embeddable window within "
            + $"{embedded_start_timeout_seconds} seconds.");
    }

    private void attachStudioWindow(nint container, nint window)
    {
        var style = EmbeddedWindowStyle(
            NativeMethods.GetWindowLongPtr(window, NativeMethods.GWL_STYLE).ToInt64());
        NativeMethods.SetWindowLongPtr(window, NativeMethods.GWL_STYLE, new nint(style));

        var extendedStyle = EmbeddedExtendedWindowStyle(
            NativeMethods.GetWindowLongPtr(window, NativeMethods.GWL_EXSTYLE).ToInt64());
        NativeMethods.SetWindowLongPtr(window, NativeMethods.GWL_EXSTYLE, new nint(extendedStyle));

        NativeMethods.SetParent(window, container);
        if (NativeMethods.GetParent(window) != container)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not embed the native Skin Studio window.");

        studioWindow = window;
        hostWindowThread = NativeMethods.GetWindowThreadProcessId(
            hostWindow,
            out _);
        studioWindowThread = NativeMethods.GetWindowThreadProcessId(
            studioWindow,
            out _);
        attachInputQueues();
        grantStudioForegroundPermission();
        resizeStudioWindow();
        NativeMethods.SetWindowPos(
            studioWindow,
            0,
            0,
            0,
            0,
            0,
            NativeMethods.SWP_NOMOVE
            | NativeMethods.SWP_NOSIZE
            | NativeMethods.SWP_NOZORDER
            | NativeMethods.SWP_FRAMECHANGED
            | NativeMethods.SWP_SHOWWINDOW);
        NativeMethods.ShowWindow(studioWindow, NativeMethods.SW_SHOW);
        FocusStudio();
    }

    private void resizeStudioWindow()
    {
        if (hostWindow == 0
            || studioWindow == 0
            || !NativeMethods.GetClientRect(hostWindow, out var bounds))
        {
            return;
        }

        NativeMethods.SetWindowPos(
            studioWindow,
            0,
            0,
            0,
            Math.Max(1, bounds.Right - bounds.Left),
            Math.Max(1, bounds.Bottom - bounds.Top),
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
    }

    private async Task stopCoreAsync()
    {
        generation++;
        var stoppingWindow = studioWindow;
        detachInputQueues();
        studioWindow = 0;
        var stopping = process;
        process = null;
        standardError = "";
        if (stopping is null)
            return;

        stopping.Exited -= processExited;
        if (!stopping.HasExited)
        {
            NativeMethods.PostMessage(
                stoppingWindow != 0
                    ? stoppingWindow
                    : NativeMethods.FindTopLevelWindow(stopping.Id),
                NativeMethods.WM_CLOSE,
                0,
                0);
            try
            {
                await stopping.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch (TimeoutException)
            {
                stopping.Kill(entireProcessTree: true);
                await stopping.WaitForExitAsync();
            }
        }
        stopping.Dispose();
    }

    private void stopCoreSynchronously()
    {
        generation++;
        var stoppingWindow = studioWindow;
        detachInputQueues();
        studioWindow = 0;
        var stopping = process;
        process = null;
        if (stopping is null)
            return;

        stopping.Exited -= processExited;
        try
        {
            if (!stopping.HasExited)
            {
                NativeMethods.PostMessage(
                    stoppingWindow != 0
                        ? stoppingWindow
                        : NativeMethods.FindTopLevelWindow(stopping.Id),
                    NativeMethods.WM_CLOSE,
                    0,
                    0);
                if (!stopping.WaitForExit(1500))
                {
                    stopping.Kill(entireProcessTree: true);
                    stopping.WaitForExit(5000);
                }
            }
        }
        catch
        {
        }
        finally
        {
            stopping.Dispose();
        }
    }

    private void processExited(object? sender, EventArgs e)
    {
        if (sender is not Process exited || !ReferenceEquals(exited, process))
            return;
        var exitCode = exited.ExitCode;
        var detail = standardError;
        detachInputQueues();
        studioWindow = 0;
        process = null;
        exited.Dispose();
        Dispatcher.BeginInvoke(() =>
            StudioExited?.Invoke(
                this,
                new SkinStudioProcessExitedEventArgs(exitCode, detail)));
    }

    private void attachInputQueues()
    {
        if (inputQueuesAttached
            || hostWindowThread == 0
            || studioWindowThread == 0
            || hostWindowThread == studioWindowThread)
        {
            return;
        }
        inputQueuesAttached = NativeMethods.AttachThreadInput(
            hostWindowThread,
            studioWindowThread,
            attach: true);
    }

    private void detachInputQueues()
    {
        if (inputQueuesAttached)
        {
            NativeMethods.AttachThreadInput(
                hostWindowThread,
                studioWindowThread,
                attach: false);
        }
        inputQueuesAttached = false;
        hostWindowThread = 0;
        studioWindowThread = 0;
    }

    private void grantStudioForegroundPermission()
    {
        if (process is { HasExited: false } running)
        {
            NativeMethods.AllowSetForegroundWindow(
                unchecked((uint)running.Id));
        }
    }

    internal static long EmbeddedWindowStyle(long style) =>
        (style & ~(NativeMethods.WS_POPUP
                   | NativeMethods.WS_CAPTION
                   | NativeMethods.WS_THICKFRAME
                   | NativeMethods.WS_MINIMIZEBOX
                   | NativeMethods.WS_MAXIMIZEBOX
                   | NativeMethods.WS_SYSMENU))
        | NativeMethods.WS_CHILD
        | NativeMethods.WS_VISIBLE
        | NativeMethods.WS_CLIPCHILDREN
        | NativeMethods.WS_CLIPSIBLINGS;

    internal static long EmbeddedExtendedWindowStyle(long style) =>
        (style & ~(NativeMethods.WS_EX_APPWINDOW
                   | NativeMethods.WS_EX_NOPARENTNOTIFY))
        | NativeMethods.WS_EX_TOOLWINDOW;

    internal static bool ShouldResizeForMessage(int message) =>
        message is NativeMethods.WM_SIZE or NativeMethods.WM_DPICHANGED;

    internal static (int Width, int Height) PixelClientSize(
        double logicalWidth,
        double logicalHeight,
        uint dpi)
    {
        if (!double.IsFinite(logicalWidth) || logicalWidth < 0)
            throw new ArgumentOutOfRangeException(nameof(logicalWidth));
        if (!double.IsFinite(logicalHeight) || logicalHeight < 0)
            throw new ArgumentOutOfRangeException(nameof(logicalHeight));
        if (dpi is < 48 or > 768)
            throw new ArgumentOutOfRangeException(nameof(dpi));
        return (
            Math.Max(
                1,
                checked((int)Math.Round(
                    logicalWidth * dpi / 96d,
                    MidpointRounding.AwayFromZero))),
            Math.Max(
                1,
                checked((int)Math.Round(
                    logicalHeight * dpi / 96d,
                    MidpointRounding.AwayFromZero))));
    }

    internal static bool ScreenPointIsInside(
        NativeMethods.Rect bounds,
        NativeMethods.Point point) =>
        point.X >= bounds.Left
        && point.X < bounds.Right
        && point.Y >= bounds.Top
        && point.Y < bounds.Bottom;

    internal static int EmbeddedWindowCandidateScore(
        string title,
        string className,
        int clientWidth,
        int clientHeight,
        bool hasOwner)
    {
        if (clientWidth < 64 || clientHeight < 64)
            return 0;
        var score = 10;
        if (!hasOwner)
            score += 20;
        if (title.Equals("Kumori Skin Studio", StringComparison.OrdinalIgnoreCase))
            score += 200;
        else if (title.Contains("Skin Studio", StringComparison.OrdinalIgnoreCase))
            score += 80;
        if (className.Contains("SDL", StringComparison.OrdinalIgnoreCase))
            score += 100;
        return score;
    }

    internal static bool IsEmbeddedReadyMessage(
        string? message,
        string expectedSession)
    {
        if (string.IsNullOrWhiteSpace(message)
            || string.IsNullOrWhiteSpace(expectedSession))
        {
            return false;
        }
        try
        {
            using var document = JsonDocument.Parse(message);
            var root = document.RootElement;
            return root.TryGetProperty("status", out var status)
                   && status.ValueKind == JsonValueKind.String
                   && status.GetString() == "embedded_ready"
                   && root.TryGetProperty("session", out var session)
                   && session.ValueKind == JsonValueKind.String
                   && session.GetString() == expectedSession;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static class NativeMethods
    {
        internal const int GWL_STYLE = -16;
        internal const int GWL_EXSTYLE = -20;
        internal const int WM_CLOSE = 0x0010;
        internal const int WM_SIZE = 0x0005;
        internal const int WM_SETFOCUS = 0x0007;
        internal const int WM_DPICHANGED = 0x02E0;
        internal const int SW_SHOW = 5;
        internal const uint GW_OWNER = 4;

        internal const long WS_CHILD = 0x40000000L;
        internal const long WS_VISIBLE = 0x10000000L;
        internal const long WS_CLIPCHILDREN = 0x02000000L;
        internal const long WS_CLIPSIBLINGS = 0x04000000L;
        internal const long WS_POPUP = 0x80000000L;
        internal const long WS_CAPTION = 0x00C00000L;
        internal const long WS_THICKFRAME = 0x00040000L;
        internal const long WS_MINIMIZEBOX = 0x00020000L;
        internal const long WS_MAXIMIZEBOX = 0x00010000L;
        internal const long WS_SYSMENU = 0x00080000L;
        internal const int WS_EX_NOPARENTNOTIFY = 0x00000004;
        internal const int WS_EX_TOOLWINDOW = 0x00000080;
        internal const int WS_EX_APPWINDOW = 0x00040000;

        internal const uint SWP_NOSIZE = 0x0001;
        internal const uint SWP_NOMOVE = 0x0002;
        internal const uint SWP_NOZORDER = 0x0004;
        internal const uint SWP_NOACTIVATE = 0x0010;
        internal const uint SWP_FRAMECHANGED = 0x0020;
        internal const uint SWP_SHOWWINDOW = 0x0040;

        internal delegate bool EnumWindowsCallback(nint window, nint parameter);

        [StructLayout(LayoutKind.Sequential)]
        internal struct Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Point
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern nint CreateWindowEx(
            int extendedStyle,
            string className,
            string windowName,
            uint style,
            int x,
            int y,
            int width,
            int height,
            nint parent,
            nint menu,
            nint instance,
            nint parameter);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DestroyWindow(nint window);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern nint SetParent(nint child, nint newParent);

        [DllImport("user32.dll")]
        internal static extern nint GetParent(nint window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetClientRect(nint window, out Rect bounds);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindowVisible(nint window);

        internal static nint GetFocus() => GetFocusWindow();

        [DllImport("user32.dll", EntryPoint = "GetFocus")]
        private static extern nint GetFocusWindow();

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetWindowPos(
            nint window,
            nint insertAfter,
            int x,
            int y,
            int width,
            int height,
            uint flags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ShowWindow(nint window, int command);

        [DllImport("user32.dll")]
        internal static extern nint SetFocus(nint window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetForegroundWindow(nint window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AllowSetForegroundWindow(
            uint processId);

        [DllImport("user32.dll")]
        internal static extern nint SetActiveWindow(nint window);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PostMessage(
            nint window,
            uint message,
            nint wParam,
            nint lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumWindows(EnumWindowsCallback callback, nint parameter);

        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(nint window, out uint processId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AttachThreadInput(
            uint sourceThread,
            uint targetThread,
            [MarshalAs(UnmanagedType.Bool)] bool attach);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextLength(nint window);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(
            nint window,
            StringBuilder text,
            int maximumCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(
            nint window,
            StringBuilder className,
            int maximumCount);

        [DllImport("user32.dll")]
        private static extern nint GetWindow(nint window, uint command);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern nint GetWindowLongPtr64(nint window, int index);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        private static extern int GetWindowLong32(nint window, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        private static extern nint SetWindowLongPtr64(nint window, int index, nint value);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
        private static extern int SetWindowLong32(nint window, int index, int value);

        internal static nint GetWindowLongPtr(nint window, int index) =>
            nint.Size == 8
                ? GetWindowLongPtr64(window, index)
                : new nint(GetWindowLong32(window, index));

        internal static nint SetWindowLongPtr(nint window, int index, nint value) =>
            nint.Size == 8
                ? SetWindowLongPtr64(window, index, value)
                : new nint(SetWindowLong32(window, index, value.ToInt32()));

        internal static nint FindTopLevelWindow(int targetProcessId)
        {
            nint found = 0;
            var bestScore = 0;
            EnumWindows((window, _) =>
            {
                GetWindowThreadProcessId(window, out var processId);
                if (processId != (uint)targetProcessId)
                    return true;

                if (!GetClientRect(window, out var bounds))
                    return true;
                var titleLength = Math.Max(0, GetWindowTextLength(window));
                var title = new StringBuilder(titleLength + 1);
                GetWindowText(window, title, title.Capacity);
                var className = new StringBuilder(256);
                GetClassName(window, className, className.Capacity);
                var score = EmbeddedWindowCandidateScore(
                    title.ToString(),
                    className.ToString(),
                    bounds.Right - bounds.Left,
                    bounds.Bottom - bounds.Top,
                    GetWindow(window, GW_OWNER) != 0);
                if (score > bestScore)
                {
                    bestScore = score;
                    found = window;
                }
                return true;
            }, 0);
            return found;
        }
    }
}

public sealed record SkinStudioProcessExitedEventArgs(
    int ExitCode,
    string StandardError);
