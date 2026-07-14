using System.Runtime.InteropServices;

namespace Kumori.Native;

/// <summary>
/// Temporarily lowers CPU, disk-I/O, and memory priority for synchronous work
/// performed on a shared worker thread. Windows background mode is restored
/// before the worker reaches an await/yield boundary.
/// </summary>
internal sealed class BackgroundThreadPriorityScope : IDisposable
{
    private const int ThreadModeBackgroundBegin = 0x00010000;
    private const int ThreadModeBackgroundEnd = 0x00020000;
    private readonly Thread thread = Thread.CurrentThread;
    private readonly ThreadPriority previousPriority;
    private readonly bool managedPriorityChanged;
    private readonly bool backgroundModeEntered;

    public BackgroundThreadPriorityScope()
    {
        try
        {
            previousPriority = thread.Priority;
            if (previousPriority > ThreadPriority.BelowNormal)
            {
                thread.Priority = ThreadPriority.BelowNormal;
                managedPriorityChanged = true;
            }
        }
        catch
        {
            // The explicit time/work budgets remain authoritative when the host
            // does not permit managed thread-priority changes.
        }

        try
        {
            backgroundModeEntered = SetThreadPriority(
                GetCurrentThread(),
                ThreadModeBackgroundBegin);
        }
        catch
        {
            // Background mode is an additional scheduling safeguard.
        }
    }

    public void Dispose()
    {
        if (backgroundModeEntered)
        {
            try { _ = SetThreadPriority(GetCurrentThread(), ThreadModeBackgroundEnd); }
            catch { }
        }
        if (managedPriorityChanged)
        {
            try { thread.Priority = previousPriority; }
            catch { }
        }
    }

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadPriority(nint thread, int priority);
}
