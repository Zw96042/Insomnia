using System.Windows;
using System.Windows.Media.Animation;

namespace Insomnia.App.Animation;

/// <summary>
/// A damped spring as a WPF easing function, parameterised like SwiftUI's
/// <c>.spring(response:dampingFraction:)</c>: <see cref="Response"/> is the
/// period of the undamped oscillation in seconds, and
/// <see cref="DampingFraction"/> is the damping ratio (1 is critical; below 1
/// overshoots). An easing function only sees normalised time, so the
/// animation using it must run for <see cref="SettleTime(double, double)"/>;
/// <see cref="Motion"/> builds animations that way. The spring starts at rest.
/// </summary>
public sealed class SpringEase : EasingFunctionBase
{
    public static readonly DependencyProperty ResponseProperty = DependencyProperty.Register(
        nameof(Response), typeof(double), typeof(SpringEase), new PropertyMetadata(0.35));

    public static readonly DependencyProperty DampingFractionProperty = DependencyProperty.Register(
        nameof(DampingFraction), typeof(double), typeof(SpringEase), new PropertyMetadata(0.72));

    /// <summary>Settled means within this fraction of the distance travelled.</summary>
    private const double Tolerance = 0.001;

    public SpringEase()
    {
        // EaseInCore is the whole curve; the base class's default EaseOut would mirror it.
        EasingMode = EasingMode.EaseIn;
    }

    public SpringEase(double response, double dampingFraction)
        : this()
    {
        Response = response;
        DampingFraction = dampingFraction;
    }

    public double Response
    {
        get => (double)GetValue(ResponseProperty);
        set => SetValue(ResponseProperty, value);
    }

    public double DampingFraction
    {
        get => (double)GetValue(DampingFractionProperty);
        set => SetValue(DampingFractionProperty, value);
    }

    /// <summary>Seconds until the spring stays within 0.1% of its target.</summary>
    public static TimeSpan SettleTime(double response, double dampingFraction)
    {
        var omega = 2 * Math.PI / Math.Max(response, 0.01);
        var zeta = Math.Clamp(dampingFraction, 0.05, 1);
        double seconds;
        if (zeta < 1)
        {
            // The oscillation's envelope is e^(-ζωt) / sqrt(1 - ζ²).
            seconds = Math.Log(1 / (Tolerance * Math.Sqrt(1 - zeta * zeta))) / (zeta * omega);
        }
        else
        {
            // Critically damped: (1 + ωt)e^(-ωt), solved by bisection.
            double lo = 0, hi = 20 / omega;
            for (var i = 0; i < 50; i++)
            {
                var mid = (lo + hi) / 2;
                if ((1 + omega * mid) * Math.Exp(-omega * mid) > Tolerance)
                {
                    lo = mid;
                }
                else
                {
                    hi = mid;
                }
            }
            seconds = hi;
        }
        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>Displacement towards the target (0 at rest, 1 at target) after <paramref name="seconds"/>.</summary>
    public static double Evaluate(double response, double dampingFraction, double seconds)
    {
        if (seconds <= 0)
        {
            return 0;
        }
        var omega = 2 * Math.PI / Math.Max(response, 0.01);
        var zeta = Math.Clamp(dampingFraction, 0.05, 1);
        if (zeta < 1)
        {
            var damped = omega * Math.Sqrt(1 - zeta * zeta);
            var decay = Math.Exp(-zeta * omega * seconds);
            return 1 - decay * (Math.Cos(damped * seconds) + zeta * omega / damped * Math.Sin(damped * seconds));
        }
        return 1 - (1 + omega * seconds) * Math.Exp(-omega * seconds);
    }

    protected override double EaseInCore(double normalizedTime)
    {
        if (normalizedTime >= 1)
        {
            return 1;
        }
        var settle = SettleTime(Response, DampingFraction).TotalSeconds;
        return Evaluate(Response, DampingFraction, normalizedTime * settle);
    }

    protected override Freezable CreateInstanceCore() => new SpringEase();
}
