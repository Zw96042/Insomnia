using System.Windows;
using System.Windows.Media;

namespace Insomnia.App.Controls;

/// <summary>
/// Attached properties the shared templates in Theme/Controls.xaml read, so
/// one button template serves the standard, accent, subtle and chip styles.
/// </summary>
public static class Ui
{
    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.RegisterAttached(
        "CornerRadius", typeof(CornerRadius), typeof(Ui), new FrameworkPropertyMetadata(new CornerRadius(4)));

    public static readonly DependencyProperty HoverBackgroundProperty = DependencyProperty.RegisterAttached(
        "HoverBackground", typeof(Brush), typeof(Ui), new FrameworkPropertyMetadata(null));

    public static readonly DependencyProperty PressedBackgroundProperty = DependencyProperty.RegisterAttached(
        "PressedBackground", typeof(Brush), typeof(Ui), new FrameworkPropertyMetadata(null));

    public static readonly DependencyProperty PressedForegroundProperty = DependencyProperty.RegisterAttached(
        "PressedForeground", typeof(Brush), typeof(Ui), new FrameworkPropertyMetadata(null));

    public static readonly DependencyProperty DisabledBackgroundProperty = DependencyProperty.RegisterAttached(
        "DisabledBackground", typeof(Brush), typeof(Ui), new FrameworkPropertyMetadata(null));

    /// <summary>Segoe Fluent Icons glyph shown by menu items and icon buttons.</summary>
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.RegisterAttached(
        "Glyph", typeof(string), typeof(Ui), new FrameworkPropertyMetadata(null));

    public static readonly DependencyProperty GlyphBrushProperty = DependencyProperty.RegisterAttached(
        "GlyphBrush", typeof(Brush), typeof(Ui), new FrameworkPropertyMetadata(null));

    /// <summary>Hint shown in an empty text box.</summary>
    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.RegisterAttached(
        "Placeholder", typeof(string), typeof(Ui), new FrameworkPropertyMetadata(null));

    public static CornerRadius GetCornerRadius(DependencyObject element) => (CornerRadius)element.GetValue(CornerRadiusProperty);

    public static void SetCornerRadius(DependencyObject element, CornerRadius value) => element.SetValue(CornerRadiusProperty, value);

    public static Brush? GetHoverBackground(DependencyObject element) => (Brush?)element.GetValue(HoverBackgroundProperty);

    public static void SetHoverBackground(DependencyObject element, Brush? value) => element.SetValue(HoverBackgroundProperty, value);

    public static Brush? GetPressedBackground(DependencyObject element) => (Brush?)element.GetValue(PressedBackgroundProperty);

    public static void SetPressedBackground(DependencyObject element, Brush? value) => element.SetValue(PressedBackgroundProperty, value);

    public static Brush? GetPressedForeground(DependencyObject element) => (Brush?)element.GetValue(PressedForegroundProperty);

    public static void SetPressedForeground(DependencyObject element, Brush? value) => element.SetValue(PressedForegroundProperty, value);

    public static Brush? GetDisabledBackground(DependencyObject element) => (Brush?)element.GetValue(DisabledBackgroundProperty);

    public static void SetDisabledBackground(DependencyObject element, Brush? value) => element.SetValue(DisabledBackgroundProperty, value);

    public static string? GetGlyph(DependencyObject element) => (string?)element.GetValue(GlyphProperty);

    public static void SetGlyph(DependencyObject element, string? value) => element.SetValue(GlyphProperty, value);

    public static Brush? GetGlyphBrush(DependencyObject element) => (Brush?)element.GetValue(GlyphBrushProperty);

    public static void SetGlyphBrush(DependencyObject element, Brush? value) => element.SetValue(GlyphBrushProperty, value);

    public static string? GetPlaceholder(DependencyObject element) => (string?)element.GetValue(PlaceholderProperty);

    public static void SetPlaceholder(DependencyObject element, string? value) => element.SetValue(PlaceholderProperty, value);
}

/// <summary>Segoe Fluent Icons / Segoe MDL2 Assets code points used across the UI (present in both fonts).</summary>
public static class Glyphs
{
    public const string Add = "\uE710";
    public const string Cancel = "\uE711";
    public const string More = "\uE712";
    public const string Settings = "\uE713";
    public const string Stop = "\uE71A";
    public const string Microphone = "\uE720";
    public const string Refresh = "\uE72C";
    public const string Lock = "\uE72E";
    public const string Minus = "\uE738";
    public const string Mute = "\uE74F";
    public const string Terminal = "\uE756";
    public const string Pause = "\uE769";
    public const string Wifi = "\uE701";
    public const string Brightness = "\uE706";
    public const string Package = "\uE7B8";
    public const string Warning = "\uE7BA";
    public const string Power = "\uE7E8";
    public const string Display = "\uE7F4";
    public const string Laptop = "\uE7F8";
    public const string Clock = "\uE823";
    public const string Folder = "\uE838";
    public const string Network = "\uE839";
    public const string BatteryCharging = "\uE83E";
    public const string Battery = "\uE83F";
    public const string Link = "\uE8A7";
    public const string Code = "\uE943";
    public const string Info = "\uE946";
    public const string Shield = "\uEA18";
    public const string Error = "\uEA39";
    public const string App = "\uECAA";
}
