using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Insomnia.App.Animation;

namespace Insomnia.App.Controls;

/// <summary>
/// One Days / Hours / Minutes well. It shows text and owns no editing logic:
/// <see cref="DurationEntry"/> turns keys into <see cref="Insomnia.Core.Logic.DurationInput"/>
/// edits and writes <see cref="Text"/> back. The template reserves the
/// placeholder's width at the typed weight, so a digit never changes the
/// layout. Focus shows as a soft accent glow that breathes in.
/// </summary>
[TemplatePart(Name = GlowPart, Type = typeof(FrameworkElement))]
[TemplatePart(Name = ValuePart, Type = typeof(FrameworkElement))]
public sealed class PillField : Control
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(PillField), new PropertyMetadata(null, OnTextChanged));

    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.Register(
        nameof(Placeholder), typeof(string), typeof(PillField), new PropertyMetadata(""));

    public static readonly DependencyProperty HelpProperty = DependencyProperty.Register(
        nameof(Help), typeof(string), typeof(PillField), new PropertyMetadata(""));

    public static readonly DependencyProperty IsInvalidProperty = DependencyProperty.Register(
        nameof(IsInvalid), typeof(bool), typeof(PillField), new PropertyMetadata(false));

    private const string GlowPart = "PART_Glow";
    private const string ValuePart = "PART_Value";

    private FrameworkElement? _glow;
    private FrameworkElement? _value;
    private readonly ScaleTransform _glowScale = new(1, 1);
    private readonly TranslateTransform _valueShift = new();

    static PillField()
    {
        FocusableProperty.OverrideMetadata(typeof(PillField), new FrameworkPropertyMetadata(true));
        FocusVisualStyleProperty.OverrideMetadata(typeof(PillField), new FrameworkPropertyMetadata(null));
    }

    /// <summary>The digits shown, or null while the placeholder shows.</summary>
    public string? Text
    {
        get => (string?)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string Placeholder
    {
        get => (string)GetValue(PlaceholderProperty);
        set => SetValue(PlaceholderProperty, value);
    }

    /// <summary>Tooltip on the "?" badge, also the field's accessible help text.</summary>
    public string Help
    {
        get => (string)GetValue(HelpProperty);
        set => SetValue(HelpProperty, value);
    }

    public bool IsInvalid
    {
        get => (bool)GetValue(IsInvalidProperty);
        set => SetValue(IsInvalidProperty, value);
    }

    /// <summary>
    /// Applies a whole value typed by an assistive client (UI Automation's
    /// Value pattern). Returns false when the owner rejects it. Null makes
    /// the field read-only to automation.
    /// </summary>
    internal Func<string, bool>? ValueSetter { get; set; }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _glow = GetTemplateChild(GlowPart) as FrameworkElement;
        _value = GetTemplateChild(ValuePart) as FrameworkElement;
        if (_glow is not null)
        {
            _glow.RenderTransformOrigin = new Point(0.5, 0.5);
            _glow.RenderTransform = _glowScale;
            _glow.Opacity = IsKeyboardFocused ? 1 : 0;
        }
        if (_value is not null)
        {
            _value.RenderTransform = _valueShift;
        }
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        if (_glow is null)
        {
            return;
        }
        var reduced = Motion.ReduceMotion;
        if (!reduced)
        {
            // The ring starts a little wide and soft and settles onto the well.
            Motion.Jump(_glowScale, ScaleTransform.ScaleXProperty, 1.06);
            Motion.Jump(_glowScale, ScaleTransform.ScaleYProperty, 1.12);
            Motion.SpringTo(_glowScale, ScaleTransform.ScaleXProperty, 1, Motion.Base);
            Motion.SpringTo(_glowScale, ScaleTransform.ScaleYProperty, 1, Motion.Base);
        }
        Motion.To(_glow, OpacityProperty, 1, Motion.Base, reduced);
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        if (_glow is not null)
        {
            Motion.To(_glow, OpacityProperty, 0, Motion.Base, Motion.ReduceMotion);
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        e.Handled = true;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new PillFieldAutomationPeer(this);

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var pill = (PillField)d;
        if (pill._value is null || e.NewValue is null || Motion.ReduceMotion || !pill.IsLoaded)
        {
            return;
        }
        // A short roll on each digit, like the countdown's.
        var next = (string)e.NewValue;
        var rising = e.OldValue is not string previous || next.Length > previous.Length
            || (next.Length == previous.Length && string.CompareOrdinal(next, previous) >= 0);
        Motion.Jump(pill._valueShift, TranslateTransform.YProperty, rising ? 5 : -5);
        Motion.SpringTo(pill._valueShift, TranslateTransform.YProperty, 0, Motion.Tick);
        pill._value.Opacity = 0.35;
        Motion.SpringTo(pill._value, OpacityProperty, 1, Motion.Tick);
    }

    private sealed class PillFieldAutomationPeer(PillField owner) : FrameworkElementAutomationPeer(owner), IValueProvider
    {
        private PillField Pill => (PillField)Owner;

        public bool IsReadOnly => Pill.ValueSetter is null || !Pill.IsEnabled;

        public string Value => Pill.Text ?? "";

        public void SetValue(string value)
        {
            if (!Pill.IsEnabled)
            {
                throw new ElementNotEnabledException();
            }
            if (Pill.ValueSetter is not { } setter || !setter(value ?? ""))
            {
                throw new ArgumentException($"{Pill.Placeholder}: {Pill.Help}", nameof(value));
            }
        }

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Edit;

        protected override string GetClassNameCore() => nameof(PillField);

        protected override string GetNameCore() => Pill.Placeholder;

        protected override string GetHelpTextCore() => Pill.Help;

        protected override bool IsKeyboardFocusableCore() => true;

        public override object? GetPattern(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.Value ? this : base.GetPattern(patternInterface);
    }
}
