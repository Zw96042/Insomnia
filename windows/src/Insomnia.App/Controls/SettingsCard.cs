using System.Windows;
using System.Windows.Controls;

namespace Insomnia.App.Controls;

/// <summary>
/// One row of Settings, Windows 11 style: an optional icon, a title with a
/// description under it, and the control on the right. Children placed in
/// <see cref="Footer"/> span the card's width under the row (notes, inline
/// errors, nested lists).
/// </summary>
[TemplatePart(Name = FooterPart, Type = typeof(FrameworkElement))]
public sealed class SettingsCard : HeaderedContentControl
{
    private const string FooterPart = "PART_Footer";

    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description), typeof(string), typeof(SettingsCard), new PropertyMetadata(null));

    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(string), typeof(SettingsCard), new PropertyMetadata(null));

    public static readonly DependencyProperty FooterProperty = DependencyProperty.Register(
        nameof(Footer), typeof(object), typeof(SettingsCard), new PropertyMetadata(null));

    static SettingsCard()
    {
        FocusableProperty.OverrideMetadata(typeof(SettingsCard), new FrameworkPropertyMetadata(false));
        IsTabStopProperty.OverrideMetadata(typeof(SettingsCard), new FrameworkPropertyMetadata(false));
    }

    public string? Description
    {
        get => (string?)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    /// <summary>A Segoe Fluent Icons glyph.</summary>
    public string? Icon
    {
        get => (string?)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public object? Footer
    {
        get => GetValue(FooterProperty);
        set => SetValue(FooterProperty, value);
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        // A footer that hides itself (a note with nothing to say) takes its spacing with it.
        if (GetTemplateChild(FooterPart) is FrameworkElement host && Footer is UIElement element)
        {
            host.SetBinding(VisibilityProperty, new System.Windows.Data.Binding(nameof(Visibility)) { Source = element });
        }
    }
}
