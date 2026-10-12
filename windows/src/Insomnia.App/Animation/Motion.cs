using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Insomnia.Core.Logging;

namespace Insomnia.App.Animation;

/// <summary>A spring's two parameters and the animation length they imply.</summary>
public readonly record struct Spring(double Response, double DampingFraction)
{
    public TimeSpan Duration => SpringEase.SettleTime(Response, DampingFraction);

    public double At(double seconds) => SpringEase.Evaluate(Response, DampingFraction, seconds);
}

/// <summary>
/// Every animation in the UI comes from here (upstream spec section 11):
/// springs for state changes, a fixed ease for the fold that closes the
/// pills, and short crossfades with no movement when Windows animation
/// effects are off. Only transforms and opacity are animated, never layout.
/// </summary>
internal static class Motion
{
    /// <summary>Baseline for layout states, pill morphs and the focus glow.</summary>
    public static readonly Spring Base = new(0.35, 0.72);

    /// <summary>Focus bounce and chip taps: quicker, slight overshoot.</summary>
    public static readonly Spring Snappy = new(0.25, 0.6);

    /// <summary>One digit roll of the 1 Hz countdown; settles well inside a second.</summary>
    public static readonly Spring Tick = new(0.18, 0.85);

    /// <summary>The lid lifting: slow enough to be seen.</summary>
    public static readonly Spring BlinkOpen = new(0.95, 0.9);

    /// <summary>The lid dropping: a little quicker, nearer critical damping.</summary>
    public static readonly Spring BlinkClose = new(0.8, 0.95);

    /// <summary>The pupil arriving behind the lid, with a small bounce.</summary>
    public static readonly Spring PupilOpen = new(0.5, 0.6);

    public static readonly TimeSpan Stagger = TimeSpan.FromMilliseconds(40);
    public static readonly TimeSpan PupilOpenDelay = TimeSpan.FromMilliseconds(200);
    public static readonly TimeSpan PupilCloseDuration = TimeSpan.FromMilliseconds(400);

    /// <summary>The fold that closes the pills: fixed length so the end is known.</summary>
    public static readonly TimeSpan CollapseDuration = TimeSpan.FromMilliseconds(320);

    /// <summary>Reduced-motion replacement for every spring.</summary>
    public static readonly TimeSpan ReducedDuration = TimeSpan.FromMilliseconds(150);
    public static readonly TimeSpan ReducedBlinkDuration = TimeSpan.FromMilliseconds(300);
    public static readonly TimeSpan ReducedCollapseDuration = TimeSpan.FromMilliseconds(200);

    /// <summary>How long the end control must be held.</summary>
    public static readonly TimeSpan HoldDuration = TimeSpan.FromMilliseconds(1200);

    public const double CollapseScale = 0.6;
    public const double AppearScale = 0.85;
    public const double CountdownScale = 0.7;
    public const double Overshoot = 1.06;
    public const double PupilClosedScale = 0.6;

    private static readonly IEasingFunction ReducedEase = Frozen(new CubicEase { EasingMode = EasingMode.EaseInOut });
    private static readonly IEasingFunction CollapseEase = Frozen(new CubicEase { EasingMode = EasingMode.EaseInOut });
    private static readonly IEasingFunction OutEase = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });
    private static readonly Dictionary<Spring, IEasingFunction> Eases = [];
    private static global::Windows.UI.ViewManagement.UISettings? _uiSettings;
    private static bool _uiSettingsFailed;

    /// <summary>
    /// True when Windows animation effects are off (Settings > Accessibility >
    /// Visual effects), read live so a change applies to the next animation.
    /// </summary>
    public static bool ReduceMotion
    {
        get
        {
            if (!SystemParameters.ClientAreaAnimation)
            {
                return true;
            }
            if (_uiSettingsFailed)
            {
                return false;
            }
            try
            {
                _uiSettings ??= new global::Windows.UI.ViewManagement.UISettings();
                return !_uiSettings.AnimationsEnabled;
            }
            catch (Exception ex)
            {
                _uiSettingsFailed = true;
                Log.Warn($"UISettings.AnimationsEnabled unavailable, using SPI only: {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>Stagger delay for item <paramref name="index"/> of <paramref name="count"/>; zero under reduced motion.</summary>
    public static TimeSpan StaggerDelay(int index, int count, bool reversed, bool reduced)
    {
        if (reduced || count <= 0)
        {
            return TimeSpan.Zero;
        }
        var position = reversed ? count - 1 - index : index;
        return Stagger * Math.Max(position, 0);
    }

    public static IEasingFunction Ease(Spring spring)
    {
        if (!Eases.TryGetValue(spring, out var ease))
        {
            ease = Frozen(new SpringEase(spring.Response, spring.DampingFraction));
            Eases[spring] = ease;
        }
        return ease;
    }

    /// <summary>Spring <paramref name="property"/> of <paramref name="target"/> to <paramref name="to"/> from wherever it is now.</summary>
    public static void SpringTo(IAnimatable target, DependencyProperty property, double to, Spring spring, TimeSpan delay = default, Action? completed = null)
    {
        var animation = new DoubleAnimation(to, new Duration(spring.Duration))
        {
            EasingFunction = Ease(spring),
            BeginTime = delay,
        };
        Run(target, property, animation, completed);
    }

    /// <summary>Ease (cubic in-out) to a value over a fixed time.</summary>
    public static void EaseTo(IAnimatable target, DependencyProperty property, double to, TimeSpan duration, TimeSpan delay = default, Action? completed = null) =>
        Run(target, property, new DoubleAnimation(to, new Duration(duration)) { EasingFunction = ReducedEase, BeginTime = delay }, completed);

    /// <summary>The collapse curve (cubic in-out over <see cref="CollapseDuration"/> unless given).</summary>
    public static void CollapseTo(IAnimatable target, DependencyProperty property, double to, TimeSpan duration, TimeSpan delay = default, Action? completed = null) =>
        Run(target, property, new DoubleAnimation(to, new Duration(duration)) { EasingFunction = CollapseEase, BeginTime = delay }, completed);

    /// <summary>Ease-out, no bounce (the pupil leaving).</summary>
    public static void EaseOutTo(IAnimatable target, DependencyProperty property, double to, TimeSpan duration, TimeSpan delay = default) =>
        Run(target, property, new DoubleAnimation(to, new Duration(duration)) { EasingFunction = OutEase, BeginTime = delay }, null);

    /// <summary>A spring, or the reduced-motion crossfade timing for the same change.</summary>
    public static void To(IAnimatable target, DependencyProperty property, double to, Spring spring, bool reduced, TimeSpan delay = default, Action? completed = null)
    {
        if (reduced)
        {
            EaseTo(target, property, to, ReducedDuration, TimeSpan.Zero, completed);
        }
        else
        {
            SpringTo(target, property, to, spring, delay, completed);
        }
    }

    /// <summary>Stop any animation on the property and set it outright.</summary>
    public static void Jump(DependencyObject target, DependencyProperty property, double value)
    {
        if (target is IAnimatable animatable)
        {
            animatable.BeginAnimation(property, null);
        }
        target.SetValue(property, value);
    }

    /// <summary>Scale 1 → <see cref="Overshoot"/> → 1 on a snappy spring (chip taps, focus).</summary>
    public static void Bounce(ScaleTransform scale)
    {
        if (ReduceMotion)
        {
            return;
        }
        foreach (var property in new[] { ScaleTransform.ScaleXProperty, ScaleTransform.ScaleYProperty })
        {
            var animation = new DoubleAnimationUsingKeyFrames();
            animation.KeyFrames.Add(new EasingDoubleKeyFrame(Overshoot, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(90)), OutEase));
            animation.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(90) + Snappy.Duration), Ease(Snappy)));
            scale.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
        }
    }

    private static void Run(IAnimatable target, DependencyProperty property, AnimationTimeline animation, Action? completed)
    {
        if (completed is not null)
        {
            animation.Completed += (_, _) => completed();
        }
        target.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private static T Frozen<T>(T freezable)
        where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    #region Press bounce

    /// <summary>Attached: give a button the snappy press bounce on click.</summary>
    public static readonly DependencyProperty BounceOnClickProperty = DependencyProperty.RegisterAttached(
        "BounceOnClick", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnBounceOnClickChanged));

    public static bool GetBounceOnClick(DependencyObject element) => (bool)element.GetValue(BounceOnClickProperty);

    public static void SetBounceOnClick(DependencyObject element, bool value) => element.SetValue(BounceOnClickProperty, value);

    private static void OnBounceOnClickChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ButtonBase button)
        {
            return;
        }
        if ((bool)e.NewValue)
        {
            button.RenderTransformOrigin = new Point(0.5, 0.5);
            if (button.RenderTransform is not ScaleTransform)
            {
                button.RenderTransform = new ScaleTransform(1, 1);
            }
            button.Click += OnBounceClick;
        }
        else
        {
            button.Click -= OnBounceClick;
        }
    }

    private static void OnBounceClick(object sender, RoutedEventArgs e)
    {
        if (sender is UIElement { RenderTransform: ScaleTransform scale } && !scale.IsFrozen)
        {
            Bounce(scale);
        }
    }

    #endregion
}
