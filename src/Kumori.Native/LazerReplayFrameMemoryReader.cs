using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Kumori.Core;
using Kumori.Tracking;

namespace Kumori.Native;

internal sealed partial class LazerReplayFrameMemoryReader
{

    private static readonly byte[] ScalingContainerTargetDrawSizePattern =
    [
        0x00, 0x00, 0x80, 0x44,
        0x00, 0x00, 0x40, 0x44,
    ];

    private const int ScoreReplayOffset = 0x10;
    private const int ReplayFramesOffset = 0x8;
    private const int ReplayFramePositionOffset = 0x20;
    private const int ReplayFrameActionsOffset = 0x18;
    private const int MaxFrameCount = 1_000_000;
    private const int MaxFramesPerTick = 64;
    private const int DiscoveryChunkBytes = 1024 * 1024;
    private const int CachedGameBaseInvalidationChecks = 3;
    private static readonly TimeSpan CachedGameBaseValidationInterval = TimeSpan.FromSeconds(1);
    private static readonly int[] ReplayFrameActionOffsets = [ReplayFrameActionsOffset, ReplayFrameActionsOffset + 0x8];
    private static readonly int[] BootstrapDeltas = [0x24, 0x28, 0x2c, 0x20, 0x30, 0x1c, 0x34];
    private static readonly int[] ReplayFrameTimeOffsetCandidates = [0x8, 0x10, 0x18, 0x28, 0x30];

    private readonly ProcessMemory _memory;
    private readonly LazerMemoryOffsets _offsets;
    private readonly int? _preferredReplayFrameTimeOffset;
    private readonly nint _preferredGameBase;
    private readonly byte[] _discoveryBuffer = new byte[DiscoveryChunkBytes];
    private MemoryRegion[]? _discoveryRegions;
    private int _discoveryRegionIndex;
    private long _discoveryRegionOffset;
    private int _discoveryChunkSearchOffset;
    private DateTimeOffset _nextDiscoveryStepAt;
    private DateTimeOffset _nextBootstrapCandidateRetryAt;
    private bool _discoveryExhausted;
    private DiscoveryPhase _discoveryPhase;
    private nint _fallbackVtableMarker;
    private int _fallbackMarkerResumeRegionIndex;
    private long _fallbackMarkerResumeRegionOffset;
    private int _fallbackMarkerResumeSearchOffset;
    private int _invalidCachedGameBasePolls;
    private int _bootstrapCandidateIndex;
    private DateTimeOffset _nextCachedGameBaseValidationAt;
    private readonly List<nint> _bootstrapCandidates = [];
    private nint _timeOffsetSearchFramesList;
    private nint _timeOffsetSearchItems;
    private int _timeOffsetSearchSize;
    private int _timeOffsetSampleCount;
    private int _timeOffsetSampleStep;
    private int _timeOffsetCandidateIndex;
    private int _timeOffsetSampleIndex;
    private double? _timeOffsetPrevious;
    private double? _timeOffsetFirst;
    private double? _timeOffsetLast;
    private int _timeOffsetSaneCount;
    private bool _timeOffsetCandidateInvalid;
    private int? _timeOffsetBestOffset;
    private double _timeOffsetBestScore = double.NegativeInfinity;
    private nint _failedTimeOffsetFramesList;
    private nint _failedTimeOffsetItems;
    private DateTimeOffset _nextTimeOffsetSearchAt;

    public LazerReplayFrameMemoryReader(
        ProcessMemory memory,
        LazerMemoryOffsets offsets,
        int? preferredReplayFrameTimeOffset = null,
        nint preferredGameBase = 0)
    {
        _memory = memory;
        _offsets = offsets;
        _preferredReplayFrameTimeOffset = preferredReplayFrameTimeOffset;
        _preferredGameBase = preferredGameBase;
    }

    public string? LastStatus { get; private set; }
    public nint LastFramesList { get; private set; }
    public bool FramesListChanged { get; private set; }
    public int? LastReplayFrameTimeOffset { get; private set; }
    public nint LastGameBase { get; private set; }

    public void ResetAttemptSearch()
    {
        ResetReplayFrameTimeOffsetSearch();
        _failedTimeOffsetFramesList = 0;
        _failedTimeOffsetItems = 0;
        _nextTimeOffsetSearchAt = DateTimeOffset.MinValue;
        if (LazerMemoryReadPolicy.ShouldRearmDiscovery(LastGameBase, _discoveryExhausted))
        {
            _discoveryExhausted = false;
            _discoveryPhase = DiscoveryPhase.BootstrapPattern;
            _fallbackVtableMarker = 0;
            _fallbackMarkerResumeRegionIndex = 0;
            _fallbackMarkerResumeRegionOffset = 0;
            _fallbackMarkerResumeSearchOffset = 0;
            ResetDiscoveryCursor();
            _nextDiscoveryStepAt = DateTimeOffset.MinValue;
            _nextBootstrapCandidateRetryAt = DateTimeOffset.MinValue;
        }
    }

    public void WarmGameBase() =>
        _ = FindGameBase(allowDiscovery: true, DeadlineFromNow(TimeSpan.FromMilliseconds(3)));

    public bool TryAdoptValidatedGameBase(nint candidate)
    {
        var vtableMatches = IsGameBase(candidate);
        var screenStackUsable = vtableMatches && HasUsableScreenStack(candidate);
        if (!TosuGameBaseAdoptionPolicy.ShouldAdopt(
                candidate,
                vtableMatches,
                screenStackUsable))
        {
            LastStatus = "tosu GameBase hint did not pass native vtable and ScreenStack validation";
            return false;
        }

        LastGameBase = candidate;
        _invalidCachedGameBasePolls = 0;
        _nextCachedGameBaseValidationAt = DateTimeOffset.UtcNow + CachedGameBaseValidationInterval;
        LastStatus = null;
        return true;
    }

    public IReadOnlyList<LazerReplayFrame> ReadFramesAfter(long lastSequence, nint previousFramesList)
    {
        LastStatus = null;
        LastFramesList = 0;
        FramesListChanged = false;
        var allowDiscovery = LazerMemoryReadPolicy.ShouldDiscover(LastGameBase);
        var deadline = DeadlineFromNow(
            allowDiscovery
                ? LazerMemoryReadPolicy.DiscoveryReadBudget
                : LazerMemoryReadPolicy.CachedReadBudget);
        var players = FindPlayers(allowDiscovery, deadline);
        if (players.Count == 0)
        {
            LastStatus ??= "player screen unavailable";
            return Array.Empty<LazerReplayFrame>();
        }

        for (var index = 0; index < players.Count; index++)
        {
            if (!LazerMemoryReadPolicy.MayAttemptUnit(index == 0, BudgetExpired(deadline)))
                break;
            try
            {
                var frames = ReadFramesAfter(players[index], lastSequence, previousFramesList, deadline: deadline);
                if (frames.Count > 0)
                {
                    return frames;
                }
            }
            catch (Win32Exception ex)
            {
                LastStatus = $"player candidate unreadable: {ex.Message}";
            }
            catch (Exception ex)
            {
                LastStatus = $"player candidate failed: {ex.Message}";
            }
        }

        LastStatus ??= $"no readable replay frames from {players.Count} player candidate(s)";
        return Array.Empty<LazerReplayFrame>();
    }

    public IReadOnlyList<LazerReplayFrame> ReadAllFrames()
    {
        LastStatus = null;
        LastFramesList = 0;
        FramesListChanged = false;
        var players = FindPlayers(allowDiscovery: false, long.MaxValue);
        if (players.Count == 0)
        {
            LastStatus ??= "player screen unavailable";
            return Array.Empty<LazerReplayFrame>();
        }

        foreach (var player in players)
        {
            try
            {
                var frames = ReadFramesAfter(
                    player,
                    lastSequence: 0,
                    previousFramesList: 0,
                    readAll: true,
                    deadline: long.MaxValue);
                if (frames.Count > 0)
                {
                    return frames;
                }
            }
            catch (Win32Exception ex)
            {
                LastStatus = $"player candidate unreadable: {ex.Message}";
            }
            catch (Exception ex)
            {
                LastStatus = $"player candidate failed: {ex.Message}";
            }
        }

        LastStatus ??= $"no readable replay frames from {players.Count} player candidate(s)";
        return Array.Empty<LazerReplayFrame>();
    }

    public bool IsWatchingReplay()
    {
        if (_offsets.PlayerDrawableRuleset < 0 || _offsets.DrawableRulesetReplayScore < 0)
        {
            LastStatus = "replay playback offsets unavailable";
            return false;
        }

        var deadline = DeadlineFromNow(LazerMemoryReadPolicy.CachedReadBudget);
        foreach (var player in FindPlayers(allowDiscovery: false, deadline))
        {
            if (BudgetExpired(deadline))
                break;
            try
            {
                var drawableRuleset = _memory.ReadIntPtr(player + _offsets.PlayerDrawableRuleset);
                if (!IsReadablePointer(drawableRuleset))
                    continue;

                var replayScore = _memory.ReadIntPtr(drawableRuleset + _offsets.DrawableRulesetReplayScore);
                if (IsReadablePointer(replayScore))
                    return true;
            }
            catch
            {
                // A screen transition can invalidate a candidate between reads.
            }
        }

        return false;
    }

    private IReadOnlyList<LazerReplayFrame> ReadFramesAfter(
        nint player,
        long lastSequence,
        nint previousFramesList,
        bool readAll = false,
        long deadline = long.MaxValue)
    {
        var score = _memory.ReadIntPtr(player + _offsets.PlayerScore);
        if (!IsReadablePointer(score))
        {
            LastStatus = "score unavailable";
            return Array.Empty<LazerReplayFrame>();
        }

        var replay = _memory.ReadIntPtr(score + ScoreReplayOffset);
        if (!IsReadablePointer(replay))
        {
            LastStatus = "replay unavailable";
            return Array.Empty<LazerReplayFrame>();
        }

        var framesList = _memory.ReadIntPtr(replay + ReplayFramesOffset);
        if (!IsReadablePointer(framesList))
        {
            LastStatus = "replay frames unavailable";
            return Array.Empty<LazerReplayFrame>();
        }
        LastFramesList = framesList;

        var (size, items) = ListItemsInfo(framesList);
        if (!IsReadablePointer(items) || size <= 0)
        {
            LastStatus = "replay frames empty";
            return Array.Empty<LazerReplayFrame>();
        }
        if (size > MaxFrameCount)
        {
            LastStatus = $"invalid replay frame count: {size}";
            return Array.Empty<LazerReplayFrame>();
        }

        var cachedTimeOffset = LastReplayFrameTimeOffset ?? _preferredReplayFrameTimeOffset;
        int? timeOffset;
        if (IsReplayFrameTimeOffsetUsable(items, size, cachedTimeOffset, deadline))
        {
            timeOffset = cachedTimeOffset;
            ResetReplayFrameTimeOffsetSearch();
            _failedTimeOffsetFramesList = 0;
            _failedTimeOffsetItems = 0;
        }
        else
        {
            timeOffset = FindReplayFrameTimeOffset(framesList, items, size, deadline);
        }
        if (timeOffset is null)
        {
            LastStatus = "replay frame time offset unavailable";
            return Array.Empty<LazerReplayFrame>();
        }
        LastReplayFrameTimeOffset = timeOffset;

        FramesListChanged = previousFramesList != 0 && previousFramesList != framesList;
        var effectiveLastSequence = FramesListChanged || lastSequence > size
            ? 0
            : lastSequence;
        var startIndex = Math.Clamp((int)Math.Max(0, effectiveLastSequence), 0, size);
        var endIndex = readAll ? size : Math.Min(size, startIndex + MaxFramesPerTick);
        var frames = new List<LazerReplayFrame>(Math.Max(0, endIndex - startIndex));
        for (var index = startIndex; index < endIndex; index++)
        {
            if (!readAll &&
                !LazerMemoryReadPolicy.MayAttemptUnit(index == startIndex, BudgetExpired(deadline)))
                break;
            try
            {
                if (ReadFrameAt(items, index, timeOffset.Value, deadline) is { } frame)
                {
                    frames.Add(frame);
                }
            }
            catch (Win32Exception)
            {
            }
            catch (AccessViolationException)
            {
            }
            catch (ArgumentOutOfRangeException)
            {
            }
        }

        LastStatus = frames.Count == 0 ? "no new sane replay frames" : null;
        return frames;
    }

    private IReadOnlyList<nint> FindPlayers(bool allowDiscovery, long deadline)
    {
        var gameBase = FindGameBase(allowDiscovery, deadline);
        if (!IsReadablePointer(gameBase))
        {
            LastStatus ??= "osu! game base unavailable";
            return Array.Empty<nint>();
        }

        var screenStack = _memory.ReadIntPtr(gameBase + _offsets.OsuGameScreenStack);
        if (!IsReadablePointer(screenStack))
        {
            LastStatus = "screen stack unavailable";
            return Array.Empty<nint>();
        }

        var stack = _memory.ReadIntPtr(screenStack + _offsets.ScreenStackStack);
        if (!IsReadablePointer(stack))
        {
            LastStatus = "screen stack list unavailable";
            return Array.Empty<nint>();
        }

        var count = _memory.ReadInt32(stack + 0x10);
        var items = _memory.ReadIntPtr(stack + 0x8);
        if (!IsReadablePointer(items) || count <= 0 || count > 128)
        {
            LastStatus = $"screen stack empty: stack=0x{stack.ToInt64():X}, count={count}, items=0x{items.ToInt64():X}";
            return Array.Empty<nint>();
        }

        var players = new List<nint>();
        for (var index = count - 1; index >= 0; index--)
        {
            if (!LazerMemoryReadPolicy.MayAttemptUnit(index == count - 1, BudgetExpired(deadline)) ||
                players.Count >= 4)
                break;
            var screen = _memory.ReadIntPtr(items + 0x10 + 0x8 * index);
            if (IsReadablePointer(screen) && LooksLikePlayer(screen))
            {
                LastStatus = null;
                players.Add(screen);
            }
        }

        if (players.Count == 0)
        {
            LastStatus = $"player screen unavailable; scanned {count} screen stack entries";
        }
        return players;
    }
}
