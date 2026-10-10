using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Insomnia.App.Interop;
using Insomnia.App.Services;
using Insomnia.App.Theme;
using Insomnia.Core.Engine;
using Insomnia.Core.Logging;
using Insomnia.Core.Model;

namespace Insomnia.App;

/// <summary>
/// The tray app: owns the tray icon, the flyout, the menu and Settings, and
/// is the only subscriber to <see cref="ISessionController.StatusChanged"/>.
/// Status arrives on background threads and is applied on the UI thread,
/// always as the controller's latest snapshot, so a burst of changes renders
/// once and never out of order.
/// </summary>
internal sealed partial class App : IAppShell, IDisposable
{
    private readonly ISessionController _controller;
    private readonly ISettingsServices _services;
    private readonly TrayNotifier _notifier;
    private readonly SingleInstance _instance;
    private readonly bool _demo;
    private ThemeManager? _theme;
    private TrayIcon? _tray;
    private TrayMenu? _menu;
    private FlyoutWindow? _flyout;
    private SettingsWindow? _settings;
    private int _statusQueued;
    private bool _quitting;

    public App(ISessionController controller, ISettingsServices services, TrayNotifier notifier, SingleInstance instance, bool demo)
    {
        _controller = controller;
        _services = services;
        _notifier = notifier;
        _instance = instance;
        _demo = demo;
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("unhandled exception", (Exception)e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("unobserved task exception", e.Exception.GetBaseException());
            e.SetObserved();
        };
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _theme = ThemeManager.Initialize(this);
        _flyout = new FlyoutWindow(_controller, this);
        _menu = new TrayMenu(_controller, this);
        _tray = new TrayIcon(this);
        _notifier.Attach(_tray);

        _controller.StatusChanged += OnStatusChanged;
        ApplyStatus();
        _instance.Listen(() => Dispatcher.BeginInvoke(ShowFlyout));
        _instance.ListenForQuit(() => Dispatcher.InvokeAsync(async () =>
        {
            Log.Info("quit requested by another process (install or uninstall)");
            await QuitAsync();
            return _quitting;
        }).Task.Unwrap());
        Log.Info(_demo ? "ready (demo: nothing on this PC is changed)" : "ready");
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        // Sign-out or shutdown: the process dies with keep-awake; the recovery
        // agent restores anything journaled at the next sign-in.
        Log.Info($"Windows session ending ({e.ReasonSessionEnding})");
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Dispose();
        base.OnExit(e);
    }

    public void Dispose()
    {
        _controller.StatusChanged -= OnStatusChanged;
        _notifier.Detach();
        _tray?.Dispose();
        _tray = null;
        _theme?.Dispose();
        _theme = null;
    }

    #region IAppShell

    public void ToggleFlyout()
    {
        if (_flyout is null)
        {
            return;
        }
        if (_flyout.IsOpen)
        {
            _flyout.Dismiss();
        }
        else if (!_flyout.WasJustDismissed)
        {
            ShowFlyout();
        }
    }

    public void ShowFlyout()
    {
        if (_flyout is null || _quitting)
        {
            return;
        }
        _menu?.Close();
        _flyout.Open(Anchor());
    }

    public void ShowMenu(NativePoint at, bool fromFlyout = false)
    {
        if (!fromFlyout)
        {
            _flyout?.Dismiss();
        }
        _menu?.Show(at, fromFlyout);
    }

    public void ShowSettings()
    {
        _flyout?.Dismiss();
        if (_settings is null)
        {
            _settings = new SettingsWindow(_controller, _services, this);
            _settings.Closed += (_, _) => _settings = null;
            _settings.Show();
        }
        if (_settings.WindowState == WindowState.Minimized)
        {
            _settings.WindowState = WindowState.Normal;
        }
        _settings.Activate();
    }

    public void OpenLogs()
    {
        var folder = _services.LogsFolder;
        try
        {
            Directory.CreateDirectory(folder);
            using var _ = Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Error($"opening the log folder {folder} failed", ex);
            ShowFlyout();
            _flyout?.ShowMessage($"Couldn't open the log folder: {ex.Message}", critical: true);
        }
    }

    public async Task ConfirmAndEndSessionAsync()
    {
        _flyout?.Dismiss();
        var confirmed = ConfirmDialog.Ask(
            "End the session now?",
            "Insomnia puts back everything it changed and lets the PC sleep again on its usual schedule.",
            "End session");
        if (!confirmed || _controller.Status.Phase != SessionPhase.Active)
        {
            return;
        }
        try
        {
            var outcome = await _controller.EndAsync(EndReason.User);
            if (outcome == EndOutcome.Incomplete)
            {
                ShowFlyout();
                _flyout?.ShowMessage("The session ended, but some changes couldn't be put back yet. Insomnia keeps retrying; the warnings below say what is left.", critical: false);
            }
        }
        catch (Exception ex)
        {
            Log.Error("end from menu failed", ex);
            ShowFlyout();
            _flyout?.ShowMessage($"Couldn't end the session: {ex.Message}", critical: true);
        }
    }

    public async Task QuitAsync()
    {
        if (_quitting)
        {
            return;
        }
        _quitting = true;
        _flyout?.Dismiss();
        if (_settings is not null)
        {
            // Settle any pending Settings save before the controller goes away.
            await _settings.FlushAsync();
        }
        bool ready;
        try
        {
            ready = await _controller.PrepareToQuitAsync();
        }
        catch (Exception ex)
        {
            Log.Error("prepare to quit failed", ex);
            _quitting = false;
            ShowMessage($"Insomnia is still running: preparing to quit failed ({ex.Message}).", critical: true);
            return;
        }
        if (!ready)
        {
            Log.Warn("quit blocked: something Insomnia changed is still in place");
            _quitting = false;
            ShowMessage("Insomnia is still running: quitting now would leave something it changed in place. The warnings below say what, and what you can do.", critical: false);
            return;
        }

        Log.Info("quitting");
        _menu?.Close();
        _flyout?.CloseForShutdown();
        _settings?.Close();
        _tray?.Dispose();
        _tray = null;
        try
        {
            switch (_controller)
            {
                case IAsyncDisposable asyncDisposable:
                    await asyncDisposable.DisposeAsync();
                    break;
                case IDisposable disposable:
                    disposable.Dispose();
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error("disposing the controller failed", ex);
        }
        Shutdown(0);
    }

    public void ShowMessage(string text, bool critical)
    {
        ShowFlyout();
        _flyout?.ShowMessage(text, critical);
    }

    #endregion

    private NativeRect Anchor()
    {
        if (_tray?.IconRect() is { } rect)
        {
            return rect;
        }
        // The icon may sit in the overflow; anchor at the cursor when it is on the taskbar.
        if (NativeMethods.GetCursorPos(out var cursor))
        {
            var monitor = NativeMethods.MonitorFromPoint(cursor, NativeMethods.MONITOR_DEFAULTTONEAREST);
            if (NativeMethods.MonitorInfoFor(monitor) is { } info && !info.Work.Contains(cursor))
            {
                return new NativeRect { Left = cursor.X, Top = cursor.Y, Right = cursor.X + 1, Bottom = cursor.Y + 1 };
            }
        }
        return FlyoutPlacement.FallbackAnchor();
    }

    private void OnStatusChanged(object? sender, SessionStatus status)
    {
        // Coalesce: one queued render reads whatever is latest when it runs.
        if (Interlocked.Exchange(ref _statusQueued, 1) == 0)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Normal, ApplyStatus);
        }
    }

    private void ApplyStatus()
    {
        Interlocked.Exchange(ref _statusQueued, 0);
        var status = _controller.Status;
        _tray?.Update(status);
        _flyout?.ApplyStatus(status);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Keep the tray app alive: a UI bug must not take the session's keep-awake down with it.
        Log.Error("unhandled UI exception", e.Exception);
        e.Handled = true;
    }
}
