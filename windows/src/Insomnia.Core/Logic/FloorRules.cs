using Insomnia.Core.Model;

namespace Insomnia.Core.Logic;

/// <summary>Thermal pressure, from the platform's thermal sensor.</summary>
public enum ThermalState
{
    /// <summary>No throttling.</summary>
    Nominal,
    /// <summary>Brief or light throttling.</summary>
    Fair,
    /// <summary>Sustained throttling: power saving is switched on.</summary>
    Serious,
    /// <summary>Sustained heavy throttling: the session ends.</summary>
    Critical,
}

/// <summary>Battery level as the floor rules see it.</summary>
public abstract record BatteryStatus
{
    private BatteryStatus() { }

    /// <summary>No internal battery (a desktop): no floor applies.</summary>
    public sealed record NoBattery : BatteryStatus;

    /// <summary>A readable level, 0 to 100.</summary>
    public sealed record Level(int Value) : BatteryStatus;

    /// <summary>A battery that is present but whose level could not be read, with the consecutive miss count.</summary>
    public sealed record Unreadable(int Misses) : BatteryStatus;

    public int? Percent => this is Level l ? l.Value : null;
}

/// <summary>Why power saving is wanted. The strongest cause is named when several hold.</summary>
public enum PowerSavingCause
{
    Battery,
    Thermal,
    Lid,
}

/// <summary>What the floor rules ask for.</summary>
public abstract record FloorAction
{
    private FloorAction() { }

    public sealed record EnablePowerSaving(PowerSavingCause Cause) : FloorAction;

    public sealed record DisablePowerSaving : FloorAction;

    public sealed record EndSession(EndReason Reason) : FloorAction;
}

/// <summary>
/// The battery, thermal and lid decision table as a pure function. One
/// evaluation owns power saving: it is switched on while any cause holds and
/// off when none does, so the causes never fight over it.
/// </summary>
public static class FloorRules
{
    /// <summary>
    /// Actions for the current inputs, in order:
    /// battery below the end floor on battery ends the session (an end floor
    /// of 0 never matches); critical thermal ends it (with thermal rules on);
    /// a battery unreadable on two consecutive reads, on battery, with an end
    /// floor set, ends it; otherwise power saving follows <see cref="Cause"/>.
    /// </summary>
    public static IReadOnlyList<FloorAction> Evaluate(
        BatteryStatus battery,
        bool onAc,
        ThermalState thermal,
        bool lidClosed,
        bool powerSavingSetByUs,
        Config config)
    {
        var onBattery = !onAc;
        if (battery.Percent is { } p && onBattery && p < config.EndFloor)
        {
            return [new FloorAction.EndSession(EndReason.BatteryFloor)];
        }
        if (config.ThermalRules && thermal == ThermalState.Critical)
        {
            return [new FloorAction.EndSession(EndReason.ThermalCritical)];
        }
        if (battery is BatteryStatus.Unreadable { Misses: >= 2 } && onBattery && config.EndFloor > 0)
        {
            return [new FloorAction.EndSession(EndReason.BatteryUnreadable)];
        }
        var cause = Cause(battery.Percent, onAc, thermal, lidClosed, config);
        if (cause is { } c && !powerSavingSetByUs)
        {
            return [new FloorAction.EnablePowerSaving(c)];
        }
        if (cause is null && powerSavingSetByUs)
        {
            return [new FloorAction.DisablePowerSaving()];
        }
        return [];
    }

    /// <summary>
    /// The strongest cause that wants power saving now (thermal, then
    /// battery, then lid), or null. An unreadable level never counts as
    /// below a floor.
    /// </summary>
    public static PowerSavingCause? Cause(int? percent, bool onAc, ThermalState thermal, bool lidClosed, Config config)
    {
        if (config.ThermalRules && thermal == ThermalState.Serious)
        {
            return PowerSavingCause.Thermal;
        }
        if (percent is { } p && !onAc && p < config.LowPowerFloor)
        {
            return PowerSavingCause.Battery;
        }
        if (lidClosed && config.PowerSavingOnLidClose)
        {
            return PowerSavingCause.Lid;
        }
        return null;
    }
}
