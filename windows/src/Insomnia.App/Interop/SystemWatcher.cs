using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Insomnia.App.Interop;

/// <summary>
/// A hidden top-level window that hears the broadcasts a tray app cares
/// about: theme and accent changes, display and scale changes. Message-only
/// windows do not receive broadcasts, so this is an ordinary window that is
/// never shown. Lives on the UI thread; events are raised there.
/// </summary>
internal sealed class SystemWatcher : IDisposable
{
    private readonly HwndSource _source;

    public SystemWatcher()
    {
        var parameters = new HwndSourceParameters("Insomnia.SystemWatcher")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ExtendedWindowStyle = (int)NativeMethods.WS_EX_TOOLWINDOW,
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    /// <summary>WM_SETTINGCHANGE; the argument is the setting's name ("ImmersiveColorSet", "WindowMetrics"...), or empty.</summary>
    public event EventHandler<string>? SettingChanged;

    /// <summary>The accent (DWM colorization) color changed.</summary>
    public event EventHandler? AccentChanged;

    /// <summary>A visual theme, high contrast, resolution or scale change.</summary>
    public event EventHandler? DisplayChanged;

    public IntPtr Handle => _source.Handle;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case NativeMethods.WM_SETTINGCHANGE:
                var area = lParam != IntPtr.Zero ? Marshal.PtrToStringUni(lParam) ?? "" : "";
                SettingChanged?.Invoke(this, area);
                break;
            case NativeMethods.WM_DWMCOLORIZATIONCOLORCHANGED:
                AccentChanged?.Invoke(this, EventArgs.Empty);
                break;
            case NativeMethods.WM_THEMECHANGED:
            case NativeMethods.WM_DISPLAYCHANGE:
            case NativeMethods.WM_DPICHANGED:
                DisplayChanged?.Invoke(this, EventArgs.Empty);
                break;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
