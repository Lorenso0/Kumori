using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Kumori.Core;
using Kumori.Tracking;

namespace Kumori.Native;

internal readonly record struct TosuGameBaseLogHint(int ProcessId, nint GameBase);

internal static class TosuGameBaseLogHintReader
{
    internal const int MaximumHeadBytes = 32 * 1024;
    internal const int MaximumTailBytes = 64 * 1024;

    internal static async Task<TosuGameBaseLogHint?> TryReadCurrentAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var path = CurrentLogPaths()
                .Select(candidate => new FileInfo(candidate))
                .Where(file => file.Exists)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Select(file => file.FullName)
                .FirstOrDefault();
            if (path is null)
                return null;

            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var length = stream.Length;
            if (length <= 0)
                return null;

            var headLength = checked((int)Math.Min(length, MaximumHeadBytes));
            var headBytes = new byte[headLength];
            var headRead = await ReadAtMostAsync(stream, headBytes, cancellationToken)
                .ConfigureAwait(false);

            byte[] tailBytes = [];
            var tailStart = Math.Max(headRead, length - MaximumTailBytes);
            if (tailStart < length)
            {
                stream.Seek(tailStart, SeekOrigin.Begin);
                tailBytes = new byte[checked((int)(length - tailStart))];
                var tailRead = await ReadAtMostAsync(stream, tailBytes, cancellationToken)
                    .ConfigureAwait(false);
                if (tailRead != tailBytes.Length)
                    Array.Resize(ref tailBytes, tailRead);
            }

            var head = Encoding.UTF8.GetString(headBytes, 0, headRead);
            var tail = tailBytes.Length == 0 ? string.Empty : Encoding.UTF8.GetString(tailBytes);
            var segmentsContiguous = tailStart <= headRead;
            if (segmentsContiguous && tail.Length > 0)
            {
                head += tail;
                tail = string.Empty;
            }
            return TosuGameBaseLogHintParser.TryParse(
                    head,
                    tail,
                    out var hint,
                    segmentsContiguous)
                ? hint
                : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private static IEnumerable<string> CurrentLogPaths()
    {
        // Vanilla tosu writes beside its executable. Kumori moves closed logs
        // to the canonical log directory at startup, so retain that as a
        // fallback for installations which configured the canonical path.
        yield return Path.Combine(AppPaths.TosuDir, "logs", "latest.log");
        yield return Path.Combine(AppPaths.TosuLogDir, "latest.log");
    }

    private static async Task<int> ReadAtMostAsync(
        Stream stream,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken)
                .ConfigureAwait(false);
            if (count == 0)
                break;
            read += count;
        }
        return read;
    }
}

internal static class TosuGameBaseLogHintParser
{
    private const string ClientMarker = "Starting regular data loop for client ";
    private const string GameBaseMarker = "GameBase address updated:";

    internal static bool TryParse(
        string head,
        string tail,
        out TosuGameBaseLogHint hint,
        bool segmentsContiguous = false)
    {
        int? currentProcessId = null;
        nint currentGameBase = 0;
        ParseSegment(head, ref currentProcessId, ref currentGameBase);
        if (!string.IsNullOrEmpty(tail))
        {
            if (segmentsContiguous)
            {
                ParseSegment(tail, ref currentProcessId, ref currentGameBase);
            }
            else
            {
                // A bounded head/tail read can omit the middle of a large log.
                // Never associate an orphan address update in the tail with a
                // process marker from the head across that unknown gap.
                int? tailProcessId = null;
                nint tailGameBase = 0;
                ParseSegment(tail, ref tailProcessId, ref tailGameBase);
                if (tailProcessId is not null)
                {
                    currentProcessId = tailProcessId;
                    currentGameBase = tailGameBase;
                }
                else
                {
                    // The skipped range may contain a newer client marker even
                    // when the visible tail has no GameBase line. Across any
                    // unknown gap, only a self-contained tail marker/address
                    // pair is safe to adopt.
                    currentProcessId = null;
                    currentGameBase = 0;
                }
            }
        }

        if (currentProcessId is not { } processId || currentGameBase == 0)
        {
            hint = default;
            return false;
        }

        hint = new TosuGameBaseLogHint(processId, currentGameBase);
        return true;
    }

    private static void ParseSegment(
        string segment,
        ref int? currentProcessId,
        ref nint currentGameBase)
    {
        using var lines = new StringReader(segment);
        while (lines.ReadLine() is { } line)
        {
            var clientMarker = line.IndexOf(ClientMarker, StringComparison.Ordinal);
            if (clientMarker >= 0)
            {
                var pidText = line.AsSpan(clientMarker + ClientMarker.Length).Trim();
                var digitCount = 0;
                while (digitCount < pidText.Length && char.IsAsciiDigit(pidText[digitCount]))
                    digitCount++;
                currentProcessId = int.TryParse(
                    pidText[..digitCount],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var processId)
                        ? processId
                        : null;
                currentGameBase = 0;
                continue;
            }

            if (currentProcessId is null ||
                line.IndexOf(GameBaseMarker, StringComparison.Ordinal) < 0)
                continue;

            // Any update supersedes the older address, including an explicit
            // transition to undefined.
            currentGameBase = 0;
            var arrow = line.LastIndexOf("=>", StringComparison.Ordinal);
            if (arrow < 0)
                continue;
            var addressText = line.AsSpan(arrow + 2).Trim();
            var tokenLength = 0;
            if (addressText.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                tokenLength = 2;
            while (tokenLength < addressText.Length && IsHex(addressText[tokenLength]))
                tokenLength++;
            var token = addressText[..tokenLength];
            if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                token = token[2..];
            if (token.Length == 0 ||
                !ulong.TryParse(token, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var address) ||
                address > long.MaxValue)
                continue;
            currentGameBase = (nint)(long)address;
        }
    }

    private static bool IsHex(char value) =>
        char.IsAsciiDigit(value) || value is >= 'a' and <= 'f' or >= 'A' and <= 'F';
}
