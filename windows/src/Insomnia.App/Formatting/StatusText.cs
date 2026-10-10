using System.Globalization;
using Insomnia.App.Controls;
using Insomnia.Core.Engine;
using Insomnia.Core.Logic;
using Insomnia.Core.Platform;

namespace Insomnia.App.Formatting;

/// <summary>One read-only line of the tray menu's status section.</summary>
internal sealed record StatusLine(string Glyph, string Text, bool Attention = false);

/// <summary>
/// User-facing text for times and status, in one place so the tray tooltip,
/// the flyout and the menu read the same. Plain, specific, sentence case.
/// </summary>
internal static class StatusText
{
    /// <summary>NotifyIcon.Text holds at most 127 characters.</summary>
    public const int TooltipLimit = 127;

    /// <summary>
    /// A deadline in local time: "6:40 PM" today, "tomorrow 6:40 PM",
    /// "Tue 6:40 PM" within the week, then "Oct 14, 6:40 PM".
    /// </summary>
    public static string EndTime(DateTimeOffset endsAt, DateTimeOffset now)
    {
        var culture = CultureInfo.CurrentCulture;
        var local = endsAt.ToLocalTime();
        var today = now.ToLocalTime().Date;
        var time = local.ToString("t", culture);
        var days = (local.Date - today).Days;
        return days switch
        {
            <= 0 => time,
            1 => $"tomorrow {time}",
            < 7 => $"{local.ToString("ddd", culture)} {time}",
            _ => $"{local.ToString("MMM d", culture)}, {time}",
        };
    }

    /// <summary>"Ends at 6:40 PM", "Ends tomorrow at 6:40 PM", "Ends Tue at 6:40 PM", "Ends Oct 14 at 6:40 PM".</summary>
    public static string EndsPhrase(DateTimeOffset endsAt, DateTimeOffset now) => "Ends " + AtPhrase(endsAt, now);

    /// <summary>"until 6:40 PM" style phrase body: "at 6:40 PM", "tomorrow at 6:40 PM"...</summary>
    public static string AtPhrase(DateTimeOffset endsAt, DateTimeOffset now)
    {
        var culture = CultureInfo.CurrentCulture;
        var local = endsAt.ToLocalTime();
        var time = local.ToString("t", culture);
        var days = (local.Date - now.ToLocalTime().Date).Days;
        return days switch
        {
            <= 0 => $"at {time}",
            1 => $"tomorrow at {time}",
            < 7 => $"{local.ToString("dddd", culture)} at {time}",
            _ => $"{local.ToString("MMM d", culture)} at {time}",
        };
    }

    public static string Tooltip(SessionStatus status, DateTimeOffset now)
    {
        var text = status.Phase switch
        {
            SessionPhase.Starting => "Insomnia — starting",
            SessionPhase.Ending => "Insomnia — ending the session",
            SessionPhase.Active when status.Session is { } session =>
                $"Insomnia — {SessionMath.FormatRemaining(session.Remaining(now))} left (ends {EndTime(session.EndsAt, now)})",
            _ => "Insomnia — not active",
        };
        var alert = status.Warnings.FirstOrDefault(w => w.Severity == WarningSeverity.Error);
        if (alert is not null)
        {
            text += "\n" + alert.Text;
        }
        return Truncate(text, TooltipLimit);
    }

    public static string Truncate(string text, int limit) =>
        text.Length <= limit ? text : string.Concat(text.AsSpan(0, limit - 1), "…");

    /// <summary>The status section of the tray menu, from a details read.</summary>
    public static IReadOnlyList<StatusLine> DetailLines(StatusDetails details)
    {
        var lines = new List<StatusLine>
        {
            details.LidClosed switch
            {
                true => new(Glyphs.Laptop, "Lid closed"),
                false => new(Glyphs.Laptop, "Lid open"),
                null => new(Glyphs.Laptop, "No lid reported"),
            },
            Battery(details.Battery),
            Wifi(details.Wifi, details.WifiSsid),
        };
        if (details.FrozenApps.Count > 0)
        {
            lines.Add(new(Glyphs.Pause, "Frozen: " + JoinNames(details.FrozenApps, 4)));
        }
        if (details.DockerFrozen)
        {
            lines.Add(new(Glyphs.Package, "Docker paused"));
        }
        if (details.PowerSavingOn)
        {
            lines.Add(new(Glyphs.Battery, "Power saving on"));
        }
        if (!string.IsNullOrWhiteSpace(details.LastNetworkGap))
        {
            lines.Add(new(Glyphs.Network, "Last network gap: " + details.LastNetworkGap));
        }
        lines.Add(details.RecoveryAgent switch
        {
            RecoveryAgentState.Armed => new(Glyphs.Shield, "Recovery agent armed"),
            RecoveryAgentState.Missing => new(Glyphs.Shield, "Recovery agent not installed", Attention: true),
            RecoveryAgentState.Disabled => new(Glyphs.Shield, "Recovery agent turned off in Task Scheduler", Attention: true),
            RecoveryAgentState.Mismatched => new(Glyphs.Shield, "Recovery agent points at another copy of Insomnia", Attention: true),
            _ => new(Glyphs.Shield, "Recovery agent state unknown", Attention: true),
        });
        return lines;
    }

    /// <summary>"Slack, Discord" or "Slack, Discord, Teams, Zoom and 2 more".</summary>
    public static string JoinNames(IReadOnlyList<string> names, int max)
    {
        if (names.Count <= max)
        {
            return string.Join(", ", names);
        }
        return $"{string.Join(", ", names.Take(max))} and {names.Count - max} more";
    }

    private static StatusLine Battery(BatteryReading battery)
    {
        if (battery.HasBattery == false)
        {
            return new(Glyphs.Power, "No battery");
        }
        if (battery.Percent is not { } percent)
        {
            return new(Glyphs.Battery, battery.HasBattery == true ? "Battery level unreadable" : "Battery unknown", Attention: battery.HasBattery == true);
        }
        var watts = battery.ChargeRateMilliwatts is { } mw && mw != 0
            ? string.Create(CultureInfo.CurrentCulture, $" {Math.Abs(mw) / 1000.0:0.0} W")
            : "";
        var state = battery.OnAc switch
        {
            true when battery.ChargeRateMilliwatts > 0 => "charging" + watts,
            true => "plugged in",
            false => "discharging" + watts,
            null => "",
        };
        var text = state.Length > 0 ? $"Battery {percent}% · {state}" : $"Battery {percent}%";
        return new(battery.OnAc == true ? Glyphs.BatteryCharging : Glyphs.Battery, text);
    }

    private static StatusLine Wifi(WifiConnectivity connectivity, string? ssid) => connectivity switch
    {
        WifiConnectivity.NoAdapter => new(Glyphs.Wifi, "No Wi-Fi adapter"),
        WifiConnectivity.Satisfied when !string.IsNullOrEmpty(ssid) => new(Glyphs.Wifi, "Wi-Fi: " + ssid),
        WifiConnectivity.Satisfied => new(Glyphs.Wifi, "Wi-Fi connected"),
        _ => new(Glyphs.Wifi, "Wi-Fi disconnected", Attention: true),
    };
}
