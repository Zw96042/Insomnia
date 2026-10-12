using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Insomnia.App.Animation;

namespace Insomnia.App.Controls;

/// <summary>
/// Press-and-hold end control: a ring fills around the ✕ while it is held
/// (mouse, or Space/Enter with the keyboard) and only a full ring ends the
/// session. Releasing early, or dragging away, springs the ring back with no
/// action, so a stray click cannot end a session. UI Automation's Invoke
/// ends it directly, since an assistive client cannot hold.
/// </summary>
public sealed class HoldToEndButton : FrameworkElement
{
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(HoldToEndButton), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(HoldToEndButton), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RingBrushProperty = DependencyProperty.Register(
        nameof(RingBrush), typeof(Brush), typeof(HoldToEndButton), new FrameworkPropertyMetadata(Brushes.SteelBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GlyphBrushProperty = DependencyProperty.Register(
        nameof(GlyphBrush), typeof(Brush), typeof(HoldToEndButton), new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty HoverBrushProperty = DependencyProperty.Register(
        nameof(HoverBrush), typeof(Brush), typeof(HoldToEndButton), new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));

    private const double ControlSize = 40;
    private const double RingRadius = 16;
    private const double RingThickness = 2.5;
    private const double GlyphHalf = 4.5;

    /// <summary>Moving the pointer this far cancels the hold.</summary>
    private const double CancelDistance = 12;

    private readonly ScaleTransform _press = new(1, 1);
    private bool _holding;
    private bool _keyHolding;
    private int _token;
    private Point _downAt;

    public HoldToEndButton()
    {
        Focusable = true;
        Cursor = Cursors.Hand;
        RenderTransformOrigin = new Point(0.5, 0.5);
        RenderTransform = _press;
        ToolTip = "Hold to end the session";
        SetResourceReference(TrackBrushProperty, "Brush.RingTrack");
        SetResourceReference(RingBrushProperty, "Brush.Accent");
        SetResourceReference(GlyphBrushProperty, "Brush.TextPrimary");
        SetResourceReference(HoverBrushProperty, "Brush.SubtleHover");
        SetResourceReference(FocusVisualStyleProperty, "FocusVisual.Circle");
        IsEnabledChanged += (_, _) =>
        {
            if (!IsEnabled)
            {
                Cancel(animate: false);
            }
            InvalidateVisual();
        };
    }

    /// <summary>The ring closed: end the session.</summary>
    public event EventHandler? Held;

    public double Progress
    {
        get => (double)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    public Brush TrackBrush
    {
        get => (Brush)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public Brush RingBrush
    {
        get => (Brush)GetValue(RingBrushProperty);
        set => SetValue(RingBrushProperty, value);
    }

    public Brush GlyphBrush
    {
        get => (Brush)GetValue(GlyphBrushProperty);
        set => SetValue(GlyphBrushProperty, value);
    }

    public Brush HoverBrush
    {
        get => (Brush)GetValue(HoverBrushProperty);
        set => SetValue(HoverBrushProperty, value);
    }

    /// <summary>Spring the ring back with no action (the view is leaving, or the session ended elsewhere).</summary>
    public void Reset() => Cancel(animate: false);

    protected override Size MeasureOverride(Size availableSize) => new(ControlSize, ControlSize);

    protected override void OnRender(DrawingContext drawingContext)
    {
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        drawingContext.PushOpacity(IsEnabled ? 1 : 0.4);
        if (IsMouseOver && IsEnabled)
        {
            drawingContext.DrawEllipse(HoverBrush, null, center, RingRadius + RingThickness, RingRadius + RingThickness);
        }
        var track = new Pen(TrackBrush, RingThickness);
        drawingContext.DrawEllipse(null, track, center, RingRadius, RingRadius);

        var progress = Math.Clamp(Progress, 0, 1);
        if (progress > 0.001)
        {
            var ring = new Pen(RingBrush, RingThickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            if (progress >= 0.999)
            {
                drawingContext.DrawEllipse(null, ring, center, RingRadius, RingRadius);
            }
            else
            {
                // Clockwise from twelve o'clock.
                var angle = progress * 2 * Math.PI;
                var start = new Point(center.X, center.Y - RingRadius);
                var end = new Point(center.X + RingRadius * Math.Sin(angle), center.Y - RingRadius * Math.Cos(angle));
                var figure = new PathFigure { StartPoint = start, IsClosed = false, IsFilled = false };
                figure.Segments.Add(new ArcSegment(end, new Size(RingRadius, RingRadius), 0, progress > 0.5, SweepDirection.Clockwise, isStroked: true));
                var geometry = new PathGeometry([figure]);
                geometry.Freeze();
                drawingContext.DrawGeometry(null, ring, geometry);
            }
        }

        var glyph = new Pen(GlyphBrush, 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        drawingContext.PushOpacity(0.85);
        drawingContext.DrawLine(glyph, new Point(center.X - GlyphHalf, center.Y - GlyphHalf), new Point(center.X + GlyphHalf, center.Y + GlyphHalf));
        drawingContext.DrawLine(glyph, new Point(center.X + GlyphHalf, center.Y - GlyphHalf), new Point(center.X - GlyphHalf, center.Y + GlyphHalf));
        drawingContext.Pop();
        drawingContext.Pop();
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!IsEnabled)
        {
            return;
        }
        e.Handled = true;
        _downAt = e.GetPosition(this);
        CaptureMouse();
        Begin();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_holding && !_keyHolding && IsMouseCaptured && (e.GetPosition(this) - _downAt).Length > CancelDistance)
        {
            Cancel(animate: true);
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }
        if (_holding && !_keyHolding)
        {
            Cancel(animate: true);
        }
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_holding && !_keyHolding)
        {
            Cancel(animate: true);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key is not (Key.Space or Key.Enter))
        {
            return;
        }
        e.Handled = true;
        if (!e.IsRepeat && !_holding && IsEnabled)
        {
            _keyHolding = true;
            Begin();
        }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.Key is (Key.Space or Key.Enter) && _keyHolding)
        {
            e.Handled = true;
            Cancel(animate: true);
        }
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        if (_keyHolding)
        {
            Cancel(animate: true);
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new HoldToEndAutomationPeer(this);

    private void Begin()
    {
        _holding = true;
        var token = ++_token;
        var reduced = Motion.ReduceMotion;
        if (!reduced)
        {
            Motion.SpringTo(_press, ScaleTransform.ScaleXProperty, 1.08, Motion.Snappy);
            Motion.SpringTo(_press, ScaleTransform.ScaleYProperty, 1.08, Motion.Snappy);
        }
        // Linear fill: the ring is a timer, so it moves at a constant rate.
        var remaining = TimeSpan.FromTicks((long)(Motion.HoldDuration.Ticks * (1 - Math.Clamp(Progress, 0, 1))));
        var fill = new DoubleAnimation(1, new Duration(remaining));
        fill.Completed += (_, _) =>
        {
            if (token == _token && _holding)
            {
                Fire();
            }
        };
        BeginAnimation(ProgressProperty, fill, HandoffBehavior.SnapshotAndReplace);
    }

    private void Cancel(bool animate)
    {
        _holding = false;
        _keyHolding = false;
        _token++;
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }
        var reduced = Motion.ReduceMotion;
        if (!animate)
        {
            Motion.Jump(this, ProgressProperty, 0);
            Motion.Jump(_press, ScaleTransform.ScaleXProperty, 1);
            Motion.Jump(_press, ScaleTransform.ScaleYProperty, 1);
            return;
        }
        Motion.To(this, ProgressProperty, 0, Motion.Snappy, reduced);
        Motion.To(_press, ScaleTransform.ScaleXProperty, 1, Motion.Snappy, reduced);
        Motion.To(_press, ScaleTransform.ScaleYProperty, 1, Motion.Snappy, reduced);
    }

    private void Fire()
    {
        _holding = false;
        _keyHolding = false;
        var token = ++_token;
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }
        Motion.To(_press, ScaleTransform.ScaleXProperty, 1, Motion.Snappy, Motion.ReduceMotion);
        Motion.To(_press, ScaleTransform.ScaleYProperty, 1, Motion.Snappy, Motion.ReduceMotion);
        Held?.Invoke(this, EventArgs.Empty);
        // The control normally leaves with the session; if it stays (the end
        // failed or nothing was active), the ring settles back for a clean retry.
        var timer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher) { Interval = TimeSpan.FromMilliseconds(700) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (token == _token)
            {
                Motion.To(this, ProgressProperty, 0, Motion.Snappy, Motion.ReduceMotion);
            }
        };
        timer.Start();
    }

    private sealed class HoldToEndAutomationPeer(HoldToEndButton owner) : FrameworkElementAutomationPeer(owner), IInvokeProvider
    {
        public void Invoke()
        {
            var button = (HoldToEndButton)Owner;
            if (!button.IsEnabled)
            {
                throw new ElementNotEnabledException();
            }
            button.Held?.Invoke(button, EventArgs.Empty);
        }

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Button;

        protected override string GetClassNameCore() => nameof(HoldToEndButton);

        protected override string GetNameCore() => "End session";

        protected override string GetHelpTextCore() => "Press and hold to end the session";

        protected override bool IsKeyboardFocusableCore() => true;

        public override object? GetPattern(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.Invoke ? this : base.GetPattern(patternInterface);
    }
}
