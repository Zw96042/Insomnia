namespace Insomnia.Core.Logic;

/// <summary>A field of the Days / Hours / Minutes entry.</summary>
public enum DurationField
{
    Days = 0,
    Hours = 1,
    Minutes = 2,
}

public static class DurationFields
{
    public static readonly IReadOnlyList<DurationField> All = [DurationField.Days, DurationField.Hours, DurationField.Minutes];

    public static string Placeholder(this DurationField field) => field switch
    {
        DurationField.Days => "Days",
        DurationField.Hours => "Hours",
        _ => "Minutes",
    };

    /// <summary>Tooltip on the field's help badge.</summary>
    public static string Help(this DurationField field) => field switch
    {
        DurationField.Days => $"Up to {DurationInput.MaxDays} days",
        DurationField.Hours => $"0–{DurationInput.MaxHours}",
        _ => $"0–{DurationInput.MaxMinutes}",
    };

    public static int Maximum(this DurationField field) => field switch
    {
        DurationField.Days => DurationInput.MaxDays,
        DurationField.Hours => DurationInput.MaxHours,
        _ => DurationInput.MaxMinutes,
    };

    public static DurationField Next(this DurationField field) => (DurationField)(((int)field + 1) % 3);

    public static DurationField Previous(this DurationField field) => (DurationField)(((int)field + 2) % 3);
}

/// <summary>
/// The three duration fields, as data. Each field is null while it still
/// shows its placeholder. Digits are appended one at a time; a field never
/// holds more than two digits and never exceeds its ceiling, so what is on
/// screen is always a value the session can use.
/// </summary>
public readonly record struct DurationInput(int? Days = null, int? Hours = null, int? Minutes = null)
{
    public const int MaxDays = 30;
    public const int MaxHours = 23;
    public const int MaxMinutes = 59;
    public const int MaxDigits = 2;

    public int? this[DurationField field] => field switch
    {
        DurationField.Days => Days,
        DurationField.Hours => Hours,
        _ => Minutes,
    };

    public DurationInput With(DurationField field, int? value) => field switch
    {
        DurationField.Days => this with { Days = value },
        DurationField.Hours => this with { Hours = value },
        _ => this with { Minutes = value },
    };

    /// <summary>True while every field still shows its placeholder.</summary>
    public bool IsEmpty => Days is null && Hours is null && Minutes is null;

    public bool IsFieldValid(DurationField field) => this[field] is not { } v || (v >= 0 && v <= field.Maximum());

    /// <summary>Every field in range and at least one non-zero.</summary>
    public bool IsValid => Total is not null;

    /// <summary>The duration, or null when a field is out of range or nothing non-zero was entered.</summary>
    public TimeSpan? Total
    {
        get
        {
            if (!DurationFields.All.All(IsFieldValid))
            {
                return null;
            }
            var seconds = (long)(Days ?? 0) * 86_400 + (long)(Hours ?? 0) * 3_600 + (long)(Minutes ?? 0) * 60;
            return seconds > 0 ? TimeSpan.FromSeconds(seconds) : null;
        }
    }

    /// <summary>
    /// Fill the fields from a preset. Seconds are floored to the minute;
    /// fields that would be zero stay empty, and values beyond 30 days are
    /// clamped.
    /// </summary>
    public static DurationInput FromSeconds(double seconds)
    {
        var totalMinutes = (int)Math.Clamp(Math.Floor(seconds / 60), 0, MaxDays * 24 * 60);
        var d = totalMinutes / (24 * 60);
        var h = totalMinutes % (24 * 60) / 60;
        var m = totalMinutes % 60;
        return new DurationInput(d > 0 ? d : null, h > 0 ? h : null, m > 0 ? m : null);
    }

    /// <summary>Text shown in the field, null while it shows its placeholder.</summary>
    public string? Text(DurationField field) => this[field]?.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>True when another digit could still be typed into the field.</summary>
    public bool CanAcceptDigit(DurationField field) =>
        this[field] is not { } v || (v.ToString(System.Globalization.CultureInfo.InvariantCulture).Length < MaxDigits && v * 10 <= field.Maximum());

    /// <summary>
    /// Append a digit to a field. Returns null, meaning nothing changed, when
    /// the field is full or the result would exceed its ceiling.
    /// </summary>
    public DurationInput? Append(int digit, DurationField field)
    {
        if (digit is < 0 or > 9)
        {
            return null;
        }
        var current = this[field];
        if (current == 0 && digit == 0)
        {
            return null;
        }
        if (current is { } c && c.ToString(System.Globalization.CultureInfo.InvariantCulture).Length >= MaxDigits)
        {
            return null;
        }
        var proposed = (current ?? 0) * 10 + digit;
        return proposed <= field.Maximum() ? With(field, proposed) : null;
    }

    /// <summary>
    /// Remove the last digit of a field. Returns null when it was already
    /// empty, so the caller can move focus to the previous field instead.
    /// </summary>
    public DurationInput? Backspace(DurationField field) =>
        this[field] is { } v ? With(field, v < 10 ? null : v / 10) : null;
}

/// <summary>Parses short durations typed into Settings ("30m", "2h", "1h30m", "3d").</summary>
public static class DurationParser
{
    /// <summary>Seconds, or null when the text is not a duration. Bare numbers are minutes.</summary>
    public static double? Seconds(string? text)
    {
        var trimmed = (text ?? "").Trim().ToLowerInvariant();
        if (trimmed.Length == 0)
        {
            return null;
        }
        if (long.TryParse(trimmed, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var bare))
        {
            if (bare <= 0 || bare > long.MaxValue / 60)
            {
                return null;
            }
            return bare * 60;
        }
        long total = 0;
        var number = "";
        var sawUnit = false;
        foreach (var ch in trimmed)
        {
            if (ch is >= '0' and <= '9')
            {
                number += ch;
            }
            else if (ch == ' ')
            {
                continue;
            }
            else
            {
                if (!long.TryParse(number, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n))
                {
                    return null;
                }
                long multiplier = ch switch
                {
                    'd' => 86_400,
                    'h' => 3_600,
                    'm' => 60,
                    _ => 0,
                };
                if (multiplier == 0)
                {
                    return null;
                }
                try
                {
                    total = checked(total + checked(n * multiplier));
                }
                catch (OverflowException)
                {
                    return null;
                }
                number = "";
                sawUnit = true;
            }
        }
        return number.Length == 0 && sawUnit && total > 0 ? total : null;
    }
}
