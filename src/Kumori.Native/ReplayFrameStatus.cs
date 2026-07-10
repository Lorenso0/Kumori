using System.Text.Json;

namespace Kumori.Native;

public interface IReplayFrameStatusSink
{
    void Update(Action<LazerReplayFrameStatus> mutate);
    LazerReplayFrameStatus Load();
}

public sealed class JsonReplayFrameStatusSink : IReplayFrameStatusSink
{
    private readonly string _path;
    private readonly object _gate = new();

    public JsonReplayFrameStatusSink(string path)
    {
        _path = Path.GetFullPath(path);
    }

    public void Update(Action<LazerReplayFrameStatus> mutate)
    {
        lock (_gate)
        {
            var status = Load();
            mutate(status);
            status.UpdatedAt = DateTimeOffset.UtcNow;
            Save(status);
        }
    }

    public LazerReplayFrameStatus Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                return JsonSerializer.Deserialize<LazerReplayFrameStatus>(
                    File.ReadAllText(_path)) ?? new LazerReplayFrameStatus();
            }
        }
        catch
        {
        }

        return new LazerReplayFrameStatus();
    }

    private void Save(LazerReplayFrameStatus status)
    {
        string? tmp = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var json = JsonSerializer.Serialize(status, new JsonSerializerOptions { WriteIndented = true });
            tmp = Path.Combine(
                Path.GetDirectoryName(_path)!,
                $"{Path.GetFileName(_path)}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
            File.WriteAllText(tmp, json);
            File.Move(tmp, _path, overwrite: true);
        }
        catch
        {
        }
        finally
        {
            try
            {
                if (tmp is not null && File.Exists(tmp))
                {
                    File.Delete(tmp);
                }
            }
            catch
            {
            }
        }
    }
}

public sealed class DelegatingReplayFrameStatusSink : IReplayFrameStatusSink
{
    public void Update(Action<LazerReplayFrameStatus> mutate) => LazerReplayFrameDiagnostics.Update(mutate);
    public LazerReplayFrameStatus Load() => LazerReplayFrameDiagnostics.Load();
}
