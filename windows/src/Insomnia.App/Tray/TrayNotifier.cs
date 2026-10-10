using System.Windows.Threading;
using Insomnia.Core.Logging;
using Insomnia.Core.Platform;

namespace Insomnia.App;

/// <summary>
/// <see cref="INotifier"/> over the tray icon's balloon tips, which Windows 10
/// and 11 show as toasts attributed to Insomnia. Safe from any thread: calls
/// are marshalled to the UI thread. Notifications raised before the tray icon
/// exists (during launch reconcile) are held and shown once it does.
/// </summary>
public sealed class TrayNotifier : INotifier
{
    private readonly Dispatcher _dispatcher;
    private readonly Queue<(string Title, string Body)> _pending = new();
    private TrayIcon? _tray;

    /// <summary>Create on the UI thread; that thread's dispatcher receives every notification.</summary>
    public TrayNotifier()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
    }

    public void Notify(string title, string body)
    {
        Log.Info($"notify: {title}: {body}");
        _dispatcher.BeginInvoke(() =>
        {
            if (_tray is null)
            {
                _pending.Enqueue((title, body));
                return;
            }
            _tray.ShowBalloon(title, body);
        });
    }

    internal void Attach(TrayIcon tray)
    {
        _tray = tray;
        while (_pending.TryDequeue(out var note))
        {
            tray.ShowBalloon(note.Title, note.Body);
        }
    }

    internal void Detach() => _tray = null;
}
