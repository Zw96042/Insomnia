using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Insomnia.App.Animation;

namespace Insomnia.App.Controls;

/// <summary>
/// A Windows 11 toggle switch: "On"/"Off" text beside a 40×20 track whose
/// knob springs across and grows a little under the pointer. The look lives
/// in Theme/Controls.xaml; the knob's motion is here so it follows the
/// reduced-motion setting.
/// </summary>
[TemplatePart(Name = KnobPart, Type = typeof(FrameworkElement))]
public sealed class ToggleSwitch : ToggleButton
{
    private const string KnobPart = "PART_Knob";
    private const double Travel = 10;

    private readonly TranslateTransform _knobShift = new();
    private readonly ScaleTransform _knobScale = new(1, 1);
    private FrameworkElement? _knob;

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _knob = GetTemplateChild(KnobPart) as FrameworkElement;
        if (_knob is null)
        {
            return;
        }
        var group = new TransformGroup();
        group.Children.Add(_knobScale);
        group.Children.Add(_knobShift);
        _knob.RenderTransformOrigin = new Point(0.5, 0.5);
        _knob.RenderTransform = group;
        Motion.Jump(_knobShift, TranslateTransform.XProperty, IsChecked == true ? Travel : -Travel);
    }

    protected override void OnChecked(RoutedEventArgs e)
    {
        base.OnChecked(e);
        MoveKnob(Travel);
    }

    protected override void OnUnchecked(RoutedEventArgs e)
    {
        base.OnUnchecked(e);
        MoveKnob(-Travel);
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        ScaleKnob(14.0 / 12);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        ScaleKnob(1);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new ToggleButtonAutomationPeer(this);

    private void MoveKnob(double x)
    {
        if (!IsLoaded || Motion.ReduceMotion)
        {
            Motion.Jump(_knobShift, TranslateTransform.XProperty, x);
            return;
        }
        Motion.SpringTo(_knobShift, TranslateTransform.XProperty, x, Motion.Snappy);
    }

    private void ScaleKnob(double scale)
    {
        if (Motion.ReduceMotion)
        {
            Motion.Jump(_knobScale, ScaleTransform.ScaleXProperty, scale);
            Motion.Jump(_knobScale, ScaleTransform.ScaleYProperty, scale);
            return;
        }
        Motion.SpringTo(_knobScale, ScaleTransform.ScaleXProperty, scale, Motion.Base);
        Motion.SpringTo(_knobScale, ScaleTransform.ScaleYProperty, scale, Motion.Base);
    }
}
