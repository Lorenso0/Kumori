using Kumori.App.Skins;
using Xunit;

namespace Kumori.App.Tests;

public sealed class SkinStudioEmbeddedHostTests
{
    [Fact]
    public void EmbeddedHostParticipatesInWpfKeyboardFocus()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var host = new SkinStudioEmbeddedHost();

                Assert.True(host.Focusable);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        Assert.Null(failure);
    }

    [Fact]
    public void EmbeddedStyleRemovesTopLevelChromeAndAddsChildClipping()
    {
        const long popup = 0x80000000L;
        const long caption = 0x00C00000L;
        const long thickFrame = 0x00040000L;
        const long systemMenu = 0x00080000L;
        const long child = 0x40000000L;
        const long visible = 0x10000000L;
        const long clipChildren = 0x02000000L;
        const long clipSiblings = 0x04000000L;

        var embedded = SkinStudioEmbeddedHost.EmbeddedWindowStyle(
            popup | caption | thickFrame | systemMenu);

        Assert.Equal(0, embedded & popup);
        Assert.Equal(0, embedded & caption);
        Assert.Equal(0, embedded & thickFrame);
        Assert.Equal(0, embedded & systemMenu);
        Assert.NotEqual(0, embedded & child);
        Assert.NotEqual(0, embedded & visible);
        Assert.NotEqual(0, embedded & clipChildren);
        Assert.NotEqual(0, embedded & clipSiblings);
    }

    [Fact]
    public void EmbeddedExtendedStyleRemovesTaskbarPresence()
    {
        const long appWindow = 0x00040000L;
        const long toolWindow = 0x00000080L;
        const long noParentNotify = 0x00000004L;

        var embedded = SkinStudioEmbeddedHost.EmbeddedExtendedWindowStyle(appWindow);

        Assert.Equal(0, embedded & appWindow);
        Assert.NotEqual(0, embedded & toolWindow);
        Assert.Equal(0, embedded & noParentNotify);
    }

    [Theory]
    [InlineData(100, 200, true)]
    [InlineData(499, 599, true)]
    [InlineData(99, 200, false)]
    [InlineData(500, 599, false)]
    [InlineData(499, 600, false)]
    public void PointerActivationUsesHalfOpenStudioScreenBounds(
        int x,
        int y,
        bool expected)
    {
        var bounds = new SkinStudioEmbeddedHost.NativeMethods.Rect
        {
            Left = 100,
            Top = 200,
            Right = 500,
            Bottom = 600,
        };
        var point = new SkinStudioEmbeddedHost.NativeMethods.Point
        {
            X = x,
            Y = y,
        };

        Assert.Equal(
            expected,
            SkinStudioEmbeddedHost.ScreenPointIsInside(bounds, point));
    }

    [Fact]
    public void WindowDiscoveryPrefersTheRealSdlStudioSurface()
    {
        var transient = SkinStudioEmbeddedHost.EmbeddedWindowCandidateScore(
            "",
            "Hidden helper",
            100,
            100,
            hasOwner: false);
        var ownedDialog = SkinStudioEmbeddedHost.EmbeddedWindowCandidateScore(
            "Kumori Skin Studio",
            "SDL_app",
            1500,
            930,
            hasOwner: true);
        var studio = SkinStudioEmbeddedHost.EmbeddedWindowCandidateScore(
            "Kumori Skin Studio",
            "SDL_app",
            1500,
            930,
            hasOwner: false);
        var zeroSized = SkinStudioEmbeddedHost.EmbeddedWindowCandidateScore(
            "Kumori Skin Studio",
            "SDL_app",
            1,
            1,
            hasOwner: false);

        Assert.True(studio > transient);
        Assert.True(studio > ownedDialog);
        Assert.Equal(0, zeroSized);
    }

    [Theory]
    [InlineData(96, 960, 540)]
    [InlineData(120, 1200, 675)]
    [InlineData(144, 1440, 810)]
    [InlineData(192, 1920, 1080)]
    public void EmbeddedClientScalesAtEveryReleaseGateDpi(
        uint dpi,
        int expectedWidth,
        int expectedHeight)
    {
        var size = SkinStudioEmbeddedHost.PixelClientSize(
            960,
            540,
            dpi);

        Assert.Equal(expectedWidth, size.Width);
        Assert.Equal(expectedHeight, size.Height);
    }

    [Theory]
    [InlineData(0x0005, true)]
    [InlineData(0x02E0, true)]
    [InlineData(0x000F, false)]
    public void SizeAndDpiMessagesTriggerChildResize(
        int message,
        bool expected)
    {
        Assert.Equal(
            expected,
            SkinStudioEmbeddedHost.ShouldResizeForMessage(message));
    }

    [Theory]
    [InlineData(
        "{\"status\":\"embedded_ready\",\"session\":\"abc123\"}",
        "abc123",
        true)]
    [InlineData(
        "{\"status\":\"embedded_ready\",\"session\":\"other\"}",
        "abc123",
        false)]
    [InlineData(
        "{\"status\":\"starting\",\"session\":\"abc123\"}",
        "abc123",
        false)]
    [InlineData("not json", "abc123", false)]
    [InlineData("", "abc123", false)]
    public void EmbeddedReadyHandshakeRequiresMatchingSession(
        string message,
        string session,
        bool expected)
    {
        Assert.Equal(
            expected,
            SkinStudioEmbeddedHost.IsEmbeddedReadyMessage(
                message,
                session));
    }
}
