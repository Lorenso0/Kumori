using Kumori.ReplayViewer;
using Xunit;

namespace Kumori.Core.Tests;

public sealed class WindowsReplayFilePickerTests
{
    [Fact]
    public void NativeDialogLayoutIsMarshalable()
    {
        // OPENFILENAMEW is 152 bytes in the viewer's win-x64 process. This
        // catches managed fields such as StringBuilder before the button is
        // exercised interactively.
        Assert.Equal(152, WindowsReplayFilePicker.NativeDialogStructureSize);
    }
}
