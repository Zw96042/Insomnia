using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Insomnia.App.Animation;
using Insomnia.App.Interop;
using Insomnia.App.Services;
using Insomnia.App.Settings;
using Insomnia.App.Theme;
using Insomnia.Core.Engine;
using Insomnia.Core.Logging;

namespace Insomnia.App;

/// <summary>
/// Settings, laid out like Windows 11 Settings: sections on the left, cards
/// on the right, Mica behind both. Changes save themselves (see
/// <see cref="SettingsViewModel"/>); this class only wires input to it.
/// </summary>
internal sealed partial class SettingsWindow
{
    private const string UpstreamRepository = "https://github.com/krishhgg/Insomnia";

    private static readonly string[] PageTitles = ["Session", "Lid close", "Agent apps", "Battery & thermal", "Network", "General"];

    private readonly SettingsViewModel _viewModel;
    private readonly IAppShell _shell;
    private readonly StackPanel[] _pages;
    private readonly TranslateTransform _pageShift = new();

    public SettingsWindow(ISessionController controller, ISettingsServices services, IAppShell shell)
    {
        _shell = shell;
        InitializeComponent();
        ThemeManager.Current.Attach(this, Backdrop.Mica, managesFrame: true);
        _viewModel = new SettingsViewModel(controller, services);
        DataContext = _viewModel;
        _pages = [SessionPage, LidPage, AgentPage, BatteryPage, NetworkPage, GeneralPage];
        Pages.RenderTransform = _pageShift;

        Mark.SetOpen(true, animate: false);
        AboutMark.SetOpen(true, animate: false);
        VersionText.Text = "Version " + AppVersion();
        PortLink.Visibility = AppComposition.PortRepository is null ? Visibility.Collapsed : Visibility.Visible;

        Nav.SelectionChanged += (_, _) => ShowPage(Nav.SelectedIndex, animate: true);
        Activated += (_, _) =>
        {
            // Task Manager and Credential Manager can change underneath us.
            _viewModel.RefreshExternal();
            _ = _viewModel.RefreshWouldFreezeAsync();
        };
        Closing += (_, _) => _ = _viewModel.FlushAsync();
    }

    /// <summary>Save anything pending before the app quits.</summary>
    public Task FlushAsync() => _viewModel.FlushAsync();

    public static string AppVersion()
    {
        var version = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString()
            ?? "unknown";
        var plus = version.IndexOf('+', StringComparison.Ordinal);
        return plus > 0 ? version[..plus] : version;
    }

    private void ShowPage(int index, bool animate)
    {
        if (index < 0 || index >= _pages.Length)
        {
            return;
        }
        for (var i = 0; i < _pages.Length; i++)
        {
            _pages[i].Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
        }
        PageHeading.Text = PageTitles[index];
        Scroller.ScrollToTop();
        if (!animate)
        {
            return;
        }
        var reduced = Motion.ReduceMotion;
        Motion.Jump(Pages, OpacityProperty, 0);
        Motion.To(Pages, OpacityProperty, 1, Motion.Base, reduced);
        if (!reduced)
        {
            Motion.Jump(_pageShift, TranslateTransform.YProperty, 12);
            Motion.SpringTo(_pageShift, TranslateTransform.YProperty, 0, Motion.Base);
        }
    }

    #region Presets

    private void OnPresetClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PresetChip chip)
        {
            PresetError.Text = null;
            _viewModel.MakeDefault(chip.Seconds);
        }
    }

    private void OnPresetRemove(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PresetChip chip)
        {
            return;
        }
        if (_viewModel.Presets.Count <= 1)
        {
            PresetError.Text = "Keep at least one preset.";
            return;
        }
        PresetError.Text = null;
        _viewModel.RemovePreset(chip.Seconds);
    }

    private void OnAddPreset(object sender, RoutedEventArgs e) => AddFrom(NewPreset, PresetError, _viewModel.AddPreset);

    #endregion

    #region Lists

    private void OnAddFreeze(object sender, RoutedEventArgs e) => AddFrom(NewFreeze, FreezeError, _viewModel.AddFreeze);

    private void OnAddAgent(object sender, RoutedEventArgs e) => AddFrom(NewAgent, AgentError, _viewModel.AddAgent);

    private void OnAddTmux(object sender, RoutedEventArgs e) => AddFrom(NewTmux, TmuxError, _viewModel.AddTmuxTarget);

    private void OnAddKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox box)
        {
            return;
        }
        e.Handled = true;
        switch (box.Tag as string)
        {
            case "preset":
                OnAddPreset(box, e);
                break;
            case "freeze":
                OnAddFreeze(box, e);
                break;
            case "agent":
                OnAddAgent(box, e);
                break;
            case "tmux":
                OnAddTmux(box, e);
                break;
        }
    }

    private static void AddFrom(TextBox box, TextBlock error, Func<string, string?> add)
    {
        var message = add(box.Text);
        error.Text = message;
        if (message is null)
        {
            box.Clear();
        }
        box.Focus();
    }

    private void OnRemoveListItem(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: string item } element)
        {
            return;
        }
        var list = FindAncestor<ItemsControl>(element);
        switch (list?.Tag as string)
        {
            case "freeze":
                _viewModel.RemoveFreeze(item);
                break;
            case "agent":
                _viewModel.RemoveAgent(item);
                break;
            case "tmux":
                _viewModel.RemoveTmuxTarget(item);
                break;
        }
    }

    private void OnAddRunningFreeze(object sender, RoutedEventArgs e) =>
        _ = ShowRunningAppsAsync(FreezeRunning, _viewModel.FreezeCandidatesAsync, image => FreezeError.Text = _viewModel.AddFreeze(image));

    private void OnAddRunningAgent(object sender, RoutedEventArgs e) =>
        _ = ShowRunningAppsAsync(AgentRunning, _viewModel.AgentCandidatesAsync, image => AgentError.Text = _viewModel.AddAgent(image));

    /// <summary>A menu under the button listing running apps by name, with the executable beside it; picking one adds the executable.</summary>
    private static async Task ShowRunningAppsAsync(Button button, Func<Task<IReadOnlyList<RunningApp>>> candidates, Action<string> add)
    {
        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
        var loading = new MenuItem { Header = "Looking for running apps…" };
        loading.SetResourceReference(StyleProperty, "MenuItem.Status");
        menu.Items.Add(loading);
        menu.IsOpen = true;
        try
        {
            var apps = await candidates();
            menu.Items.Clear();
            if (apps.Count == 0)
            {
                var none = new MenuItem { Header = "No other apps with a window are running" };
                none.SetResourceReference(StyleProperty, "MenuItem.Status");
                menu.Items.Add(none);
                return;
            }
            foreach (var app in apps)
            {
                var item = new MenuItem { Header = app.DisplayName, InputGestureText = app.Image };
                item.Click += (_, _) => add(app.Image);
                menu.Items.Add(item);
            }
        }
        catch (Exception ex)
        {
            Log.Error("settings: listing running apps failed", ex);
            menu.Items.Clear();
            var failed = new MenuItem { Header = "Couldn't list running apps: " + ex.Message };
            failed.SetResourceReference(StyleProperty, "MenuItem.Status");
            menu.Items.Add(failed);
        }
    }

    #endregion

    #region Network

    private void OnCommitOnEnter(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TextBox box)
        {
            box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            box.SelectAll();
            e.Handled = true;
        }
    }

    private void OnPasswordKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            OnSavePassword(sender, e);
        }
    }

    private void OnSavePassword(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SaveHotspotPassword(HotspotPassword.Password) is null)
        {
            HotspotPassword.Clear();
        }
    }

    private void OnClearPassword(object sender, RoutedEventArgs e)
    {
        HotspotPassword.Clear();
        _viewModel.ClearHotspotPassword();
    }

    #endregion

    #region General

    private void OnOpenLogs(object sender, RoutedEventArgs e) => _shell.OpenLogs();

    private void OnOpenUpstream(object sender, RoutedEventArgs e) => OpenUrl(UpstreamRepository);

    private void OnOpenPort(object sender, RoutedEventArgs e)
    {
        if (AppComposition.PortRepository is { } uri)
        {
            OpenUrl(uri.AbsoluteUri);
        }
    }

    private static void OpenUrl(string url)
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Error($"settings: opening {url} failed", ex);
        }
    }

    #endregion

    private static T? FindAncestor<T>(DependencyObject start)
        where T : DependencyObject
    {
        var current = VisualTreeHelper.GetParent(start);
        while (current is not null and not T)
        {
            current = VisualTreeHelper.GetParent(current);
        }
        return current as T;
    }
}
