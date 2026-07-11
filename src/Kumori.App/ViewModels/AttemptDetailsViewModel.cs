using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kumori.App.Controls;
using Kumori.Core.Models;
using Kumori.Storage;
using Kumori.Tracking;
using Serilog;
using static System.FormattableString;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace Kumori.App.ViewModels;

/// <summary>
/// Inspector state. Loads details off the UI thread when the selection
/// changes, cancels superseded loads, and caches models per attempt id.
/// </summary>
public partial class AttemptDetailsViewModel : ObservableObject
{
    private readonly AttemptDetailsRepository _repository;
    private readonly ReplayViewerContractService? _replayViewer;
    private readonly Dictionary<long, AttemptDetails> _cache = new();
    private readonly Dictionary<long, IReadOnlyList<PressurePoint>> _curveCache = new();
    private readonly Dictionary<string, double> _originalStarCache = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _loadCts;
    private long? _requestedAttemptId;
    private double? _calculatedOriginalStars;

    [ObservableProperty]
    private IReadOnlyList<PressurePoint> _pressureCurve = Array.Empty<PressurePoint>();

    // Captured difficulty with configurable-mod overrides applied, rebuilt on
    // each selection. Keyed by "ar"/"cs"/"od"/"hp"/"stars"/"bpm".
    private Dictionary<string, DifficultyPair> _adjustedDifficulty = new();

    [ObservableProperty]
    private AttemptDetails? _details;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _loadError;

    [ObservableProperty]
    private bool _showMiss = true;

    [ObservableProperty]
    private bool _showBreak = true;

    [ObservableProperty]
    private bool _showUr = true;

    public Process? LastReplayInspectorProcess { get; private set; }

    public AttemptDetailsViewModel(
        AttemptDetailsRepository repository,
        ReplayViewerContractService? replayViewer = null)
    {
        _repository = repository;
        _replayViewer = replayViewer;
    }

    public string TitleLine => Details is { } d ? $"{d.Summary.Artist} — {d.Summary.Title}" : "";
    public string SubtitleLine => Details is { } d
        ? $"[{(string.IsNullOrEmpty(d.Summary.Difficulty) ? "Unknown" : d.Summary.Difficulty)}]  ·  mapped by {(string.IsNullOrEmpty(d.Mapper) ? "Unknown" : d.Mapper)}"
        : "";
    public string StartedLine => Details is { } d ? FormatStarted(d.Summary.StartedAt) : "";
    public string Grade => Details?.Summary.Grade ?? "-";
    public double GradeProgress => Details is { } d ? Math.Clamp(d.Summary.Accuracy / 100d, 0d, 1d) : 0d;
    public string GradeColor => Grade switch
    {
        "X" or "XH" or "SS" => "#DE31AE",
        "S" or "SH" => "#02B5C3",
        "A" => "#88DA20",
        "B" => "#E3B130",
        "C" => "#FF8E5D",
        "D" => "#FF5A5A",
        "F" => "#3F3F3F",
        _ => "#3F3F3F",
    };
    public string GradeTextColor => "#FFFFFF";
    public string OutcomeUpper => Details?.Summary.Outcome.ToUpperInvariant() ?? "";
    // osu!'s in-game HUD truncates accuracy to two decimal places rather than
    // rounding it. Mirror that behavior so the inspector agrees with the HUD.
    public string AccuracyValue => Details is { } d
        ? Invariant($"{Math.Truncate(d.Summary.Accuracy * 100d) / 100d:0.00}%")
        : "";
    public string ScoreValue => Details is { } d ? d.Summary.Score.ToString("N0", CultureInfo.InvariantCulture) : "";
    public string ComboValue => Details is { } d ? Invariant($"{d.Summary.Combo:N0} / {d.BeatmapMaxCombo:N0}") : "";
    public string PpValue => Details is { } d ? Invariant($"{d.Summary.Pp:0.0}  ({d.FcPp:0.0} FC)") : "";
    public string ProgressValue => Details is { } d ? d.Summary.Progress.ToString("P1", CultureInfo.InvariantCulture) : "";
    public string MissValue => Details is { } d ? $"{d.Summary.Misses}" : "";
    public string StarsValue => Details?.Summary.Stars is { } s ? Invariant($"{s:0.0}*") : "-";

    // ── Mod-settings badge summary (ports _mod_settings_summary) ──
    public string ModSettingsSummary => Details is { } d ? BuildModSettingsSummary(d.Mods) : "";

    // ── Difficulty grid: effective value with base in parentheses ──
    public string StarsDisplay
    {
        get
        {
            if (Details is not { } d)
            {
                return "—";
            }
            var pair = _adjustedDifficulty.TryGetValue("stars", out var s) ? s : default;
            double? original = pair.Original ?? _calculatedOriginalStars ?? d.BaseStars;
            double? adjusted = pair.Converted ?? d.AdjustedStars ?? d.Summary.Stars;
            return DifficultyDisplay(original, adjusted, decimals: 2, suffix: "★");
        }
    }
    public string StarsNumberDisplay
    {
        get
        {
            if (Details is not { } d)
            {
                return "—";
            }
            var pair = _adjustedDifficulty.TryGetValue("stars", out var s) ? s : default;
            double? original = pair.Original ?? _calculatedOriginalStars ?? d.BaseStars;
            double? adjusted = pair.Converted ?? d.AdjustedStars ?? d.Summary.Stars;
            return DifficultyDisplay(original, adjusted, decimals: 2);
        }
    }
    public string ArDisplay => DifficultyFor("ar", Details?.BeatmapAr);
    public string CsDisplay => DifficultyFor("cs", Details?.BeatmapCs);
    public string OdDisplay => DifficultyFor("od", Details?.BeatmapOd);
    public string HpDisplay => DifficultyFor("hp", Details?.BeatmapHp);
    public string BpmDisplay => DifficultyFor("bpm", Details?.Bpm);

    // ── Judgement counts ──
    public string N300Text => Details is { } d ? d.N300.ToString("N0", CultureInfo.InvariantCulture) : "";
    public string N100Text => Details is { } d ? d.N100.ToString("N0", CultureInfo.InvariantCulture) : "";
    public string N50Text => Details is { } d ? d.N50.ToString("N0", CultureInfo.InvariantCulture) : "";
    public string MissCountText => Details is { } d ? d.Summary.Misses.ToString("N0", CultureInfo.InvariantCulture) : "";
    public string LargeTickText => Details is { } d ? HitTotal(d.LargeTickHits, d.LargeTickMisses) : "";
    public string SmallTickText => Details is { } d ? HitTotal(d.SmallTickHits, d.SmallTickMisses) : "";
    public string SliderTailText => Details is { } d ? HitTotal(d.SliderTailHits, d.SliderTailMisses) : "";
    public string SliderBreakText => Details is { } d ? d.SliderBreaks.ToString("N0", CultureInfo.InvariantCulture) : "";

    // ── Sliders · keys line ──
    public string SlidersLine
    {
        get
        {
            if (Details is not { } d)
            {
                return "";
            }
            var tickHit = d.LargeTickHits + d.SmallTickHits;
            var tickTotal = tickHit + d.LargeTickMisses + d.SmallTickMisses;
            var tailHit = d.SliderTailHits;
            var tailTotal = tailHit + d.SliderTailMisses;
            return Invariant($"Ticks {tickHit:N0}/{tickTotal:N0}   ·   Ends {tailHit:N0}/{tailTotal:N0}   ·   Breaks {d.SliderBreaks:N0}   ·   K1 {d.Key1Count:N0}   ·   K2 {d.Key2Count:N0}");
        }
    }

    // ── Movement / tablet quality line ──
    public string TechnicalDetailsLine
    {
        get
        {
            var baseLine = TechnicalInputLine;
            if (Details?.Movement is not { Available: true } m)
            {
                return string.IsNullOrWhiteSpace(baseLine)
                    ? "Source none"
                    : $"{baseLine}   -   Source none";
            }

            var rawSource = string.IsNullOrWhiteSpace(m.Source) ? "live" : m.Source;
            return Invariant($"{baseLine}   -   Source {SourceLabel(m.Source)} ({rawSource})   -   {m.SampleCount:N0} samples   -   {m.SampleRate:F0} Hz   -   {m.DroppedSamples:N0} dropped");
        }
    }

    public string TechnicalInputLine
    {
        get
        {
            if (Details is not { } d)
            {
                return "";
            }
            var input = d.Input;
            var key1 = input?.Key1Presses ?? d.Key1Count;
            var key2 = input?.Key2Presses ?? d.Key2Count;
            return Invariant($"K1 {key1:N0} presses   -   K2 {key2:N0} presses");
        }
    }

    public string TechnicalInputStatsLine => Details?.Input is { } i
        ? Invariant($"Alternations {i.Alternations:N0}   -   simultaneous {i.SimultaneousPresses:N0}   -   peak {i.PeakKps:N0} KPS   -   avg {i.AverageKps:0.0} KPS")
        : "Input summary was not captured.";

    public string TechnicalHoldLine => Details?.Input is { } i
        ? Invariant($"Hold time: K1 {i.Key1HoldMs:N0}ms   -   K2 {i.Key2HoldMs:N0}ms")
        : "Hold-time detail unavailable.";

    public string TechnicalRecordingLine => Details?.Movement is { Available: true } m
        ? Invariant($"Replay recording source: {SourceLabel(m.Source)} ({(string.IsNullOrWhiteSpace(m.Source) ? "live" : m.Source)})")
        : "Replay recording source: none";

    public string TechnicalRecordingStatsLine => Details?.Movement is { Available: true } m
        ? Invariant($"Recording samples {m.SampleCount:N0}   -   sample rate {m.SampleRate:F0} Hz   -   dropped {m.DroppedSamples:N0}")
        : "No cursor movement samples were stored for this attempt.";

    public string TechnicalTimingStatsLine => Details?.Timing is { } t
        ? Invariant($"Timing samples {t.HitCount:N0}   -   mean {t.Mean:+0.0;-0.0;+0.0}ms   -   median {t.Median:+0.0;-0.0;+0.0}ms   -   deviation {t.Deviation:0.0}ms")
        : "Timing summary unavailable.";

    public string TechnicalJudgementLine => Details is { } d
        ? Invariant($"Judgement events {d.Events.Count:N0}   -   misses {d.Events.Count(e => e.EventType == "miss"):N0}   -   slider breaks {d.Events.Count(e => e.EventType == "slider_break"):N0}")
        : "";

    public string TechnicalSliderLine => Details is { } d
        ? SlidersLine
        : "";

    public string TechnicalMapLine => Details is { } d
        ? Invariant($"Duration {TimeSpan.FromSeconds(d.DurationSeconds):mm\\:ss}   -   progress {d.Summary.Progress:P1}   -   termination {(string.IsNullOrWhiteSpace(d.TerminationEvidence) ? "unknown" : d.TerminationEvidence)}")
        : "";

    public string TechnicalAttemptNumber => Details is { } d ? Invariant($"Attempt #{d.Summary.Id:N0}") : "";
    public string TechnicalSessionNumber => Details is { Summary.SessionId: > 0 } d ? Invariant($"Session #{d.Summary.SessionId:N0}") : "";
    public string TechnicalBeatmapId => Details is { Summary.OsuBeatmapId: > 0 } d ? Invariant($"{d.Summary.OsuBeatmapId:N0}") : "-";
    public string TechnicalChecksum => Details?.Summary.Checksum is { Length: > 8 } checksum ? checksum[..8] : Details?.Summary.Checksum ?? "-";

    public string TechnicalKey1Presses => Details is { } d
        ? Invariant($"{(d.Input?.Key1Presses ?? d.Key1Count):N0}")
        : "-";

    public string TechnicalKey2Presses => Details is { } d
        ? Invariant($"{(d.Input?.Key2Presses ?? d.Key2Count):N0}")
        : "-";

    public string TechnicalAlternations => Details?.Input is { } i ? Invariant($"{i.Alternations:N0}") : "-";
    public string TechnicalSimultaneous => Details?.Input is { } i ? Invariant($"{i.SimultaneousPresses:N0}") : "-";
    public string TechnicalPeakKps => Details?.Input is { } i ? Invariant($"{i.PeakKps:N0} KPS") : "-";
    public string TechnicalAverageKps => Details?.Input is { } i ? Invariant($"{i.AverageKps:0.0} KPS") : "-";
    public string TechnicalKey1Hold => Details?.Input is { } i ? Invariant($"{i.Key1HoldMs:N0} ms") : "-";
    public string TechnicalKey2Hold => Details?.Input is { } i ? Invariant($"{i.Key2HoldMs:N0} ms") : "-";

    public string TechnicalRecordingSource => Details?.Movement is { Available: true } m ? SourceLabel(m.Source) : "none";
    public string TechnicalRecordingSamples => Details?.Movement is { Available: true } m ? Invariant($"{m.SampleCount:N0}") : "-";
    public string TechnicalSampleRate => Details?.Movement is { Available: true } m ? Invariant($"{m.SampleRate:F0} Hz") : "-";
    public string TechnicalDroppedSamples => Details?.Movement is { Available: true } m ? Invariant($"{m.DroppedSamples:N0}") : "-";
    public string TechnicalTimingSamples => Details?.Timing is { } t ? Invariant($"{t.HitCount:N0}") : "-";
    public string TechnicalMeanTiming => Details?.Timing is { } t ? Invariant($"{t.Mean:+0.0;-0.0;+0.0} ms") : "-";
    public string TechnicalMedianTiming => Details?.Timing is { } t ? Invariant($"{t.Median:+0.0;-0.0;+0.0} ms") : "-";
    public string TechnicalDeviationTiming => Details?.Timing is { } t ? Invariant($"{t.Deviation:0.0} ms") : "-";

    public string TechnicalDuration => Details is { } d ? Invariant($"{TimeSpan.FromSeconds(d.DurationSeconds):mm\\:ss}") : "-";
    public string TechnicalProgress => Details is { } d ? Invariant($"{d.Summary.Progress:P1}") : "-";
    public string TechnicalTermination => Details is { } d
        ? string.IsNullOrWhiteSpace(d.TerminationEvidence) ? "unknown" : d.TerminationEvidence
        : "-";
    public string TechnicalTicks => Details is { } d
        ? Invariant($"{d.LargeTickHits + d.SmallTickHits:N0}/{d.LargeTickHits + d.SmallTickHits + d.LargeTickMisses + d.SmallTickMisses:N0}")
        : "-";
    public string TechnicalEnds => Details is { } d
        ? Invariant($"{d.SliderTailHits:N0}/{d.SliderTailHits + d.SliderTailMisses:N0}")
        : "-";
    public string TechnicalBreaks => Details is { } d ? Invariant($"{d.SliderBreaks:N0}") : "-";
    public string TechnicalJudgementEvents => Details is { } d ? Invariant($"{d.Events.Count:N0}") : "-";
    public string TechnicalMisses => Details is { } d ? Invariant($"{d.Events.Count(e => e.EventType == "miss"):N0}") : "-";
    public string TechnicalSliderBreaks => Details is { } d ? Invariant($"{d.Events.Count(e => e.EventType == "slider_break"):N0}") : "-";

    public bool HasMovement => Details?.Movement is { Available: true };
    public bool NoMovement => Details is not null && !HasMovement;
    public string MovementLine => Details?.Movement is { Available: true } m
        ? Invariant($"{SourceLabel(m.Source)}  ·  {m.SampleCount:N0} samples  ·  {m.SampleRate:F0} Hz  ·  {m.DroppedSamples:N0} dropped")
        : "No cursor movement was captured for this attempt.";
    public Brush MovementBrush => Details?.Movement?.Source == "opentabletdriver+fallback"
        ? WarningBrush : PositiveBrush;

    // ── Hit-timing availability ──
    public bool HasTimingSamples => Details?.Timing is { } t && t.Offsets.Count > 0;
    public bool NoTimingSamples => Details is not null && !HasTimingSamples;
    public string HitLine => Details is { } d
        ? $"{d.N300} / {d.N100} / {d.N50} / {d.Summary.Misses}"
        : "";
    public string UrText => Details is { } d && d.UnstableRate > 0
        ? Invariant($"{d.UnstableRate:0.0}") : "-";
    public string PpLine => Details is { } d
        ? Invariant($"{d.Summary.Pp:0.0}pp  (FC {d.FcPp:0.0} / SS {d.MaxPp:0.0})") : "";
    public string ModsDisplay => Details is { } d ? ModDisplayText.FromKey(d.Summary.ModsKey) : "";
    public string? ArtworkSource => Details is { } d ? BeatmapArtworkResolver.Resolve(d.Summary) : null;
    public IReadOnlyList<double> TimingOffsets => Details?.Timing?.Offsets ?? Array.Empty<double>();
    public string TimingLine => Details?.Timing is { } t
        ? Invariant($"{t.HitCount:N0} hits  ·  mean {t.Mean:+0.0;-0.0;+0.0}ms  ·  early {t.EarlyCount:N0}  ·  late {t.LateCount:N0}")
        : "No timing data";
    public string InputLine => Details?.Input is { } i
        ? Invariant($"K1:{i.Key1Presses} - K2:{i.Key2Presses} - peak {i.PeakKps}kps - avg {i.AverageKps:0.0}kps")
        : "No input data";
    public string ModsLine => Details is { } d && d.Mods.Count > 0
        ? string.Join(" ", d.Mods.Select(m => m.Acronym))
        : "NM";
    public string EventsLine => Details is { } d
        ? $"{d.Events.Count(e => e.EventType == "miss")} miss - {d.Events.Count(e => e.EventType == "slider_break")} breaks - {d.Events.Count} events"
        : "";
    public string DurationLine => Details is { } d
        ? $"{TimeSpan.FromSeconds(d.DurationSeconds):mm\\:ss} - {d.Summary.Progress.ToString("P0", System.Globalization.CultureInfo.InvariantCulture)} of map"
        : "";
    public IReadOnlyList<TimingBarViewModel> TimingBars => BuildTimingBars(Details?.Timing?.Offsets);
    public PointCollection PressurePoints => BuildPressureGraph(Details).Line;
    public PointCollection PressureAreaPoints => BuildPressureGraph(Details).Area;
    public IReadOnlyList<MapMarkerViewModel> MapMarkers => BuildPressureGraph(Details).Markers;
    public string PressureLine => Details is { } d
        ? Invariant($"{MapDuration(d) / 1000.0:0}s map - {d.Events.Count(e => e.MapTimeMs is not null)} timed events")
        : "No map pressure data";
    public string PressureEndText
    {
        get
        {
            if (Details is not { } d)
            {
                return "0:00";
            }
            var seconds = MapDuration(d) / 1000;
            return Invariant($"{seconds / 60}:{seconds % 60:00}");
        }
    }
    public IReadOnlyList<UrPoint> UrSamples => BuildUrSamples(Details);
    public bool CanOpenReplayInspector => Details is not null && _replayViewer is not null;
    public bool CanValidateOsr => Details is not null;

    partial void OnDetailsChanged(AttemptDetails? value)
    {
        _calculatedOriginalStars = null;
        _adjustedDifficulty = value is { } d
            ? ApplyModDifficultySettings(d.Mods, d.CapturedDifficulty)
            : new Dictionary<string, DifficultyPair>();

        OnPropertyChanged(nameof(TitleLine));
        OnPropertyChanged(nameof(SubtitleLine));
        OnPropertyChanged(nameof(StartedLine));
        OnPropertyChanged(nameof(Grade));
        OnPropertyChanged(nameof(GradeProgress));
        OnPropertyChanged(nameof(GradeColor));
        OnPropertyChanged(nameof(GradeTextColor));
        OnPropertyChanged(nameof(OutcomeUpper));
        OnPropertyChanged(nameof(AccuracyValue));
        OnPropertyChanged(nameof(ScoreValue));
        OnPropertyChanged(nameof(ComboValue));
        OnPropertyChanged(nameof(PpValue));
        OnPropertyChanged(nameof(ProgressValue));
        OnPropertyChanged(nameof(MissValue));
        OnPropertyChanged(nameof(StarsValue));
        OnPropertyChanged(nameof(ModSettingsSummary));
        OnPropertyChanged(nameof(StarsDisplay));
        OnPropertyChanged(nameof(StarsNumberDisplay));
        OnPropertyChanged(nameof(ArDisplay));
        OnPropertyChanged(nameof(CsDisplay));
        OnPropertyChanged(nameof(OdDisplay));
        OnPropertyChanged(nameof(HpDisplay));
        OnPropertyChanged(nameof(BpmDisplay));
        OnPropertyChanged(nameof(N300Text));
        OnPropertyChanged(nameof(N100Text));
        OnPropertyChanged(nameof(N50Text));
        OnPropertyChanged(nameof(MissCountText));
        OnPropertyChanged(nameof(LargeTickText));
        OnPropertyChanged(nameof(SmallTickText));
        OnPropertyChanged(nameof(SliderTailText));
        OnPropertyChanged(nameof(SliderBreakText));
        OnPropertyChanged(nameof(SlidersLine));
        OnPropertyChanged(nameof(TechnicalDetailsLine));
        OnPropertyChanged(nameof(TechnicalInputLine));
        OnPropertyChanged(nameof(TechnicalInputStatsLine));
        OnPropertyChanged(nameof(TechnicalHoldLine));
        OnPropertyChanged(nameof(TechnicalRecordingLine));
        OnPropertyChanged(nameof(TechnicalRecordingStatsLine));
        OnPropertyChanged(nameof(TechnicalTimingStatsLine));
        OnPropertyChanged(nameof(TechnicalJudgementLine));
        OnPropertyChanged(nameof(TechnicalSliderLine));
        OnPropertyChanged(nameof(TechnicalMapLine));
        OnPropertyChanged(nameof(TechnicalAttemptNumber));
        OnPropertyChanged(nameof(TechnicalSessionNumber));
        OnPropertyChanged(nameof(TechnicalBeatmapId));
        OnPropertyChanged(nameof(TechnicalChecksum));
        OnPropertyChanged(nameof(TechnicalKey1Presses));
        OnPropertyChanged(nameof(TechnicalKey2Presses));
        OnPropertyChanged(nameof(TechnicalAlternations));
        OnPropertyChanged(nameof(TechnicalSimultaneous));
        OnPropertyChanged(nameof(TechnicalPeakKps));
        OnPropertyChanged(nameof(TechnicalAverageKps));
        OnPropertyChanged(nameof(TechnicalKey1Hold));
        OnPropertyChanged(nameof(TechnicalKey2Hold));
        OnPropertyChanged(nameof(TechnicalRecordingSource));
        OnPropertyChanged(nameof(TechnicalRecordingSamples));
        OnPropertyChanged(nameof(TechnicalSampleRate));
        OnPropertyChanged(nameof(TechnicalDroppedSamples));
        OnPropertyChanged(nameof(TechnicalTimingSamples));
        OnPropertyChanged(nameof(TechnicalMeanTiming));
        OnPropertyChanged(nameof(TechnicalMedianTiming));
        OnPropertyChanged(nameof(TechnicalDeviationTiming));
        OnPropertyChanged(nameof(TechnicalDuration));
        OnPropertyChanged(nameof(TechnicalProgress));
        OnPropertyChanged(nameof(TechnicalTermination));
        OnPropertyChanged(nameof(TechnicalTicks));
        OnPropertyChanged(nameof(TechnicalEnds));
        OnPropertyChanged(nameof(TechnicalBreaks));
        OnPropertyChanged(nameof(TechnicalJudgementEvents));
        OnPropertyChanged(nameof(TechnicalMisses));
        OnPropertyChanged(nameof(TechnicalSliderBreaks));
        OnPropertyChanged(nameof(HasMovement));
        OnPropertyChanged(nameof(NoMovement));
        OnPropertyChanged(nameof(MovementLine));
        OnPropertyChanged(nameof(MovementBrush));
        OnPropertyChanged(nameof(HasTimingSamples));
        OnPropertyChanged(nameof(NoTimingSamples));
        OnPropertyChanged(nameof(HitLine));
        OnPropertyChanged(nameof(UrText));
        OnPropertyChanged(nameof(PpLine));
        OnPropertyChanged(nameof(ModsDisplay));
        OnPropertyChanged(nameof(ArtworkSource));
        OnPropertyChanged(nameof(TimingLine));
        OnPropertyChanged(nameof(TimingOffsets));
        OnPropertyChanged(nameof(InputLine));
        OnPropertyChanged(nameof(ModsLine));
        OnPropertyChanged(nameof(EventsLine));
        OnPropertyChanged(nameof(DurationLine));
        OnPropertyChanged(nameof(TimingBars));
        OnPropertyChanged(nameof(PressurePoints));
        OnPropertyChanged(nameof(PressureAreaPoints));
        OnPropertyChanged(nameof(MapMarkers));
        OnPropertyChanged(nameof(PressureLine));
        OnPropertyChanged(nameof(PressureEndText));
        OnPropertyChanged(nameof(UrSamples));
        OnPropertyChanged(nameof(CanOpenReplayInspector));
        OnPropertyChanged(nameof(CanValidateOsr));
        OpenReplayInspectorCommand.NotifyCanExecuteChanged();
        ValidateOsrCommand.NotifyCanExecuteChanged();

        PressureCurve = Array.Empty<PressurePoint>();
        _ = LoadPressureCurveAsync(value);
        _ = LoadOriginalStarsAsync(value);
    }

    private async Task LoadOriginalStarsAsync(AttemptDetails? details)
    {
        if (details is null || details.CapturedDifficulty.TryGetValue("stars", out var stars) && stars.Original is not null)
        {
            return;
        }

        var path = BeatmapArtworkResolver.ResolveBeatmapFile(details.Summary);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (!_originalStarCache.TryGetValue(path, out var original))
            {
                original = await Task.Run(() => BeatmapStarRatingCalculator.CalculateOriginal(path));
                _originalStarCache[path] = original;
            }

            if (ReferenceEquals(Details, details))
            {
                _calculatedOriginalStars = original;
                OnPropertyChanged(nameof(StarsDisplay));
                OnPropertyChanged(nameof(StarsNumberDisplay));
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Original star calculation failed for attempt {AttemptId}", details.Summary.Id);
        }
    }

    private async Task LoadPressureCurveAsync(AttemptDetails? details)
    {
        if (details is null)
        {
            return;
        }
        var id = details.Summary.Id;
        if (_curveCache.TryGetValue(id, out var cached))
        {
            if (ReferenceEquals(Details, details))
            {
                PressureCurve = cached;
            }
            return;
        }
        var path = BeatmapArtworkResolver.ResolveBeatmapFile(details.Summary);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }
        try
        {
            var curve = await Task.Run(() => MapPressureGraph.BuildDifficultyCurve(path, details.Mods));
            _curveCache[id] = curve;
            if (ReferenceEquals(Details, details))
            {
                PressureCurve = curve;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Map pressure curve build failed for attempt {AttemptId}", id);
        }
    }

    private static IReadOnlyList<UrPoint> BuildUrSamples(AttemptDetails? details)
    {
        if (details is null)
        {
            return Array.Empty<UrPoint>();
        }
        var list = new List<UrPoint>();
        foreach (var e in details.Events)
        {
            if (e.EventType != "checkpoint" || e.MapTimeMs is null)
            {
                continue;
            }
            try
            {
                using var doc = JsonDocument.Parse(e.DataJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("unstable_rate", out var urElement)
                    && TryNumber(urElement) is { } ur && ur > 0)
                {
                    list.Add(new UrPoint((int)Math.Max(0, e.MapTimeMs.Value), ur));
                }
            }
            catch (JsonException ex)
            {
                Log.Debug(ex, "Skipping malformed checkpoint payload for attempt {AttemptId}", details.Summary.Id);
            }
        }
        list.Sort((a, b) => a.TimeMs.CompareTo(b.TimeMs));
        return list;
    }

    [RelayCommand(CanExecute = nameof(CanOpenReplayInspector))]
    private async Task OpenReplayInspector()
    {
        LastReplayInspectorProcess = null;
        if (Details is not { } details || _replayViewer is null)
        {
            return;
        }

        try
        {
            var lazer = LazerStorage.ResolveBeatmapAssets(details.Summary.OsuBeatmapId, details.Summary.BeatmapSetId, details.Summary.Difficulty);
            var beatmapPath = lazer?.BeatmapPath ?? BeatmapArtworkResolver.ResolveBeatmapFile(details.Summary);
            if (string.IsNullOrWhiteSpace(beatmapPath))
            {
                LoadError = "Replay Analyzer needs cached beatmap media for this play";
                return;
            }

            var mediaDirectory = BeatmapArtworkResolver.ResolveMediaDirectory(details.Summary);
            var contract = await Task.Run(() => _replayViewer.WriteContract(
                details.Summary.Id,
                beatmapPath,
                mediaDirectory,
                lazer?.Files));
            await _replayViewer.PrepareAnalysisAsync(contract);
            LastReplayInspectorProcess = _replayViewer.LaunchViewer(contract);
            LoadError = null;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Replay Analyzer launch failed for attempt {AttemptId}", details.Summary.Id);
            LoadError = ex.Message;
        }
    }

    [RelayCommand(CanExecute = nameof(CanValidateOsr))]
    private async Task ValidateOsr()
    {
        if (Details is not { } details)
        {
            return;
        }

        var picker = new OpenFileDialog
        {
            Title = "Select the .osr replay for this play",
            Filter = "osu! replay (*.osr)|*.osr",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (picker.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var beatmapPath = BeatmapArtworkResolver.ResolveBeatmapFile(details.Summary);
            if (string.IsNullOrWhiteSpace(beatmapPath))
            {
                LoadError = "Replay validation needs the cached .osu beatmap file for this play.";
                return;
            }
            var capturedSamples = _replayViewer?.GetMovementSamples(details.Summary.Id) ?? Array.Empty<MovementSample>();
            var result = await Task.Run(() => OsrValidationService.Validate(picker.FileName, details, beatmapPath, capturedSamples));
            new OsrValidationWindow(result) { Owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) }.ShowDialog();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or EndOfStreamException)
        {
            Log.Warning(ex, "Could not validate .osr for attempt {AttemptId}", details.Summary.Id);
            KumoriDialog.Show(
                Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive),
                $"Kumori could not read that .osr file.\n\n{ex.Message}",
                ".osr validation", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    public async Task LoadAsync(long? attemptId)
    {
        _requestedAttemptId = attemptId;
        _loadCts?.Cancel();
        if (attemptId is null)
        {
            Details = null;
            IsLoading = false;
            LoadError = null;
            return;
        }
        if (_cache.TryGetValue(attemptId.Value, out var cached))
        {
            if (_requestedAttemptId != attemptId)
            {
                return;
            }
            Details = cached;
            IsLoading = false;
            LoadError = null;
            return;
        }

        // Do not keep the last play visible while the next one is loading.
        // Apart from being misleading, this made a newly highlighted history row
        // appear to have the previous row's title and score in the inspector.
        Details = null;
        var cts = new CancellationTokenSource();
        _loadCts = cts;
        IsLoading = true;
        LoadError = null;
        try
        {
            var loaded = await Task.Run(() => _repository.GetDetails(attemptId.Value), cts.Token);
            if (cts.IsCancellationRequested || _requestedAttemptId != attemptId)
            {
                return;
            }
            if (loaded is not null)
            {
                _cache[attemptId.Value] = loaded;
            }
            Details = loaded;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Detail load failed for attempt {AttemptId}", attemptId);
            if (!cts.IsCancellationRequested)
            {
                LoadError = "Could not load details (see logs)";
            }
        }
        finally
        {
            if (ReferenceEquals(_loadCts, cts))
            {
                IsLoading = false;
            }
        }
    }

    /// <summary>Removes deleted attempts from the inspector and its detail cache.</summary>
    public void ForgetAttempt(long attemptId)
    {
        _cache.Remove(attemptId);
        _curveCache.Remove(attemptId);
        if (Details?.Summary.Id == attemptId || _requestedAttemptId == attemptId)
        {
            _requestedAttemptId = null;
            _loadCts?.Cancel();
            Details = null;
            IsLoading = false;
            LoadError = null;
        }
    }

    private static readonly Brush PositiveBrush = FrozenBrush("#4ade80");
    private static readonly Brush WarningBrush = FrozenBrush("#f59e0b");

    private static readonly Dictionary<string, string> SettingAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["approach_rate"] = "AR", ["overall_difficulty"] = "OD",
        ["circle_size"] = "CS", ["drain_rate"] = "HP",
        ["ar"] = "AR", ["od"] = "OD", ["cs"] = "CS", ["hp"] = "HP",
        ["speed_change"] = "Speed", ["adjust_pitch"] = "Pitch", ["pitch_adjust"] = "Pitch",
        ["minimum_accuracy"] = "Min acc", ["accuracy_judge_mode"] = "Accuracy mode",
        ["restart"] = "Restart", ["use_classic_notelock"] = "Classic notelock",
        ["no_slider_head_accuracy"] = "No slider-head acc",
    };

    private static Brush FrozenBrush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
        brush.Freeze();
        return brush;
    }

    private string DifficultyFor(string name, double? fallback)
    {
        if (Details is null)
        {
            return "—";
        }
        if (_adjustedDifficulty.TryGetValue(name, out var pair)
            && (pair.Original is not null || pair.Converted is not null))
        {
            return DifficultyDisplay(pair.Original, pair.Converted);
        }
        return DifficultyDisplay(null, fallback);
    }

    /// <summary>Ports osu_tracking._difficulty_display.</summary>
    private static string DifficultyDisplay(double? original, double? adjusted, int decimals = 1, string suffix = "")
    {
        var value = adjusted ?? original;
        if (value is null)
        {
            return "—";
        }
        static string Render(double number, int dec)
        {
            var text = number.ToString("F" + dec, CultureInfo.InvariantCulture);
            return text.Contains('.') ? text.TrimEnd('0').TrimEnd('.') : text;
        }
        if (original is { } o && adjusted is { } a && Math.Abs(o - a) > 0.001)
        {
            return $"{Render(a, decimals)} ({Render(o, decimals)}){suffix}";
        }
        return $"{Render(value.Value, decimals)}{suffix}";
    }

    /// <summary>Ports osu_tracking._apply_mod_difficulty_settings.</summary>
    private static Dictionary<string, DifficultyPair> ApplyModDifficultySettings(
        IReadOnlyList<ModEntry> mods, IReadOnlyDictionary<string, DifficultyPair> captured)
    {
        var result = new Dictionary<string, DifficultyPair>();
        foreach (var entry in captured)
        {
            result[entry.Key] = entry.Value;
        }
        var statKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["approach_rate"] = "ar", ["ar"] = "ar",
            ["circle_size"] = "cs", ["cs"] = "cs",
            ["overall_difficulty"] = "od", ["od"] = "od",
            ["drain_rate"] = "hp", ["hp"] = "hp",
        };
        foreach (var mod in mods)
        {
            foreach (var setting in ParseSettings(mod.SettingsJson))
            {
                var number = TryNumber(setting.Value);
                if (statKeys.TryGetValue(setting.Key, out var stat) && number is { } v)
                {
                    var original = result.TryGetValue(stat, out var pair) ? pair.Original : null;
                    result[stat] = new DifficultyPair(original, v);
                }
                if (string.Equals(setting.Key, "speed_change", StringComparison.OrdinalIgnoreCase)
                    && number is { } speed)
                {
                    var original = result.TryGetValue("bpm", out var bpmPair) ? bpmPair.Original : null;
                    if (original is { } baseBpm)
                    {
                        result["bpm"] = new DifficultyPair(baseBpm, baseBpm * speed);
                    }
                }
            }
        }
        return result;
    }

    /// <summary>Ports osu_tracking._mod_settings_summary.</summary>
    private static string BuildModSettingsSummary(IReadOnlyList<ModEntry> mods)
    {
        var groups = new List<string>();
        foreach (var mod in mods)
        {
            var settings = ParseSettings(mod.SettingsJson);
            if (settings.Count == 0)
            {
                continue;
            }
            var values = new List<string>();
            foreach (var setting in settings)
            {
                var label = SettingAliases.TryGetValue(setting.Key, out var alias)
                    ? alias : Titleize(setting.Key.Replace('_', ' '));
                var element = setting.Value;
                string rendered;
                if (string.Equals(setting.Key, "speed_change", StringComparison.OrdinalIgnoreCase)
                    && element.ValueKind == JsonValueKind.Number)
                {
                    rendered = FormatG(element.GetDouble()) + "×";
                }
                else if (element.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    rendered = element.GetBoolean() ? "On" : "Off";
                }
                else if (element.ValueKind == JsonValueKind.Number)
                {
                    rendered = FormatG(element.GetDouble());
                }
                else if (element.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                {
                    continue;
                }
                else
                {
                    rendered = element.ToString();
                }
                values.Add($"{label} {rendered}");
            }
            if (values.Count > 0)
            {
                groups.Add($"{mod.Acronym.ToUpperInvariant()}  " + string.Join(" · ", values));
            }
        }
        return string.Join("   |   ", groups);
    }

    private static IReadOnlyList<KeyValuePair<string, JsonElement>> ParseSettings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<KeyValuePair<string, JsonElement>>();
        }
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Array.Empty<KeyValuePair<string, JsonElement>>();
            }
            return doc.RootElement.EnumerateObject()
                .Select(p => new KeyValuePair<string, JsonElement>(p.Name, p.Value.Clone()))
                .ToArray();
        }
        catch (JsonException ex)
        {
            Log.Debug(ex, "Skipping malformed mod settings JSON");
            return Array.Empty<KeyValuePair<string, JsonElement>>();
        }
    }

    private static double? TryNumber(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Number => element.TryGetDouble(out var d) ? d : null,
        JsonValueKind.String => double.TryParse(
            element.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var s) ? s : null,
        _ => null,
    };

    private static string SourceLabel(string? source) => (source ?? "live") switch
    {
        "replay" => "Authoritative Replay",
        "lazer_replay_frame" => "Lazer Replay",
        "lazer_memory" => "Lazer Memory",
        "opentabletdriver" => "Tablet",
        "opentabletdriver+fallback" => "Tablet (Gaps)",
        _ => "Mouse",
    };

    private static string FormatStarted(string started)
    {
        return LocalTimeDisplay.DateTimeWithSeconds(started, started);
    }

    private static string FormatG(double value)
        => value.ToString("0.######", CultureInfo.InvariantCulture);

    private static string HitTotal(int hits, int misses)
        => Invariant($"{hits:N0}/{hits + misses:N0}");

    private static string Titleize(string text)
        => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(text.ToLowerInvariant());

    private static IReadOnlyList<TimingBarViewModel> BuildTimingBars(IReadOnlyList<double>? offsets)
    {
        const int bins = 44;
        const double min = -75;
        const double max = 75;
        var counts = new int[bins];
        foreach (var offset in offsets ?? Array.Empty<double>())
        {
            var clamped = Math.Clamp(offset, min, max);
            var index = (int)Math.Floor((clamped - min) / (max - min) * bins);
            counts[Math.Clamp(index, 0, bins - 1)]++;
        }

        var peak = Math.Max(1, counts.Max());
        return counts
            .Select((count, index) =>
            {
                var distanceFromCenter = Math.Abs(index - (bins / 2.0)) / (bins / 2.0);
                var color = distanceFromCenter < 0.12 ? "#FFD8EA" : "#F4B7DD";
                return new TimingBarViewModel(count == 0 ? 2 : 4 + (count * 52.0 / peak), color);
            })
            .ToArray();
    }

    private static PressureGraph BuildPressureGraph(AttemptDetails? details)
    {
        const double left = 22;
        const double right = 620;
        const double top = 12;
        const double bottom = 126;
        const int sampleCount = 42;

        if (details is null)
        {
            return EmptyPressureGraph(left, right, bottom);
        }

        var duration = MapDuration(details);
        var events = details.Events
            .Where(e => e.MapTimeMs is >= 0)
            .OrderBy(e => e.MapTimeMs)
            .ToArray();

        if (duration <= 0 || events.Length == 0)
        {
            return EmptyPressureGraph(left, right, bottom);
        }

        var values = new double[sampleCount];
        for (var i = 0; i < sampleCount; i++)
        {
            var t = duration * i / (sampleCount - 1);
            var local = events.Sum(e =>
            {
                var distance = Math.Abs((e.MapTimeMs ?? 0) - t);
                if (distance > 4500)
                {
                    return 0;
                }

                var weight = 1.0 - (distance / 4500.0);
                return e.EventType switch
                {
                    "miss" => 1.2 * weight,
                    "slider_break" => 0.9 * weight,
                    "hit_50" => 0.65 * weight,
                    "hit_100" => 0.45 * weight,
                    "pp_peak" => 0.35 * weight,
                    "combo_peak" => 0.25 * weight,
                    _ => 0.18 * weight,
                };
            });
            values[i] = 0.16 + Math.Min(1.0, local);
        }

        var maxValue = Math.Max(0.2, values.Max());
        var line = new PointCollection();
        foreach (var (value, index) in values.Select((v, i) => (v, i)))
        {
            var x = left + ((right - left) * index / (sampleCount - 1));
            var y = bottom - ((bottom - top) * value / maxValue);
            line.Add(new System.Windows.Point(x, y));
        }

        var area = new PointCollection(line);
        area.Add(new System.Windows.Point(right, bottom + 12));
        area.Add(new System.Windows.Point(left, bottom + 12));

        var markers = events
            .Where(e => e.EventType is "miss" or "slider_break")
            .Select(e =>
            {
                var x = left + (right - left) * Math.Clamp((e.MapTimeMs ?? 0) / (double)duration, 0, 1);
                return e.EventType == "miss"
                    ? new MapMarkerViewModel(x - 4, top + 8, "x", "#FF4F7B")
                    : new MapMarkerViewModel(x - 2, bottom + 3, "|", "#FFD43B");
            })
            .ToArray();

        return new PressureGraph(line, area, markers);
    }

    private static PressureGraph EmptyPressureGraph(double left, double right, double bottom)
    {
        var line = new PointCollection { new(left, bottom), new(right, bottom) };
        var area = new PointCollection { new(left, bottom), new(right, bottom), new(right, bottom + 12), new(left, bottom + 12) };
        return new PressureGraph(line, area, Array.Empty<MapMarkerViewModel>());
    }

    private static int MapDuration(AttemptDetails details)
    {
        var fromEvents = details.Events
            .Where(e => e.MapTimeMs is > 0)
            .Select(e => (int)e.MapTimeMs!.Value)
            .DefaultIfEmpty(0)
            .Max();
        var fromDuration = (int)Math.Round(details.DurationSeconds * 1000);
        return Math.Max(Math.Max(fromEvents, fromDuration), 1);
    }

    private sealed record PressureGraph(
        PointCollection Line,
        PointCollection Area,
        IReadOnlyList<MapMarkerViewModel> Markers);
}

public sealed record TimingBarViewModel(double Height, string Fill);

public sealed record MapMarkerViewModel(double X, double Y, string Glyph, string Color);
