using System.Globalization;
using Insomnia.Core.Model;

namespace Insomnia.Core.Logic;

/// <summary>Pure session arithmetic. No clocks, no side effects.</summary>
public static class SessionMath
{
    /// <summary>Shortest session accepted, in seconds.</summary>
    public const double MinimumDuration = 60;

    /// <summary>Clamp a requested duration into [<see cref="MinimumDuration"/>, max].</summary>
    public static TimeSpan Clamp(TimeSpan duration, double maxDurationSeconds)
    {
        var upper = Math.Max(maxDurationSeconds, MinimumDuration);
        return TimeSpan.FromSeconds(Math.Min(Math.Max(duration.TotalSeconds, MinimumDuration), upper));
    }

    /// <summary>Truncate to whole seconds, the precision the store writes.</summary>
    public static DateTimeOffset WholeSeconds(DateTimeOffset t) =>
        new(t.UtcTicks - (t.UtcTicks % TimeSpan.TicksPerSecond), TimeSpan.Zero);

    /// <summary>Fresh session starting at <paramref name="now"/>.</summary>
    public static Session NewSession(DateTimeOffset now, TimeSpan duration, double maxDurationSeconds)
    {
        var start = WholeSeconds(now);
        return new Session(start, start + WholeSecondsSpan(Clamp(duration, maxDurationSeconds)));
    }

    /// <summary>
    /// Push the deadline out by <paramref name="extra"/>, never past
    /// <c>now + max</c>. Extending an already-expired session restarts the
    /// countdown from now.
    /// </summary>
    public static Session Extended(Session session, TimeSpan extra, DateTimeOffset now, double maxDurationSeconds)
    {
        var nowWhole = WholeSeconds(now);
        var baseline = session.EndsAt > nowWhole ? session.EndsAt : nowWhole;
        var ceiling = nowWhole + TimeSpan.FromSeconds(Math.Max(maxDurationSeconds, MinimumDuration));
        var proposed = baseline + WholeSecondsSpan(extra < TimeSpan.Zero ? TimeSpan.Zero : extra);
        var newEnd = proposed < ceiling ? proposed : ceiling;
        var extensions = session.Extensions.Append((newEnd - session.EndsAt).TotalSeconds).ToList();
        return session with { EndsAt = newEnd, Extensions = extensions };
    }

    /// <summary>Time from now to the deadline, floored at zero.</summary>
    public static TimeSpan Remaining(DateTimeOffset endsAt, DateTimeOffset now) =>
        endsAt > now ? endsAt - now : TimeSpan.Zero;

    public static bool IsExpired(DateTimeOffset endsAt, DateTimeOffset now) => endsAt <= now;

    /// <summary>
    /// Minute-granularity remaining time: "3d 2h", "2h 14m", "14m", "&lt;1m".
    /// Zero secondary units are dropped. Seconds are floored.
    /// </summary>
    public static string FormatRemaining(TimeSpan interval)
    {
        var totalMinutes = (long)(Math.Max(interval.TotalSeconds, 0) / 60);
        if (totalMinutes < 1)
        {
            return "<1m";
        }
        var days = totalMinutes / (24 * 60);
        var hours = totalMinutes % (24 * 60) / 60;
        var minutes = totalMinutes % 60;
        if (days > 0)
        {
            return hours > 0 ? $"{days}d {hours}h" : $"{days}d";
        }
        if (hours > 0)
        {
            return minutes > 0 ? $"{hours}h {minutes}m" : $"{hours}h";
        }
        return $"{minutes}m";
    }

    /// <summary>
    /// Live countdown in a fixed shape. Partial seconds round up, so a fresh
    /// 30-minute session opens on 30:00 and only reads all zeros at the
    /// deadline. Minutes and seconds are two digits; hours and days are not
    /// padded.
    /// </summary>
    public static string FormatCountdown(TimeSpan remaining, CountdownShape shape)
    {
        var total = (long)Math.Ceiling(Math.Max(remaining.TotalSeconds, 0));
        var seconds = total % 60;
        var minutes = total / 60 % 60;
        string Clock(long hours) => string.Create(CultureInfo.InvariantCulture, $"{hours}:{minutes:00}:{seconds:00}");
        switch (shape)
        {
            case CountdownShape.Days:
                var days = total / 86400;
                return days > 0 ? $"{days}d {Clock(total % 86400 / 3600)}" : Clock(total / 3600);
            case CountdownShape.Hours:
                return Clock(total / 3600);
            default:
                return string.Create(CultureInfo.InvariantCulture, $"{total / 60:00}:{seconds:00}");
        }
    }

    /// <summary>Short duration label for presets: "30m", "1h 30m", "3d".</summary>
    public static string FormatPreset(double seconds) => FormatRemaining(TimeSpan.FromSeconds(seconds));

    private static TimeSpan WholeSecondsSpan(TimeSpan t) => TimeSpan.FromSeconds(Math.Floor(t.TotalSeconds));
}

/// <summary>
/// Shape of the live countdown, chosen from the session's span and held for
/// the whole session so the text keeps a stable width.
/// </summary>
public enum CountdownShape
{
    /// <summary><c>Nd H:MM:SS</c> while a day or more remains, then <c>H:MM:SS</c>.</summary>
    Days,
    /// <summary><c>H:MM:SS</c> for the whole session.</summary>
    Hours,
    /// <summary><c>MM:SS</c> for the whole session.</summary>
    Minutes,
}

public static class CountdownShapes
{
    public static CountdownShape ForInitialDuration(TimeSpan duration) =>
        duration.TotalSeconds >= 86400 ? CountdownShape.Days
        : duration.TotalSeconds >= 3600 ? CountdownShape.Hours
        : CountdownShape.Minutes;
}
