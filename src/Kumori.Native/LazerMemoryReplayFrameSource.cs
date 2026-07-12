using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Kumori.Core;
using Kumori.Tracking;

namespace Kumori.Native;

public sealed class LazerMemoryReplayFrameSource : ILazerReplayFrameSource, ILazerReplayFrameSnapshotSource
{
    private static readonly string[] ProcessNames = ["osu!", "osu"];
    private readonly TimeSpan _pollInterval;
    private readonly IReplayFrameStatusSink _status;
    private readonly string? _offsetsPath;
    private long _lastSequence;
    private nint _lastFramesList;
    private nint _lastGameBase;
    private int? _lastProcessId;
    private int? _lastReplayFrameTimeOffset;

    public LazerMemoryReplayFrameSource(
        TimeSpan? pollInterval = null,
        IReplayFrameStatusSink? status = null,
        string? offsetsPath = null)
    {
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(16);
        _status = status ?? new DelegatingReplayFrameStatusSink();
        _offsetsPath = offsetsPath;
    }

    public async IAsyncEnumerable<LazerReplayFrame> ReadFramesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Refresh the upstream tosu offsets once when the reader starts. The
        // snapshot path intentionally reuses this cache so finalisation never
        // blocks on the network.
        var offsets = LazerMemoryOffsets.Load(_offsetsPath, refreshOfficialCache: _offsetsPath is null);
        _status.Update(s =>
        {
            s.Enabled = true;
            s.State = "lazer_memory_starting";
            s.Detail = $"Loaded osu!lazer offsets {offsets.OsuVersion}.";
            s.LastError = null;
        });

        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(_pollInterval, cancellationToken);
            IReadOnlyList<LazerReplayFrame> frames = Array.Empty<LazerReplayFrame>();

            using var process = FindProcess();
            if (process is null)
            {
                ResetCachedPointers();
                _status.Update(s =>
                {
                    s.State = "osu_lazer_not_running";
                    s.Detail = "osu!lazer process was not found.";
                    s.LastError = null;
                    s.ProcessId = null;
                    s.ProcessName = null;
                    s.ProcessPath = null;
                });
                continue;
            }

            try
            {
                var processId = process.Id;
                if (_lastProcessId != processId)
                {
                    ResetCachedPointers();
                    _lastProcessId = processId;
                }
                var processName = process.ProcessName;
                var processPath = SafeProcessPath(process);
                _status.Update(s =>
                {
                    s.ProcessId = processId;
                    s.ProcessName = processName;
                    s.ProcessPath = processPath;
                });
                using var memory = ProcessMemory.Open(process);
                var reader = new LazerReplayFrameMemoryReader(
                    memory,
                    offsets,
                    _lastReplayFrameTimeOffset,
                    _lastGameBase);
                frames = reader.ReadFramesAfter(_lastSequence, _lastFramesList);
                if (reader.LastGameBase != 0)
                {
                    _lastGameBase = reader.LastGameBase;
                }
                if (reader.LastReplayFrameTimeOffset is { } timeOffset)
                {
                    _lastReplayFrameTimeOffset = timeOffset;
                }
                if (reader.FramesListChanged)
                {
                    _lastSequence = 0;
                }
                if (reader.LastFramesList != 0)
                {
                    _lastFramesList = reader.LastFramesList;
                }
                if (frames.Count == 0)
                {
                    _status.Update(s =>
                    {
                        s.State = "lazer_memory_waiting";
                        s.Detail = reader.LastStatus ?? "osu!lazer is running; replay frames are not available yet.";
                        s.LastError = null;
                    });
                    continue;
                }

            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 5)
            {
                _status.Update(s =>
                {
                    s.State = "lazer_memory_access_denied";
                    s.Detail = "Could not read osu!lazer memory. Run the tool with matching elevation if osu!lazer is elevated.";
                    s.LastError = ex.Message;
                });
                continue;
            }
            catch (Exception ex)
            {
                _status.Update(s =>
                {
                    s.State = "lazer_memory_error";
                    s.Detail = "osu!lazer memory reader failed; waiting for the next poll.";
                    s.LastError = ex.Message;
                });
                continue;
            }

            var lastFrame = frames[^1];
            _status.Update(s =>
            {
                s.State = "lazer_memory_frame";
                s.Detail = $"Reading replay frames from osu!lazer memory ({frames.Count} frame batch).";
                s.FramesEmitted += frames.Count;
                s.LastFrameMapTimeMs = lastFrame.MapTimeMs;
                s.LastFrameX = lastFrame.X;
                s.LastFrameY = lastFrame.Y;
                s.LastFrameLeftPressed = lastFrame.LeftPressed;
                s.LastFrameRightPressed = lastFrame.RightPressed;
                s.LastError = null;
            });

            foreach (var frame in frames)
            {
                _lastSequence = Math.Max(_lastSequence, frame.Sequence ?? _lastSequence);
                yield return frame;
            }
        }
    }

    public IReadOnlyList<LazerReplayFrame> ReadCurrentFramesSnapshot()
    {
        var offsets = LazerMemoryOffsets.Load(_offsetsPath);
        using var process = FindProcess();
        if (process is null)
        {
            return Array.Empty<LazerReplayFrame>();
        }

        using var memory = ProcessMemory.Open(process);
        if (_lastProcessId != process.Id)
        {
            ResetCachedPointers();
            _lastProcessId = process.Id;
        }
        var reader = new LazerReplayFrameMemoryReader(
            memory,
            offsets,
            _lastReplayFrameTimeOffset,
            _lastGameBase);
        var frames = reader.ReadAllFrames();
        if (reader.LastGameBase != 0)
        {
            _lastGameBase = reader.LastGameBase;
        }
        if (reader.LastReplayFrameTimeOffset is { } timeOffset)
        {
            _lastReplayFrameTimeOffset = timeOffset;
        }
        if (reader.LastFramesList != 0)
        {
            _lastFramesList = reader.LastFramesList;
        }
        return frames;
    }

    private void ResetCachedPointers()
    {
        _lastSequence = 0;
        _lastFramesList = 0;
        _lastGameBase = 0;
        _lastReplayFrameTimeOffset = null;
        _lastProcessId = null;
    }

    private static string? SafeProcessPath(Process process)
    {
        try { return process.MainModule?.FileName; }
        catch { return null; }
    }

    private static Process? FindProcess()
    {
        foreach (var name in ProcessNames)
        {
            var process = Process.GetProcessesByName(name)
                .Where(IsLikelyLazer)
                .OrderByDescending(p =>
                {
                    try { return p.StartTime; }
                    catch { return DateTime.MinValue; }
                })
                .FirstOrDefault();
            if (process is not null)
            {
                return process;
            }
        }

        return null;
    }

    private static bool IsLikelyLazer(Process process)
    {
        try
        {
            string? directory = Path.GetDirectoryName(process.MainModule?.FileName);
            // Stable installations own Songs and Data/r next to osu!.exe.
            // Lazer may use the same process name, so never attach its 64-bit
            // reader to an obvious stable installation.
            return directory is not null
                   && !Directory.Exists(Path.Combine(directory, "Songs"))
                   && !Directory.Exists(Path.Combine(directory, "Data", "r"));
        }
        catch { return false; }
    }
}

internal sealed class LazerReplayFrameMemoryReader
{
    private static readonly byte?[] ScalingContainerTargetDrawSizePattern =
    [
        0x00, 0x00, 0x80, 0x44,
        0x00, 0x00, 0x40, 0x44,
    ];

    private const int ScoreReplayOffset = 0x10;
    private const int ReplayFramesOffset = 0x8;
    private const int ReplayFramePositionOffset = 0x20;
    private const int ReplayFrameActionsOffset = 0x18;
    private const int MaxFrameCount = 1_000_000;
    private const int MaxFramesPerTick = 4096;

    private readonly ProcessMemory _memory;
    private readonly LazerMemoryOffsets _offsets;
    private readonly int? _preferredReplayFrameTimeOffset;
    private readonly nint _preferredGameBase;

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

    public IReadOnlyList<LazerReplayFrame> ReadFramesAfter(long lastSequence, nint previousFramesList)
    {
        LastFramesList = 0;
        FramesListChanged = false;
        var players = FindPlayers();
        if (players.Count == 0)
        {
            LastStatus ??= "player screen unavailable";
            return Array.Empty<LazerReplayFrame>();
        }

        foreach (var player in players)
        {
            try
            {
                var frames = ReadFramesAfter(player, lastSequence, previousFramesList);
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
        LastFramesList = 0;
        FramesListChanged = false;
        var players = FindPlayers();
        if (players.Count == 0)
        {
            LastStatus ??= "player screen unavailable";
            return Array.Empty<LazerReplayFrame>();
        }

        foreach (var player in players)
        {
            try
            {
                var frames = ReadFramesAfter(player, lastSequence: 0, previousFramesList: 0, readAll: true);
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

    private IReadOnlyList<LazerReplayFrame> ReadFramesAfter(nint player, long lastSequence, nint previousFramesList, bool readAll = false)
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

        var timeOffset = IsReplayFrameTimeOffsetUsable(items, size, _preferredReplayFrameTimeOffset)
            ? _preferredReplayFrameTimeOffset
            : FindReplayFrameTimeOffset(items, size);
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
        if (!readAll)
        {
            startIndex = Math.Max(startIndex, size - MaxFramesPerTick);
        }
        var frames = new List<LazerReplayFrame>();
        for (var index = startIndex; index < size; index++)
        {
            try
            {
                if (ReadFrameAt(items, index, timeOffset.Value) is { } frame)
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

    private IReadOnlyList<nint> FindPlayers()
    {
        var gameBase = FindGameBase();
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

    private nint FindGameBase()
    {
        // Resolving GameBase requires scanning large portions of osu!'s managed
        // heap. Doing that on every 16 ms poll can occasionally take tens of
        // seconds and leave an otherwise exact replay with a missing tail.
        // The object is stable for the process lifetime, so reuse it while its
        // vtable and screen stack still validate, then fall back to a scan.
        if (IsGameBase(_preferredGameBase) && HasUsableScreenStack(_preferredGameBase))
        {
            LastGameBase = _preferredGameBase;
            return _preferredGameBase;
        }

        foreach (var fromPattern in FindGameBasesFromTosuBootstrapPattern())
        {
            // The bootstrap pattern follows a live object graph directly to
            // OsuGame. Its vtable marker can lag an osu!lazer update even when
            // the graph and ScreenStack offsets are already valid. The screen
            // stack is the data this reader actually needs and is a stronger
            // practical validation here than rejecting the candidate solely on
            // a stale vtable value.
            if (HasUsableScreenStack(fromPattern))
            {
                LastGameBase = fromPattern;
                return fromPattern;
            }
        }

        // Fallback: scan for object references whose vtable points at the
        // expected GameBase vtable marker. This is slower and less precise
        // than tosu's bootstrap path, but keeps diagnostics alive if the
        // pattern changes.
        foreach (var region in _memory.Regions())
        {
            if (region.RegionSize <= 0 || region.RegionSize > 256 * 1024 * 1024)
            {
                continue;
            }

            var vtableMarker = _memory.FindPointer(region.BaseAddress, region.RegionSize, (nint)_offsets.GameBaseVtable);
            if (vtableMarker == 0)
            {
                continue;
            }

            var gameBase = FindObjectWithVtable(vtableMarker);
            if (IsGameBase(gameBase) && HasUsableScreenStack(gameBase))
            {
                LastGameBase = gameBase;
                return gameBase;
            }
        }

        LastStatus = "osu! game base unavailable; no bootstrap or fallback candidate had a usable screen stack";
        return 0;
    }

    private IEnumerable<nint> FindGameBasesFromTosuBootstrapPattern()
    {
        foreach (var region in _memory.Regions())
        {
            if (region.RegionSize <= 0 || region.RegionSize > 256 * 1024 * 1024)
            {
                continue;
            }

            foreach (var patternAddress in _memory.FindPatterns(region.BaseAddress, region.RegionSize, ScalingContainerTargetDrawSizePattern, maxMatches: 16))
            {
                // osu!lazer has shifted this field relative to the
                // ScalingContainer anchor across releases. Mirror tosu's
                // compatible sweep instead of pinning the old 0x24 layout.
                foreach (var delta in new[] { 0x24, 0x28, 0x2c, 0x20, 0x30, 0x1c, 0x34 })
                {
                    nint game = 0;
                    try
                    {
                        var externalLinkOpener = _memory.ReadIntPtr(patternAddress - delta);
                        if (!IsReadablePointer(externalLinkOpener))
                        {
                            continue;
                        }

                        var api = _memory.ReadIntPtr(externalLinkOpener + _offsets.ExternalLinkOpenerApi);
                        if (!IsReadablePointer(api))
                        {
                            continue;
                        }

                        game = _memory.ReadIntPtr(api + _offsets.ApiAccessGame);
                    }
                    catch
                    {
                        // A wrong delta can land on an unreadable field; the
                        // remaining offsets are still valid candidates.
                    }

                    if (IsReadablePointer(game) && HasUsableScreenStack(game))
                    {
                        yield return game;
                    }
                }
            }
        }
    }

    private nint FindObjectWithVtable(nint vtableAddress)
    {
        foreach (var region in _memory.Regions())
        {
            if (region.RegionSize <= 0 || region.RegionSize > 256 * 1024 * 1024)
            {
                continue;
            }

            var candidate = _memory.FindPointer(region.BaseAddress, region.RegionSize, vtableAddress);
            if (candidate != 0)
            {
                return candidate;
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

    private LazerReplayFrame? ReadFrameAt(nint items, int index, int replayFrameTimeOffset)
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

        var (leftPressed, rightPressed) = ReadActionsFromFrame(frame);
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

    private int? FindReplayFrameTimeOffset(nint items, int size)
    {
        if (size < 2)
        {
            return null;
        }

        var candidates = new[] { 0x8, 0x10, 0x18, 0x28, 0x30 };
        int? bestOffset = null;
        var bestScore = double.NegativeInfinity;
        var sampleCount = Math.Min(size, 64);
        var step = Math.Max(1, size / sampleCount);

        foreach (var offset in candidates)
        {
            double? previous = null;
            double? first = null;
            double? last = null;
            var monotonic = true;
            var saneCount = 0;
            var index = 0;

            while (index < size && saneCount < sampleCount)
            {
                double time;
                try
                {
                    var frame = ReadItem(items, index);
                    if (!IsReadablePointer(frame))
                    {
                        monotonic = false;
                        break;
                    }

                    time = _memory.ReadDouble(frame + offset);
                }
                catch
                {
                    monotonic = false;
                    break;
                }

                if (!double.IsFinite(time) || time <= -300_000 || time >= 12 * 60 * 60 * 1000)
                {
                    monotonic = false;
                    break;
                }

                if (previous is not null && time < previous - 0.001)
                {
                    monotonic = false;
                    break;
                }

                first ??= time;
                last = time;
                previous = time;
                saneCount++;
                index += step;
            }

            if (!monotonic || saneCount < Math.Min(size, 4))
            {
                continue;
            }

            var span = (last ?? 0) - (first ?? 0);
            if (size > 1 && span <= 0.001)
            {
                continue;
            }

            var score = saneCount * 1000 + span;
            if (score > bestScore)
            {
                bestOffset = offset;
                bestScore = score;
            }
        }

        return bestOffset;
    }

    private bool IsReplayFrameTimeOffsetUsable(nint items, int size, int? offset)
    {
        if (offset is not { } value || size < 2)
        {
            return false;
        }

        try
        {
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

    private (bool LeftPressed, bool RightPressed) ReadActionsFromFrame(nint frame)
    {
        foreach (var offset in new[] { ReplayFrameActionsOffset, ReplayFrameActionsOffset + 0x8 })
        {
            try
            {
                var actionsList = _memory.ReadIntPtr(frame + offset);
                var result = ReadActions(actionsList);
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

    private (bool LeftPressed, bool RightPressed, bool Readable) ReadActions(nint actionsList)
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

internal sealed record LazerMemoryOffsets(
    string OsuVersion,
    long GameBaseVtable,
    int OsuGameScreenStack,
    int ScreenStackStack,
    int PlayerScore,
    int ExternalLinkOpenerApi,
    int ApiAccessGame)
{
    private const string OfficialOffsetsUrl =
        "https://raw.githubusercontent.com/tosuapp/tosu/master/packages/tosu/src/assets/offsets.json";

    public static LazerMemoryOffsets Load(string? path, bool refreshOfficialCache = false)
    {
        path ??= EnsureDefaultOffsetsPath(refreshOfficialCache);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("osu!lazer offsets.json was not found.", path);
        }

        return Parse(File.ReadAllText(path));
    }

    private static LazerMemoryOffsets Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return new LazerMemoryOffsets(
            root.TryGetProperty("OsuVersion", out var version) ? version.GetString() ?? "unknown" : "unknown",
            GetInt64(root, "GameBaseVtable"),
            GetOffset(root, "osu.Game.OsuGame", "<ScreenStack>k__BackingField"),
            GetOffset(root, "osu.Framework.Screens.ScreenStack", "stack"),
            GetOffset(root, "osu.Game.Screens.Play.Player", "<Score>k__BackingField"),
            GetOffset(root, "osu.Game.Online.Chat.ExternalLinkOpener", "<api>k__BackingField"),
            GetOffset(root, "osu.Game.Online.API.APIAccess", "game"));
    }

    private static string EnsureDefaultOffsetsPath(bool refreshOfficialCache)
    {
        var path = Path.Combine(AppPaths.CacheDir, "tosu", "offsets.json");
        if (File.Exists(path) && !refreshOfficialCache)
            return path;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Kumori");
            var json = http.GetStringAsync(OfficialOffsetsUrl).GetAwaiter().GetResult();
            _ = Parse(json); // validate before replacing the last known-good cache.

            var temp = path + ".new";
            File.WriteAllText(temp, json);
            File.Move(temp, path, overwrite: true);
        }
        catch when (File.Exists(path))
        {
            // Offline or malformed upstream response: a previous valid cache
            // is safer than disabling replay capture entirely.
        }

        return path;
    }

    private static int GetOffset(JsonElement root, string type, string field)
    {
        if (!root.TryGetProperty(type, out var typeElement) ||
            !typeElement.TryGetProperty(field, out var fieldElement) ||
            !fieldElement.TryGetInt32(out var offset))
        {
            throw new InvalidDataException($"Missing offset {type}.{field}.");
        }

        return offset;
    }

    private static long GetInt64(JsonElement root, string field)
    {
        if (!root.TryGetProperty(field, out var element) || !element.TryGetInt64(out var value))
        {
            throw new InvalidDataException($"Missing offset {field}.");
        }

        return value;
    }
}

public sealed class ProcessMemory : IDisposable
{
    private const int ProcessVmRead = 0x0010;
    private const int ProcessQueryInformation = 0x0400;
    private readonly nint _handle;

    private ProcessMemory(nint handle)
    {
        _handle = handle;
    }

    public static ProcessMemory Open(Process process)
    {
        var handle = OpenProcess(ProcessVmRead | ProcessQueryInformation, false, process.Id);
        if (handle == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return new ProcessMemory(handle);
    }

    public int ReadInt32(nint address) => BitConverter.ToInt32(Read(address, 4));
    public long ReadInt64(nint address) => BitConverter.ToInt64(Read(address, 8));
    public float ReadFloat(nint address) => BitConverter.ToSingle(Read(address, 4));
    public double ReadDouble(nint address) => BitConverter.ToDouble(Read(address, 8));
    public nint ReadIntPtr(nint address) => (nint)BitConverter.ToInt64(Read(address, 8));
    public byte[] ReadBytes(nint address, int count) => Read(address, count);

    public IEnumerable<MemoryRegion> Regions()
    {
        nint address = 0x10000;
        while (VirtualQueryEx(_handle, address, out var info, (nuint)Marshal.SizeOf<MemoryBasicInformation>()) != 0)
        {
            if (info.State == 0x1000 && (info.Protect & 0x100) == 0 && (info.Protect & 0x01) == 0)
            {
                bool writable = (info.Protect & (0x04 | 0x08 | 0x40 | 0x80)) != 0;
                bool executable = (info.Protect & (0x10 | 0x20 | 0x40 | 0x80)) != 0;
                yield return new MemoryRegion(info.BaseAddress, (long)info.RegionSize, writable, executable, unchecked((uint)info.Type));
            }

            var next = info.BaseAddress.ToInt64() + (long)info.RegionSize;
            if (next <= address.ToInt64())
            {
                yield break;
            }

            address = (nint)next;
        }
    }

    public nint FindPointer(nint baseAddress, long size, nint value)
    {
        const int chunkSize = 1024 * 1024;
        var needle = BitConverter.GetBytes(value.ToInt64());
        var remaining = size;
        var address = baseAddress;
        while (remaining > 0)
        {
            var readSize = (int)Math.Min(chunkSize, remaining);
            byte[] buffer;
            try
            {
                buffer = Read(address, readSize);
            }
            catch
            {
                return 0;
            }

            for (var i = 0; i <= buffer.Length - 8; i += 8)
            {
                if (buffer.AsSpan(i, 8).SequenceEqual(needle))
                {
                    return address + i;
                }
            }

            address += readSize;
            remaining -= readSize;
        }

        return 0;
    }

    public nint FindPattern(nint baseAddress, long size, IReadOnlyList<byte?> pattern)
        => FindPatterns(baseAddress, size, pattern, maxMatches: 1).FirstOrDefault();

    public IEnumerable<nint> FindPatterns(nint baseAddress, long size, IReadOnlyList<byte?> pattern, int maxMatches)
    {
        const int chunkSize = 1024 * 1024;
        var exactPattern = pattern.All(value => value.HasValue)
            ? pattern.Select(value => value!.Value).ToArray()
            : null;
        var remaining = size;
        var address = baseAddress;
        var overlap = Math.Max(0, pattern.Count - 1);
        byte[] previousTail = [];
        var matches = 0;

        while (remaining > 0)
        {
            var readSize = (int)Math.Min(chunkSize, remaining);
            byte[] current;
            try
            {
                current = Read(address, readSize);
            }
            catch
            {
                yield break;
            }

            var buffer = previousTail.Length == 0
                ? current
                : previousTail.Concat(current).ToArray();
            var bufferBase = address - previousTail.Length;
            if (exactPattern is not null)
            {
                var searchStart = 0;
                while (searchStart <= buffer.Length - exactPattern.Length)
                {
                    var found = buffer.AsSpan(searchStart).IndexOf(exactPattern);
                    if (found < 0)
                    {
                        break;
                    }

                    var index = searchStart + found;
                    yield return bufferBase + index;
                    matches++;
                    if (matches >= maxMatches)
                    {
                        yield break;
                    }
                    searchStart = index + 1;
                }
            }
            else
            {
                for (var i = 0; i <= buffer.Length - pattern.Count; i++)
                {
                    if (!Matches(buffer, i, pattern))
                    {
                        continue;
                    }

                    yield return bufferBase + i;
                    matches++;
                    if (matches >= maxMatches)
                    {
                        yield break;
                    }
                }
            }

            previousTail = current.Length > overlap
                ? current[^overlap..]
                : current;
            address += readSize;
            remaining -= readSize;
        }
    }

    private static bool Matches(byte[] buffer, int offset, IReadOnlyList<byte?> pattern)
    {
        for (var i = 0; i < pattern.Count; i++)
        {
            if (pattern[i] is { } expected && buffer[offset + i] != expected)
            {
                return false;
            }
        }

        return true;
    }

    private byte[] Read(nint address, int count)
    {
        var buffer = new byte[count];
        if (!ReadProcessMemory(_handle, address, buffer, count, out var bytesRead) || bytesRead != count)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return buffer;
    }

    public void Dispose()
    {
        if (_handle != 0)
        {
            CloseHandle(_handle);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(int desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(nint process, nint baseAddress, byte[] buffer, int size, out int bytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int VirtualQueryEx(nint process, nint address, out MemoryBasicInformation buffer, nuint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);
}

public readonly record struct MemoryRegion(nint BaseAddress, long RegionSize, bool Writable, bool Executable, uint Type);

[StructLayout(LayoutKind.Sequential)]
internal struct MemoryBasicInformation
{
    public nint BaseAddress;
    public nint AllocationBase;
    public int AllocationProtect;
    public nuint RegionSize;
    public int State;
    public int Protect;
    public int Type;
}
