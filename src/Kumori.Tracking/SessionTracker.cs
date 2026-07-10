namespace Kumori.Tracking;

public interface ISessionSink
{
    void StartSession(SessionStart start);
    void AddActiveSeconds(double seconds);
    void PromptOsuClosed(SessionClosePrompt prompt);
    void EndSession(SessionEnd end);
}

public sealed record SessionStart(double WallTime, double MonoTime);
public sealed record SessionClosePrompt(double WallTime, double MonoTime, double DeadlineMonoTime);
public sealed record SessionEnd(double WallTime, double MonoTime, bool Interrupted);

public sealed class SessionTracker
{
    public const double SessionGraceSeconds = 600.0;

    private readonly ISessionSink _sink;
    private bool _hasSession;
    private bool _lastPlaying;
    private bool _promptedForClose;
    private double? _closeDeadline;
    private double _lastMonoTime;

    public SessionTracker(ISessionSink sink)
    {
        _sink = sink;
    }

    public bool HasSession => _hasSession;

    public sealed record Frame
    {
        public double WallTime { get; init; }
        public double MonoTime { get; init; }
        public bool IsStandardMode { get; init; } = true;
        public bool IsPlaying { get; init; }
        public bool OsuRunning { get; init; } = true;
    }

    public void Ingest(Frame frame)
    {
        if (!frame.IsStandardMode)
        {
            return;
        }

        if (!_hasSession && frame.OsuRunning && frame.IsPlaying)
        {
            _sink.StartSession(new SessionStart(frame.WallTime, frame.MonoTime));
            _hasSession = true;
            _lastMonoTime = frame.MonoTime;
            _lastPlaying = frame.IsPlaying;
            _closeDeadline = null;
            _promptedForClose = false;
            return;
        }

        if (_hasSession && _lastPlaying)
        {
            var delta = Math.Clamp(frame.MonoTime - _lastMonoTime, 0, 1);
            if (delta > 0)
            {
                _sink.AddActiveSeconds(delta);
            }
        }

        if (_hasSession && !frame.OsuRunning)
        {
            _closeDeadline ??= frame.MonoTime + SessionGraceSeconds;
            if (!_promptedForClose)
            {
                _sink.PromptOsuClosed(new SessionClosePrompt(
                    frame.WallTime,
                    frame.MonoTime,
                    _closeDeadline.Value));
                _promptedForClose = true;
            }
            if (frame.MonoTime >= _closeDeadline.Value)
            {
                End(frame, interrupted: true);
                return;
            }
        }
        else if (_hasSession && frame.OsuRunning)
        {
            _closeDeadline = null;
            _promptedForClose = false;
        }

        _lastMonoTime = frame.MonoTime;
        _lastPlaying = frame.IsPlaying && frame.OsuRunning;
    }

    public void EndClean(double wallTime, double monoTime) =>
        End(new Frame { WallTime = wallTime, MonoTime = monoTime }, interrupted: false);

    public void EndInterrupted(double wallTime, double monoTime) =>
        End(new Frame { WallTime = wallTime, MonoTime = monoTime }, interrupted: true);

    private void End(Frame frame, bool interrupted)
    {
        if (!_hasSession)
        {
            return;
        }

        _sink.EndSession(new SessionEnd(frame.WallTime, frame.MonoTime, interrupted));
        _hasSession = false;
        _lastPlaying = false;
        _promptedForClose = false;
        _closeDeadline = null;
        _lastMonoTime = frame.MonoTime;
    }
}
