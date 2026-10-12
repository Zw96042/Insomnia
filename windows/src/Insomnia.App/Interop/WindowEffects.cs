using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Insomnia.App.Interop;

/// <summary>Which Windows 11 system backdrop a window asks for.</summary>
internal enum Backdrop
{
    /// <summary>Solid themed surface everywhere.</summary>
    None,
    /// <summary>Mica, for long-lived windows (Settings).</summary>
    Mica,
    /// <summary>Acrylic for transient surfaces (the flyout, dialogs).</summary>
    Transient,
}

/// <summary>
/// DWM attributes for a WPF window: immersive dark title bar, rounded corners
/// and the system backdrop. Windows 10 and Windows 11 before 22H2 get the
/// solid fallback; the caller paints the window background in that case.
/// </summary>
internal static class WindowEffects
{
    /// <summary>
    /// Apply the attributes to a window that already has a handle. Returns true
    /// when the system backdrop is drawn behind the client area, in which case
    /// the window background must stay transparent; false means the caller must
    /// paint a solid surface.
    /// </summary>
    public static bool Apply(Window window, Backdrop backdrop, bool dark, bool allowBackdrop) =>
        Apply(window, backdrop, dark, allowBackdrop, manageFrame: true);

    /// <summary>
    /// As <see cref="Apply(Window, Backdrop, bool, bool)"/>, for a window whose
    /// glass frame belongs to <see cref="System.Windows.Shell.WindowChrome"/>
    /// (which re-extends it on its own schedule).
    /// </summary>
    public static bool ApplyWithoutFrame(Window window, Backdrop backdrop, bool dark, bool allowBackdrop) =>
        Apply(window, backdrop, dark, allowBackdrop, manageFrame: false);

    private static bool Apply(Window window, Backdrop backdrop, bool dark, bool allowBackdrop, bool manageFrame)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        var darkValue = dark ? 1 : 0;
        _ = NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkValue, sizeof(int));

        if (NativeMethods.SupportsRoundedCorners)
        {
            var corner = NativeMethods.DWMWCP_ROUND;
            _ = NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        }

        var source = HwndSource.FromHwnd(hwnd);
        var wantBackdrop = allowBackdrop && backdrop != Backdrop.None && NativeMethods.SupportsSystemBackdrop && source?.CompositionTarget is not null;
        if (wantBackdrop)
        {
            source!.CompositionTarget.BackgroundColor = Colors.Transparent;
            if (manageFrame)
            {
                var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
                _ = NativeMethods.DwmExtendFrameIntoClientArea(hwnd, ref margins);
            }
            var type = backdrop == Backdrop.Mica ? NativeMethods.DWMSBT_MAINWINDOW : NativeMethods.DWMSBT_TRANSIENTWINDOW;
            if (NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_SYSTEMBACKDROP_TYPE, ref type, sizeof(int)) == 0)
            {
                return true;
            }
        }

        // Solid fallback: undo any earlier backdrop so a theme or high
        // contrast switch never leaves a transparent client area behind.
        if (NativeMethods.SupportsSystemBackdrop)
        {
            var none = NativeMethods.DWMSBT_NONE;
            _ = NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_SYSTEMBACKDROP_TYPE, ref none, sizeof(int));
        }
        if (manageFrame)
        {
            var zero = new Margins();
            _ = NativeMethods.DwmExtendFrameIntoClientArea(hwnd, ref zero);
        }
        if (source?.CompositionTarget is not null)
        {
            source.CompositionTarget.BackgroundColor = dark ? Color.FromRgb(0x20, 0x20, 0x20) : Color.FromRgb(0xF3, 0xF3, 0xF3);
        }
        return false;
    }

    /// <summary>Keep a window out of Alt+Tab and the taskbar (flyouts and menus).</summary>
    public static void MakeToolWindow(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }
        var style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        style = (style | NativeMethods.WS_EX_TOOLWINDOW) & ~NativeMethods.WS_EX_APPWINDOW;
        _ = NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(style));
    }
}
