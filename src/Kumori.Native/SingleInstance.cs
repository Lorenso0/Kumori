namespace Kumori.Native;

/// <summary>
/// Named-mutex single-instance guard.
/// The first instance holds the mutex and listens on a named event; later
/// instances signal the event (so the first instance can bring its window
/// forward) and exit.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = "Kumori.App.SingleInstance";
    private const string ActivateEventName = "Kumori.App.Activate";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activateEvent;
    private RegisteredWaitHandle? _waitHandle;

    public bool IsPrimaryInstance { get; }

    public SingleInstance()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        IsPrimaryInstance = createdNew;
        _activateEvent = new EventWaitHandle(
            initialState: false, EventResetMode.AutoReset, ActivateEventName);
    }

    /// <summary>Primary instance: invoke <paramref name="onActivateRequested"/> each time another instance launches.</summary>
    public void ListenForActivation(Action onActivateRequested)
    {
        if (!IsPrimaryInstance)
        {
            return;
        }
        _waitHandle = ThreadPool.RegisterWaitForSingleObject(
            _activateEvent,
            (_, _) => onActivateRequested(),
            state: null,
            millisecondsTimeOutInterval: -1,
            executeOnlyOnce: false);
    }

    /// <summary>Secondary instance: ask the primary instance to show itself.</summary>
    public void SignalPrimaryInstance() => _activateEvent.Set();

    public void Dispose()
    {
        _waitHandle?.Unregister(null);
        if (IsPrimaryInstance)
        {
            try { _mutex.ReleaseMutex(); } catch { /* not owned on this thread */ }
        }
        _mutex.Dispose();
        _activateEvent.Dispose();
    }
}
