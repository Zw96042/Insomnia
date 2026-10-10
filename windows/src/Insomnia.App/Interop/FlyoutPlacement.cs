using System.Runtime.InteropServices;

namespace Insomnia.App.Interop;

/// <summary>Which screen edge the taskbar (and so the flyout's anchor) sits on.</summary>
internal enum TaskbarEdge
{
    Bottom,
    Top,
    Left,
    Right,
}

/// <summary>Where a flyout goes, in physical pixels on the anchor's monitor.</summary>
internal readonly record struct Placement(int X, int Y, TaskbarEdge Edge, uint Dpi, NativeRect WorkArea);

/// <summary>
/// Places a flyout the way Windows 11 places its own: against the taskbar,
/// 12 px (scaled) clear of it and of the screen edges, centred on the tray
/// icon along the taskbar and clamped into the work area. Works with the
/// taskbar on any edge, auto-hidden, and on any monitor.
/// </summary>
internal static class FlyoutPlacement
{
    private const int MarginDip = 12;

    /// <param name="anchor">Tray icon rectangle (or a 1×1 rectangle at the cursor), physical pixels.</param>
    /// <param name="widthDip">Flyout width in DIPs.</param>
    /// <param name="heightDip">Flyout height in DIPs.</param>
    public static Placement Compute(NativeRect anchor, double widthDip, double heightDip)
    {
        var center = new NativePoint(anchor.Left + anchor.Width / 2, anchor.Top + anchor.Height / 2);
        var monitor = NativeMethods.MonitorFromPoint(center, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var info = NativeMethods.MonitorInfoFor(monitor);
        var work = info?.Work ?? new NativeRect { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
        var bounds = info?.Monitor ?? work;
        var dpi = NativeMethods.DpiForMonitor(monitor);
        var scale = dpi / 96.0;
        var width = (int)Math.Ceiling(widthDip * scale);
        var height = (int)Math.Ceiling(heightDip * scale);
        var margin = (int)Math.Round(MarginDip * scale);
        var edge = DetectEdge(work, bounds, anchor);

        int x, y;
        switch (edge)
        {
            case TaskbarEdge.Top:
                x = center.X - width / 2;
                y = work.Top + margin;
                break;
            case TaskbarEdge.Left:
                x = work.Left + margin;
                y = center.Y - height / 2;
                break;
            case TaskbarEdge.Right:
                x = work.Right - width - margin;
                y = center.Y - height / 2;
                break;
            default:
                x = center.X - width / 2;
                y = work.Bottom - height - margin;
                break;
        }
        x = Clamp(x, work.Left + margin, work.Right - width - margin);
        y = Clamp(y, work.Top + margin, work.Bottom - height - margin);
        return new Placement(x, y, edge, dpi, work);
    }

    /// <summary>A rectangle for "near the tray" when the icon's own rectangle is unknown.</summary>
    public static NativeRect FallbackAnchor()
    {
        if (TaskbarRect() is { } taskbar)
        {
            // The notification area sits at the far end of the taskbar.
            return taskbar.Edge is TaskbarEdge.Left or TaskbarEdge.Right
                ? new NativeRect { Left = taskbar.Rect.Left, Top = taskbar.Rect.Bottom - 48, Right = taskbar.Rect.Right, Bottom = taskbar.Rect.Bottom - 47 }
                : new NativeRect { Left = taskbar.Rect.Right - 160, Top = taskbar.Rect.Top, Right = taskbar.Rect.Right - 159, Bottom = taskbar.Rect.Bottom };
        }
        var primary = NativeMethods.MonitorFromPoint(new NativePoint(0, 0), NativeMethods.MONITOR_DEFAULTTOPRIMARY);
        var work = NativeMethods.MonitorInfoFor(primary)?.Work ?? new NativeRect { Right = 1920, Bottom = 1080 };
        return new NativeRect { Left = work.Right - 1, Top = work.Bottom - 1, Right = work.Right, Bottom = work.Bottom };
    }

    private static TaskbarEdge DetectEdge(NativeRect work, NativeRect bounds, NativeRect anchor)
    {
        // The work area is inset on the taskbar's side of this monitor.
        var gaps = new (int Gap, TaskbarEdge Edge)[]
        {
            (work.Top - bounds.Top, TaskbarEdge.Top),
            (bounds.Bottom - work.Bottom, TaskbarEdge.Bottom),
            (work.Left - bounds.Left, TaskbarEdge.Left),
            (bounds.Right - work.Right, TaskbarEdge.Right),
        };
        var widest = gaps.MaxBy(g => g.Gap);
        if (widest.Gap > 0)
        {
            return widest.Edge;
        }
        // Auto-hide leaves no gap: ask the shell, then fall back to where the anchor is.
        if (TaskbarRect() is { } taskbar)
        {
            return taskbar.Edge;
        }
        var nearest = new (int Distance, TaskbarEdge Edge)[]
        {
            (anchor.Top - bounds.Top, TaskbarEdge.Top),
            (bounds.Bottom - anchor.Bottom, TaskbarEdge.Bottom),
            (anchor.Left - bounds.Left, TaskbarEdge.Left),
            (bounds.Right - anchor.Right, TaskbarEdge.Right),
        };
        return nearest.MinBy(n => n.Distance).Edge;
    }

    private static (TaskbarEdge Edge, NativeRect Rect)? TaskbarRect()
    {
        var data = new AppBarData { Size = (uint)Marshal.SizeOf<AppBarData>() };
        if (NativeMethods.SHAppBarMessage(NativeMethods.ABM_GETTASKBARPOS, ref data) == UIntPtr.Zero)
        {
            return null;
        }
        var edge = data.Edge switch
        {
            NativeMethods.ABE_TOP => TaskbarEdge.Top,
            NativeMethods.ABE_LEFT => TaskbarEdge.Left,
            NativeMethods.ABE_RIGHT => TaskbarEdge.Right,
            _ => TaskbarEdge.Bottom,
        };
        return (edge, data.Rect);
    }

    private static int Clamp(int value, int min, int max) => max < min ? min : Math.Clamp(value, min, max);
}
