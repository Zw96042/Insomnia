using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Insomnia.App.Animation;
using Insomnia.Core.Logic;

namespace Insomnia.App.Controls;

/// <summary>
/// The three duration pills and their keyboard model. Digits go through
/// <see cref="DurationInput.Append"/>, Backspace through
/// <see cref="DurationInput.Backspace"/>; a full field moves focus on, an
/// empty Backspace moves it back, and a digit the field cannot take shakes
/// the pill. Enter raises <see cref="Submitted"/>; Escape is left to the
/// window. Tab, Shift+Tab and the arrow keys move between pills.
/// </summary>
/// <remarks>
/// Motion: <see cref="Appear"/> staggers the pills in (scale 0.85 to 1 from
/// the leading edge, with a fade, 40 ms apart); <see cref="Fold"/> slides
/// each one left by the slots before it while it shrinks to 0.6 and fades,
/// farthest first, sliding under its neighbour. Only transforms and opacity
/// move; the slots stay in the layout.
/// </remarks>
public sealed class DurationEntry : Grid
{
    private const double Gap = 8;

    private readonly PillField[] _pills = new PillField[3];
    private readonly ScaleTransform[] _appearScale = new ScaleTransform[3];
    private readonly ScaleTransform[] _bounceScale = new ScaleTransform[3];
    private readonly TranslateTransform[] _foldShift = new TranslateTransform[3];
    private readonly TranslateTransform[] _shake = new TranslateTransform[3];
    private DurationInput _input;
    private int _motionToken;
    private bool _shown = true;

    public DurationEntry()
    {
        Focusable = false;
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Continue);
        foreach (var field in DurationFields.All)
        {
            var i = (int)field;
            ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _appearScale[i] = new ScaleTransform(1, 1);
            _bounceScale[i] = new ScaleTransform(1, 1);
            _foldShift[i] = new TranslateTransform();
            _shake[i] = new TranslateTransform();
            var transforms = new TransformGroup();
            transforms.Children.Add(_appearScale[i]);
            transforms.Children.Add(_bounceScale[i]);
            transforms.Children.Add(_foldShift[i]);
            transforms.Children.Add(_shake[i]);
            var pill = new PillField
            {
                Placeholder = field.Placeholder(),
                Help = field.Help(),
                Margin = new Thickness(i == 0 ? 0 : Gap, 0, 0, 0),
                RenderTransformOrigin = new Point(0, 0.5),
                RenderTransform = transforms,
                TabIndex = i,
            };
            var bounce = _bounceScale[i];
            pill.SizeChanged += (_, e) =>
            {
                // The bounce scales about the pill's centre; the other transforms use its leading edge.
                bounce.CenterX = e.NewSize.Width / 2;
            };
            pill.GotKeyboardFocus += OnPillFocused;
            var current = field;
            pill.ValueSetter = text => SetFromAutomation(current, text);
            SetColumn(pill, i);
            // Farther pills slide under nearer ones when the row folds.
            Panel.SetZIndex(pill, 3 - i);
            _pills[i] = pill;
            Children.Add(pill);
        }
        PreviewTextInput += OnPreviewTextInput;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>Enter was pressed in a pill.</summary>
    public event EventHandler? Submitted;

    /// <summary>The input changed through typing or <see cref="SetInput"/>.</summary>
    public event EventHandler? InputChanged;

    public DurationInput Input => _input;

    /// <summary>Smaller pills (the extend row).</summary>
    public bool IsCompact
    {
        set
        {
            foreach (var pill in _pills)
            {
                pill.Height = value ? 34 : 40;
                pill.FontSize = value ? 13 : 14;
            }
        }
    }

    /// <summary>Field holding keyboard focus, or null.</summary>
    public DurationField? FocusedField
    {
        get
        {
            for (var i = 0; i < 3; i++)
            {
                if (_pills[i].IsKeyboardFocused)
                {
                    return (DurationField)i;
                }
            }
            return null;
        }
    }

    public void SetInput(DurationInput input)
    {
        _input = input;
        foreach (var field in DurationFields.All)
        {
            var pill = _pills[(int)field];
            pill.Text = input.Text(field);
            pill.IsInvalid = !input.IsFieldValid(field);
        }
        InputChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Clear() => SetInput(default);

    public void FocusField(DurationField field) => Keyboard.Focus(_pills[(int)field]);

    /// <summary>Shake the focused pill: the key did nothing.</summary>
    public void Reject() => Reject(FocusedField ?? DurationField.Hours);

    /// <summary>Show the pills: staggered spring in, or a plain fade with animation effects off.</summary>
    public void Appear(bool animate)
    {
        // Cancels the completion of a fold still in flight.
        _motionToken++;
        _shown = true;
        Visibility = Visibility.Visible;
        var reduced = Motion.ReduceMotion;
        for (var i = 0; i < 3; i++)
        {
            var pill = _pills[i];
            Motion.Jump(_foldShift[i], TranslateTransform.XProperty, 0);
            Motion.Jump(_shake[i], TranslateTransform.XProperty, 0);
            if (!animate)
            {
                SetScale(_appearScale[i], 1);
                Motion.Jump(pill, OpacityProperty, 1);
                continue;
            }
            if (reduced)
            {
                SetScale(_appearScale[i], 1);
                Motion.Jump(pill, OpacityProperty, 0);
                Motion.EaseTo(pill, OpacityProperty, 1, Motion.ReducedDuration);
                continue;
            }
            var delay = Motion.StaggerDelay(i, 3, reversed: false, reduced: false);
            SetScale(_appearScale[i], Motion.AppearScale);
            Motion.Jump(pill, OpacityProperty, 0);
            Motion.SpringTo(_appearScale[i], ScaleTransform.ScaleXProperty, 1, Motion.Base, delay);
            Motion.SpringTo(_appearScale[i], ScaleTransform.ScaleYProperty, 1, Motion.Base, delay);
            Motion.SpringTo(pill, OpacityProperty, 1, Motion.Base, delay);
        }
    }

    /// <summary>
    /// Fold the pills toward the leading edge and hide them. <paramref name="completed"/>
    /// runs once the last pill is gone (immediately when not animated), unless
    /// <see cref="Appear"/> ran in the meantime.
    /// </summary>
    public void Fold(bool animate, Action? completed = null)
    {
        var token = ++_motionToken;
        _shown = false;
        if (IsKeyboardFocusWithin)
        {
            // Keep focus out of pills that are leaving.
            Keyboard.ClearFocus();
        }
        var reduced = Motion.ReduceMotion;
        if (!animate)
        {
            for (var i = 0; i < 3; i++)
            {
                Motion.Jump(_pills[i], OpacityProperty, 0);
            }
            Visibility = Visibility.Hidden;
            completed?.Invoke();
            return;
        }

        TimeSpan total;
        if (reduced)
        {
            for (var i = 0; i < 3; i++)
            {
                Motion.EaseTo(_pills[i], OpacityProperty, 0, Motion.ReducedCollapseDuration);
            }
            total = Motion.ReducedCollapseDuration;
        }
        else
        {
            var offset = 0.0;
            for (var i = 0; i < 3; i++)
            {
                var delay = Motion.StaggerDelay(i, 3, reversed: true, reduced: false);
                Motion.CollapseTo(_foldShift[i], TranslateTransform.XProperty, -offset, Motion.CollapseDuration, delay);
                Motion.CollapseTo(_appearScale[i], ScaleTransform.ScaleXProperty, Motion.CollapseScale, Motion.CollapseDuration, delay);
                Motion.CollapseTo(_appearScale[i], ScaleTransform.ScaleYProperty, Motion.CollapseScale, Motion.CollapseDuration, delay);
                Motion.CollapseTo(_pills[i], OpacityProperty, 0, Motion.CollapseDuration, delay);
                offset += _pills[i].ActualWidth + Gap;
            }
            total = Motion.CollapseDuration + Motion.StaggerDelay(0, 3, reversed: true, reduced: false);
        }

        // A frame of margin so the pill drawn at zero opacity has rendered before it is hidden.
        var timer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher) { Interval = total + TimeSpan.FromMilliseconds(40) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (token != _motionToken)
            {
                return;
            }
            Visibility = Visibility.Hidden;
            completed?.Invoke();
        };
        timer.Start();
    }

    private void Reject(DurationField field)
    {
        var i = (int)field;
        if (Motion.ReduceMotion)
        {
            // No movement: flag the pill for a moment instead.
            var pill = _pills[i];
            pill.IsInvalid = true;
            var timer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher) { Interval = TimeSpan.FromMilliseconds(300) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                pill.IsInvalid = !_input.IsFieldValid(field);
            };
            timer.Start();
            return;
        }
        var shake = new DoubleAnimationUsingKeyFrames();
        shake.KeyFrames.Add(new EasingDoubleKeyFrame(-3, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(45))));
        shake.KeyFrames.Add(new EasingDoubleKeyFrame(3, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(110))));
        shake.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(110) + Motion.Snappy.Duration), Motion.Ease(Motion.Snappy)));
        _shake[i].BeginAnimation(TranslateTransform.XProperty, shake, HandoffBehavior.SnapshotAndReplace);
    }

    /// <summary>A whole value from UI Automation: empty clears the field; otherwise it must be in range.</summary>
    private bool SetFromAutomation(DurationField field, string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            SetInput(_input.With(field, null));
            return true;
        }
        if (!int.TryParse(trimmed, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value)
            || value > field.Maximum())
        {
            return false;
        }
        SetInput(_input.With(field, value == 0 ? null : value));
        return true;
    }

    private void OnPillFocused(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!_shown || e.OldFocus is not PillField)
        {
            return;
        }
        var i = Array.IndexOf(_pills, sender);
        if (i >= 0)
        {
            Motion.Bounce(_bounceScale[i]);
        }
    }

    private void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (FocusedField is not { } field || e.Text.Length != 1 || !char.IsAsciiDigit(e.Text[0]))
        {
            return;
        }
        e.Handled = true;
        if (_input.Append(e.Text[0] - '0', field) is not { } next)
        {
            Reject(field);
            return;
        }
        SetInput(next);
        if (!next.CanAcceptDigit(field) && field != DurationField.Minutes)
        {
            FocusField(field.Next());
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (FocusedField is not { } field || Keyboard.Modifiers is not (ModifierKeys.None or ModifierKeys.Shift))
        {
            return;
        }
        switch (e.Key)
        {
            case Key.Back:
                e.Handled = true;
                if (_input.Backspace(field) is { } shorter)
                {
                    SetInput(shorter);
                }
                else if (field != DurationField.Days)
                {
                    FocusField(field.Previous());
                }
                break;
            case Key.Delete:
                e.Handled = true;
                if (_input[field] is not null)
                {
                    SetInput(_input.With(field, null));
                }
                break;
            case Key.Left:
                e.Handled = true;
                if (field != DurationField.Days)
                {
                    FocusField(field.Previous());
                }
                break;
            case Key.Right:
                e.Handled = true;
                if (field != DurationField.Minutes)
                {
                    FocusField(field.Next());
                }
                break;
            case Key.Enter:
                e.Handled = true;
                Submitted?.Invoke(this, EventArgs.Empty);
                break;
            case Key.Space:
                e.Handled = true;
                break;
        }
    }

    private static void SetScale(ScaleTransform scale, double value)
    {
        Motion.Jump(scale, ScaleTransform.ScaleXProperty, value);
        Motion.Jump(scale, ScaleTransform.ScaleYProperty, value);
    }
}
