using osu.Game.Rulesets.Objects;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets.Osu.Objects;
using osu.Game.Rulesets.Osu.Replays;
using osuTK;

namespace Kumori.ReplayViewer;

internal enum AnalysisDataSource
{
    Lazer,
    Inferred,
}

internal sealed record ReplayJudgementSnapshot(
    HitObject HitObject,
    KumoriTimelineMarkerKind Kind,
    double EventTime,
    double TimeOffset,
    int ComboBefore,
    int ComboAfter);

internal sealed record MissAnalysisModel(IReadOnlyList<MissAnalysisEntry> Entries)
{
    public IEnumerable<KumoriTimelineMarker> Markers => Entries.Select(e => new KumoriTimelineMarker(e.EventTime, e.Kind));
}

internal sealed record MissAnalysisEntry(
    int Index,
    double EventTime,
    KumoriTimelineMarkerKind Kind,
    string Label,
    string ObjectType,
    AnalysisDataSource Source,
    Vector2 TargetPosition,
    double TargetRadius,
    double TargetStartTime,
    double TargetEndTime,
    IReadOnlyList<Vector2> TargetPath,
    Vector2? PreviousPosition,
    double? PreviousTime,
    Vector2? NextPosition,
    double? NextTime,
    double WindowStart,
    double WindowEnd,
    IReadOnlyList<MissReplayFrameSample> ReplayFrames,
    MissReplayFrameSample? NearestFrame,
    MissReplayFrameSample? InputFrame,
    double? DistanceFromTarget,
    double? InputOffsetMs,
    bool ExactTiming,
    int? ComboBefore,
    int? ComboAfter)
{
    public double? TapOffsetMs => InputOffsetMs;
    public MissReplayFrameSample? TapFrame => InputFrame;
}

internal sealed record MissReplayFrameSample(
    double Time,
    Vector2 Position,
    bool HasAction,
    bool Pressed = false,
    bool Released = false);

internal static class MissAnalysisBuilder
{
    private const double sample_window_before = 650;
    private const double sample_window_after = 450;
    private const double maximum_analysis_window = 2000;
    private const double object_match_window = 900;

    public static MissAnalysisModel Build(ViewerContract contract, IReadOnlyList<HitObject> hitObjects)
        => Build(contract, hitObjects, contract.Samples.Select(sample =>
            new OsuReplayFrame(sample.MapTimeMs, new Vector2((float)sample.X, (float)sample.Y))));

    public static MissAnalysisModel Build(ViewerContract contract, IReadOnlyList<HitObject> hitObjects, IEnumerable<OsuReplayFrame> replayFrames)
    {
        PreparedReplay prepared = prepareReplay(hitObjects, replayFrames);
        var events = contract.JudgementEvents
                             .Select(e => (Event: e, Kind: KumoriTimelineMarkers.KindFromContract(e.Kind)))
                             .Where(e => e.Kind is not null)
                             .OrderBy(e => e.Event.MapTimeMs)
                             .ToArray();
        List<MissAnalysisEntry> entries = [];
        Dictionary<KumoriTimelineMarkerKind, int> ordinals = [];

        foreach (var (judgement, nullableKind) in events)
        {
            KumoriTimelineMarkerKind kind = nullableKind!.Value;
            for (int occurrence = 0; occurrence < Math.Max(1, judgement.Delta); occurrence++)
            {
                int ordinal = nextOrdinal(ordinals, kind);
                OsuHitObject? matched = findBestObject(prepared.Objects, judgement.MapTimeMs);
                entries.Add(createEntry(entries.Count + 1, kind, judgement.MapTimeMs, null, matched,
                    AnalysisDataSource.Inferred, ordinal, prepared));
            }
        }

        return new MissAnalysisModel(entries);
    }

    public static MissAnalysisModel Build(ViewerContract contract, BeatmapAnalysis analysis, IEnumerable<OsuReplayFrame> replayFrames)
    {
        PreparedReplay prepared = prepareReplay([], replayFrames);
        HitObjectAnalysis[] objects = analysis.Objects.OrderBy(o => o.StartTime).ToArray();
        List<MissAnalysisEntry> entries = [];
        Dictionary<KumoriTimelineMarkerKind, int> ordinals = [];
        HashSet<HitObjectAnalysis> assignedObjects = [];

        foreach (var item in contract.JudgementEvents
                                     .Select(e => (Event: e, Kind: KumoriTimelineMarkers.KindFromContract(e.Kind)))
                                     .Where(e => e.Kind is not null)
                                     .OrderBy(e => e.Event.MapTimeMs))
        {
            KumoriTimelineMarkerKind kind = item.Kind!.Value;
            for (int occurrence = 0; occurrence < Math.Max(1, item.Event.Delta); occurrence++)
            {
                int ordinal = nextOrdinal(ordinals, kind);
                HitObjectAnalysis? targetObject = findBestObject(
                    objects, item.Event.MapTimeMs, kind, analysis.HitWindows, assignedObjects);
                if (targetObject != null && kind != KumoriTimelineMarkerKind.SliderBreak)
                    assignedObjects.Add(targetObject);
                int targetIndex = targetObject == null ? -1 : Array.IndexOf(objects, targetObject);
                NestedObjectAnalysis? targetNested = kind == KumoriTimelineMarkerKind.SliderBreak
                    ? targetObject?.NestedObjects.MinBy(nested => Math.Abs(nested.StartTime - item.Event.MapTimeMs))
                    : null;
                if (targetNested != null && Math.Abs(targetNested.StartTime - item.Event.MapTimeMs) > object_match_window)
                    targetNested = null;
                double analysisTime = targetNested?.StartTime ?? targetObject?.StartTime ?? item.Event.MapTimeMs;
                MissReplayFrameSample? nearest = nearestFrame(prepared.Frames, analysisTime);
                Vector2 target = targetNested != null
                    ? new Vector2(targetNested.X, targetNested.Y)
                    : targetObject == null
                    ? nearest?.Position ?? new Vector2(256, 192)
                    : new Vector2(targetObject.X, targetObject.Y);
                double inputWindow = kind == KumoriTimelineMarkerKind.SliderBreak
                    ? 300
                    : Math.Max(180, analysis.HitWindows.Miss + 50);
                MissReplayFrameSample? input = nearestInput(
                    prepared.InputFrames, analysisTime, kind == KumoriTimelineMarkerKind.SliderBreak, inputWindow);
                double referenceTime = kind == KumoriTimelineMarkerKind.SliderBreak
                    ? analysisTime
                    : targetObject?.StartTime ?? item.Event.MapTimeMs;

                entries.Add(new MissAnalysisEntry(
                    entries.Count + 1,
                    analysisTime,
                    kind,
                    labelFor(kind),
                    targetNested != null ? objectNameFor(targetNested) : targetObject == null ? $"{labelFor(kind)} {ordinal}" : objectNameFor(targetObject),
                    AnalysisDataSource.Inferred,
                    target,
                    targetObject?.Radius ?? OsuHitObject.OBJECT_RADIUS,
                    targetNested?.StartTime ?? targetObject?.StartTime ?? item.Event.MapTimeMs,
                    targetNested?.StartTime ?? targetObject?.EndTime ?? item.Event.MapTimeMs,
                    [target],
                    targetIndex > 0 ? new Vector2(objects[targetIndex - 1].X, objects[targetIndex - 1].Y) : null,
                    targetIndex > 0 ? objects[targetIndex - 1].EndTime : null,
                    targetIndex >= 0 && targetIndex + 1 < objects.Length ? new Vector2(objects[targetIndex + 1].X, objects[targetIndex + 1].Y) : null,
                    targetIndex >= 0 && targetIndex + 1 < objects.Length ? objects[targetIndex + 1].StartTime : null,
                    analysisTime - sample_window_before,
                    analysisTime + sample_window_after,
                    prepared.Frames.Where(frame => frame.Time >= analysisTime - maximum_analysis_window
                                                  && frame.Time <= analysisTime + maximum_analysis_window).ToArray(),
                    nearest,
                    input,
                    nearest == null ? null : (nearest.Position - target).Length,
                    input == null ? null : input.Time - referenceTime,
                    false,
                    null,
                    null));
            }
        }

        return new MissAnalysisModel(entries);
    }

    public static MissAnalysisModel BuildFromJudgements(
        IReadOnlyList<HitObject> hitObjects,
        IEnumerable<OsuReplayFrame> replayFrames,
        IEnumerable<ReplayJudgementSnapshot> judgements)
    {
        PreparedReplay prepared = prepareReplay(hitObjects, replayFrames);
        List<MissAnalysisEntry> entries = [];
        Dictionary<KumoriTimelineMarkerKind, int> ordinals = [];
        HashSet<(HitObject Object, KumoriTimelineMarkerKind Kind, int Time)> seen = [];

        foreach (ReplayJudgementSnapshot judgement in judgements.OrderBy(j => j.EventTime))
        {
            if (!seen.Add((judgement.HitObject, judgement.Kind, (int)Math.Round(judgement.EventTime))))
                continue;

            OsuHitObject? root = rootObjectFor(prepared.Objects, judgement.HitObject);
            int ordinal = nextOrdinal(ordinals, judgement.Kind);
            entries.Add(createEntry(entries.Count + 1, judgement.Kind, judgement.EventTime, judgement,
                root, AnalysisDataSource.Lazer, ordinal, prepared));
        }

        return new MissAnalysisModel(entries);
    }

    public static MissAnalysisModel BuildFromPrepared(
        BeatmapAnalysis analysis,
        IEnumerable<OsuReplayFrame> replayFrames,
        IEnumerable<PreparedReplayJudgement> judgements,
        IEnumerable<PreparedReplayFrame>? preparedFrames = null)
    {
        PreparedReplay prepared = prepareReplay([], replayFrames);
        MissReplayFrameSample[] canonicalFrames = preparedFrames?.Select(frame => new MissReplayFrameSample(
            frame.Time,
            new Vector2(frame.X, frame.Y),
            frame.HasAction,
            frame.Pressed,
            frame.Released)).ToArray() ?? prepared.Frames;
        HitObjectAnalysis[] objects = analysis.Objects.OrderBy(o => o.StartTime).ToArray();
        List<MissAnalysisEntry> entries = [];

        foreach (PreparedReplayJudgement judgement in judgements.OrderBy(j => j.ObjectStartTime))
        {
            HitObjectAnalysis? root = objects.MinBy(o => Math.Abs(o.StartTime - judgement.RootStartTime));
            int rootIndex = root == null ? -1 : Array.IndexOf(objects, root);
            double reviewTime = judgement.ObjectStartTime;
            var target = new Vector2(judgement.X, judgement.Y);
            MissReplayFrameSample? nearest = nearestFrame(canonicalFrames, reviewTime);
            double frameStart = double.IsFinite(judgement.FrameStartTime)
                ? judgement.FrameStartTime
                : reviewTime - 450;
            double frameEnd = double.IsFinite(judgement.FrameEndTime)
                ? judgement.FrameEndTime
                : reviewTime + 180;
            MissReplayFrameSample[] ownershipFrames = canonicalFrames
                .Where(frame => frame.Time >= frameStart && frame.Time <= frameEnd)
                .ToArray();
            MissReplayFrameSample[] ownedInputs = ownershipFrames
                .Where(frame => frame.Pressed || (judgement.Kind == KumoriTimelineMarkerKind.SliderBreak && frame.Released))
                .ToArray();
            double inputReference = judgement.Kind is KumoriTimelineMarkerKind.Ok or KumoriTimelineMarkerKind.Meh
                ? judgement.EventTime
                : reviewTime;
            double inputWindow = judgement.Kind is KumoriTimelineMarkerKind.Ok or KumoriTimelineMarkerKind.Meh
                ? 60
                : Math.Max(180, analysis.HitWindows.Miss + 30);
            MissReplayFrameSample? input = nearestInput(
                ownedInputs, inputReference, judgement.Kind == KumoriTimelineMarkerKind.SliderBreak, inputWindow);
            double? inputOffset = judgement.Kind is KumoriTimelineMarkerKind.Ok or KumoriTimelineMarkerKind.Meh
                ? judgement.TimeOffset
                : input?.Time - reviewTime;
            MissReplayFrameSample[] ownedFrames = isolateContiguousFrames(
                canonicalFrames, frameStart, frameEnd, input?.Time ?? judgement.EventTime);
            ownedFrames = isolateLocalApproach(
                ownedFrames, target, Math.Max(judgement.Radius * 2.25, 48), input?.Time ?? judgement.EventTime);

            entries.Add(new MissAnalysisEntry(
                entries.Count + 1,
                reviewTime,
                judgement.Kind,
                labelFor(judgement.Kind),
                judgement.ObjectType,
                AnalysisDataSource.Lazer,
                target,
                judgement.Radius,
                reviewTime,
                root?.EndTime ?? judgement.RootEndTime,
                [target],
                rootIndex > 0 ? new Vector2(objects[rootIndex - 1].X, objects[rootIndex - 1].Y) : null,
                rootIndex > 0 ? objects[rootIndex - 1].EndTime : null,
                rootIndex >= 0 && rootIndex + 1 < objects.Length ? new Vector2(objects[rootIndex + 1].X, objects[rootIndex + 1].Y) : null,
                rootIndex >= 0 && rootIndex + 1 < objects.Length ? objects[rootIndex + 1].StartTime : null,
                reviewTime - sample_window_before,
                reviewTime + sample_window_after,
                ownedFrames,
                nearest,
                input,
                (input ?? nearest) is { } aimFrame ? (aimFrame.Position - target).Length : null,
                inputOffset,
                true,
                judgement.ComboBefore,
                judgement.ComboAfter));
        }

        return new MissAnalysisModel(entries);
    }

    internal static MissReplayFrameSample[] isolateContiguousFrames(
        IEnumerable<MissReplayFrameSample> source,
        double startTime,
        double endTime,
        double anchorTime)
    {
        MissReplayFrameSample[] candidates = source
            .Where(frame => frame.Time >= startTime && frame.Time <= endTime)
            .OrderBy(frame => frame.Time)
            .GroupBy(frame => Math.Round(frame.Time, 3))
            .Select(group => group.Last())
            .ToArray();
        if (candidates.Length < 2)
            return candidates;

        List<List<MissReplayFrameSample>> segments = [[]];
        foreach (MissReplayFrameSample frame in candidates)
        {
            List<MissReplayFrameSample> current = segments[^1];
            if (current.Count > 0 && !framesAreContinuous(current[^1], frame))
            {
                current = [];
                segments.Add(current);
            }
            current.Add(frame);
        }

        return segments
               .OrderBy(segment => segment.Min(frame => Math.Abs(frame.Time - anchorTime)))
               .ThenByDescending(segment => segment.Count)
               .First()
               .ToArray();
    }

    private static bool framesAreContinuous(MissReplayFrameSample previous, MissReplayFrameSample current)
    {
        double elapsed = current.Time - previous.Time;
        if (elapsed <= 0 || elapsed > 50)
            return false;

        float maximumDistance = 12 + (float)elapsed * 2.5f;
        return (current.Position - previous.Position).Length <= maximumDistance;
    }

    internal static MissReplayFrameSample[] isolateLocalApproach(
        IReadOnlyList<MissReplayFrameSample> frames,
        Vector2 target,
        double maximumDistance,
        double anchorTime)
    {
        if (frames.Count == 0)
            return [];

        var local = frames
            .Select((frame, index) => (Frame: frame, Index: index, Distance: (frame.Position - target).Length))
            .Where(item => item.Distance <= maximumDistance)
            .ToArray();

        if (local.Length == 0)
        {
            MissReplayFrameSample closest = frames
                .OrderBy(frame => (frame.Position - target).Length)
                .ThenBy(frame => Math.Abs(frame.Time - anchorTime))
                .First();
            return [closest];
        }

        var pivot = local
            .OrderBy(item => item.Distance)
            .ThenBy(item => Math.Abs(item.Frame.Time - anchorTime))
            .First();
        int start = pivot.Index;
        int end = pivot.Index;

        while (start > 0
               && (frames[start - 1].Position - target).Length <= maximumDistance
               && framesAreContinuous(frames[start - 1], frames[start]))
            start--;
        while (end + 1 < frames.Count
               && (frames[end + 1].Position - target).Length <= maximumDistance
               && framesAreContinuous(frames[end], frames[end + 1]))
            end++;

        return frames.Skip(start).Take(end - start + 1).ToArray();
    }

    private static MissAnalysisEntry createEntry(
        int index,
        KumoriTimelineMarkerKind kind,
        double eventTime,
        ReplayJudgementSnapshot? judgement,
        OsuHitObject? matched,
        AnalysisDataSource source,
        int ordinal,
        PreparedReplay prepared)
    {
        int objectIndex = matched == null ? -1 : Array.IndexOf(prepared.Objects, matched);
        MissReplayFrameSample? nearest = nearestFrame(prepared.Frames, eventTime);
        Vector2 target = targetPosition(judgement?.HitObject, matched, nearest);
        MissReplayFrameSample? input = nearestInput(prepared.InputFrames, eventTime, kind == KumoriTimelineMarkerKind.SliderBreak);
        double? inferredOffset = input == null ? null : input.Time - (kind == KumoriTimelineMarkerKind.SliderBreak ? eventTime : matched?.StartTime ?? eventTime);
        double? offset = judgement != null && kind is KumoriTimelineMarkerKind.Ok or KumoriTimelineMarkerKind.Meh
            ? judgement.TimeOffset
            : inferredOffset;

        return new MissAnalysisEntry(
            index,
            eventTime,
            kind,
            labelFor(kind),
            objectNameFor(judgement?.HitObject, matched, kind, ordinal),
            source,
            target,
            matched?.Radius ?? OsuHitObject.OBJECT_RADIUS,
            matched?.StartTime ?? eventTime,
            matched?.GetEndTime() ?? eventTime,
            pathFor(matched),
            objectIndex > 0 ? exitPosition(prepared.Objects[objectIndex - 1]) : null,
            objectIndex > 0 ? prepared.Objects[objectIndex - 1].GetEndTime() : null,
            objectIndex >= 0 && objectIndex + 1 < prepared.Objects.Length ? prepared.Objects[objectIndex + 1].StackedPosition : null,
            objectIndex >= 0 && objectIndex + 1 < prepared.Objects.Length ? prepared.Objects[objectIndex + 1].StartTime : null,
            eventTime - sample_window_before,
            eventTime + sample_window_after,
            prepared.Frames.Where(frame => frame.Time >= eventTime - maximum_analysis_window && frame.Time <= eventTime + maximum_analysis_window).ToArray(),
            nearest,
            input,
            nearest == null ? null : (nearest.Position - target).Length,
            offset,
            judgement != null && kind is KumoriTimelineMarkerKind.Ok or KumoriTimelineMarkerKind.Meh,
            judgement?.ComboBefore,
            judgement?.ComboAfter);
    }

    private static PreparedReplay prepareReplay(IReadOnlyList<HitObject> hitObjects, IEnumerable<OsuReplayFrame> replayFrames)
    {
        OsuHitObject[] objects = hitObjects.OfType<OsuHitObject>().OrderBy(h => h.StartTime).ToArray();
        OsuReplayFrame[] osuFrames = replayFrames.OrderBy(frame => frame.Time).ToArray();
        List<MissReplayFrameSample> frames = [];
        List<MissReplayFrameSample> inputs = [];
        bool previousAction = false;

        foreach (OsuReplayFrame frame in osuFrames)
        {
            bool action = frame.Actions.Count > 0;
            var sample = new MissReplayFrameSample(frame.Time, frame.Position, action, action && !previousAction, !action && previousAction);
            frames.Add(sample);
            if (sample.Pressed || sample.Released)
                inputs.Add(sample);
            previousAction = action;
        }

        return new PreparedReplay(objects, frames.ToArray(), inputs.ToArray());
    }

    private static OsuHitObject? rootObjectFor(IEnumerable<OsuHitObject> roots, HitObject hitObject)
    {
        OsuHitObject[] candidates = roots.ToArray();
        if (hitObject is OsuHitObject osu && candidates.Contains(osu))
            return osu;
        OsuHitObject? nestedOwner = candidates.FirstOrDefault(root => root.NestedHitObjects.Contains(hitObject));
        if (nestedOwner != null)
            return nestedOwner;

        // ReplayPlayer uses a playable beatmap clone, so judgement objects do
        // not necessarily share references with the decoded source beatmap.
        OsuHitObject? timeOwner = candidates
            .Where(root => hitObject.StartTime >= root.StartTime - 5 && hitObject.StartTime <= root.GetEndTime() + 5)
            .MinBy(root => root.GetEndTime() - root.StartTime);
        return timeOwner ?? findBestObject(candidates, hitObject.StartTime);
    }

    private static Vector2 targetPosition(HitObject? judged, OsuHitObject? root, MissReplayFrameSample? nearest)
        => judged is OsuHitObject osu ? osu.StackedPosition : root?.StackedPosition ?? nearest?.Position ?? new Vector2(256, 192);

    private static string objectNameFor(HitObject? judged, OsuHitObject? root, KumoriTimelineMarkerKind kind, int ordinal)
        => judged switch
        {
            SliderHeadCircle => "Slider head",
            SliderTick => "Slider tick",
            SliderRepeat => "Slider repeat",
            SliderTailCircle => "Slider tail",
            HitCircle => "Circle",
            Spinner => "Spinner",
            _ when root is Slider => "Slider",
            _ when root is HitCircle => "Circle",
            _ when root is Spinner => "Spinner",
            _ => $"{labelFor(kind)} {ordinal}",
        };

    private static OsuHitObject? findBestObject(IReadOnlyList<OsuHitObject> objects, double eventTime)
    {
        OsuHitObject? best = objects.MinBy(hitObject => Math.Min(Math.Abs(hitObject.StartTime - eventTime), Math.Abs(hitObject.GetEndTime() - eventTime)));
        if (best == null)
            return null;
        double distance = Math.Min(Math.Abs(best.StartTime - eventTime), Math.Abs(best.GetEndTime() - eventTime));
        return distance <= object_match_window ? best : null;
    }

    private static HitObjectAnalysis? findBestObject(
        IReadOnlyList<HitObjectAnalysis> objects,
        double eventTime,
        KumoriTimelineMarkerKind kind,
        HitWindowAnalysis hitWindows,
        IReadOnlySet<HitObjectAnalysis> assigned)
    {
        IEnumerable<HitObjectAnalysis> candidates = objects.Where(hitObject => !assigned.Contains(hitObject));
        if (kind == KumoriTimelineMarkerKind.SliderBreak)
            candidates = candidates.Where(hitObject => hitObject.Kind == nameof(Slider));

        HitObjectAnalysis? best = candidates.MinBy(hitObject => resolutionDistance(hitObject, eventTime, kind, hitWindows));
        if (best == null)
            return null;
        double distance = resolutionDistance(best, eventTime, kind, hitWindows, penaliseFuture: false);
        return distance <= object_match_window ? best : null;
    }

    private static double resolutionDistance(
        HitObjectAnalysis hitObject,
        double eventTime,
        KumoriTimelineMarkerKind kind,
        HitWindowAnalysis hitWindows,
        bool penaliseFuture = true)
    {
        if (kind == KumoriTimelineMarkerKind.SliderBreak)
        {
            if (eventTime >= hitObject.StartTime && eventTime <= hitObject.EndTime)
                return 0;
            return Math.Min(Math.Abs(hitObject.StartTime - eventTime), Math.Abs(hitObject.EndTime - eventTime));
        }

        double resolutionTime = hitObject.Kind == nameof(HitCircle)
            ? hitObject.StartTime + (kind == KumoriTimelineMarkerKind.Miss ? hitWindows.Miss : 0)
            : hitObject.EndTime;
        double futurePenalty = penaliseFuture && resolutionTime > eventTime + 100 ? 1000 : 0;
        return Math.Abs(resolutionTime - eventTime) + futurePenalty;
    }

    private static string objectNameFor(HitObjectAnalysis hitObject) => hitObject.Kind switch
    {
        nameof(HitCircle) => "Circle",
        nameof(Slider) => "Slider",
        nameof(Spinner) => "Spinner",
        _ => hitObject.Kind,
    };

    private static string objectNameFor(NestedObjectAnalysis hitObject) => hitObject.Kind switch
    {
        nameof(SliderHeadCircle) => "Slider head",
        nameof(SliderTick) => "Slider tick",
        nameof(SliderRepeat) => "Slider repeat",
        nameof(SliderTailCircle) => "Slider tail",
        _ => hitObject.Kind,
    };

    private static MissReplayFrameSample? nearestFrame(IReadOnlyList<MissReplayFrameSample> frames, double time)
        => frames.Count == 0 ? null : frames.MinBy(frame => Math.Abs(frame.Time - time));

    private static MissReplayFrameSample? nearestInput(
        IReadOnlyList<MissReplayFrameSample> inputs,
        double time,
        bool includeReleases,
        double maximumDistance = 300)
    {
        MissReplayFrameSample? input = nearestFrame(includeReleases ? inputs : inputs.Where(i => i.Pressed).ToArray(), time);
        return input != null && Math.Abs(input.Time - time) <= maximumDistance ? input : null;
    }

    private static int nextOrdinal(Dictionary<KumoriTimelineMarkerKind, int> ordinals, KumoriTimelineMarkerKind kind)
        => ordinals[kind] = ordinals.GetValueOrDefault(kind) + 1;

    private static string labelFor(KumoriTimelineMarkerKind kind) => kind switch
    {
        KumoriTimelineMarkerKind.Miss => "Miss",
        KumoriTimelineMarkerKind.SliderBreak => "Slider break",
        KumoriTimelineMarkerKind.Meh => "50",
        KumoriTimelineMarkerKind.Ok => "100",
        _ => "Bad hit",
    };

    private static IReadOnlyList<Vector2> pathFor(OsuHitObject? hitObject)
    {
        if (hitObject is not Slider slider)
            return hitObject == null ? [] : [hitObject.StackedPosition];
        List<Vector2> path = [];
        for (int span = 0; span <= slider.RepeatCount; span++)
        for (int point = 0; point <= 24; point++)
        {
            double progress = point / 24.0;
            path.Add(slider.StackedPositionAt(span % 2 == 1 ? 1 - progress : progress));
        }
        return path;
    }

    private static Vector2 exitPosition(OsuHitObject hitObject)
        => hitObject is Slider slider && slider.RepeatCount % 2 == 0 ? slider.StackedPositionAt(1) : hitObject.StackedPosition;

    private sealed record PreparedReplay(OsuHitObject[] Objects, MissReplayFrameSample[] Frames, MissReplayFrameSample[] InputFrames);
}
