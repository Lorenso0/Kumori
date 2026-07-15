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
    private nint FindGameBase(bool allowDiscovery, long deadline)
    {
        var now = DateTimeOffset.UtcNow;
        // Screen transitions can briefly make ScreenStack unreadable, while a
        // compacting GC can permanently move GameBase. Tolerate the former,
        // but eventually invalidate the cached object and retry the already
        // discovered bootstrap anchors instead of remaining stuck forever.
        if (LastGameBase != 0)
        {
            if (now < _nextCachedGameBaseValidationAt)
                return LastGameBase;
            _nextCachedGameBaseValidationAt = now + CachedGameBaseValidationInterval;

            if (!BudgetExpired(deadline) && HasUsableScreenStack(LastGameBase))
            {
                _invalidCachedGameBasePolls = 0;
                return LastGameBase;
            }

            if (++_invalidCachedGameBasePolls < CachedGameBaseInvalidationChecks)
            {
                LastStatus = "cached osu! game base is temporarily unavailable";
                return LastGameBase;
            }

            LastGameBase = 0;
            _invalidCachedGameBasePolls = 0;
            _nextBootstrapCandidateRetryAt = DateTimeOffset.MinValue;
            // If the previous bootstrap anchors no longer resolve after an
            // update, bounded discovery may run again. It remains subject to
            // the 1 MiB / 3 ms step budget below.
            _discoveryExhausted = false;
            _discoveryRegions = null;
            _discoveryRegionIndex = 0;
            _discoveryRegionOffset = 0;
            _discoveryChunkSearchOffset = 0;
            _discoveryPhase = DiscoveryPhase.BootstrapPattern;
            _fallbackVtableMarker = 0;
            _fallbackMarkerResumeRegionIndex = 0;
            _fallbackMarkerResumeRegionOffset = 0;
            _fallbackMarkerResumeSearchOffset = 0;
            _nextDiscoveryStepAt = now + TimeSpan.FromSeconds(1);
        }

        if (IsReadablePointer(_preferredGameBase)
            && !BudgetExpired(deadline)
            && HasUsableScreenStack(_preferredGameBase))
        {
            LastGameBase = _preferredGameBase;
            return _preferredGameBase;
        }

        if (_bootstrapCandidates.Count > 0 && now >= _nextBootstrapCandidateRetryAt)
        {
            _nextBootstrapCandidateRetryAt = now + TimeSpan.FromSeconds(1);
            var candidatesToTry = _bootstrapCandidates.Count;
            while (candidatesToTry-- > 0 && !BudgetExpired(deadline))
            {
                var index = _bootstrapCandidateIndex++ % _bootstrapCandidates.Count;
                var candidate = TryResolveBootstrapGameBase(_bootstrapCandidates[index]);
                if (candidate == 0)
                    continue;
                LastGameBase = candidate;
                LastStatus = null;
                return candidate;
            }
        }

        if (_discoveryExhausted)
        {
            LastStatus = _bootstrapCandidates.Count > 0
                ? "osu! game base bootstrap found; waiting for its object graph to become readable"
                : "osu! game base discovery exhausted without a usable bootstrap or vtable candidate";
            return 0;
        }

        if (!allowDiscovery)
        {
            LastStatus = _bootstrapCandidates.Count > 0
                ? "osu! game base bootstrap is not readable; waiting for menu prewarm"
                : "osu! game base is not prewarmed; bounded discovery is paused";
            return 0;
        }

        if (now < _nextDiscoveryStepAt)
        {
            LastStatus = "osu! game base discovery is waiting for its next bounded scan step";
            return 0;
        }
        _nextDiscoveryStepAt = now + LazerMemoryReadPolicy.DiscoveryStepInterval;

        _discoveryRegions ??= _memory.Regions()
            .Where(region => region.RegionSize > 0 && region.RegionSize <= 256 * 1024 * 1024)
            .OrderByDescending(region => region.Writable && region.Type == 0x20000)
            .ToArray();

        var stopwatch = Stopwatch.StartNew();
        var remainingBudget = LazerMemoryReadPolicy.DiscoveryBytesPerStep;
        while (_discoveryRegionIndex < _discoveryRegions.Length
               && remainingBudget > 0
               && stopwatch.Elapsed < TimeSpan.FromMilliseconds(3))
        {
            var region = _discoveryRegions[_discoveryRegionIndex];
            var remainingInRegion = region.RegionSize - _discoveryRegionOffset;
            if (remainingInRegion <= 0)
            {
                _discoveryRegionIndex++;
                _discoveryRegionOffset = 0;
                _discoveryChunkSearchOffset = 0;
                continue;
            }

            var readSize = (int)Math.Min(
                Math.Min(DiscoveryChunkBytes, remainingBudget),
                remainingInRegion);
            var chunkAddress = region.BaseAddress + checked((int)_discoveryRegionOffset);
            try
            {
                _memory.ReadBytes(chunkAddress, _discoveryBuffer, readSize);
            }
            catch
            {
                _discoveryRegionOffset += readSize;
                _discoveryChunkSearchOffset = 0;
                remainingBudget -= readSize;
                continue;
            }

            if (_discoveryPhase == DiscoveryPhase.BootstrapPattern)
            {
                var searchStart = _discoveryChunkSearchOffset;
                var buffer = _discoveryBuffer.AsSpan(0, readSize);
                while (searchStart <= buffer.Length - ScalingContainerTargetDrawSizePattern.Length)
                {
                    var relative = buffer[searchStart..].IndexOf(ScalingContainerTargetDrawSizePattern);
                    if (relative < 0)
                        break;
                    var patternOffset = searchStart + relative;
                    var patternAddress = chunkAddress + patternOffset;
                    _discoveryChunkSearchOffset = patternOffset + 1;
                    var gameBase = TryResolveBootstrapGameBase(patternAddress);
                    if (gameBase != 0)
                    {
                        LastGameBase = gameBase;
                        LastStatus = null;
                        return gameBase;
                    }
                    if (_bootstrapCandidates.Count < 64 && !_bootstrapCandidates.Contains(patternAddress))
                        _bootstrapCandidates.Add(patternAddress);
                    searchStart = _discoveryChunkSearchOffset;
                    if (BudgetExpired(deadline))
                    {
                        LastStatus = "osu! game base bootstrap discovery will resume within the current bounded chunk";
                        return 0;
                    }
                }
            }
            else if (_discoveryPhase == DiscoveryPhase.VtableMarker)
            {
                var markerOffset = FindPointerOffsetInDiscoveryBuffer(
                    readSize,
                    (nint)_offsets.GameBaseVtable,
                    _discoveryChunkSearchOffset);
                if (markerOffset >= 0)
                {
                    _fallbackVtableMarker = chunkAddress + markerOffset;
                    _discoveryChunkSearchOffset = markerOffset + sizeof(long);
                    _fallbackMarkerResumeRegionIndex = _discoveryRegionIndex;
                    _fallbackMarkerResumeRegionOffset = _discoveryRegionOffset;
                    _fallbackMarkerResumeSearchOffset = _discoveryChunkSearchOffset;
                    _discoveryPhase = DiscoveryPhase.GameBaseObject;
                    ResetDiscoveryCursor();
                    LastStatus = "osu! game base vtable marker found; bounded object discovery will continue";
                    return 0;
                }
            }
            else if (_discoveryPhase == DiscoveryPhase.GameBaseObject)
            {
                while (true)
                {
                    var candidateOffset = FindPointerOffsetInDiscoveryBuffer(
                        readSize,
                        _fallbackVtableMarker,
                        _discoveryChunkSearchOffset);
                    if (candidateOffset < 0)
                        break;
                    _discoveryChunkSearchOffset = candidateOffset + sizeof(long);
                    var candidate = chunkAddress + candidateOffset;
                    if (IsGameBase(candidate) && HasUsableScreenStack(candidate))
                    {
                        LastGameBase = candidate;
                        LastStatus = null;
                        return candidate;
                    }
                    if (BudgetExpired(deadline))
                    {
                        LastStatus = "osu! game base object fallback will resume within the current bounded chunk";
                        return 0;
                    }
                }
            }

            var overlap = _discoveryPhase == DiscoveryPhase.BootstrapPattern
                ? ScalingContainerTargetDrawSizePattern.Length - 1
                : 0;
            var advance = remainingInRegion <= readSize ? readSize : Math.Max(1, readSize - overlap);
            _discoveryRegionOffset += advance;
            _discoveryChunkSearchOffset = 0;
            remainingBudget -= readSize;
        }

        if (_discoveryRegionIndex >= _discoveryRegions.Length)
        {
            if (_discoveryPhase == DiscoveryPhase.BootstrapPattern && _offsets.GameBaseVtable > 0)
            {
                _discoveryPhase = DiscoveryPhase.VtableMarker;
                ResetDiscoveryCursor();
                LastStatus = "osu! game base bootstrap scan completed; bounded vtable fallback will continue";
            }
            else if (_discoveryPhase == DiscoveryPhase.GameBaseObject)
            {
                // A region can contain multiple values that look like the
                // vtable marker. Resume the bounded marker scan after the last
                // one instead of permanently negative-caching a false lead.
                _discoveryPhase = DiscoveryPhase.VtableMarker;
                _discoveryRegionIndex = _fallbackMarkerResumeRegionIndex;
                _discoveryRegionOffset = _fallbackMarkerResumeRegionOffset;
                _discoveryChunkSearchOffset = _fallbackMarkerResumeSearchOffset;
                _fallbackVtableMarker = 0;
                LastStatus = "osu! game base object was not found for the current vtable marker; bounded fallback will continue";
            }
            else
            {
                _discoveryExhausted = true;
                LastStatus = "osu! game base discovery completed without a usable candidate";
            }
        }
        else
        {
            LastStatus = $"osu! game base {_discoveryPhase switch
            {
                DiscoveryPhase.BootstrapPattern => "bootstrap",
                DiscoveryPhase.VtableMarker => "vtable-marker fallback",
                _ => "object fallback",
            }} discovery in progress ({_discoveryRegionIndex + 1}/{_discoveryRegions.Length} regions)";
        }
        return 0;
    }

    private void ResetDiscoveryCursor()
    {
        _discoveryRegionIndex = 0;
        _discoveryRegionOffset = 0;
        _discoveryChunkSearchOffset = 0;
    }

    private int FindPointerOffsetInDiscoveryBuffer(
        int readSize,
        nint value,
        int searchOffset)
        => LazerMemoryReadPolicy.FindAlignedPointerOffset(
            _discoveryBuffer.AsSpan(0, readSize),
            value.ToInt64(),
            searchOffset);

    private nint TryResolveBootstrapGameBase(nint patternAddress)
    {
        // osu!lazer has shifted this field relative to the ScalingContainer
        // anchor across releases. The candidate dereferences are constant-size;
        // the surrounding pattern search is incrementally budgeted above.
        foreach (var delta in BootstrapDeltas)
        {
            try
            {
                var externalLinkOpener = _memory.ReadIntPtr(patternAddress - delta);
                if (!IsReadablePointer(externalLinkOpener))
                    continue;
                var api = _memory.ReadIntPtr(externalLinkOpener + _offsets.ExternalLinkOpenerApi);
                if (!IsReadablePointer(api))
                    continue;
                var game = _memory.ReadIntPtr(api + _offsets.ApiAccessGame);
                if (IsReadablePointer(game) && HasUsableScreenStack(game))
                    return game;
            }
            catch
            {
                // A wrong delta can land on an unreadable field.
                continue;
            }
        }
        return 0;
    }

    private bool IsGameBase(nint address)
    {
        if (!IsReadablePointer(address) || _offsets.GameBaseVtable <= 0)
        {
            return false;
        }

        try
        {
            var vtable = _memory.ReadIntPtr(address);
            return IsReadablePointer(vtable) && _memory.ReadInt64(vtable) == _offsets.GameBaseVtable;
        }
        catch
        {
            return false;
        }
    }

    private bool HasUsableScreenStack(nint gameBase)
    {
        try
        {
            var screenStack = _memory.ReadIntPtr(gameBase + _offsets.OsuGameScreenStack);
            if (!IsReadablePointer(screenStack))
            {
                return false;
            }

            var stack = _memory.ReadIntPtr(screenStack + _offsets.ScreenStackStack);
            if (!IsReadablePointer(stack))
            {
                return false;
            }

            var count = _memory.ReadInt32(stack + 0x10);
            var items = _memory.ReadIntPtr(stack + 0x8);
            return IsReadablePointer(items) && count > 0 && count <= 128;
        }
        catch
        {
            return false;
        }
    }

    private bool LooksLikePlayer(nint address)
    {
        try
        {
            var score = _memory.ReadIntPtr(address + _offsets.PlayerScore);
            return IsReadablePointer(score);
        }
        catch
        {
            return false;
        }
    }

    private LazerReplayFrame? ReadFrameAt(
        nint items,
        int index,
        int replayFrameTimeOffset,
        long deadline)
    {
        var frame = ReadItem(items, index);
        if (!IsReadablePointer(frame))
        {
            return null;
        }

        var mapTimeMs = _memory.ReadDouble(frame + replayFrameTimeOffset);
        var x = _memory.ReadFloat(frame + ReplayFramePositionOffset);
        var y = _memory.ReadFloat(frame + ReplayFramePositionOffset + 0x4);
        if (!IsSaneReplayFrame(mapTimeMs, x, y))
        {
            return null;
        }

        var (leftPressed, rightPressed) = ReadActionsFromFrame(frame, deadline);
        return new LazerReplayFrame
        {
            MapTimeMs = mapTimeMs,
            X = x,
            Y = y,
            LeftPressed = leftPressed,
            RightPressed = rightPressed,
            Focused = true,
            Paused = false,
            Sequence = index + 1,
        };
    }

    private int? FindReplayFrameTimeOffset(nint framesList, nint items, int size, long deadline)
    {
        if (size < 2)
        {
            ResetReplayFrameTimeOffsetSearch();
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        if (_failedTimeOffsetFramesList == framesList
            && _failedTimeOffsetItems == items
            && now < _nextTimeOffsetSearchAt)
        {
            return null;
        }

        if (_timeOffsetSearchFramesList != framesList || _timeOffsetSearchItems != items)
        {
            BeginReplayFrameTimeOffsetSearch(framesList, items, size);
        }

        while (_timeOffsetCandidateIndex < ReplayFrameTimeOffsetCandidates.Length)
        {
            var offset = ReplayFrameTimeOffsetCandidates[_timeOffsetCandidateIndex];
            while (_timeOffsetSampleIndex < _timeOffsetSampleCount && !_timeOffsetCandidateInvalid)
            {
                if (BudgetExpired(deadline))
                {
                    // Retain both candidate and sample position. Restarting at
                    // candidate zero every 16 ms can permanently starve a valid
                    // later offset while consuming the full live-read budget.
                    return null;
                }

                var index = _timeOffsetSampleIndex * _timeOffsetSampleStep;
                double time;
                try
                {
                    var frame = ReadItem(items, index);
                    if (!IsReadablePointer(frame))
                    {
                        _timeOffsetCandidateInvalid = true;
                        continue;
                    }

                    time = _memory.ReadDouble(frame + offset);
                }
                catch
                {
                    _timeOffsetCandidateInvalid = true;
                    continue;
                }

                if (!double.IsFinite(time) || time <= -300_000 || time >= 12 * 60 * 60 * 1000)
                {
                    _timeOffsetCandidateInvalid = true;
                    continue;
                }

                if (_timeOffsetPrevious is not null && time < _timeOffsetPrevious - 0.001)
                {
                    _timeOffsetCandidateInvalid = true;
                    continue;
                }

                _timeOffsetFirst ??= time;
                _timeOffsetLast = time;
                _timeOffsetPrevious = time;
                _timeOffsetSaneCount++;
                _timeOffsetSampleIndex++;
            }

            if (!_timeOffsetCandidateInvalid && _timeOffsetSaneCount == _timeOffsetSampleCount)
            {
                var span = (_timeOffsetLast ?? 0) - (_timeOffsetFirst ?? 0);
                if (_timeOffsetSearchSize <= 1 || span > 0.001)
                {
                    var score = _timeOffsetSaneCount * 1000 + span;
                    if (score > _timeOffsetBestScore)
                    {
                        _timeOffsetBestOffset = offset;
                        _timeOffsetBestScore = score;
                    }
                }
            }

            AdvanceReplayFrameTimeOffsetCandidate();
        }

        var bestOffset = _timeOffsetBestOffset;
        ResetReplayFrameTimeOffsetSearch();
        if (bestOffset is null)
        {
            _failedTimeOffsetFramesList = framesList;
            _failedTimeOffsetItems = items;
            _nextTimeOffsetSearchAt = now + TimeSpan.FromMilliseconds(100);
        }
        return bestOffset;
    }

    private void BeginReplayFrameTimeOffsetSearch(nint framesList, nint items, int size)
    {
        ResetReplayFrameTimeOffsetSearch();
        _failedTimeOffsetFramesList = 0;
        _failedTimeOffsetItems = 0;
        _timeOffsetSearchFramesList = framesList;
        _timeOffsetSearchItems = items;
        _timeOffsetSearchSize = size;
        _timeOffsetSampleCount = Math.Min(size, 4);
        _timeOffsetSampleStep = Math.Max(1, size / _timeOffsetSampleCount);
    }

    private void AdvanceReplayFrameTimeOffsetCandidate()
    {
        _timeOffsetCandidateIndex++;
        _timeOffsetSampleIndex = 0;
        _timeOffsetPrevious = null;
        _timeOffsetFirst = null;
        _timeOffsetLast = null;
        _timeOffsetSaneCount = 0;
        _timeOffsetCandidateInvalid = false;
    }

    private void ResetReplayFrameTimeOffsetSearch()
    {
        _timeOffsetSearchFramesList = 0;
        _timeOffsetSearchItems = 0;
        _timeOffsetSearchSize = 0;
        _timeOffsetSampleCount = 0;
        _timeOffsetSampleStep = 0;
        _timeOffsetCandidateIndex = 0;
        _timeOffsetSampleIndex = 0;
        _timeOffsetPrevious = null;
        _timeOffsetFirst = null;
        _timeOffsetLast = null;
        _timeOffsetSaneCount = 0;
        _timeOffsetCandidateInvalid = false;
        _timeOffsetBestOffset = null;
        _timeOffsetBestScore = double.NegativeInfinity;
    }

    private bool IsReplayFrameTimeOffsetUsable(nint items, int size, int? offset, long deadline)
    {
        if (offset is not { } value || size < 2)
        {
            return false;
        }

        try
        {
            if (BudgetExpired(deadline))
                return false;
            var firstFrame = ReadItem(items, 0);
            var lastFrame = ReadItem(items, size - 1);
            if (!IsReadablePointer(firstFrame) || !IsReadablePointer(lastFrame))
            {
                return false;
            }

            var first = _memory.ReadDouble(firstFrame + value);
            var last = _memory.ReadDouble(lastFrame + value);
            return double.IsFinite(first)
                   && double.IsFinite(last)
                   && first > -300_000
                   && last < 12 * 60 * 60 * 1000
                   && last >= first - 0.001;
        }
        catch
        {
            return false;
        }
    }

    private static long DeadlineFromNow(TimeSpan budget) =>
        Stopwatch.GetTimestamp() + Math.Max(1, (long)(budget.TotalSeconds * Stopwatch.Frequency));

    private static bool BudgetExpired(long deadline) =>
        deadline != long.MaxValue && Stopwatch.GetTimestamp() >= deadline;

    private enum DiscoveryPhase
    {
        BootstrapPattern,
        VtableMarker,
        GameBaseObject,
    }

    private (bool LeftPressed, bool RightPressed) ReadActionsFromFrame(nint frame, long deadline)
    {
        foreach (var offset in ReplayFrameActionOffsets)
        {
            if (BudgetExpired(deadline))
                break;
            try
            {
                var actionsList = _memory.ReadIntPtr(frame + offset);
                var result = ReadActions(actionsList, deadline);
                if (result.Readable)
                {
                    return (result.LeftPressed, result.RightPressed);
                }
            }
            catch
            {
            }
        }

        return (false, false);
    }

    private (bool LeftPressed, bool RightPressed, bool Readable) ReadActions(nint actionsList, long deadline)
    {
        var leftPressed = false;
        var rightPressed = false;
        if (!IsReadablePointer(actionsList))
        {
            return (leftPressed, rightPressed, false);
        }

        var (size, items) = ListItemsInfo(actionsList);
        if (!IsReadablePointer(items) || size < 0 || size > 16)
        {
            return (leftPressed, rightPressed, size == 0);
        }

        for (var i = 0; i < size; i++)
        {
            if (BudgetExpired(deadline))
                return (leftPressed, rightPressed, false);
            var action = _memory.ReadInt32(items + 0x10 + 0x4 * i);
            if (action == 0)
            {
                leftPressed = true;
            }
            else if (action == 1)
            {
                rightPressed = true;
            }
        }

        return (leftPressed, rightPressed, true);
    }

    private (int Size, nint Items) ListItemsInfo(nint list)
    {
        var array = _memory.ReadIntPtr(list + 0x8);
        var size = _memory.ReadInt32(list + 0x10);
        if (!IsReadablePointer(array) || size < 0 || size > MaxFrameCount)
        {
            return (0, 0);
        }

        return (size, array);
    }

    private nint ReadItem(nint items, int index) => _memory.ReadIntPtr(items + 0x10 + 0x8 * index);

    private static bool IsReadablePointer(nint address) => address.ToInt64() > 0x10000;

    private static bool IsSaneReplayFrame(double time, double x, double y)
        => double.IsFinite(time)
           && double.IsFinite(x)
           && double.IsFinite(y)
           && time > -300_000
           && time < 12 * 60 * 60 * 1000
           && x > -10_000
           && x < 10_000
           && y > -10_000
           && y < 10_000;
}
