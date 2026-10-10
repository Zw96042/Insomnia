using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Insomnia.App.Interop;
using Insomnia.Core.Logging;
using Microsoft.Win32;

namespace Insomnia.App.Theme;

/// <summary>
/// Follows the system: app light/dark mode (AppsUseLightTheme), the taskbar's
/// mode for the tray icon (SystemUsesLightTheme), high contrast and the accent
/// color. Swaps the palette dictionary, rebuilds the accent brushes and
/// re-applies DWM attributes to registered windows when any of them change.
/// UI thread only.
/// </summary>
internal sealed class ThemeManager : IDisposable
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const int PaletteIndex = 0;
    private const int AccentIndex = 1;

    private readonly Application _app;
    private readonly DispatcherTimer _debounce;
    private readonly List<(WeakReference<Window> Window, Backdrop Backdrop, bool ManagesFrame)> _windows = [];
    private global::Windows.UI.ViewManagement.UISettings? _uiSettings;
    private AccentSet? _accent;
    private bool _initialized;

    private ThemeManager(Application app)
    {
        _app = app;
        Watcher = new SystemWatcher();
        Watcher.SettingChanged += (_, _) => Schedule();
        Watcher.AccentChanged += (_, _) => Schedule();
        Watcher.DisplayChanged += (_, _) => Schedule();
        SystemParameters.StaticPropertyChanged += OnSystemParameterChanged;
        _debounce = new DispatcherTimer(TimeSpan.FromMilliseconds(120), DispatcherPriority.Normal, OnDebounce, app.Dispatcher);
        _debounce.Stop();
        try
        {
            _uiSettings = new global::Windows.UI.ViewManagement.UISettings();
            _uiSettings.ColorValuesChanged += OnColorValuesChanged;
        }
        catch (Exception ex)
        {
            _uiSettings = null;
            Log.Warn($"UISettings unavailable, accent from DWM colorization: {ex.Message}");
        }
        Refresh();
    }

    public static ThemeManager Current { get; private set; } = null!;

    /// <summary>The hidden broadcast window; also the foreground target before a tray menu opens.</summary>
    public SystemWatcher Watcher { get; }

    /// <summary>Apps use the dark theme.</summary>
    public bool IsDark { get; private set; }

    /// <summary>The taskbar uses the light theme, so the tray glyph is drawn dark.</summary>
    public bool IsTaskbarLight { get; private set; }

    public bool IsHighContrast { get; private set; }

    /// <summary>The accent as fills use it in the current theme.</summary>
    public Color AccentColor => _accent?.Fill(IsDark) ?? Color.FromRgb(0x00, 0x5F, 0xB8);

    /// <summary>App theme, high contrast or accent changed (resources already swapped).</summary>
    public event EventHandler? ThemeChanged;

    /// <summary>Raised after every system settings broadcast, so the tray icon can re-check its theme and size.</summary>
    public event EventHandler? SystemChanged;

    public static ThemeManager Initialize(Application app)
    {
        Current = new ThemeManager(app);
        return Current;
    }

    /// <summary>
    /// Keep a window's DWM attributes and background in step with the theme.
    /// <paramref name="managesFrame"/> is false for windows whose glass frame
    /// is owned by <see cref="System.Windows.Shell.WindowChrome"/>.
    /// </summary>
    public void Attach(Window window, Backdrop backdrop, bool managesFrame)
    {
        _windows.Add((new WeakReference<Window>(window), backdrop, managesFrame));
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
        {
            ApplyTo(window, backdrop, managesFrame);
        }
        else
        {
            window.SourceInitialized += (_, _) => ApplyTo(window, backdrop, managesFrame);
        }
    }

    public void Dispose()
    {
        SystemParameters.StaticPropertyChanged -= OnSystemParameterChanged;
        if (_uiSettings is not null)
        {
            _uiSettings.ColorValuesChanged -= OnColorValuesChanged;
        }
        _debounce.Stop();
        Watcher.Dispose();
    }

    private void OnColorValuesChanged(global::Windows.UI.ViewManagement.UISettings sender, object args) =>
        _app.Dispatcher.BeginInvoke(Schedule);

    private void OnSystemParameterChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SystemParameters.HighContrast) or nameof(SystemParameters.ClientAreaAnimation))
        {
            Schedule();
        }
    }

    private void Schedule()
    {
        _debounce.Stop();
        _debounce.Start();
    }

    private void OnDebounce(object? sender, EventArgs e)
    {
        _debounce.Stop();
        Refresh();
    }

    private void Refresh()
    {
        var dark = ReadPersonalize("AppsUseLightTheme", 1) == 0;
        var taskbarLight = ReadPersonalize("SystemUsesLightTheme", 0) != 0;
        var highContrast = SystemParameters.HighContrast;
        var accent = ReadAccent();

        var themeChanged = !_initialized || dark != IsDark || highContrast != IsHighContrast || accent != _accent;
        IsDark = dark;
        IsTaskbarLight = taskbarLight;
        IsHighContrast = highContrast;
        _accent = accent;

        if (themeChanged)
        {
            ApplyResources();
            foreach (var (reference, backdrop, managesFrame) in _windows.ToList())
            {
                if (reference.TryGetTarget(out var window))
                {
                    ApplyTo(window, backdrop, managesFrame);
                }
                else
                {
                    _windows.RemoveAll(w => ReferenceEquals(w.Window, reference));
                }
            }
            if (_initialized)
            {
                Log.Info($"theme: {(dark ? "dark" : "light")}{(highContrast ? ", high contrast" : "")}, accent {AccentColor}");
                ThemeChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        _initialized = true;
        SystemChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyResources()
    {
        var merged = _app.Resources.MergedDictionaries;
        ResourceDictionary palette;
        if (IsHighContrast)
        {
            palette = HighContrastPalette();
        }
        else
        {
            palette = new ResourceDictionary { Source = new Uri($"/Insomnia;component/Theme/{(IsDark ? "Dark" : "Light")}.xaml", UriKind.Relative) };
        }
        var accent = IsHighContrast ? new ResourceDictionary() : AccentDictionary(_accent!, IsDark);
        merged[PaletteIndex] = palette;
        merged[AccentIndex] = accent;
    }

    private void ApplyTo(Window window, Backdrop backdrop, bool managesFrame)
    {
        bool backdropOn;
        if (managesFrame)
        {
            backdropOn = WindowEffects.Apply(window, backdrop, IsDark, allowBackdrop: !IsHighContrast);
        }
        else
        {
            backdropOn = WindowEffects.ApplyWithoutFrame(window, backdrop, IsDark, allowBackdrop: !IsHighContrast);
        }
        if (backdropOn)
        {
            window.Background = Brushes.Transparent;
        }
        else
        {
            window.SetResourceReference(Window.BackgroundProperty, backdrop == Backdrop.Mica ? "Brush.SurfaceSolid" : "Brush.FlyoutSolid");
        }
        BackdropState.SetIsBackdropOn(window, backdropOn);
    }

    private static int ReadPersonalize(string name, int fallback)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue(name) is int value ? value : fallback;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return fallback;
        }
    }

    private AccentSet ReadAccent()
    {
        if (_uiSettings is not null)
        {
            try
            {
                return new AccentSet(
                    Convert(_uiSettings.GetColorValue(global::Windows.UI.ViewManagement.UIColorType.Accent)),
                    Convert(_uiSettings.GetColorValue(global::Windows.UI.ViewManagement.UIColorType.AccentDark1)),
                    Convert(_uiSettings.GetColorValue(global::Windows.UI.ViewManagement.UIColorType.AccentDark2)),
                    Convert(_uiSettings.GetColorValue(global::Windows.UI.ViewManagement.UIColorType.AccentLight2)),
                    Convert(_uiSettings.GetColorValue(global::Windows.UI.ViewManagement.UIColorType.AccentLight3)));
            }
            catch (Exception ex)
            {
                Log.Warn($"UISettings accent read failed: {ex.Message}");
            }
        }
        var baseColor = Color.FromRgb(0x00, 0x78, 0xD4);
        if (NativeMethods.DwmGetColorizationColor(out var argb, out _) == 0)
        {
            baseColor = Color.FromRgb((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
        }
        return new AccentSet(
            baseColor,
            Blend(baseColor, Colors.Black, 0.2),
            Blend(baseColor, Colors.Black, 0.4),
            Blend(baseColor, Colors.White, 0.45),
            Blend(baseColor, Colors.White, 0.65));
    }

    private static Color Convert(global::Windows.UI.Color c) => Color.FromArgb(c.A, c.R, c.G, c.B);

    private static Color Blend(Color a, Color b, double t) => Color.FromRgb(
        (byte)Math.Round(a.R + (b.R - a.R) * t),
        (byte)Math.Round(a.G + (b.G - a.G) * t),
        (byte)Math.Round(a.B + (b.B - a.B) * t));

    private static ResourceDictionary AccentDictionary(AccentSet accent, bool dark)
    {
        var fill = accent.Fill(dark);
        var text = dark ? accent.Light3 : accent.Dark2;
        var onAccent = dark ? Colors.Black : Colors.White;
        var dictionary = new ResourceDictionary();
        Add(dictionary, "Accent", fill);
        Add(dictionary, "AccentHover", Color.FromArgb(0xE6, fill.R, fill.G, fill.B));
        Add(dictionary, "AccentPressed", Color.FromArgb(0xCC, fill.R, fill.G, fill.B));
        Add(dictionary, "AccentText", text);
        Add(dictionary, "TextOnAccent", onAccent);
        return dictionary;
    }

    private static void Add(ResourceDictionary dictionary, string key, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        dictionary["Color." + key] = color;
        dictionary["Brush." + key] = brush;
    }

    /// <summary>
    /// High contrast: every palette key mapped onto the user's system colors
    /// by role, so the UI stays legible in any contrast theme.
    /// </summary>
    private static ResourceDictionary HighContrastPalette()
    {
        var template = new ResourceDictionary { Source = new Uri("/Insomnia;component/Theme/Light.xaml", UriKind.Relative) };
        var dictionary = new ResourceDictionary();
        foreach (var raw in template.Keys)
        {
            if (raw is not string key)
            {
                continue;
            }
            var name = key[(key.IndexOf('.', StringComparison.Ordinal) + 1)..];
            var color = HighContrastColor(name);
            if (key.StartsWith("Color.", StringComparison.Ordinal))
            {
                dictionary[key] = color;
            }
            else
            {
                var brush = new SolidColorBrush(color);
                brush.Freeze();
                dictionary[key] = brush;
            }
        }
        return dictionary;
    }

    private static Color HighContrastColor(string name) => name switch
    {
        "TextDisabled" => SystemColors.GrayTextColor,
        "Accent" or "AccentHover" or "AccentPressed" => SystemColors.HighlightColor,
        "TextOnAccent" => SystemColors.HighlightTextColor,
        "AccentText" => SystemColors.HotTrackColor,
        "Shadow" => Colors.Transparent,
        _ when name.StartsWith("Text", StringComparison.Ordinal) => SystemColors.WindowTextColor,
        _ when name.EndsWith("Stroke", StringComparison.Ordinal) || name.EndsWith("StrokeStrong", StringComparison.Ordinal)
            || name.EndsWith("StrokeBottom", StringComparison.Ordinal) || name is "Divider" or "RingTrack" or "ToggleOffKnob"
            || name is "ScrollThumb" or "FocusOuter" or "PillPlaceholder" or "Caution" or "Critical" or "Success" => SystemColors.WindowTextColor,
        // Hover and pressed fills stay the window color: text on them is WindowText,
        // and a Highlight fill under WindowText fails contrast in most HC themes.
        _ => SystemColors.WindowColor,
    };

    private sealed record AccentSet(Color Base, Color Dark1, Color Dark2, Color Light2, Color Light3)
    {
        public Color Fill(bool dark) => dark ? Light2 : Dark1;
    }
}

/// <summary>Attached flag telling templates whether a system backdrop is behind the window.</summary>
internal static class BackdropState
{
    public static readonly DependencyProperty IsBackdropOnProperty = DependencyProperty.RegisterAttached(
        "IsBackdropOn", typeof(bool), typeof(BackdropState), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    public static bool GetIsBackdropOn(DependencyObject element) => (bool)element.GetValue(IsBackdropOnProperty);

    public static void SetIsBackdropOn(DependencyObject element, bool value) => element.SetValue(IsBackdropOnProperty, value);
}
