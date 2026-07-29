using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
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
    private const string QueueKey = "lazer-current-skin-reload";
    private readonly object gate = new();
    private readonly GameplayWorkCoordinator coordinator;
    private readonly Window owner;
    private readonly LazerSkinReloadExecutor executor;
    private PendingReload? pending;
    private long nextRequestId;
    private bool disposed;

    public LazerSkinReloadService(
        GameplayWorkCoordinator coordinator,
        Window owner,
        ILazerSkinRealmService? realmService = null,
        ILazerSkinReloadPlatform? platform = null)
    {
        this.coordinator = coordinator;
        this.owner = owner;
        var realm = realmService ?? new LazerSkinRealmService();
        executor = new LazerSkinReloadExecutor(
            realm.LoadGlobalKeyBindings,
            platform ?? new WindowsLazerSkinReloadPlatform());
        owner.Activated += Owner_Activated;
    }

    public void RequestReload(
        string rootPath,
        Guid editedSkinId,
        Action<LazerSkinReloadResult>? completed = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        lock (gate)
        {
            if (disposed)
                return;
            pending = new PendingReload(
                ++nextRequestId,
                Path.GetFullPath(rootPath),
                editedSkinId,
                completed);
        }
        SchedulePending();
    }

    private void Owner_Activated(object? sender, EventArgs e) => SchedulePending();

    private void SchedulePending()
    {
        PendingReload? request;
        lock (gate)
        {
            if (disposed || pending is null)
                return;
            request = pending;
        }

        try
        {
            _ = coordinator.Enqueue(
                QueueKey,
                token => ExecuteAsync(request, token),
                coalesce: true);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task ExecuteAsync(PendingReload request, CancellationToken cancellationToken)
    {
        nint ownerHandle = 0;
        await owner.Dispatcher.InvokeAsync(() =>
            ownerHandle = new WindowInteropHelper(owner).Handle);
        if (ownerHandle == 0)
            return;

        var result = await executor.ExecuteAsync(
            request.RootPath,
            request.EditedSkinId,
            ownerHandle,
            cancellationToken).ConfigureAwait(false);
        if (result.Status == LazerSkinReloadStatus.WaitingForForeground)
            return;

        Action<LazerSkinReloadResult>? completed = null;
        lock (gate)
        {
            if (pending?.Id != request.Id)
                return;
            completed = pending.Completed;
            pending = null;
        }

        Log.Information(
            "Lazer skin reload finished with {Status}: {Message}",
            result.Status,
            result.Message);
        if (completed is not null)
            await owner.Dispatcher.InvokeAsync(() => completed(result));
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
                return;
            disposed = true;
            pending = null;
        }
        owner.Activated -= Owner_Activated;
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
    private readonly TimeSpan selectionTimeout;
    private readonly TimeSpan selectionPollInterval;

    public LazerSkinReloadExecutor(
        Func<string, int, IReadOnlyList<string>> loadBindings,
        ILazerSkinReloadPlatform platform,
        TimeSpan? selectionTimeout = null,
        TimeSpan? selectionPollInterval = null)
    {
        this.loadBindings = loadBindings;
        this.platform = platform;
        this.selectionTimeout = selectionTimeout ?? TimeSpan.FromSeconds(2);
        this.selectionPollInterval = selectionPollInterval ?? TimeSpan.FromMilliseconds(25);
    }

    public async Task<LazerSkinReloadResult> ExecuteAsync(
        string rootPath,
        Guid editedSkinId,
        nint ownerWindow,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var selectedSkin = ReadSelectedSkinId(rootPath);
        if (selectedSkin != editedSkinId)
        {
            return new LazerSkinReloadResult(
                LazerSkinReloadStatus.NotActive,
                "The edited skin is not active; lazer will load it fresh when selected.");
        }

        var lazerWindow = platform.FindLazerWindow();
        if (lazerWindow is null)
        {
            return new LazerSkinReloadResult(
                LazerSkinReloadStatus.LazerNotRunning,
                "The skin will load fresh the next time lazer starts.");
        }
        if (platform.IsMinimized(lazerWindow.Value.Handle))
        {
            return new LazerSkinReloadResult(
                LazerSkinReloadStatus.ManualReloadRequired,
                "Lazer is minimized; switch skins manually to reload.");
        }

        var previousForeground = platform.GetForegroundWindow();
        if (previousForeground != ownerWindow && previousForeground != lazerWindow.Value.Handle)
        {
            return new LazerSkinReloadResult(
                LazerSkinReloadStatus.WaitingForForeground,
                "Reload is waiting until Kumori or lazer is active.");
        }

        var nextKeys = ResolveKeyboardBinding(rootPath, GlobalAction.NextSkin);
        var previousKeys = ResolveKeyboardBinding(rootPath, GlobalAction.PreviousSkin);
        if (nextKeys is null || previousKeys is null)
        {
            return new LazerSkinReloadResult(
                LazerSkinReloadStatus.ManualReloadRequired,
                "Lazer's Next/Previous Skin bindings are unavailable for keyboard input.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!platform.TryActivate(lazerWindow.Value.Handle))
        {
            return new LazerSkinReloadResult(
                LazerSkinReloadStatus.WaitingForForeground,
                "Reload is waiting until lazer can receive input.");
        }

        try
        {
            // Once the first action is sent, always attempt to return to the
            // original skin even if gameplay cancellation arrives meanwhile.
            if (!platform.SendChord(nextKeys))
                return ManualInputFailure();

            var cycledSkin = await WaitForSelectionAsync(
                rootPath,
                value => value is not null && value != editedSkinId).ConfigureAwait(false);
            if (cycledSkin is null)
                return ManualInputFailure();

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
                if (!platform.SendChord(previousKeys)
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
            if (previousForeground != 0
                && previousForeground != lazerWindow.Value.Handle)
            {
                platform.TryActivate(previousForeground);
            }
        }
    }

    internal static Guid? ReadSelectedSkinId(string rootPath)
    {
        var path = Path.Combine(rootPath, "game.ini");
        try
        {
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
                return Guid.TryParse(line[(separator + 1)..].Trim(), out var skinId)
                    ? skinId
                    : null;
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
            return loadBindings(rootPath, (int)action)
                .Select(ParseKeyboardBinding)
                .FirstOrDefault(binding => binding is { Length: > 0 });
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not read lazer binding for {Action}", action);
            return null;
        }
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
    bool IsMinimized(nint window);
    bool TryActivate(nint window);
    bool SendChord(IReadOnlyList<ushort> virtualKeys);
}

internal sealed class WindowsLazerSkinReloadPlatform : ILazerSkinReloadPlatform
{
    private static readonly string[] ProcessNames = ["osu!", "osu", "osu.Desktop", "osulazer"];

    public LazerWindow? FindLazerWindow()
    {
        var preferredId = LazerReplayFrameDiagnostics.Load().ProcessId;
        var candidates = new List<(Process Process, DateTime Started)>();
        try
        {
            foreach (var name in ProcessNames)
            {
                foreach (var process in Process.GetProcessesByName(name))
                {
                    if (!IsLikelyLazer(process) || process.MainWindowHandle == 0)
                    {
                        process.Dispose();
                        continue;
                    }
                    DateTime started;
                    try { started = process.StartTime; }
                    catch { started = DateTime.MinValue; }
                    candidates.Add((process, started));
                }
            }

            var selected = candidates
                .OrderByDescending(candidate => candidate.Process.Id == preferredId)
                .ThenByDescending(candidate => candidate.Started)
                .FirstOrDefault();
            return selected.Process is null
                ? null
                : new LazerWindow(selected.Process.Id, selected.Process.MainWindowHandle);
        }
        finally
        {
            foreach (var candidate in candidates)
                candidate.Process.Dispose();
        }
    }

    public nint GetForegroundWindow() => NativeMethods.GetForegroundWindow();

    public bool IsMinimized(nint window) => NativeMethods.IsIconic(window);

    public bool TryActivate(nint window)
    {
        if (window == 0)
            return false;
        if (NativeMethods.GetForegroundWindow() == window)
            return true;
        if (!NativeMethods.SetForegroundWindow(window))
            return false;
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromMilliseconds(500))
        {
            if (NativeMethods.GetForegroundWindow() == window)
                return true;
            Thread.Sleep(20);
        }
        return false;
    }

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

        var sent = NativeMethods.SendInput(
            (uint)inputs.Length,
            inputs,
            Marshal.SizeOf<NativeMethods.Input>());
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
            Marshal.SizeOf<NativeMethods.Input>());
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
        internal static extern bool SetForegroundWindow(nint window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsIconic(nint window);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern uint SendInput(
            uint inputCount,
            [In] Input[] inputs,
            int inputSize);

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
    }
}
