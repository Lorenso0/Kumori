using osu.Framework.Configuration;
using osu.Framework.Graphics;
using osu.Framework.Platform;

namespace Kumori.ReplayViewer;

public enum KumoriViewerSetting
{
    ShowMissMarkers,
    ShowMehMarkers,
    ShowOkMarkers,
    ShowSliderBreakMarkers,
    SnakingInSliders,
    SnakingOutSliders,
    HitAnimations,
    DisableHidden,
    ContractSettingsSeeded,
    SkinPath,
    SkinId,
    MissAnalyzerIdealPathEnabled,
    MissAnalyzerLoopBefore,
    MissAnalyzerLoopAfter,
    MissAnalyzerPlaybackRate,
    MissAnalyzerLoopEnabled,
    MissAnalyzerShowInputMarkers,
    MissAnalyzerShowMovementSamples,
    MissAnalyzerShowHeldSamples,
    MissAnalyzerShowSelectedClickMarker,
    MissAnalyzerRecolourSelectedNote,
    MissAnalyzerShowSelectedNoteIndicator,
    MissAnalyzerSelectedNoteColour,
    MissAnalyzerDefaultsVersion,
    BackgroundOpacity,
    MasterVolume,
    MusicVolume,
    HitsoundVolume,
    AudioSettingsSeeded,
    ComparisonReplayCursorColour,
    ComparisonReplayCursorTrailColour,
}

/// <summary>
/// Viewer-local persistent settings, stored as kumori-viewer.ini in the
/// host's data directory (%AppData%\Kumori.ReplayViewer). Separate from both
/// lazer's game.ini and the Kumori app's settings.json: these are knobs the
/// user flips inside the viewer window itself.
/// </summary>
public class KumoriViewerConfig : IniConfigManager<KumoriViewerSetting>
{
    protected override string Filename => @"kumori-viewer.ini";

    public KumoriViewerConfig(Storage storage)
        : base(storage)
    {
        int defaultsVersion = Get<int>(KumoriViewerSetting.MissAnalyzerDefaultsVersion);
        if (defaultsVersion < 1)
        {
            SetValue(KumoriViewerSetting.MissAnalyzerLoopBefore, 800.0);
        }
        if (defaultsVersion < 2)
        {
            SetValue(KumoriViewerSetting.MissAnalyzerDefaultsVersion, 2);
            Save();
        }
    }

    protected override void InitialiseDefaults()
    {
        SetDefault(KumoriViewerSetting.ShowMissMarkers, true);
        SetDefault(KumoriViewerSetting.ShowMehMarkers, false);
        SetDefault(KumoriViewerSetting.ShowOkMarkers, false);
        SetDefault(KumoriViewerSetting.ShowSliderBreakMarkers, true);
        SetDefault(KumoriViewerSetting.SnakingInSliders, true);
        SetDefault(KumoriViewerSetting.SnakingOutSliders, true);
        SetDefault(KumoriViewerSetting.HitAnimations, true);
        SetDefault(KumoriViewerSetting.DisableHidden, false);
        SetDefault(KumoriViewerSetting.ContractSettingsSeeded, false);
        SetDefault(KumoriViewerSetting.SkinPath, string.Empty);
        SetDefault(KumoriViewerSetting.SkinId, string.Empty);
        SetDefault(KumoriViewerSetting.MissAnalyzerIdealPathEnabled, true);
        SetDefault(KumoriViewerSetting.MissAnalyzerLoopBefore, 800.0, 150.0, 2000.0, 50.0);
        SetDefault(KumoriViewerSetting.MissAnalyzerLoopAfter, 450.0, 150.0, 2000.0, 50.0);
        SetDefault(KumoriViewerSetting.MissAnalyzerPlaybackRate, 0.5, 0.05, 2.0, 0.01);
        SetDefault(KumoriViewerSetting.MissAnalyzerLoopEnabled, true);
        SetDefault(KumoriViewerSetting.MissAnalyzerShowInputMarkers, true);
        SetDefault(KumoriViewerSetting.MissAnalyzerShowMovementSamples, true);
        SetDefault(KumoriViewerSetting.MissAnalyzerShowHeldSamples, true);
        SetDefault(KumoriViewerSetting.MissAnalyzerShowSelectedClickMarker, true);
        SetDefault(KumoriViewerSetting.MissAnalyzerRecolourSelectedNote, true);
        SetDefault(KumoriViewerSetting.MissAnalyzerShowSelectedNoteIndicator, true);
        SetDefault(KumoriViewerSetting.MissAnalyzerSelectedNoteColour, Colour4.FromHex("#8b5cf6"));
        SetDefault(KumoriViewerSetting.MissAnalyzerDefaultsVersion, 0);
        SetDefault(KumoriViewerSetting.BackgroundOpacity, 0.0, 0, 1, 0.01);
        SetDefault(KumoriViewerSetting.MasterVolume, 1.0, 0, 1, 0.01);
        SetDefault(KumoriViewerSetting.MusicVolume, 1.0, 0, 1, 0.01);
        SetDefault(KumoriViewerSetting.HitsoundVolume, 1.0, 0, 1, 0.01);
        SetDefault(KumoriViewerSetting.AudioSettingsSeeded, false);
        SetDefault(KumoriViewerSetting.ComparisonReplayCursorColour, Colour4.FromHex("#ff4fa3"));
        SetDefault(KumoriViewerSetting.ComparisonReplayCursorTrailColour, Colour4.FromHex("#ff4fa3"));
    }
}
