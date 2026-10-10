using System.Security.Principal;
using Insomnia.App.Interop;

namespace Insomnia.App;

/// <summary>
/// One Insomnia per user session. The first instance holds a named mutex;
/// a later launch signals a named event, which makes the first instance open
/// its flyout, and exits. <c>--demo</c> uses its own names, so a demo can run
/// beside the real app.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _signal;
    private readonly EventWaitHandle _quit;
    private readonly EventWaitHandle _quitRefused;
    private RegisteredWaitHandle? _registration;
    private RegisteredWaitHandle? _quitRegistration;
    private bool _disposed;

    private SingleInstance(Mutex mutex, EventWaitHandle signal, EventWaitHandle quit, EventWaitHandle quitRefused, bool isPrimary)
    {
        _mutex = mutex;
        _signal = signal;
        _quit = quit;
        _quitRefused = quitRefused;
        IsPrimary = isPrimary;
    }

    /// <summary>True when this process owns the mutex and should run the app.</summary>
    public bool IsPrimary { get; }

    public static SingleInstance Acquire(bool demo)
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        var name = $@"Local\Insomnia.App.{sid}{(demo ? ".Demo" : "")}";
        var mutex = new Mutex(initiallyOwned: true, name, out var createdNew);
        var owned = createdNew;
        if (!owned)
        {
            try
            {
                owned = mutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                // The previous instance died holding it; it is ours now.
                owned = true;
            }
        }
        var signal = new EventWaitHandle(false, EventResetMode.AutoReset, name + ".Show");
        var quit = new EventWaitHandle(false, EventResetMode.AutoReset, name + ".Quit");
        var quitRefused = new EventWaitHandle(false, EventResetMode.AutoReset, name + ".QuitRefused");
        return new SingleInstance(mutex, signal, quit, quitRefused, owned);
    }

    /// <summary>Second launch: let the first instance take the foreground and ask it to show the flyout.</summary>
    public void SignalPrimary()
    {
        NativeMethods.AllowSetForegroundWindow(NativeMethods.ASFW_ANY);
        _signal.Set();
    }

    /// <summary>First instance: run <paramref name="onSignal"/> (on a pool thread) whenever a later launch signals.</summary>
    public void Listen(Action onSignal)
    {
        _registration?.Unregister(null);
        _registration = ThreadPool.RegisterWaitForSingleObject(_signal, (_, _) => onSignal(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    /// <summary>
    /// <c>--quit</c> from the install scripts: ask the first instance to quit
    /// (which ends any session) and wait for it. True when it exited, false
    /// when it refused or did not answer within <paramref name="timeout"/>.
    /// </summary>
    public bool RequestPrimaryQuit(TimeSpan timeout)
    {
        _quitRefused.Reset();
        _quit.Set();
        int index;
        try
        {
            index = WaitHandle.WaitAny([_mutex, _quitRefused], timeout);
        }
        catch (AbandonedMutexException)
        {
            // The first instance exited without releasing it: it is gone, and the mutex is ours.
            index = 0;
        }
        switch (index)
        {
            case 0:
                // The first instance released the mutex on exit; let go of it again.
                _mutex.ReleaseMutex();
                return true;
            case 1:
                return false;
            default:
                return false;
        }
    }

    /// <summary>First instance: run <paramref name="onQuit"/> when <c>--quit</c> asks; it returns whether the app is quitting.</summary>
    public void ListenForQuit(Func<Task<bool>> onQuit)
    {
        _quitRegistration?.Unregister(null);
        _quitRegistration = ThreadPool.RegisterWaitForSingleObject(_quit, (_, _) =>
        {
            _ = Task.Run(async () =>
            {
                if (!await onQuit().ConfigureAwait(false))
                {
                    _quitRefused.Set();
                }
            });
        }, null, Timeout.Infinite, executeOnlyOnce: false);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _registration?.Unregister(null);
        _quitRegistration?.Unregister(null);
        if (IsPrimary)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Released from a thread that does not own it; disposal still frees the handle.
            }
        }
        _mutex.Dispose();
        _signal.Dispose();
        _quit.Dispose();
        _quitRefused.Dispose();
    }
}
