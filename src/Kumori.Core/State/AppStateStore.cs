namespace Kumori.Core.State;

/// <summary>
/// Central state store. Services call <see cref="Update"/> with a pure
/// transform; subscribers receive the new immutable snapshot. Thread-safe.
/// Event handlers may be invoked on any thread — UI subscribers must
/// marshal to the dispatcher themselves.
/// </summary>
public sealed class AppStateStore
{
    private readonly object _lock = new();
    private AppState _state = new();

    public AppState Current
    {
        get { lock (_lock) { return _state; } }
    }

    public event Action<AppState>? StateChanged;

    public void Update(Func<AppState, AppState> transform)
    {
        AppState next;
        lock (_lock)
        {
            next = transform(_state);
            if (ReferenceEquals(next, _state) || EqualityComparer<AppState>.Default.Equals(next, _state))
            {
                return;
            }
            _state = next;
        }
        StateChanged?.Invoke(next);
    }
}
