using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using Insomnia.App.Animation;
using Insomnia.App.Controls;
using Insomnia.App.Demo;
using Insomnia.App.Formatting;
using Insomnia.App.Interop;
using Insomnia.App.Theme;
using Insomnia.Core.Engine;
using Insomnia.Core.Logging;

namespace Insomnia.App;

/// <summary>
/// The right-click menu: status lines read from the controller when it
/// opens, warnings with their actions, then End session, Settings, Open logs
/// and Quit. A WPF <see cref="ContextMenu"/> styled like Windows 11 menus,
/// placed at the cursor in physical pixels and flipped to stay on screen.
/// </summary>
internal sealed class TrayMenu
{
    /// <summary>Wait this long for the status read before opening with a placeholder.</summary>
    private static readonly TimeSpan DetailsWait = TimeSpan.FromMilliseconds(200);

    private readonly ISessionController _controller;
    private readonly IAppShell _shell;
    private ContextMenu? _open;

    public TrayMenu(ISessionController controller, IAppShell shell)
    {
        _controller = controller;
        _shell = shell;
    }

    public bool IsOpen => _open?.IsOpen == true;

    public void Close()
    {
        if (_open is not null)
        {
            _open.IsOpen = false;
        }
    }

    public void Show(NativePoint at, bool fromFlyout) => _ = ShowAsync(at, fromFlyout);

    private async Task ShowAsync(NativePoint at, bool fromFlyout)
    {
        try
        {
            await OpenAsync(at, fromFlyout);
        }
        catch (Exception ex)
        {
            Log.Error("menu: open failed", ex);
        }
    }

    private async Task OpenAsync(NativePoint at, bool fromFlyout)
    {
        Close();
        // A tray app must own the foreground for its menu to close when the
        // user clicks elsewhere (as with TrackPopupMenu). The flyout's own
        // button keeps the flyout in front instead.
        if (!fromFlyout)
        {
            NativeMethods.SetForegroundWindow(ThemeManager.Current.Watcher.Handle);
        }

        var detailsTask = Task.Run(_controller.ReadDetails);
        await Task.WhenAny(detailsTask, Task.Delay(DetailsWait));

        var menu = new ContextMenu
        {
            Placement = PlacementMode.AbsolutePoint,
            StaysOpen = false,
        };
        var statusSection = new List<Control>();
        if (detailsTask.IsCompletedSuccessfully)
        {
            statusSection.AddRange(StatusItems(detailsTask.Result));
        }
        else
        {
            statusSection.Add(StatusItem(Glyphs.Refresh, "Reading status…", attention: false));
        }
        Build(menu, statusSection);

        // Place at the cursor: DIPs for WPF's first layout, then exact physical pixels once open.
        var dpi = NativeMethods.DpiForMonitor(NativeMethods.MonitorFromPoint(at, NativeMethods.MONITOR_DEFAULTTONEAREST)) / 96.0;
        menu.HorizontalOffset = at.X / dpi;
        menu.VerticalOffset = at.Y / dpi;
        menu.Opened += (_, _) =>
        {
            Place(menu, at);
            AnimateIn(menu);
            if (!fromFlyout && PresentationSource.FromVisual(menu) is HwndSource source)
            {
                NativeMethods.SetForegroundWindow(source.Handle);
            }
        };
        menu.SizeChanged += (_, _) => Place(menu, at);
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_open, menu))
            {
                _open = null;
            }
        };
        _open = menu;
        menu.IsOpen = true;

        if (!detailsTask.IsCompleted)
        {
            try
            {
                var details = await detailsTask;
                if (menu.IsOpen)
                {
                    ReplaceStatus(menu, statusSection, StatusItems(details));
                }
            }
            catch (Exception ex)
            {
                Log.Error("menu: reading status failed", ex);
                if (menu.IsOpen)
                {
                    ReplaceStatus(menu, statusSection, [StatusItem(Glyphs.Warning, "Status unavailable: " + ex.Message, attention: true)]);
                }
            }
        }
        else if (detailsTask.IsFaulted)
        {
            Log.Error("menu: reading status failed", detailsTask.Exception!.GetBaseException());
            ReplaceStatus(menu, statusSection, [StatusItem(Glyphs.Warning, "Status unavailable", attention: true)]);
        }
    }

    private void Build(ContextMenu menu, List<Control> statusSection)
    {
        foreach (var item in statusSection)
        {
            menu.Items.Add(item);
        }

        var status = _controller.Status;
        if (status.Warnings.Count > 0)
        {
            menu.Items.Add(new Separator());
            foreach (var warning in status.Warnings)
            {
                menu.Items.Add(WarningItem(warning));
                if (warning.Action is { } action)
                {
                    menu.Items.Add(Command(action.Label, null, () => _ = PerformAsync(action)));
                }
            }
        }

        menu.Items.Add(new Separator());
        if (status.Phase == SessionPhase.Active)
        {
            menu.Items.Add(Command("End session…", Glyphs.Stop, () => _ = _shell.ConfirmAndEndSessionAsync()));
        }
        menu.Items.Add(Command("Settings…", Glyphs.Settings, _shell.ShowSettings));
        menu.Items.Add(Command("Open logs", Glyphs.Folder, _shell.OpenLogs));
        menu.Items.Add(new Separator());
        menu.Items.Add(Command("Quit Insomnia", Glyphs.Power, () => _ = _shell.QuitAsync()));

        if (_controller is IDemoControls demo)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(SectionItem("Demo"));
            foreach (var (label, run) in demo.DemoCommands)
            {
                menu.Items.Add(Command(label, null, run));
            }
        }
    }

    private static void ReplaceStatus(ContextMenu menu, List<Control> current, IReadOnlyList<Control> replacement)
    {
        var index = current.Count > 0 ? menu.Items.IndexOf(current[0]) : 0;
        foreach (var item in current)
        {
            menu.Items.Remove(item);
        }
        current.Clear();
        foreach (var item in replacement)
        {
            menu.Items.Insert(Math.Max(index, 0) + current.Count, item);
            current.Add(item);
        }
    }

    private static List<Control> StatusItems(StatusDetails details) =>
        StatusText.DetailLines(details).Select(line => (Control)StatusItem(line.Glyph, line.Text, line.Attention)).ToList();

    private static MenuItem StatusItem(string glyph, string text, bool attention)
    {
        var item = new MenuItem { Header = text };
        item.SetResourceReference(FrameworkElement.StyleProperty, "MenuItem.Status");
        Ui.SetGlyph(item, glyph);
        if (attention)
        {
            item.SetResourceReference(Control.ForegroundProperty, "Brush.Caution");
        }
        return item;
    }

    private static MenuItem SectionItem(string text)
    {
        var item = new MenuItem { Header = text };
        item.SetResourceReference(FrameworkElement.StyleProperty, "MenuItem.Section");
        return item;
    }

    private static MenuItem WarningItem(MenuWarning warning)
    {
        var text = new TextBlock { Text = warning.Text, TextWrapping = TextWrapping.Wrap, MaxWidth = 320 };
        var item = new MenuItem { Header = text };
        item.SetResourceReference(FrameworkElement.StyleProperty, "MenuItem.Status");
        item.SetResourceReference(Control.ForegroundProperty, "Brush.TextPrimary");
        Ui.SetGlyph(item, warning.Severity switch
        {
            WarningSeverity.Error => Glyphs.Error,
            WarningSeverity.Warning => Glyphs.Warning,
            _ => Glyphs.Info,
        });
        var brushKey = warning.Severity switch
        {
            WarningSeverity.Error => "Brush.Critical",
            WarningSeverity.Warning => "Brush.Caution",
            _ => "Brush.AccentText",
        };
        item.SetResourceReference(Ui.GlyphBrushProperty, brushKey);
        return item;
    }

    private static MenuItem Command(string text, string? glyph, Action run)
    {
        var item = new MenuItem { Header = text };
        Ui.SetGlyph(item, glyph);
        item.Click += (_, _) => run();
        return item;
    }

    private async Task PerformAsync(MenuAction action)
    {
        if (action is MenuAction.OpenLogs)
        {
            _shell.OpenLogs();
            return;
        }
        try
        {
            await _controller.PerformAsync(action);
        }
        catch (Exception ex)
        {
            Log.Error($"menu action \"{action.Label}\" failed", ex);
            _shell.ShowMessage($"{action.Label} failed: {ex.Message}", critical: true);
        }
    }

    /// <summary>
    /// Put the menu's visible corner at the point, opening up and/or left when
    /// there is no room, like the shell's own tray menus. The template's
    /// shadow margin is outside the visible menu, so it is subtracted.
    /// </summary>
    private static void Place(ContextMenu menu, NativePoint at)
    {
        if (PresentationSource.FromVisual(menu) is not HwndSource source || !NativeMethods.GetWindowRect(source.Handle, out var rect))
        {
            return;
        }
        var monitor = NativeMethods.MonitorFromPoint(at, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var work = NativeMethods.MonitorInfoFor(monitor)?.Work ?? rect;
        var scale = NativeMethods.DpiForMonitor(monitor) / 96.0;
        // Matches the Margin of the ContextMenu template's root (12, 8, 12, 16).
        int left = (int)Math.Round(12 * scale), top = (int)Math.Round(8 * scale), right = left, bottom = (int)Math.Round(16 * scale);
        var visibleWidth = rect.Width - left - right;
        var visibleHeight = rect.Height - top - bottom;

        var x = at.X + visibleWidth <= work.Right ? at.X : at.X - visibleWidth;
        var y = at.Y + visibleHeight <= work.Bottom ? at.Y : at.Y - visibleHeight;
        x = Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - visibleWidth));
        y = Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - visibleHeight));
        var targetX = x - left;
        var targetY = y - top;
        if (targetX != rect.Left || targetY != rect.Top)
        {
            NativeMethods.SetWindowPos(source.Handle, IntPtr.Zero, targetX, targetY, 0, 0,
                NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        }
        menu.Tag = y < at.Y ? "up" : "down";
    }

    /// <summary>Windows 11 menus slide a few pixels from the point they open at.</summary>
    private static void AnimateIn(ContextMenu menu)
    {
        if (menu.Template?.FindName("MenuRoot", menu) is not FrameworkElement root)
        {
            return;
        }
        var reduced = Motion.ReduceMotion;
        if (reduced)
        {
            Motion.Jump(root, UIElement.OpacityProperty, 0);
            Motion.EaseTo(root, UIElement.OpacityProperty, 1, Motion.ReducedDuration);
            return;
        }
        var shift = new TranslateTransform(0, Equals(menu.Tag, "up") ? 10 : -10);
        root.RenderTransform = shift;
        Motion.Jump(root, UIElement.OpacityProperty, 0);
        Motion.SpringTo(shift, TranslateTransform.YProperty, 0, Motion.Base);
        Motion.EaseOutTo(root, UIElement.OpacityProperty, 1, TimeSpan.FromMilliseconds(120));
    }
}
