using System.Text.Json;
using Kumori.Native;

string? gameFolder = args.Length > 0 ? args[0] : null;
using StableClrReplayReader? reader = StableClrReplayReader.TryAttach(gameFolder);
if (reader is null)
{
    Console.Error.WriteLine($"stable attach failed: {StableClrReplayReader.LastAttachDiagnostic}");
    return 2;
}
using StableRawReplayReader? diagnosticReader = StableRawReplayReader.TryAttach(gameFolder);

var emissionCursor = new StableReplayFrameEmissionCursor();
string? lastDiagnostic = null;
Task<string>? diagnosticCapture = null;
bool diagnosticReported = false;
var diagnosticReportInterval = System.Diagnostics.Stopwatch.StartNew();
string diagnosticRequestPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "Kumori", "runtime", "debug", "stable-memory-snapshot.request");
while (true)
{
    IReadOnlyList<Kumori.Tracking.LazerReplayFrame> frames;
    try
    {
        Task<IReadOnlyList<Kumori.Tracking.LazerReplayFrame>> readTask = Task.Run(reader.ReadReplayFrames);
        while (!readTask.IsCompleted)
        {
            Task completed = await Task.WhenAny(readTask, Task.Delay(1000));
            if (completed != readTask)
            {
                Console.Error.WriteLine("stable CLR replay discovery is still running");
                Console.Error.Flush();
            }
        }
        frames = await readTask;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"stable memory reader error: {ex.GetType().Name}: {ex.Message}");
        Console.Error.Flush();
        return 3;
    }

    IReadOnlyList<Kumori.Tracking.LazerReplayFrame> newFrames = emissionCursor.TakeNew(frames, out bool rotated);
    if (rotated && newFrames.Count > 0)
    {
        Console.Error.WriteLine($"stable replay list rotated; continuing at map time {newFrames[0].MapTimeMs:0.###}ms");
        Console.Error.Flush();
    }
    foreach (var frame in newFrames)
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
    if (newFrames.Count > 0)
        Console.Out.Flush();
    if (reader.LastDiagnostic != lastDiagnostic
        && (lastDiagnostic is null || frames.Count > 0 || diagnosticReportInterval.Elapsed >= TimeSpan.FromSeconds(2)))
    {
        lastDiagnostic = reader.LastDiagnostic;
        diagnosticReportInterval.Restart();
        Console.Error.WriteLine(lastDiagnostic);
        Console.Error.Flush();
    }
    if (frames.Count == 0 && diagnosticCapture is null && diagnosticReader is not null && File.Exists(diagnosticRequestPath))
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
    await Task.Delay(frames.Count == 0 ? 250 : 16);
}
