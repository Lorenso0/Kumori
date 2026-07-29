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
    public async Task Active_skin_cycles_and_returns_before_restoring_foreground()
    {
        var original = Guid.NewGuid();
        var neighbour = Guid.NewGuid();
        WriteSelectedSkin(original);
        var platform = new FakePlatform { Foreground = 1 };
        platform.ChordSent = count => WriteSelectedSkin(count == 1 ? neighbour : original);
        var executor = CreateExecutor(platform);

        var result = await executor.ExecuteAsync(root, original, 1, CancellationToken.None);

        Assert.Equal(LazerSkinReloadStatus.Reloaded, result.Status);
        Assert.Equal(2, platform.SentChords.Count);
        Assert.Equal([2, 1], platform.ActivatedWindows);
        Assert.Equal(original, LazerSkinReloadExecutor.ReadSelectedSkinId(root));
    }

    [Fact]
    public async Task Inactive_skin_does_not_focus_or_send_input()
    {
        WriteSelectedSkin(Guid.NewGuid());
        var platform = new FakePlatform { Foreground = 1 };
        var executor = CreateExecutor(platform);

        var result = await executor.ExecuteAsync(
            root,
            Guid.NewGuid(),
            1,
            CancellationToken.None);

        Assert.Equal(LazerSkinReloadStatus.NotActive, result.Status);
        Assert.Empty(platform.SentChords);
        Assert.Empty(platform.ActivatedWindows);
    }

    [Fact]
    public async Task Unrelated_foreground_keeps_reload_pending_without_input()
    {
        var original = Guid.NewGuid();
        WriteSelectedSkin(original);
        var platform = new FakePlatform { Foreground = 99 };
        var executor = CreateExecutor(platform);

        var result = await executor.ExecuteAsync(root, original, 1, CancellationToken.None);

        Assert.Equal(LazerSkinReloadStatus.WaitingForForeground, result.Status);
        Assert.Empty(platform.SentChords);
    }

    [Fact]
    public async Task Rejected_next_action_is_reported_and_foreground_is_restored()
    {
        var original = Guid.NewGuid();
        WriteSelectedSkin(original);
        var platform = new FakePlatform { Foreground = 1 };
        var executor = CreateExecutor(platform);

        var result = await executor.ExecuteAsync(root, original, 1, CancellationToken.None);

        Assert.Equal(LazerSkinReloadStatus.ManualReloadRequired, result.Status);
        Assert.Single(platform.SentChords);
        Assert.Equal([2, 1], platform.ActivatedWindows);
    }

    [Fact]
    public async Task Dropped_previous_input_is_retried_before_reporting_success()
    {
        var original = Guid.NewGuid();
        var neighbour = Guid.NewGuid();
        WriteSelectedSkin(original);
        var platform = new FakePlatform { Foreground = 1 };
        platform.ChordResult = count => count != 2;
        platform.ChordSent = count =>
        {
            if (count == 1)
                WriteSelectedSkin(neighbour);
            else if (count == 3)
                WriteSelectedSkin(original);
        };
        var executor = CreateExecutor(platform);

        var result = await executor.ExecuteAsync(root, original, 1, CancellationToken.None);

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
        selectionPollInterval: TimeSpan.FromMilliseconds(5));

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
        public bool ActivationSucceeds { get; set; } = true;
        public Action<int>? ChordSent { get; set; }
        public Func<int, bool>? ChordResult { get; set; }
        public List<nint> ActivatedWindows { get; } = [];
        public List<IReadOnlyList<ushort>> SentChords { get; } = [];

        public LazerWindow? FindLazerWindow() => new(123, 2);

        public nint GetForegroundWindow() => Foreground;

        public bool IsMinimized(nint window) => Minimized;

        public bool TryActivate(nint window)
        {
            ActivatedWindows.Add(window);
            if (!ActivationSucceeds)
                return false;
            Foreground = window;
            return true;
        }

        public bool SendChord(IReadOnlyList<ushort> virtualKeys)
        {
            SentChords.Add(virtualKeys.ToArray());
            ChordSent?.Invoke(SentChords.Count);
            return ChordResult?.Invoke(SentChords.Count) ?? true;
        }
    }
}
