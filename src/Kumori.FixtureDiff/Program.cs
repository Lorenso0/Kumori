using System.Globalization;
using System.Text.Json;
using Kumori.Core;
using Kumori.Storage;
using Kumori.Tracking;
using Microsoft.Data.Sqlite;

var fixturesDir = args.Length > 0
    ? args[0]
    : AppPaths.FixturesDir;
var realDb = args.Length > 1
    ? args[1]
    : AppPaths.TrackingDatabase;

if (!Directory.Exists(fixturesDir))
{
    Console.Error.WriteLine($"Fixture directory not found: {fixturesDir}");
    return 2;
}

if (!File.Exists(realDb))
{
    Console.Error.WriteLine($"Real tracking DB not found: {realDb}");
    return 2;
}

var files = Directory.GetFiles(fixturesDir, "*.jsonl")
    .OrderBy(File.GetLastWriteTimeUtc)
    .ToArray();
if (files.Length == 0)
{
    Console.Error.WriteLine($"No .jsonl fixtures found in {fixturesDir}");
    return 2;
}

Console.WriteLine($"Fixtures: {fixturesDir}");
Console.WriteLine($"Python DB: {realDb}");
Console.WriteLine();

var failures = 0;
foreach (var fixture in files)
{
    var window = FixtureWindow.Read(fixture);
    var tempDb = Path.Combine(Path.GetTempPath(), $"kumori-fixture-diff-{Path.GetFileNameWithoutExtension(fixture)}-{Guid.NewGuid():N}.sqlite3");
    try
    {
        var sink = new AttemptSqliteSink(new SqliteConnectionFactory(tempDb, readOnly: false));
        var runner = new TrackingReplayRunner(new AttemptTracker(sink), new SessionTracker(sink));
        await runner.RunAsync(new FixturePacketSource(fixture));
        await sink.FlushPendingPersistenceAsync();

        var expected = AttemptRow.ReadWindow(realDb, window.StartWall, window.EndWall);
        var actual = AttemptRow.ReadAll(tempDb);
        var result = Compare(expected, actual);
        failures += result.Passed ? 0 : 1;

        Console.WriteLine($"{Path.GetFileName(fixture)}");
        Console.WriteLine($"  packets={window.PacketCount} wall={FormatWall(window.StartWall)}..{FormatWall(window.EndWall)}");
        Console.WriteLine($"  python: {Describe(expected)}");
        Console.WriteLine($"  csharp: {Describe(actual)}");
        Console.WriteLine($"  {(result.Passed ? "PASS" : "FAIL")} {result.Message}");
        Console.WriteLine();
    }
    finally
    {
        SqliteConnection.ClearAllPools();
        TryDelete(tempDb);
        TryDelete(tempDb + "-wal");
        TryDelete(tempDb + "-shm");
    }
}

return failures == 0 ? 0 : 1;

static DiffResult Compare(IReadOnlyList<AttemptRow> expected, IReadOnlyList<AttemptRow> actual)
{
    if (expected.Count != actual.Count)
    {
        return DiffResult.Fail($"attempt count differs: expected {expected.Count}, got {actual.Count}");
    }

    for (var i = 0; i < expected.Count; i++)
    {
        var e = expected[i];
        var a = actual[i];
        if (e.Outcome != a.Outcome)
        {
            return DiffResult.Fail($"row {i + 1} outcome differs: expected {e.Outcome}, got {a.Outcome}");
        }
        if (e.Evidence != a.Evidence)
        {
            return DiffResult.Fail($"row {i + 1} evidence differs: expected {e.Evidence}, got {a.Evidence}");
        }
        if (e.Identity != a.Identity)
        {
            return DiffResult.Fail($"row {i + 1} identity differs: expected {e.Identity}, got {a.Identity}");
        }
        if (Math.Abs(e.Score - a.Score) > 0)
        {
            return DiffResult.Fail($"row {i + 1} score differs: expected {e.Score}, got {a.Score}");
        }
        if (Math.Abs(e.Progress - a.Progress) > 0.01)
        {
            return DiffResult.Fail($"row {i + 1} progress differs: expected {e.Progress:0.000}, got {a.Progress:0.000}");
        }
        if (Math.Abs(e.Accuracy - a.Accuracy) > 0.05)
        {
            return DiffResult.Fail($"row {i + 1} accuracy differs: expected {e.Accuracy:0.000}, got {a.Accuracy:0.000}");
        }
        if (Math.Abs(e.Pp - a.Pp) > 0.05)
        {
            return DiffResult.Fail($"row {i + 1} pp differs: expected {e.Pp:0.000}, got {a.Pp:0.000}");
        }
        if (e.Combo != a.Combo || e.Misses != a.Misses)
        {
            return DiffResult.Fail($"row {i + 1} combo/miss differs: expected {e.Combo}x/{e.Misses}, got {a.Combo}x/{a.Misses}");
        }
    }

    return DiffResult.Pass();
}

static string Describe(IReadOnlyList<AttemptRow> rows) =>
    rows.Count == 0
        ? "<none>"
        : string.Join(" | ", rows.Select(r => $"{r.Outcome}:{r.Score}:{r.Accuracy:0.00}%:{r.Pp:0.00}pp:{r.Combo}x:{r.Misses}m:{r.Progress:0.000}:{r.Identity[..Math.Min(8, r.Identity.Length)]}"));

static string FormatWall(double wall) =>
    DateTimeOffset.FromUnixTimeMilliseconds((long)(wall * 1000))
        .ToLocalTime()
        .ToString("HH:mm:ss", CultureInfo.InvariantCulture);

static void TryDelete(string path)
{
    try { File.Delete(path); } catch { }
}

sealed record DiffResult(bool Passed, string Message)
{
    public static DiffResult Pass() => new(true, "");
    public static DiffResult Fail(string message) => new(false, message);
}

sealed record FixtureWindow(double StartWall, double EndWall, int PacketCount)
{
    public static FixtureWindow Read(string path)
    {
        double? first = null;
        double? last = null;
        var count = 0;
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            using var doc = JsonDocument.Parse(line);
            var wall = doc.RootElement.GetProperty("wall").GetDouble();
            first ??= wall;
            last = wall;
            count++;
        }

        if (first is null || last is null)
        {
            throw new InvalidOperationException($"Fixture is empty: {path}");
        }

        return new FixtureWindow(first.Value, last.Value, count);
    }
}

sealed record AttemptRow(
    string Outcome,
    string? Evidence,
    long Score,
    double Accuracy,
    double Pp,
    long Combo,
    long Misses,
    double Progress,
    string Identity)
{
    public static IReadOnlyList<AttemptRow> ReadWindow(string dbPath, double startWall, double endWall)
    {
        var start = DateTimeOffset.FromUnixTimeMilliseconds((long)(startWall * 1000)).AddSeconds(-2);
        var end = DateTimeOffset.FromUnixTimeMilliseconds((long)(endWall * 1000)).AddSeconds(2);
        return Read(dbPath, """
            SELECT a.outcome, a.termination_evidence, a.score, a.accuracy, a.pp,
                   a.combo, a.misses, a.progress, b.identity
            FROM attempts a
            JOIN beatmaps b ON b.id = a.beatmap_id
            WHERE julianday(a.started_at) >= julianday(@start)
              AND julianday(a.started_at) <= julianday(@end)
            ORDER BY a.id
            """, cmd =>
        {
            cmd.Parameters.AddWithValue("@start", start.ToString("O"));
            cmd.Parameters.AddWithValue("@end", end.ToString("O"));
        });
    }

    public static IReadOnlyList<AttemptRow> ReadAll(string dbPath) =>
        Read(dbPath, """
            SELECT a.outcome, a.termination_evidence, a.score, a.accuracy, a.pp,
                   a.combo, a.misses, a.progress, b.identity
            FROM attempts a
            JOIN beatmaps b ON b.id = a.beatmap_id
            ORDER BY a.id
            """, _ => { });

    private static IReadOnlyList<AttemptRow> Read(
        string dbPath,
        string sql,
        Action<SqliteCommand> bind)
    {
        var rows = new List<AttemptRow>();
        using var con = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        bind(cmd);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new AttemptRow(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetInt64(2),
                reader.GetDouble(3),
                reader.GetDouble(4),
                reader.GetInt64(5),
                reader.GetInt64(6),
                reader.GetDouble(7),
                reader.GetString(8)));
        }
        return rows;
    }
}
