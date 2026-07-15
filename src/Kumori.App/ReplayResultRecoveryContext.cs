using Kumori.Core.Models;
using Kumori.Storage;

namespace Kumori.App;

internal sealed record ReplayResultRecoveryContext(
    long AttemptId,
    ReplayResultRecoveryOutcome HeaderRecovery,
    string? ReplayPath,
    string BeatmapPath,
    string? MediaDirectory,
    IReadOnlyDictionary<string, string>? MediaPaths,
    IReadOnlyList<MovementSample> Samples,
    bool RequiresSimulation = true,
    bool RequiresTosuRestart = true,
    bool SimulationOwnsCoreResult = false,
    bool TosuResultWasMissing = false);
