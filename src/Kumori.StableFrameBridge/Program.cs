using System.Text.Json;
using Kumori.Native;

try { System.Diagnostics.Process.GetCurrentProcess().PriorityClass = System.Diagnostics.ProcessPriorityClass.BelowNormal; }
catch { }

string? gameFolder = null;
int? parentProcessId = null;
long? parentStartUtcTicks = null;
for (var index = 0; index < args.Length; index++)
{
    switch (args[index])
    {
        case "--game-folder" when index + 1 < args.Length:
            gameFolder = args[++index];
            break;
        case "--parent-pid" when index + 1 < args.Length
            && int.TryParse(args[++index], out var parsedProcessId):
            parentProcessId = parsedProcessId;
            break;
        case "--parent-start-utc-ticks" when index + 1 < args.Length
            && long.TryParse(args[++index], out var parsedStartTicks):
            parentStartUtcTicks = parsedStartTicks;
            break;
        default:
            // Preserve the bridge's historical positional game-folder option
            // for direct diagnostic launches.
            gameFolder ??= args[index];
            break;
    }
}

using System.Diagnostics.Process? parentProcess = openParentProcess(
    parentProcessId,
    parentStartUtcTicks);
if (parentProcessId is not null && parentProcess is null)
    return 4;

StableClrReplayReader? attachingReader = null;
string? lastAttachDiagnostic = null;
var attachDiagnosticInterval = System.Diagnostics.Stopwatch.StartNew();
while (attachingReader is null && parentIsAlive(parentProcess, parentStartUtcTicks))
{
    attachingReader = StableClrReplayReader.TryAttach(gameFolder);
    string diagnostic = StableClrReplayReader.LastAttachDiagnostic;
    if (diagnostic != lastAttachDiagnostic && attachDiagnosticInterval.Elapsed >= TimeSpan.FromSeconds(1))
    {
        lastAttachDiagnostic = diagnostic;
        attachDiagnosticInterval.Restart();
        Console.Error.WriteLine(diagnostic);
        Console.Error.Flush();
    }
    if (attachingReader is null)
        await Task.Delay(StableClrReplayReader.AttachPollInterval);
}
if (attachingReader is null)
    return 0;
using StableClrReplayReader reader = attachingReader;
using StableRawReplayReader? diagnosticReader = StableRawReplayReader.TryAttach(gameFolder);

string? lastDiagnostic = null;
Task<string>? diagnosticCapture = null;
bool diagnosticReported = false;
var diagnosticReportInterval = System.Diagnostics.Stopwatch.StartNew();
string diagnosticRequestPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "Kumori", "runtime", "debug", "stable-memory-snapshot.request");
string diagnosticAttemptSignalPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "Kumori", "runtime", "debug", "stable-memory-attempt-active.signal");
while (parentIsAlive(parentProcess, parentStartUtcTicks))
{
    IReadOnlyList<Kumori.Tracking.LazerReplayFrame> frames;
    bool attemptActive = File.Exists(diagnosticAttemptSignalPath);
    try
    {
        // The reader already returns only the newly appended tail. Avoid a
        // Task allocation and a second full-list emission cursor every poll.
        // The CLR attachment remains prewarmed in menus, but stable leaves a
        // stale ruleset pointer behind after gameplay. Never traverse that
        // stale graph (or the typed heap fallback) without Kumori's explicit
        // attempt signal.
        frames = attemptActive
            ? reader.ReadReplayFrames()
            : [];
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"stable memory reader error: {ex.GetType().Name}: {ex.Message}");
        Console.Error.Flush();
        return 3;
    }

    foreach (var frame in frames)
    {
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            type = "frame",
            mapTimeMs = frame.MapTimeMs,
            monotonicMs = frame.MonotonicMs,
            x = frame.X,
            y = frame.Y,
            leftPressed = frame.LeftPressed,
            rightPressed = frame.RightPressed,
            sequence = frame.Sequence,
        }));
    }
    if (frames.Count > 0)
        Console.Out.Flush();
    if (reader.LastDiagnostic != lastDiagnostic
        && (lastDiagnostic is null || frames.Count > 0 || diagnosticReportInterval.Elapsed >= TimeSpan.FromSeconds(2)))
    {
        lastDiagnostic = reader.LastDiagnostic;
        diagnosticReportInterval.Restart();
        Console.Error.WriteLine(lastDiagnostic);
        Console.Error.Flush();
    }
    if (frames.Count == 0
        && diagnosticCapture is null
        && diagnosticReader is not null
        && File.Exists(diagnosticRequestPath)
        && attemptActive
        && StableGraphDiscoveryPolicy.ShouldCaptureDiagnosticSnapshot(reader.LastDiagnostic))
    {
        try { File.Delete(diagnosticRequestPath); } catch { }
        diagnosticCapture = Task.Run(diagnosticReader.CaptureDiagnosticSnapshot);
    }
    if (diagnosticCapture is { IsCompletedSuccessfully: true } && !diagnosticReported)
    {
        diagnosticReported = true;
        Console.Error.WriteLine(await diagnosticCapture);
        Console.Error.Flush();
    }
    await Task.Delay(reader.PollInterval);
}

return 0;

static System.Diagnostics.Process? openParentProcess(int? processId, long? expectedStartUtcTicks)
{
    if (processId is null)
        return null;
    try
    {
        var process = System.Diagnostics.Process.GetProcessById(processId.Value);
        if (!parentIsAlive(process, expectedStartUtcTicks))
        {
            process.Dispose();
            return null;
        }
        return process;
    }
    catch
    {
        return null;
    }
}

static bool parentIsAlive(
    System.Diagnostics.Process? process,
    long? expectedStartUtcTicks)
{
    if (process is null)
        return true;
    try
    {
        return !process.HasExited
               && expectedStartUtcTicks is not null
               && process.StartTime.ToUniversalTime().Ticks == expectedStartUtcTicks.Value;
    }
    catch
    {
        return false;
    }
}
