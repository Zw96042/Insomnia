using System.Reflection;
using System.Windows.Media;
using System.Windows.Threading;
using Insomnia.App.Animation;
using Insomnia.App.Controls;
using Insomnia.App.Formatting;
using Insomnia.App.Interop;
using Insomnia.App.Theme;
using Insomnia.App.Tray;
using Insomnia.Core.Engine;
using Insomnia.Core.Logging;
using WinForms = System.Windows.Forms;

namespace Insomnia.App;

/// <summary>
/// The notification-area icon: a closed eye while idle, an open eye during a
/// session (it blinks between the two), and a small red dot when a warning of
/// Error severity is up. Drawn at the taskbar's icon size for its DPI and in
/// the taskbar's light or dark color. Left click toggles the flyout, right
/// click opens the menu, and the tooltip shows the time left.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private static readonly FieldInfo? WindowField;
    private static readonly FieldInfo? IdField;
    private static bool _loggedRectFailure;

    private readonly WinForms.NotifyIcon _notifyIcon;
    private readonly IAppShell _shell;
    private readonly DispatcherTimer _blinkTimer;
    private readonly DispatcherTimer _tooltipTimer;
    private SessionStatus _status = SessionStatus.Initial;
    private IntPtr _hicon;
    private System.Drawing.Icon? _icon;
    private EyeFrame _frame = EyeFrame.Closed;
    private bool _active;
    private bool _alert;
    private int _size;
    private bool _lightTaskbar;
    private DateTime _blinkStarted;
    private EyeFrame _blinkFrom;
    private bool _disposed;

    static TrayIcon()
    {
        // NotifyIcon does not expose the window and id the shell knows it by;
        // Shell_NotifyIconGetRect needs them to find the icon on screen.
        var fields = typeof(WinForms.NotifyIcon).GetFields(BindingFlags.Instance | BindingFlags.NonPublic);
        WindowField = fields.FirstOrDefault(f => typeof(WinForms.NativeWindow).IsAssignableFrom(f.FieldType));
        IdField = fields.FirstOrDefault(f => f.Name is "_id" or "id")
            ?? fields.FirstOrDefault(f => (f.FieldType == typeof(int) || f.FieldType == typeof(uint))
                && f.Name.EndsWith("id", StringComparison.OrdinalIgnoreCase));
    }

    public TrayIcon(IAppShell shell)
    {
        _shell = shell;
        _notifyIcon = new WinForms.NotifyIcon { Text = "Insomnia" };
        _notifyIcon.MouseClick += OnMouseClick;
        _notifyIcon.BalloonTipClicked += (_, _) => Guard("notification click", _shell.ShowFlyout);
        _blinkTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, OnBlinkFrame, Dispatcher.CurrentDispatcher);
        _blinkTimer.Stop();
        _tooltipTimer = new DispatcherTimer(TimeSpan.FromSeconds(15), DispatcherPriority.Background, (_, _) => UpdateTooltip(), Dispatcher.CurrentDispatcher);
        _tooltipTimer.Stop();
        ThemeManager.Current.SystemChanged += OnSystemChanged;
        RefreshMetrics();
        Render();
        _notifyIcon.Visible = true;
    }

    /// <summary>Render a status snapshot: icon state, warning dot, tooltip.</summary>
    public void Update(SessionStatus status)
    {
        _status = status;
        var active = status.Phase != SessionPhase.Idle;
        var alert = status.Warnings.Any(w => w.Severity == WarningSeverity.Error);
        if (active != _active)
        {
            _active = active;
            _alert = alert;
            StartBlink();
        }
        else if (alert != _alert)
        {
            _alert = alert;
            Render();
        }
        UpdateTooltip();
        // The tooltip changes once a minute at most; refreshing while the lid
        // is closed would be redraw work nobody can see.
        if (active && !status.LidClosed)
        {
            _tooltipTimer.Start();
        }
        else
        {
            _tooltipTimer.Stop();
        }
    }

    /// <summary>The icon's rectangle on screen in physical pixels, or null when the shell cannot say.</summary>
    public NativeRect? IconRect()
    {
        try
        {
            if (WindowField?.GetValue(_notifyIcon) is WinForms.NativeWindow window && window.Handle != IntPtr.Zero && IdField is not null)
            {
                var identifier = new NotifyIconIdentifier
                {
                    Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NotifyIconIdentifier>(),
                    Window = window.Handle,
                    Id = Convert.ToUInt32(IdField.GetValue(_notifyIcon), System.Globalization.CultureInfo.InvariantCulture),
                };
                if (NativeMethods.Shell_NotifyIconGetRect(ref identifier, out var rect) == 0 && !rect.IsEmpty)
                {
                    return rect;
                }
            }
        }
        catch (Exception ex) when (ex is TargetException or FieldAccessException or InvalidCastException or OverflowException)
        {
            // Falls through to the logged failure below.
        }
        if (!_loggedRectFailure)
        {
            _loggedRectFailure = true;
            Log.Warn("tray: icon rectangle unavailable, anchoring the flyout at the cursor");
        }
        return null;
    }

    /// <summary>A toast (Windows renders balloon tips as notifications attributed to the app).</summary>
    public void ShowBalloon(string title, string body)
    {
        if (_disposed)
        {
            return;
        }
        // NOTIFYICONDATA holds 63 characters of title and 255 of text, and
        // ShowBalloonTip refuses empty text: a title-only note shows as text.
        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(body))
        {
            return;
        }
        var text = string.IsNullOrWhiteSpace(body) ? title : body;
        var heading = string.IsNullOrWhiteSpace(body) ? "Insomnia" : title;
        _notifyIcon.ShowBalloonTip(10_000, StatusText.Truncate(heading, 63), StatusText.Truncate(text, 255), WinForms.ToolTipIcon.None);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        ThemeManager.Current.SystemChanged -= OnSystemChanged;
        _blinkTimer.Stop();
        _tooltipTimer.Stop();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _icon?.Dispose();
        if (_hicon != IntPtr.Zero)
        {
            NativeMethods.DestroyIcon(_hicon);
            _hicon = IntPtr.Zero;
        }
    }

    private void OnMouseClick(object? sender, WinForms.MouseEventArgs e)
    {
        switch (e.Button)
        {
            case WinForms.MouseButtons.Left:
                Guard("tray click", _shell.ToggleFlyout);
                break;
            case WinForms.MouseButtons.Right:
                NativeMethods.GetCursorPos(out var cursor);
                Guard("tray menu", () => _shell.ShowMenu(cursor));
                break;
        }
    }

    /// <summary>These run inside Windows Forms' window procedure: never let an exception escape into it.</summary>
    private static void Guard(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log.Error($"{what} failed", ex);
        }
    }

    private void OnSystemChanged(object? sender, EventArgs e)
    {
        if (RefreshMetrics())
        {
            Render();
        }
    }

    /// <summary>Re-read the icon size and taskbar theme; true when either changed.</summary>
    private bool RefreshMetrics()
    {
        var primary = NativeMethods.MonitorFromPoint(new NativePoint(0, 0), NativeMethods.MONITOR_DEFAULTTOPRIMARY);
        var dpi = NativeMethods.DpiForMonitor(primary);
        var size = NativeMethods.GetSystemMetricsForDpi(NativeMethods.SM_CXSMICON, dpi);
        if (size <= 0)
        {
            size = (int)Math.Round(16 * dpi / 96.0);
        }
        var light = ThemeManager.Current.IsTaskbarLight;
        var changed = size != _size || light != _lightTaskbar;
        _size = size;
        _lightTaskbar = light;
        return changed;
    }

    private void StartBlink()
    {
        if (Motion.ReduceMotion)
        {
            _blinkTimer.Stop();
            _frame = _active ? EyeFrame.Open : EyeFrame.Closed;
            Render();
            return;
        }
        _blinkFrom = _frame;
        _blinkStarted = DateTime.UtcNow;
        _blinkTimer.Start();
        OnBlinkFrame(null, EventArgs.Empty);
    }

    private void OnBlinkFrame(object? sender, EventArgs e)
    {
        var t = (DateTime.UtcNow - _blinkStarted).TotalSeconds;
        double openness, pupilScale, pupilOpacity;
        TimeSpan length;
        if (_active)
        {
            var lid = Motion.BlinkOpen.At(t);
            var pupilT = t - Motion.PupilOpenDelay.TotalSeconds;
            var pupil = pupilT > 0 ? Motion.PupilOpen.At(pupilT) : 0;
            openness = Lerp(_blinkFrom.Openness, 1, lid);
            pupilScale = Lerp(_blinkFrom.PupilScale, 1, pupil);
            pupilOpacity = Lerp(_blinkFrom.PupilOpacity, 1, pupil);
            length = Max(Motion.BlinkOpen.Duration, Motion.PupilOpenDelay + Motion.PupilOpen.Duration);
        }
        else
        {
            var lid = Motion.BlinkClose.At(t);
            var u = Math.Clamp(t / Motion.PupilCloseDuration.TotalSeconds, 0, 1);
            var pupil = 1 - Math.Pow(1 - u, 3);
            openness = Lerp(_blinkFrom.Openness, 0, lid);
            pupilScale = Lerp(_blinkFrom.PupilScale, Motion.PupilClosedScale, pupil);
            pupilOpacity = Lerp(_blinkFrom.PupilOpacity, 0, pupil);
            length = Max(Motion.BlinkClose.Duration, Motion.PupilCloseDuration);
        }
        if (t >= length.TotalSeconds)
        {
            _blinkTimer.Stop();
            _frame = _active ? EyeFrame.Open : EyeFrame.Closed;
        }
        else
        {
            _frame = new EyeFrame(openness, pupilScale, pupilOpacity);
        }
        Render();
    }

    private void Render()
    {
        if (_disposed || _size <= 0)
        {
            return;
        }
        var glyph = _lightTaskbar ? Color.FromRgb(0x1B, 0x1B, 0x1B) : Colors.White;
        Color? dot = _alert ? (_lightTaskbar ? Color.FromRgb(0xC4, 0x2B, 0x1C) : Color.FromRgb(0xFF, 0x99, 0xA4)) : null;
        var hicon = TrayIconRenderer.CreateIcon(_size, _frame, glyph, dot);
        if (hicon == IntPtr.Zero)
        {
            return;
        }
        var icon = System.Drawing.Icon.FromHandle(hicon);
        _notifyIcon.Icon = icon;
        // The shell copied the old icon when it was set; it is safe to free now.
        _icon?.Dispose();
        if (_hicon != IntPtr.Zero)
        {
            NativeMethods.DestroyIcon(_hicon);
        }
        _icon = icon;
        _hicon = hicon;
    }

    private void UpdateTooltip()
    {
        if (!_disposed)
        {
            _notifyIcon.Text = StatusText.Tooltip(_status, DateTimeOffset.UtcNow);
        }
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}
