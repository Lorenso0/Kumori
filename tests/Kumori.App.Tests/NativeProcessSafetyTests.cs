using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Kumori.Core.State;
using Kumori.Native;
using Kumori.Tracking;
using Xunit;

namespace Kumori.App.Tests;

public sealed class NativeProcessSafetyTests
{
    [Theory]
    [InlineData("Kumori", "Kumori", true)]
    [InlineData("KUMORI", null, true)]
    [InlineData("osu!(lazer)", "osu!(lazer)", false)]
    [InlineData(null, null, false)]
    public void CustomLazerReplaySafeguardIsScopedToKumoriProduct(
        string? productName,
        string? fileDescription,
        bool expected)
    {
        Assert.Equal(
            expected,
            LazerMemoryReplayFrameSource.IsKumoriCustomProduct(productName, fileDescription));
    }

    [Fact]
    public void ReadProcessMemoryUsesPointerSizedSizeTParametersAndSafeHandle()
    {
        var method = typeof(ProcessMemory).GetMethod(
            "ReadProcessMemory",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        var parameters = method.GetParameters();
        Assert.Equal(typeof(SafeProcessHandle), parameters[0].ParameterType);
        Assert.Equal(typeof(nuint), parameters[3].ParameterType);
        Assert.Equal(typeof(nuint).MakeByRefType(), parameters[4].ParameterType);

        var handleField = typeof(ProcessMemory).GetField(
            "_handle",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(handleField);
        Assert.Equal(typeof(SafeProcessHandle), handleField.FieldType);
    }

    [Fact]
    public void ProcessMemoryReadsCurrentProcessAndCanBeDisposedTwice()
    {
        byte[] expected = [0x4b, 0x75, 0x6d, 0x6f, 0x72, 0x69, 0x21, 0x00];
        var pinned = GCHandle.Alloc(expected, GCHandleType.Pinned);
        try
        {
            using var process = Process.GetCurrentProcess();
            var memory = ProcessMemory.Open(process);
            Assert.Equal(expected, memory.ReadBytes(pinned.AddrOfPinnedObject(), expected.Length));
            memory.Dispose();
            memory.Dispose();
        }
        finally
        {
            pinned.Free();
        }
    }

    [Fact]
    public void OpenTabletDriverOwnershipRequiresPidStartTimeAndPathToMatch()
    {
        var expected = new OpenTabletDriverService.OwnedProcessIdentity(
            123,
            456,
            Path.Combine("C:\\", "OpenTabletDriver", "OpenTabletDriver.UX.Wpf.exe"));

        Assert.True(OpenTabletDriverService.ProcessIdentityMatches(expected, expected));
        Assert.False(OpenTabletDriverService.ProcessIdentityMatches(
            expected,
            expected with { ProcessId = 124 }));
        Assert.False(OpenTabletDriverService.ProcessIdentityMatches(
            expected,
            expected with { StartTimeUtcTicks = 457 }));
        Assert.False(OpenTabletDriverService.ProcessIdentityMatches(
            expected,
            expected with { ExecutablePath = Path.Combine("C:\\", "Other", "OpenTabletDriver.UX.Wpf.exe") }));
    }

    [Fact]
    public void OpenTabletDriverOwnershipRequiresExactInstallationDirectory()
    {
        var installation = Path.Combine("C:\\", "Tools", "OpenTabletDriver");

        Assert.True(OpenTabletDriverService.IsExecutableFromInstallation(
            Path.Combine(installation, "OpenTabletDriver.Daemon.exe"),
            installation + Path.DirectorySeparatorChar));
        Assert.False(OpenTabletDriverService.IsExecutableFromInstallation(
            Path.Combine("C:\\", "Other", "OpenTabletDriver.Daemon.exe"),
            installation));
        Assert.False(OpenTabletDriverService.IsExecutableFromInstallation(
            Path.Combine(installation, "Nested", "OpenTabletDriver.Daemon.exe"),
            installation));
    }

    [Fact]
    public void PointerSearchUsesX86WidthAndFindsPointerSplitAcrossChunks()
    {
        var value = (nint)0x12345678;
        var needle = BitConverter.GetBytes(0x12345678);
        var search = new ProcessMemoryPointerSearch(value, pointerSize: sizeof(int));
        byte[] firstChunk = [0xaa, 0xaa, 0xaa, needle[0], needle[1]];
        byte[] secondChunk = [needle[2], needle[3], 0xbb, 0xbb, 0xbb];

        Assert.False(search.TrySearch((nint)0x1001, firstChunk, out _));
        Assert.True(search.TrySearch((nint)0x1006, secondChunk, out var match));
        Assert.Equal((nint)0x1004, match);
    }

    [Fact]
    public async Task LazerMemorySourceDisposalStopsItsLifetimeWorkAndIsIdempotent()
    {
        var source = new LazerMemoryReplayFrameSource();
        Task prewarm = source.PrewarmGameBaseAsync(CancellationToken.None);
        Task read = ConsumeFramesUntilStoppedAsync(source);

        await source.DisposeAsync();
        source.Dispose();

        await prewarm.WaitAsync(TimeSpan.FromSeconds(2));
        await read.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void ReplayDetectorReturnsFalseAfterDisposalAndIsIdempotent()
    {
        var source = new LazerMemoryReplayFrameSource();
        var detector = new OsuReplayPlaybackDetector(source);

        detector.Dispose();
        detector.Dispose();

        Assert.False(detector.IsWatchingReplay(OsuClientKind.Lazer));
        Assert.False(detector.IsWatchingReplay(OsuClientKind.Stable));
    }

    [Fact]
    public async Task TrackingServiceDisposesItsReplayDetector()
    {
        var detector = new DisposableDetector();
        var tracking = new TosuTrackingService(
            new AppStateStore(),
            replayPlaybackDetector: detector);

        await tracking.DisposeAsync();

        Assert.True(detector.Disposed);
    }

    private sealed class DisposableDetector : IReplayPlaybackDetector, IDisposable
    {
        public bool Disposed { get; private set; }
        public bool IsWatchingReplay(OsuClientKind clientKind) => false;
        public void Dispose() => Disposed = true;
    }

    private static async Task ConsumeFramesUntilStoppedAsync(LazerMemoryReplayFrameSource source)
    {
        try
        {
            await foreach (var _ in source.ReadFramesAsync(CancellationToken.None))
            {
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
