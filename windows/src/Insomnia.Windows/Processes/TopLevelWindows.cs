using System.Runtime.InteropServices;

namespace Insomnia.Windows.Processes;

/// <summary>A top-level window and what the taskbar rules need to know about it.</summary>
internal readonly record struct TopLevelWindow(nint Handle, int Pid, bool Visible, bool Unowned, bool AppearsOnTaskbar);

/// <summary>
/// Enumerates top-level windows. The callback only collects handles; every
/// query runs afterwards in managed code, since an exception escaping an
/// unmanaged callback would end the process.
/// </summary>
internal static unsafe class TopLevelWindows
{
    public static List<TopLevelWindow> Enumerate()
    {
        var handles = new List<nint>(512);
        var gc = GCHandle.Alloc(handles);
        try
        {
            ProcessNative.EnumWindows(&Collect, GCHandle.ToIntPtr(gc));
        }
        finally
        {
            gc.Free();
        }

        var selfPid = Environment.ProcessId;
        var windows = new List<TopLevelWindow>(handles.Count);
        foreach (var hwnd in handles)
        {
            if (ProcessNative.GetWindowThreadProcessId(hwnd, out var pid) == 0)
            {
                continue;
            }
            var visible = ProcessNative.IsWindowVisible(hwnd);
            var unowned = ProcessNative.GetWindow(hwnd, ProcessNative.GwOwner) == 0;
            // Insomnia's own windows are never candidates, and skipping them
            // keeps every query below from touching our UI thread.
            var onTaskbar = visible && unowned && (int)pid != selfPid && AppearsOnTaskbar(hwnd);
            windows.Add(new TopLevelWindow(hwnd, (int)pid, visible, unowned, onTaskbar));
        }
        return windows;
    }

    /// <summary>
    /// The style, cloaking and title tests for a visible, unowned window. A
    /// tool window shows on the taskbar only with <c>WS_EX_APPWINDOW</c>; a
    /// cloaked window (a suspended UWP frame, a window on another virtual
    /// desktop) does not count; neither does an untitled one. The title is
    /// read with <c>InternalGetWindowText</c>, which never sends a message, so
    /// a hung window cannot block the snapshot.
    /// </summary>
    private static bool AppearsOnTaskbar(nint hwnd)
    {
        var exStyle = ProcessNative.GetWindowExStyle(hwnd);
        if ((exStyle & ProcessNative.WsExToolWindow) != 0 && (exStyle & ProcessNative.WsExAppWindow) == 0)
        {
            return false;
        }
        int cloaked = 0;
        if (ProcessNative.DwmGetWindowAttribute(hwnd, ProcessNative.DwmwaCloaked, &cloaked, sizeof(int)) >= 0 && cloaked != 0)
        {
            return false;
        }
        char* title = stackalloc char[2];
        return ProcessNative.InternalGetWindowText(hwnd, title, 2) > 0;
    }

    [UnmanagedCallersOnly]
    private static int Collect(nint hwnd, nint parameter)
    {
        if (GCHandle.FromIntPtr(parameter).Target is List<nint> list)
        {
            list.Add(hwnd);
        }
        return 1;
    }
}
