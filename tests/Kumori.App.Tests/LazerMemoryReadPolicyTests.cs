using Kumori.Native;
using Kumori.Tracking;
using System.Buffers.Binary;
using Xunit;

namespace Kumori.App.Tests;

public sealed class LazerMemoryReadPolicyTests
{
    [Fact]
    public void ParsesLatestTosuSessionAndHexGameBaseFromBoundedSegments()
    {
        const string head = """
            00:00:01.103 [debug] Starting regular data loop for client 111
            00:00:02.555 [debug] lazer GameBase address updated: undefined => aaaabbbb
            """;
        const string tail = """
            00:10:01.103 [debug] Starting regular data loop for client 51016
            00:10:02.555 [debug] lazer GameBase address updated: undefined => bf388910
            """;

        Assert.True(TosuGameBaseLogHintParser.TryParse(head, tail, out var hint));
        Assert.Equal(51016, hint.ProcessId);
        Assert.Equal(unchecked((nint)0xbf388910L), hint.GameBase);
        Assert.Equal(32 * 1024, TosuGameBaseLogHintReader.MaximumHeadBytes);
        Assert.Equal(64 * 1024, TosuGameBaseLogHintReader.MaximumTailBytes);
    }

    [Fact]
    public void TosuHintNeverSurvivesNewSessionOrUndefinedAddress()
    {
        const string head = """
            Starting regular data loop for client 111
            lazer GameBase address updated: undefined => aaaabbbb
            """;
        const string newSessionWithoutAddress = "Starting regular data loop for client 222";
        const string addressBecameUndefined =
            "lazer GameBase address updated: aaaabbbb => undefined";
        const string orphanAddressAfterSkippedMiddle =
            "lazer GameBase address updated: undefined => bf388910";
        const string genericTailAfterSkippedMiddle =
            "lazer Current attributes updated to 4.94 stars";

        Assert.False(TosuGameBaseLogHintParser.TryParse(
            head,
            newSessionWithoutAddress,
            out _));
        Assert.False(TosuGameBaseLogHintParser.TryParse(
            head,
            addressBecameUndefined,
            out _));
        Assert.False(TosuGameBaseLogHintParser.TryParse(
            head,
            orphanAddressAfterSkippedMiddle,
            out _));
        Assert.False(TosuGameBaseLogHintParser.TryParse(
            head,
            genericTailAfterSkippedMiddle,
            out _));
    }

    [Fact]
    public void TosuHintAdoptionRequiresNativeVtableAndScreenStackValidation()
    {
        var candidate = unchecked((nint)0xbf388910L);
        Assert.False(TosuGameBaseAdoptionPolicy.ShouldAdopt(0, true, true));
        Assert.False(TosuGameBaseAdoptionPolicy.ShouldAdopt(candidate, false, true));
        Assert.False(TosuGameBaseAdoptionPolicy.ShouldAdopt(candidate, true, false));
        Assert.True(TosuGameBaseAdoptionPolicy.ShouldAdopt(candidate, true, true));
    }

    [Fact]
    public void TosuPidWinsAcrossAliasesOtherwiseNewestLazerWins()
    {
        var candidates = new[]
        {
            new LazerProcessCandidate(100, new DateTime(2026, 7, 14, 7, 0, 0)),
            new LazerProcessCandidate(200, new DateTime(2026, 7, 14, 8, 0, 0)),
        };

        Assert.Equal(100, LazerProcessSelectionPolicy.Select(candidates, 100));
        Assert.Equal(200, LazerProcessSelectionPolicy.Select(candidates, 999));
        Assert.Equal(200, LazerProcessSelectionPolicy.Select(candidates, null));
    }

    [Fact]
    public void NewReplayGenerationClearsOldAttemptSnapshotBeforeAppending()
    {
        var attemptFrames = Enumerable.Range(1, 100)
            .Select(sequence => new LazerReplayFrame { Sequence = sequence })
            .ToList();
        var replacement = new[]
        {
            new LazerReplayFrame { Sequence = 1 },
            new LazerReplayFrame { Sequence = 2 },
        };
        var changed = LazerAttemptFrameBufferPolicy.BeginsNewGeneration(
            framesListChanged: true,
            previousSequence: 100,
            replacement);

        LazerAttemptFrameBufferPolicy.Append(
            attemptFrames,
            replacement,
            attemptActive: true,
            beginsNewGeneration: changed);

        Assert.True(changed);
        Assert.Equal(new long?[] { 1, 2 }, attemptFrames.Select(frame => frame.Sequence));
        Assert.True(LazerAttemptFrameBufferPolicy.BeginsNewGeneration(
            framesListChanged: false,
            previousSequence: 100,
            replacement));
    }

    [Fact]
    public void MissingGameBaseEnablesBoundedDiscoveryDuringLiveCapture()
    {
        Assert.True(LazerMemoryReadPolicy.ShouldDiscover(0));
        Assert.False(LazerMemoryReadPolicy.ShouldDiscover((nint)0x10000));
        Assert.True(LazerMemoryReadPolicy.ShouldRearmDiscovery(0, discoveryExhausted: true));
        Assert.False(LazerMemoryReadPolicy.ShouldRearmDiscovery(0, discoveryExhausted: false));
        Assert.False(LazerMemoryReadPolicy.ShouldRearmDiscovery((nint)0x10000, discoveryExhausted: true));
        Assert.Equal(1024 * 1024, LazerMemoryReadPolicy.DiscoveryBytesPerStep);
        Assert.Equal(TimeSpan.FromMilliseconds(16), LazerMemoryReadPolicy.DiscoveryStepInterval);
        Assert.True(
            LazerMemoryReadPolicy.DiscoveryBytesPerStep /
            LazerMemoryReadPolicy.DiscoveryStepInterval.TotalSeconds
            >= 60 * 1024 * 1024,
            "Continuous bounded discovery must make meaningful progress before a short map finishes.");
        Assert.InRange(LazerMemoryReadPolicy.DiscoveryReadBudget.TotalMilliseconds, 3, 4);
    }

    [Fact]
    public void SoftDeadlineAlwaysAllowsOneProgressUnit()
    {
        Assert.True(LazerMemoryReadPolicy.MayAttemptUnit(isFirst: true, budgetExpired: true));
        Assert.True(LazerMemoryReadPolicy.MayAttemptUnit(isFirst: false, budgetExpired: false));
        Assert.False(LazerMemoryReadPolicy.MayAttemptUnit(isFirst: false, budgetExpired: true));
    }

    [Fact]
    public void PointerFallbackStaysAlignedAndCanResumePastAStaleMatch()
    {
        const long pointer = 0x1020_3040_5060_7080;
        var buffer = new byte[40];
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(0, sizeof(long)), pointer);
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(16, sizeof(long)), pointer);

        Assert.Equal(0, LazerMemoryReadPolicy.FindAlignedPointerOffset(buffer, pointer, 0));
        Assert.Equal(16, LazerMemoryReadPolicy.FindAlignedPointerOffset(buffer, pointer, sizeof(long)));
        Assert.Equal(16, LazerMemoryReadPolicy.FindAlignedPointerOffset(buffer, pointer, 9));
        Assert.Equal(-1, LazerMemoryReadPolicy.FindAlignedPointerOffset(buffer, pointer, 24));
    }
}
