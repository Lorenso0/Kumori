using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Kumori.Native;
using Kumori.Tracking;
using osu.Framework.Input;
using osu.Framework.Input.Bindings;
using osu.Game.Input.Bindings;
using Serilog;

namespace Kumori.App;

internal enum LazerSkinReloadStatus
{
    Reloaded,
    NotActive,
    LazerNotRunning,
    WaitingForForeground,
    ManualReloadRequired,
}

internal sealed record LazerSkinReloadResult(
    LazerSkinReloadStatus Status,
    string Message);

internal interface ILazerSkinReloadService
{
    void RequestReload(
        string rootPath,
        Guid editedSkinId,
        Action<LazerSkinReloadResult>? completed = null);
}

internal sealed class LazerSkinReloadService : ILazerSkinReloadService, IDisposable
{
    private static readonly TimeSpan DefaultForegroundPollInterval =
        TimeSpan.FromMilliseconds(150);
    private readonly object gate = new();
    private readonly Func<Action, Task> dispatchCompletion;
    private readonly LazerSkinReloadExecutor executor;
    private readonly TimeSpan foregroundPollInterval;
    private PendingReload? pending;
    private CancellationTokenSource? pendingCancellation;
    private long nextRequestId;
    private bool disposed;

    public LazerSkinReloadService(
        Window owner,
        ILazerSkinRealmService? realmService = null,
        ILazerSkinReloadPlatform? platform = null)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var realm = realmService ?? new LazerSkinRealmService();
        executor = new LazerSkinReloadExecutor(
            realm.LoadGlobalKeyBindings,
            platform ?? new WindowsLazerSkinReloadPlatform());
        dispatchCompletion = action => owner.Dispatcher.InvokeAsync(action).Task;
        foregroundPollInterval = DefaultForegroundPollInterval;
    }

    internal LazerSkinReloadService(
        LazerSkinReloadExecutor executor,
        TimeSpan? foregroundPollInterval = null)
    {
        this.executor = executor;
        dispatchCompletion = action =>
        {
            action();
            return Task.CompletedTask;
        };
        this.foregroundPollInterval =
            foregroundPollInterval ?? DefaultForegroundPollInterval;
    }

    public void RequestReload(
        string rootPath,
        Guid editedSkinId,
        Action<LazerSkinReloadResult>? completed = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        PendingReload request;
        CancellationTokenSource cancellation;
        CancellationTokenSource? previousCancellation;
        lock (gate)
        {
            if (disposed)
                return;
            request = new PendingReload(
                ++nextRequestId,
                Path.GetFullPath(rootPath),
                editedSkinId,
                completed);
            pending = request;
            cancellation = new CancellationTokenSource();
            previousCancellation = pendingCancellation;
            pendingCancellation = cancellation;
        }
        previousCancellation?.Cancel();
        Log.Information(
            "Queued lazer skin reload request {RequestId} for skin {SkinId}",
            request.Id,
            request.EditedSkinId);
        _ = Task.Run(() => RunRequestAsync(request, cancellation));
    }

    private async Task RunRequestAsync(
        PendingReload request,
        CancellationTokenSource cancellation)
    {
        try
        {
            string? previousWaitingMessage = null;
            while (true)
            {
                var result = await executor.ExecuteAsync(
                    request.RootPath,
                    request.EditedSkinId,
                    cancellation.Token).ConfigureAwait(false);
                if (result.Status != LazerSkinReloadStatus.WaitingForForeground)
                {
                    await CompleteAsync(request, cancellation, result)
                        .ConfigureAwait(false);
                    return;
                }

                if (!string.Equals(
                        previousWaitingMessage,
                        result.Message,
                        StringComparison.Ordinal))
                {
                    previousWaitingMessage = result.Message;
                    Log.Information(
                        "Lazer skin reload request {RequestId} is waiting: {Message}",
                        request.Id,
                        result.Message);
                }
                await Task.Delay(foregroundPollInterval, cancellation.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Warning(
                ex,
                "Lazer skin reload request {RequestId} failed unexpectedly",
                request.Id);
            await CompleteAsync(
                request,
                cancellation,
                new LazerSkinReloadResult(
                    LazerSkinReloadStatus.ManualReloadRequired,
                    "Automatic lazer reload failed; switch skins manually."))
                .ConfigureAwait(false);
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private async Task CompleteAsync(
        PendingReload request,
        CancellationTokenSource cancellation,
        LazerSkinReloadResult result)
    {
        Action<LazerSkinReloadResult>? completed;
        lock (gate)
        {
            if (disposed
                || pending?.Id != request.Id
                || !ReferenceEquals(pendingCancellation, cancellation))
            {
                return;
            }
            completed = pending.Completed;
            pending = null;
            pendingCancellation = null;
        }

        Log.Information(
            "Lazer skin reload request {RequestId} finished with {Status}: {Message}",
            request.Id,
            result.Status,
            result.Message);
        if (completed is not null)
            await dispatchCompletion(() => completed(result)).ConfigureAwait(false);
    }

    public void Dispose()
    {
        CancellationTokenSource? cancellation;
        lock (gate)
        {
            if (disposed)
                return;
            disposed = true;
            pending = null;
            cancellation = pendingCancellation;
            pendingCancellation = null;
        }
        cancellation?.Cancel();
    }

    private sealed record PendingReload(
        long Id,
        string RootPath,
        Guid EditedSkinId,
        Action<LazerSkinReloadResult>? Completed);
}

internal sealed class LazerSkinReloadExecutor
{
    private readonly Func<string, int, IReadOnlyList<string>> loadBindings;
    private readonly ILazerSkinReloadPlatform platform;
    private readonly SemaphoreSlim cycleGate = new(1, 1);
    private readonly TimeSpan selectionTimeout;
    private readonly TimeSpan selectionPollInterval;
    private readonly TimeSpan returnFocusTimeout;

    public LazerSkinReloadExecutor(
        Func<string, int, IReadOnlyList<string>> loadBindings,
        ILazerSkinReloadPlatform platform,
        TimeSpan? selectionTimeout = null,
        TimeSpan? selectionPollInterval = null,
        TimeSpan? returnFocusTimeout = null)
    {
        this.loadBindings = loadBindings;
        this.platform = platform;
        this.selectionTimeout = selectionTimeout ?? TimeSpan.FromSeconds(5);
        this.selectionPollInterval = selectionPollInterval ?? TimeSpan.FromMilliseconds(25);
        this.returnFocusTimeout = returnFocusTimeout ?? TimeSpan.FromSeconds(30);
    }

    public async Task<LazerSkinReloadResult> ExecuteAsync(
        string rootPath,
        Guid editedSkinId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var lazerWindow = platform.FindLazerWindow();
        if (lazerWindow is null)
        {
            return new LazerSkinReloadResult(
                LazerSkinReloadStatus.LazerNotRunning,
                "The skin will load fresh the next time lazer starts.");
        }
        if (lazerWindow.Value.Handle != 0
            && platform.IsMinimized(lazerWindow.Value.Handle))
        {
            return new LazerSkinReloadResult(
                LazerSkinReloadStatus.WaitingForForeground,
                "Reload is queued and will run when osu!lazer is restored and focused.");
        }

        if (!IsProcessForeground(lazerWindow.Value.ProcessId))
        {
            return new LazerSkinReloadResult(
                LazerSkinReloadStatus.WaitingForForeground,
                "Reload is queued and will run when osu!lazer is focused.");
        }

        var selectedSkin = ReadSelectedSkinId(rootPath);
        if (selectedSkin != editedSkinId)
        {
            return new LazerSkinReloadResult(
                LazerSkinReloadStatus.NotActive,
                "The edited skin is not active; lazer will load it fresh when selected.");
        }

        await cycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsProcessForeground(lazerWindow.Value.ProcessId))
            {
                return new LazerSkinReloadResult(
                    LazerSkinReloadStatus.WaitingForForeground,
                    "Reload is queued and will run when osu!lazer is focused.");
            }

            var nextKeys = ResolveKeyboardBinding(rootPath, GlobalAction.NextSkin);
            var previousKeys = ResolveKeyboardBinding(rootPath, GlobalAction.PreviousSkin);
            if (nextKeys is null || previousKeys is null)
            {
                return new LazerSkinReloadResult(
                    LazerSkinReloadStatus.ManualReloadRequired,
                    "Lazer's Next/Previous Skin bindings are unavailable for keyboard input.");
            }

            Log.Information("Sending osu!lazer Next Skin shortcut");
            if (!platform.SendChord(nextKeys))
                return ManualInputFailure();

            var cycledSkin = await WaitForSelectionAsync(
                rootPath,
                value => value is not null && value != editedSkinId).ConfigureAwait(false);
            if (cycledSkin is null)
                return ManualInputFailure();

            if (!await WaitForForegroundProcessAsync(
                    lazerWindow.Value.ProcessId,
                    returnFocusTimeout).ConfigureAwait(false))
            {
                return ManualReturnFailure();
            }

            Log.Information("Sending osu!lazer Previous Skin shortcut");
            if (!platform.SendChord(previousKeys)
                && !platform.SendChord(previousKeys))
            {
                return ManualReturnFailure();
            }
            if (await WaitForSelectionAsync(
                    rootPath,
                    value => value == editedSkinId).ConfigureAwait(false) is null)
            {
                // A transient dropped input should not strand the user on the
                // neighbouring skin. Retry the return action once.
                if (!await WaitForForegroundProcessAsync(
                        lazerWindow.Value.ProcessId,
                        returnFocusTimeout).ConfigureAwait(false)
                    || !platform.SendChord(previousKeys)
                    || await WaitForSelectionAsync(
                        rootPath,
                        value => value == editedSkinId).ConfigureAwait(false) is null)
                {
                    return ManualReturnFailure();
                }
            }

            return new LazerSkinReloadResult(
                LazerSkinReloadStatus.Reloaded,
                "Active skin reloaded in lazer.");
        }
        finally
        {
            cycleGate.Release();
        }
    }

    internal static Guid? ReadSelectedSkinId(string rootPath)
    {
        var path = Path.Combine(rootPath, "game.ini");
        try
        {
            if (!File.Exists(path))
                return null;
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                var separator = line.IndexOf('=');
                if (separator < 0
                    || !line[..separator].Trim().Equals("Skin", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (Guid.TryParse(line[(separator + 1)..].Trim(), out var skinId))
                    return skinId;
                return null;
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        return null;
    }

    internal static ushort[]? ParseKeyboardBinding(string serialised)
    {
        try
        {
            KeyCombination combination = serialised;
            var keys = new List<ushort>();
            foreach (var key in combination.Keys)
            {
                if (!TryMapVirtualKey(key, out var virtualKey))
                    return null;
                keys.Add(virtualKey);
            }
            return keys.Count == 0 ? null : keys.Distinct().ToArray();
        }
        catch
        {
            return null;
        }
    }

    private ushort[]? ResolveKeyboardBinding(string rootPath, GlobalAction action)
    {
        try
        {
            var configured = loadBindings(rootPath, (int)action);
            var resolved = configured
                .Select(ParseKeyboardBinding)
                .FirstOrDefault(binding => binding is { Length: > 0 });
            return resolved ?? (configured.Count == 0
                ? DefaultKeyboardBinding(action)
                : null);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not read lazer binding for {Action}", action);
            return DefaultKeyboardBinding(action);
        }
    }

    private static ushort[]? DefaultKeyboardBinding(GlobalAction action) =>
        action switch
        {
            GlobalAction.NextSkin => [0x10, 0x11, (ushort)'T'],
            GlobalAction.PreviousSkin => [0x10, 0x11, (ushort)'E'],
            _ => null,
        };

    private bool IsProcessForeground(int processId)
    {
        var foreground = platform.GetForegroundWindow();
        var foregroundProcessId = foreground == 0
            ? 0
            : platform.GetWindowProcessId(foreground);
        return foreground != 0 && foregroundProcessId == processId;
    }

    private async Task<bool> WaitForForegroundProcessAsync(
        int processId,
        TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (IsProcessForeground(processId))
                return true;
            await Task.Delay(selectionPollInterval).ConfigureAwait(false);
        }
        return false;
    }

    private async Task<Guid?> WaitForSelectionAsync(
        string rootPath,
        Func<Guid?, bool> predicate)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < selectionTimeout)
        {
            var selected = ReadSelectedSkinId(rootPath);
            if (predicate(selected))
                return selected;
            await Task.Delay(selectionPollInterval).ConfigureAwait(false);
        }
        return null;
    }

    private static bool TryMapVirtualKey(InputKey key, out ushort virtualKey)
    {
        var name = key.ToString();
        if (name.Length == 1 && name[0] is >= 'A' and <= 'Z')
        {
            virtualKey = name[0];
            return true;
        }
        if (name.StartsWith("Number", StringComparison.Ordinal)
            && name.Length == 7
            && name[6] is >= '0' and <= '9')
        {
            virtualKey = name[6];
            return true;
        }
        if (name.StartsWith('F')
            && int.TryParse(name.AsSpan(1), out var function)
            && function is >= 1 and <= 24)
        {
            virtualKey = (ushort)(0x70 + function - 1);
            return true;
        }

        virtualKey = name switch
        {
            "Shift" => 0x10,
            "Control" => 0x11,
            "Alt" => 0x12,
            "Super" => 0x5B,
            "BackSpace" => 0x08,
            "Tab" => 0x09,
            "Enter" => 0x0D,
            "Escape" => 0x1B,
            "Space" => 0x20,
            "PageUp" => 0x21,
            "PageDown" => 0x22,
            "End" => 0x23,
            "Home" => 0x24,
            "Left" => 0x25,
            "Up" => 0x26,
            "Right" => 0x27,
            "Down" => 0x28,
            "Insert" => 0x2D,
            "Delete" => 0x2E,
            _ => 0,
        };
        return virtualKey != 0;
    }

    private static LazerSkinReloadResult ManualInputFailure() => new(
        LazerSkinReloadStatus.ManualReloadRequired,
        "Lazer did not accept the skin-cycle shortcut; switch skins manually to reload.");

    private static LazerSkinReloadResult ManualReturnFailure() => new(
        LazerSkinReloadStatus.ManualReloadRequired,
        "Lazer did not return to the edited skin; select it again manually.");
}

internal readonly record struct LazerWindow(int ProcessId, nint Handle);

internal interface ILazerSkinReloadPlatform
{
    LazerWindow? FindLazerWindow();
    nint GetForegroundWindow();
    int GetWindowProcessId(nint window);
    bool IsMinimized(nint window);
    bool SendChord(IReadOnlyList<ushort> virtualKeys);
}

internal sealed class WindowsLazerSkinReloadPlatform : ILazerSkinReloadPlatform
{
    private static readonly string[] ProcessNames = ["osu!", "osu", "osu.Desktop", "osulazer"];

    internal static int NativeInputSize => Marshal.SizeOf<NativeMethods.Input>();

    public LazerWindow? FindLazerWindow()
    {
        var preferredId = LazerReplayFrameDiagnostics.Load().ProcessId;
        var candidates = new List<(Process Process, DateTime Started, nint Handle)>();
        try
        {
            foreach (var name in ProcessNames)
            {
                foreach (var process in Process.GetProcessesByName(name))
                {
                    if (!IsLikelyLazer(process))
                    {
                        process.Dispose();
                        continue;
                    }
                    process.Refresh();
                    var handle = process.MainWindowHandle;
                    if (handle == 0)
                        handle = NativeMethods.FindVisibleTopLevelWindow(process.Id);
                    DateTime started;
                    try { started = process.StartTime; }
                    catch { started = DateTime.MinValue; }
                    candidates.Add((process, started, handle));
                }
            }

            var selected = candidates
                .OrderByDescending(candidate => candidate.Process.Id == preferredId)
                .ThenByDescending(candidate => candidate.Started)
                .FirstOrDefault();
            return selected.Process is null
                ? null
                : new LazerWindow(selected.Process.Id, selected.Handle);
        }
        finally
        {
            foreach (var candidate in candidates)
                candidate.Process.Dispose();
        }
    }

    public nint GetForegroundWindow() => NativeMethods.GetForegroundWindow();

    public int GetWindowProcessId(nint window)
    {
        _ = NativeMethods.GetWindowThreadProcessId(window, out var processId);
        return unchecked((int)processId);
    }

    public bool IsMinimized(nint window) => NativeMethods.IsIconic(window);

    public bool SendChord(IReadOnlyList<ushort> virtualKeys)
    {
        if (virtualKeys.Count == 0)
            return false;
        var inputs = new NativeMethods.Input[virtualKeys.Count * 2];
        for (var index = 0; index < virtualKeys.Count; index++)
        {
            inputs[index] = NativeMethods.Input.Key(virtualKeys[index], keyUp: false);
            inputs[inputs.Length - index - 1] =
                NativeMethods.Input.Key(virtualKeys[index], keyUp: true);
        }

        var inputSize = NativeInputSize;
        var sent = NativeMethods.SendInput(
            (uint)inputs.Length,
            inputs,
            inputSize);
        if (sent == inputs.Length)
            return true;

        // Ensure a partial SendInput call cannot leave a modifier held.
        var releases = virtualKeys
            .Reverse()
            .Select(key => NativeMethods.Input.Key(key, keyUp: true))
            .ToArray();
        _ = NativeMethods.SendInput(
            (uint)releases.Length,
            releases,
            inputSize);
        return false;
    }

    private static bool IsLikelyLazer(Process process)
    {
        try
        {
            var directory = Path.GetDirectoryName(process.MainModule?.FileName);
            return directory is not null
                   && !Directory.Exists(Path.Combine(directory, "Songs"))
                   && !Directory.Exists(Path.Combine(directory, "Data", "r"));
        }
        catch
        {
            return false;
        }
    }

    private static class NativeMethods
    {
        private const uint InputKeyboard = 1;
        private const uint KeyEventKeyUp = 0x0002;

        [DllImport("user32.dll")]
        internal static extern nint GetForegroundWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsIconic(nint window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumWindows(EnumWindowsCallback callback, nint parameter);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(nint window);

        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(
            nint window,
            out uint processId);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern uint SendInput(
            uint inputCount,
            [In] Input[] inputs,
            int inputSize);

        internal static nint FindVisibleTopLevelWindow(int processId)
        {
            nint result = 0;
            _ = EnumWindows((window, parameter) =>
            {
                _ = GetWindowThreadProcessId(window, out var candidateProcessId);
                if (candidateProcessId != processId || !IsWindowVisible(window))
                    return true;
                result = window;
                return false;
            }, 0);
            return result;
        }

        private delegate bool EnumWindowsCallback(nint window, nint parameter);

        [StructLayout(LayoutKind.Sequential)]
        internal struct Input
        {
            internal uint Type;
            internal InputUnion Union;

            internal static Input Key(ushort virtualKey, bool keyUp) => new()
            {
                Type = InputKeyboard,
                Union = new InputUnion
                {
                    Keyboard = new KeyboardInput
                    {
                        VirtualKey = virtualKey,
                        Flags = keyUp ? KeyEventKeyUp : 0,
                    },
                },
            };
        }

        [StructLayout(LayoutKind.Explicit)]
        internal struct InputUnion
        {
            [FieldOffset(0)]
            internal KeyboardInput Keyboard;

            // INPUT contains a native union. Its size is determined by
            // MOUSEINPUT, even when this particular event is a keyboard event.
            [FieldOffset(0)]
            internal MouseInput Mouse;

            [FieldOffset(0)]
            internal HardwareInput Hardware;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct KeyboardInput
        {
            internal ushort VirtualKey;
            internal ushort ScanCode;
            internal uint Flags;
            internal uint Time;
            internal nuint ExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MouseInput
        {
            internal int X;
            internal int Y;
            internal uint MouseData;
            internal uint Flags;
            internal uint Time;
            internal nuint ExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct HardwareInput
        {
            internal uint Message;
            internal ushort ParameterLow;
            internal ushort ParameterHigh;
        }
    }
}
