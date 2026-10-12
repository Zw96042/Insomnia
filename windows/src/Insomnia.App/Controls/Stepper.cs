using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Insomnia.App.Controls;

/// <summary>
/// A compact − value + control for whole numbers in fixed steps (battery
/// floors, the nudge threshold). One tab stop; Up/Down and the buttons step
/// it. <see cref="Value"/> binds two-way, so a view model can couple values
/// (the battery floors push each other along).
/// </summary>
[TemplatePart(Name = DownPart, Type = typeof(ButtonBase))]
[TemplatePart(Name = UpPart, Type = typeof(ButtonBase))]
public sealed class Stepper : Control
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(int), typeof(Stepper),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnValueChanged));

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(int), typeof(Stepper), new PropertyMetadata(0, OnRangeChanged));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(int), typeof(Stepper), new PropertyMetadata(100, OnRangeChanged));

    public static readonly DependencyProperty StepProperty = DependencyProperty.Register(
        nameof(Step), typeof(int), typeof(Stepper), new PropertyMetadata(1));

    public static readonly DependencyProperty SuffixProperty = DependencyProperty.Register(
        nameof(Suffix), typeof(string), typeof(Stepper), new PropertyMetadata("", OnValueChanged));

    /// <summary>Text shown for <see cref="Minimum"/> instead of the number ("Off").</summary>
    public static readonly DependencyProperty MinimumLabelProperty = DependencyProperty.Register(
        nameof(MinimumLabel), typeof(string), typeof(Stepper), new PropertyMetadata(null, OnValueChanged));

    private static readonly DependencyPropertyKey DisplayTextPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(DisplayText), typeof(string), typeof(Stepper), new PropertyMetadata(""));

    public static readonly DependencyProperty DisplayTextProperty = DisplayTextPropertyKey.DependencyProperty;

    private const string DownPart = "PART_Down";
    private const string UpPart = "PART_Up";

    private ButtonBase? _down;
    private ButtonBase? _up;

    static Stepper()
    {
        FocusableProperty.OverrideMetadata(typeof(Stepper), new FrameworkPropertyMetadata(true));
    }

    public Stepper()
    {
        UpdateDisplay();
    }

    public int Value
    {
        get => (int)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public int Minimum
    {
        get => (int)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public int Maximum
    {
        get => (int)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public int Step
    {
        get => (int)GetValue(StepProperty);
        set => SetValue(StepProperty, value);
    }

    public string Suffix
    {
        get => (string)GetValue(SuffixProperty);
        set => SetValue(SuffixProperty, value);
    }

    public string? MinimumLabel
    {
        get => (string?)GetValue(MinimumLabelProperty);
        set => SetValue(MinimumLabelProperty, value);
    }

    public string DisplayText => (string)GetValue(DisplayTextProperty);

    public override void OnApplyTemplate()
    {
        if (_down is not null)
        {
            _down.Click -= OnDown;
        }
        if (_up is not null)
        {
            _up.Click -= OnUp;
        }
        base.OnApplyTemplate();
        _down = GetTemplateChild(DownPart) as ButtonBase;
        _up = GetTemplateChild(UpPart) as ButtonBase;
        if (_down is not null)
        {
            _down.Click += OnDown;
        }
        if (_up is not null)
        {
            _up.Click += OnUp;
        }
        UpdateButtons();
    }

    public void StepBy(int steps)
    {
        var next = Math.Clamp(Value + steps * Step, Minimum, Maximum);
        if (next != Value)
        {
            // SetCurrentValue keeps the binding; the two-way binding pushes it to the source.
            SetCurrentValue(ValueProperty, next);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.Key)
        {
            case Key.Up or Key.Right or Key.Add or Key.OemPlus:
                StepBy(1);
                e.Handled = true;
                break;
            case Key.Down or Key.Left or Key.Subtract or Key.OemMinus:
                StepBy(-1);
                e.Handled = true;
                break;
            case Key.Home:
                StepBy(-(Maximum - Minimum));
                e.Handled = true;
                break;
            case Key.End:
                StepBy(Maximum - Minimum);
                e.Handled = true;
                break;
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (IsKeyboardFocusWithin)
        {
            StepBy(e.Delta > 0 ? 1 : -1);
            e.Handled = true;
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new StepperAutomationPeer(this);

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var stepper = (Stepper)d;
        stepper.UpdateDisplay();
        stepper.UpdateButtons();
    }

    private static void OnRangeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((Stepper)d).UpdateButtons();

    private void OnDown(object sender, RoutedEventArgs e)
    {
        StepBy(-1);
        Focus();
    }

    private void OnUp(object sender, RoutedEventArgs e)
    {
        StepBy(1);
        Focus();
    }

    private void UpdateDisplay()
    {
        var text = Value == Minimum && MinimumLabel is { } label
            ? label
            : Value.ToString(CultureInfo.CurrentCulture) + Suffix;
        SetValue(DisplayTextPropertyKey, text);
    }

    private void UpdateButtons()
    {
        if (_down is not null)
        {
            _down.IsEnabled = Value > Minimum;
        }
        if (_up is not null)
        {
            _up.IsEnabled = Value < Maximum;
        }
    }

    private sealed class StepperAutomationPeer(Stepper owner) : FrameworkElementAutomationPeer(owner), IRangeValueProvider
    {
        private Stepper Stepper => (Stepper)Owner;

        public double Value => Stepper.Value;

        public bool IsReadOnly => !Stepper.IsEnabled;

        public double Maximum => Stepper.Maximum;

        public double Minimum => Stepper.Minimum;

        public double LargeChange => Stepper.Step;

        public double SmallChange => Stepper.Step;

        public void SetValue(double value)
        {
            var steps = (int)Math.Round((value - Stepper.Value) / Math.Max(Stepper.Step, 1));
            Stepper.StepBy(steps);
        }

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Spinner;

        protected override string GetClassNameCore() => nameof(Stepper);

        public override object? GetPattern(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.RangeValue ? this : base.GetPattern(patternInterface);
    }
}
