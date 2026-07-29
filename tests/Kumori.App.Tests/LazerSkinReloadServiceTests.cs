using osu.Framework.Input;
using osu.Framework.Input.Bindings;
using osu.Game.Input.Bindings;
using Xunit;

namespace Kumori.App.Tests;

public sealed class LazerSkinReloadServiceTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        $"kumori-lazer-reload-{Guid.NewGuid():N}");

    [Fact]
    public void Selected_skin_reader_and_default_bindings_are_supported()
    {
        var skinId = Guid.NewGuid();
        WriteSelectedSkin(skinId);

        Assert.Equal(skinId, LazerSkinReloadExecutor.ReadSelectedSkinId(root));
        Assert.Equal(
            [0x10, 0x11, (ushort)'T'],
            LazerSkinReloadExecutor.ParseKeyboardBinding(NextSkinBinding));
        Assert.Null(LazerSkinReloadExecutor.ParseKeyboardBinding("MouseLeft"));
    }

    [Fact]
    public void Windows_input_layout_matches_the_native_input_structure()
    {
        var expectedSize = IntPtr.Size == 8 ? 40 : 28;

        Assert.Equal(expectedSize, WindowsLazerSkinReloadPlatform.NativeInputSize);
    }

    [Fact]
    public async Task Focused_lazer_cycles_and_returns_to_the_active_skin()
    {
        var original = Guid.NewGuid();
        var neighbour = Guid.NewGuid();
        WriteSelectedSkin(original);
        var platform = new FakePlatform { Foreground = 2 };
        platform.ChordSent = count => WriteSelectedSkin(count == 1 ? neighbour : original);
        var executor = CreateExecutor(platform);

        var result = await executor.ExecuteAsync(root, original, CancellationToken.None);

        Assert.Equal(LazerSkinReloadStatus.Reloaded, result.Status);
        Assert.Equal(2, platform.SentChords.Count);
        Assert.Equal(original, LazerSkinReloadExecutor.ReadSelectedSkinId(root));
    }

    [Fact]
    public async Task Missing_binding_overrides_use_lazer_default_skin_shortcuts()
    {
        var original = Guid.NewGuid();
        var neighbour = Guid.NewGuid();
        WriteSelectedSkin(original);
        var platform = new FakePlatform { Foreground = 2 };
        platform.ChordSent = count =>
            WriteSelectedSkin(count == 1 ? neighbour : original);
        var executor = new LazerSkinReloadExecutor(
            (_, _) => [],
            platform,
            selectionTimeout: TimeSpan.FromMilliseconds(75),
            selectionPollInterval: TimeSpan.FromMilliseconds(5));

        var result = await executor.ExecuteAsync(
            root,
            original,
            CancellationToken.None);

        Assert.Equal(LazerSkinReloadStatus.Reloaded, result.Status);
        Assert.Equal(
            [0x10, 0x11, (ushort)'T'],
            platform.SentChords[0]);
        Assert.Equal(
            [0x10, 0x11, (ushort)'E'],
            platform.SentChords[1]);
    }

    [Fact]
    public async Task Reload_waits_without_input_until_lazer_is_focused()
    {
        var original = Guid.NewGuid();
        var neighbour = Guid.NewGuid();
        WriteSelectedSkin(original);
        var platform = new FakePlatform { Foreground = 1 };
        platform.ChordSent = count =>
            WriteSelectedSkin(count == 1 ? neighbour : original);
        var executor = CreateExecutor(platform);

        var waiting = await executor.ExecuteAsync(
            root,
            original,
            CancellationToken.None);

        Assert.Equal(LazerSkinReloadStatus.WaitingForForeground, waiting.Status);
        Assert.Empty(platform.SentChords);

        platform.Foreground = 2;
        var result = await executor.ExecuteAsync(
            root,
            original,
            CancellationToken.None);

        Assert.Equal(LazerSkinReloadStatus.Reloaded, result.Status);
        Assert.Equal(2, platform.SentChords.Count);
    }

    [Fact]
    public async Task Service_polls_independently_until_lazer_is_focused()
    {
        var original = Guid.NewGuid();
        var neighbour = Guid.NewGuid();
        WriteSelectedSkin(original);
        var platform = new FakePlatform { Foreground = 99 };
        platform.ChordSent = count =>
            WriteSelectedSkin(count == 1 ? neighbour : original);
        using var service = new LazerSkinReloadService(
            CreateExecutor(platform),
            TimeSpan.FromMilliseconds(5));
        var completion = new TaskCompletionSource<LazerSkinReloadResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        service.RequestReload(root, original, completion.SetResult);
        await Task.Delay(25);
        Assert.Empty(platform.SentChords);

        platform.Foreground = 2;
        var result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(LazerSkinReloadStatus.Reloaded, result.Status);
        Assert.Equal(2, platform.SentChords.Count);
    }

    [Fact]
    public async Task Any_foreground_window_owned_by_lazer_allows_the_reload()
    {
        var original = Guid.NewGuid();
        var neighbour = Guid.NewGuid();
        WriteSelectedSkin(original);
        var platform = new FakePlatform { Foreground = 22 };
        platform.WindowProcessIds[22] = 123;
        platform.ChordSent = count =>
            WriteSelectedSkin(count == 1 ? neighbour : original);
        var executor = CreateExecutor(platform);

        var result = await executor.ExecuteAsync(
            root,
            original,
            CancellationToken.None);

        Assert.Equal(LazerSkinReloadStatus.Reloaded, result.Status);
        Assert.Equal(2, platform.SentChords.Count);
    }

    [Fact]
    public async Task Inactive_skin_does_not_focus_or_send_input()
    {
        WriteSelectedSkin(Guid.NewGuid());
        var platform = new FakePlatform { Foreground = 2 };
        var executor = CreateExecutor(platform);

        var result = await executor.ExecuteAsync(
            root,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.Equal(LazerSkinReloadStatus.NotActive, result.Status);
        Assert.Empty(platform.SentChords);
    }

    [Fact]
    public async Task Return_shortcut_waits_for_lazer_to_regain_focus()
    {
        var original = Guid.NewGuid();
        var neighbour = Guid.NewGuid();
        WriteSelectedSkin(original);
        var platform = new FakePlatform { Foreground = 2 };
        platform.ChordSent = count =>
        {
            if (count == 1)
            {
                WriteSelectedSkin(neighbour);
                platform.Foreground = 99;
                _ = Task.Run(async () =>
                {
                    await Task.Delay(15);
                    platform.Foreground = 2;
                });
            }
            else
            {
                WriteSelectedSkin(original);
            }
        };
        var executor = CreateExecutor(platform);

        var result = await executor.ExecuteAsync(
            root,
            original,
            CancellationToken.None);

        Assert.Equal(LazerSkinReloadStatus.Reloaded, result.Status);
        Assert.Equal(2, platform.SentChords.Count);
    }

    [Fact]
    public async Task Unrelated_foreground_keeps_reload_pending_without_input()
    {
        var original = Guid.NewGuid();
        WriteSelectedSkin(original);
        var platform = new FakePlatform { Foreground = 99 };
        var executor = CreateExecutor(platform);

        var result = await executor.ExecuteAsync(root, original, CancellationToken.None);

        Assert.Equal(LazerSkinReloadStatus.WaitingForForeground, result.Status);
        Assert.Empty(platform.SentChords);
    }

    [Fact]
    public async Task Minimized_lazer_keeps_reload_pending_without_input()
    {
        var original = Guid.NewGuid();
        WriteSelectedSkin(original);
        var platform = new FakePlatform
        {
            Foreground = 2,
            Minimized = true,
        };
        var executor = CreateExecutor(platform);

        var result = await executor.ExecuteAsync(
            root,
            original,
            CancellationToken.None);

        Assert.Equal(LazerSkinReloadStatus.WaitingForForeground, result.Status);
        Assert.Empty(platform.SentChords);
    }

    [Fact]
    public async Task Rejected_next_action_is_reported()
    {
        var original = Guid.NewGuid();
        WriteSelectedSkin(original);
        var platform = new FakePlatform { Foreground = 2 };
        var executor = CreateExecutor(platform);

        var result = await executor.ExecuteAsync(root, original, CancellationToken.None);

        Assert.Equal(LazerSkinReloadStatus.ManualReloadRequired, result.Status);
        Assert.Single(platform.SentChords);
    }

    [Fact]
    public async Task Dropped_previous_input_is_retried_before_reporting_success()
    {
        var original = Guid.NewGuid();
        var neighbour = Guid.NewGuid();
        WriteSelectedSkin(original);
        var platform = new FakePlatform { Foreground = 2 };
        platform.ChordResult = count => count != 2;
        platform.ChordSent = count =>
        {
            if (count == 1)
                WriteSelectedSkin(neighbour);
            else if (count == 3)
                WriteSelectedSkin(original);
        };
        var executor = CreateExecutor(platform);

        var result = await executor.ExecuteAsync(root, original, CancellationToken.None);

        Assert.Equal(LazerSkinReloadStatus.Reloaded, result.Status);
        Assert.Equal(3, platform.SentChords.Count);
        Assert.Equal(original, LazerSkinReloadExecutor.ReadSelectedSkinId(root));
    }

    private LazerSkinReloadExecutor CreateExecutor(FakePlatform platform) => new(
        (_, action) => action switch
        {
            (int)GlobalAction.NextSkin => [NextSkinBinding],
            (int)GlobalAction.PreviousSkin => [PreviousSkinBinding],
            _ => [],
        },
        platform,
        selectionTimeout: TimeSpan.FromMilliseconds(75),
        selectionPollInterval: TimeSpan.FromMilliseconds(5),
        returnFocusTimeout: TimeSpan.FromMilliseconds(100));

    private static string NextSkinBinding =>
        new KeyCombination(InputKey.Control, InputKey.Shift, InputKey.T).ToString();

    private static string PreviousSkinBinding =>
        new KeyCombination(InputKey.Control, InputKey.Shift, InputKey.E).ToString();

    private void WriteSelectedSkin(Guid skinId)
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(
            Path.Combine(root, "game.ini"),
            $"Username = player{Environment.NewLine}Skin = {skinId:D}{Environment.NewLine}");
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    private sealed class FakePlatform : ILazerSkinReloadPlatform
    {
        public nint Foreground { get; set; }
        public bool Minimized { get; set; }
        public Action<int>? ChordSent { get; set; }
        public Func<int, bool>? ChordResult { get; set; }
        public List<IReadOnlyList<ushort>> SentChords { get; } = [];
        public Dictionary<nint, int> WindowProcessIds { get; } = new()
        {
            [2] = 123,
        };

        public LazerWindow? FindLazerWindow() => new(123, 2);

        public nint GetForegroundWindow() => Foreground;

        public int GetWindowProcessId(nint window) =>
            WindowProcessIds.GetValueOrDefault(window);

        public bool IsMinimized(nint window) => Minimized;

        public bool SendChord(IReadOnlyList<ushort> virtualKeys)
        {
            SentChords.Add(virtualKeys.ToArray());
            ChordSent?.Invoke(SentChords.Count);
            return ChordResult?.Invoke(SentChords.Count) ?? true;
        }
    }
}
