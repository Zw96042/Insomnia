using Insomnia.Core.Logging;
using static Insomnia.Windows.Power.PowerNative;

namespace Insomnia.Windows.Power;

/// <summary>
/// The parts of <c>SYSTEM_POWER_CAPABILITIES</c> Insomnia acts on, read once
/// per process.
/// </summary>
internal static class PowerCapabilities
{
    private static readonly Lazy<(bool HasLid, bool ModernStandby)> Value = new(Read);

    public static bool HasLid => Value.Value.HasLid;

    /// <summary>
    /// True on an S0 low power idle (Modern Standby) machine, where any
    /// display-off that is not an idle timeout starts standby.
    /// </summary>
    public static bool ModernStandby => Value.Value.ModernStandby;

    private static unsafe (bool HasLid, bool ModernStandby) Read()
    {
        var capabilities = stackalloc byte[SystemPowerCapabilitiesSize];
        var status = CallNtPowerInformation(SystemPowerCapabilities, null, 0, capabilities, SystemPowerCapabilitiesSize);
        if (status != 0)
        {
            // Assume a lid and Modern Standby: applying a lid policy on a
            // desktop and leaving displays on are harmless, while missing the
            // lid policy or turning displays off on Modern Standby sleeps the PC.
            Log.Warn($"Power capabilities: reading them failed (NTSTATUS 0x{status:X8}); assuming a lid and Modern Standby");
            return (true, true);
        }
        var hasLid = capabilities[LidPresentOffset] != 0;
        var modernStandby = capabilities[AoAcOffset] != 0;
        Log.Info($"Power capabilities: lid {(hasLid ? "present" : "absent")}, Modern Standby {(modernStandby ? "yes" : "no")}");
        return (hasLid, modernStandby);
    }
}
