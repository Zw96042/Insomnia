using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Documents;
using System.Windows.Media;
using Insomnia.App.Animation;

namespace Insomnia.App.Controls;

/// <summary>
/// The eye mark as a vector element. Closed while idle, open while a session
/// holds sleep off. <see cref="SetOpen"/> blinks between the two: the lid
/// lifts on a slow spring while the lashes hand over, and the pupil arrives a
/// beat later on a bouncier spring; closing drops the lid and eases the pupil
/// away. With animation effects off it crossfades instead. Drawn in the
/// inherited foreground so it follows the theme and never takes a tint.
/// </summary>
public sealed class EyeMark : FrameworkElement
{
    public static readonly DependencyProperty OpennessProperty = DependencyProperty.Register(
        nameof(Openness), typeof(double), typeof(EyeMark), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PupilScaleProperty = DependencyProperty.Register(
        nameof(PupilScale), typeof(double), typeof(EyeMark), new FrameworkPropertyMetadata(Motion.PupilClosedScale, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PupilOpacityProperty = DependencyProperty.Register(
        nameof(PupilOpacity), typeof(double), typeof(EyeMark), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CrossfadeProperty = DependencyProperty.Register(
        nameof(Crossfade), typeof(bool), typeof(EyeMark), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(EyeMark), new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.Inherits));

    private bool? _open;

    public double Openness
    {
        get => (double)GetValue(OpennessProperty);
        set => SetValue(OpennessProperty, value);
    }

    public double PupilScale
    {
        get => (double)GetValue(PupilScaleProperty);
        set => SetValue(PupilScaleProperty, value);
    }

    public double PupilOpacity
    {
        get => (double)GetValue(PupilOpacityProperty);
        set => SetValue(PupilOpacityProperty, value);
    }

    public bool Crossfade
    {
        get => (bool)GetValue(CrossfadeProperty);
        set => SetValue(CrossfadeProperty, value);
    }

    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public bool IsOpen => _open == true;

    /// <summary>Open or close the eye, blinking when <paramref name="animate"/> is true.</summary>
    public void SetOpen(bool open, bool animate)
    {
        if (_open == open)
        {
            return;
        }
        _open = open;
        if (!animate)
        {
            Motion.Jump(this, OpennessProperty, open ? 1 : 0);
            Motion.Jump(this, PupilScaleProperty, open ? 1 : Motion.PupilClosedScale);
            Motion.Jump(this, PupilOpacityProperty, open ? 1 : 0);
            return;
        }

        var reduced = Motion.ReduceMotion;
        Crossfade = reduced;
        if (reduced)
        {
            Motion.Jump(this, PupilScaleProperty, 1);
            Motion.EaseTo(this, OpennessProperty, open ? 1 : 0, Motion.ReducedBlinkDuration);
            Motion.EaseTo(this, PupilOpacityProperty, open ? 1 : 0, Motion.ReducedBlinkDuration);
        }
        else if (open)
        {
            Motion.SpringTo(this, OpennessProperty, 1, Motion.BlinkOpen);
            Motion.SpringTo(this, PupilScaleProperty, 1, Motion.PupilOpen, Motion.PupilOpenDelay);
            Motion.SpringTo(this, PupilOpacityProperty, 1, Motion.PupilOpen, Motion.PupilOpenDelay);
        }
        else
        {
            Motion.SpringTo(this, OpennessProperty, 0, Motion.BlinkClose);
            Motion.EaseOutTo(this, PupilScaleProperty, Motion.PupilClosedScale, Motion.PupilCloseDuration);
            Motion.EaseOutTo(this, PupilOpacityProperty, 0, Motion.PupilCloseDuration);
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var w = double.IsInfinity(availableSize.Width) ? EyeGeometry.DesignSize : availableSize.Width;
        var h = double.IsInfinity(availableSize.Height) ? EyeGeometry.DesignSize : availableSize.Height;
        var side = Math.Min(w, h);
        return new Size(side, side);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var side = Math.Min(ActualWidth, ActualHeight);
        if (side <= 0)
        {
            return;
        }
        var scale = side / EyeGeometry.DesignSize;
        drawingContext.PushTransform(new TranslateTransform((ActualWidth - side) / 2, (ActualHeight - side) / 2));
        drawingContext.PushTransform(new ScaleTransform(scale, scale));
        EyeGeometry.Draw(drawingContext, new EyeFrame(Openness, PupilScale, PupilOpacity, Crossfade), Foreground);
        drawingContext.Pop();
        drawingContext.Pop();
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new EyeMarkAutomationPeer(this);

    private sealed class EyeMarkAutomationPeer(EyeMark owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;

        protected override string GetNameCore() => ((EyeMark)Owner).IsOpen ? "Insomnia, session active" : "Insomnia, not active";

        protected override bool IsControlElementCore() => false;
    }
}
