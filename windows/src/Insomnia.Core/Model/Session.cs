using Insomnia.Core.Logic;

namespace Insomnia.Core.Model;

/// <summary>
/// An active timed session, persisted as <c>session.json</c>. <see cref="EndsAt"/>
/// is the only thing that keeps the PC awake; everything else is bookkeeping.
/// Times are whole seconds in UTC, as the store writes them.
/// </summary>
public sealed record Session(DateTimeOffset StartedAt, DateTimeOffset EndsAt, IReadOnlyList<double> Extensions)
{
    public Session(DateTimeOffset startedAt, DateTimeOffset endsAt) : this(startedAt, endsAt, []) { }

    /// <summary>Seconds left until <see cref="EndsAt"/>, never negative.</summary>
    public TimeSpan Remaining(DateTimeOffset now) => SessionMath.Remaining(EndsAt, now);

    /// <summary>True once <paramref name="now"/> reaches <see cref="EndsAt"/>.</summary>
    public bool IsExpired(DateTimeOffset now) => SessionMath.IsExpired(EndsAt, now);

    /// <summary>
    /// Shape of the countdown, derived from the session's span so it survives
    /// a relaunch without another persisted field.
    /// </summary>
    public CountdownShape CountdownShape => CountdownShapes.ForInitialDuration(EndsAt - StartedAt);

    public bool Equals(Session? other) =>
        other is not null && StartedAt == other.StartedAt && EndsAt == other.EndsAt && Extensions.SequenceEqual(other.Extensions);

    public override int GetHashCode() => HashCode.Combine(StartedAt, EndsAt, Extensions.Count);
}

/// <summary>Why a session ended.</summary>
public enum EndReason
{
    /// <summary>The deadline passed.</summary>
    Timer,
    /// <summary>Hold to end, or End from the menu.</summary>
    User,
    /// <summary>Quit Insomnia.</summary>
    Quit,
    /// <summary>Battery below the end floor.</summary>
    BatteryFloor,
    /// <summary>Battery present but unreadable on two consecutive reads while on battery.</summary>
    BatteryUnreadable,
    /// <summary>Sustained heavy thermal throttling.</summary>
    ThermalCritical,
    /// <summary>The recovery agent found the session expired or unreadable.</summary>
    Recovery,
    /// <summary>A session on disk could not be resumed safely at launch.</summary>
    RecoveryUnavailable,
    /// <summary>Applying the lid or keep-awake setting failed during start.</summary>
    StartFailed,
}

public static class EndReasonText
{
    /// <summary>Sentence used in the end notification.</summary>
    public static string Describe(EndReason reason) => reason switch
    {
        EndReason.Timer => "The timer ran out.",
        EndReason.User => "You ended the session.",
        EndReason.Quit => "Insomnia quit.",
        EndReason.BatteryFloor => "The battery fell below the end floor.",
        EndReason.BatteryUnreadable => "The battery level could not be read twice in a row, so the end floor could not be applied.",
        EndReason.ThermalCritical => "The PC stayed heavily throttled by heat.",
        EndReason.Recovery => "The recovery agent ended an expired session.",
        EndReason.RecoveryUnavailable => "The session could not be resumed safely after Insomnia restarted.",
        EndReason.StartFailed => "The session could not start.",
        _ => "The session ended.",
    };
}
