using Kumori.App.Skins;
using Xunit;

namespace Kumori.App.Tests;

public sealed class SkinExtrasSyncPresentationTests
{
    [Fact]
    public void Catalog_reload_keeps_navigation_changed_while_the_scan_was_running()
    {
        var selection = SkinExtrasPickerWindow.ResolveReloadSelection(
            familyAtStart: "osu.cursor",
            packAtStart: @"Extras\Cursor A",
            currentFamily: "osu.hitcircles",
            currentPack: @"Extras\Hitcircle B",
            explicitlyPreferredPack: null);

        Assert.Equal("osu.hitcircles", selection.FamilyId);
        Assert.Equal(@"Extras\Hitcircle B", selection.PackPath);
    }

    [Theory]
    [InlineData((int)SkinExtrasSyncStage.Checking, true, true, false)]
    [InlineData((int)SkinExtrasSyncStage.Planning, true, true, false)]
    [InlineData((int)SkinExtrasSyncStage.Downloading, true, true, true)]
    [InlineData((int)SkinExtrasSyncStage.Installing, true, true, true)]
    [InlineData((int)SkinExtrasSyncStage.Downloading, false, true, false)]
    [InlineData((int)SkinExtrasSyncStage.Installing, false, true, false)]
    [InlineData((int)SkinExtrasSyncStage.UpToDate, true, false, false)]
    [InlineData((int)SkinExtrasSyncStage.Offline, true, false, false)]
    [InlineData((int)SkinExtrasSyncStage.Paused, true, false, false)]
    [InlineData((int)SkinExtrasSyncStage.Failed, true, false, false)]
    public void Only_manual_download_and_install_are_presented_in_the_foreground(
        int stageValue,
        bool manual,
        bool synchronizationRunning,
        bool foreground)
    {
        var stage = (SkinExtrasSyncStage)stageValue;
        Assert.Equal(
            synchronizationRunning,
            SkinExtrasPickerWindow.IsSynchronizationRunningStage(stage));
        Assert.Equal(
            foreground,
            SkinExtrasPickerWindow.ShouldPresentSyncInForeground(
                new SkinExtrasSyncProgress(stage, "", IsManual: manual)));
    }
}
