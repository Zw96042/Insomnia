using Insomnia.Core.Logging;
using Insomnia.Core.Platform;
using static Insomnia.Windows.Power.PowerNative;

namespace Insomnia.Windows.Power;

/// <summary>
/// The lid-close action (<c>SUB_BUTTONS\LIDACTION</c>) of a power scheme,
/// through the per-user power APIs, which need no administrator rights.
/// </summary>
public sealed class LidPolicy : ILidPolicy
{
    /// <summary>GUID_SYSTEM_BUTTON_SUBGROUP.</summary>
    private static readonly Guid ButtonSubgroup = new("4f971e89-eebd-4455-a8de-9e59040e7347");

    /// <summary>GUID_LIDCLOSE_ACTION.</summary>
    private static readonly Guid LidCloseAction = new("5ca83367-6e45-459f-a27b-476b1d01c936");

    private readonly Lazy<bool> _hasLid = new(ReadHasLid);

    public bool HasLid => _hasLid.Value;

    public Guid? ActiveScheme()
    {
        var error = PowerGetActiveScheme(0, out var pointer);
        if (error != ERROR_SUCCESS || pointer == 0)
        {
            Log.Warn($"Lid policy: PowerGetActiveScheme failed (error {error})");
            return null;
        }
        try
        {
            return System.Runtime.InteropServices.Marshal.PtrToStructure<Guid>(pointer);
        }
        finally
        {
            LocalFree(pointer);
        }
    }

    public LidRead Read(Guid scheme)
    {
        var error = PowerReadACValueIndex(0, in scheme, in ButtonSubgroup, in LidCloseAction, out var ac);
        if (error != ERROR_SUCCESS)
        {
            return Failed("AC", scheme, error);
        }
        error = PowerReadDCValueIndex(0, in scheme, in ButtonSubgroup, in LidCloseAction, out var dc);
        if (error != ERROR_SUCCESS)
        {
            return Failed("DC", scheme, error);
        }
        return new LidRead(LidReadStatus.Ok, ac, dc);
    }

    /// <summary>
    /// The power APIs answer ERROR_FILE_NOT_FOUND for a scheme that does not
    /// exist; anything else proves nothing about the scheme.
    /// </summary>
    private static LidRead Failed(string source, Guid scheme, uint error)
    {
        if (error == ERROR_FILE_NOT_FOUND)
        {
            Log.Warn($"Lid policy: scheme {scheme} does not exist");
            return LidRead.Missing;
        }
        Log.Warn($"Lid policy: reading the {source} lid action of {scheme} failed (error {error})");
        return LidRead.Failure;
    }

    /// <remarks>
    /// Writes to a scheme are stored but only take effect when the scheme is
    /// (re)applied, so the active scheme is re-applied after a write to it.
    /// A write to an inactive scheme is not applied: activating it would
    /// switch the user's plan.
    /// </remarks>
    public bool Write(Guid scheme, uint ac, uint dc)
    {
        var error = PowerWriteACValueIndex(0, in scheme, in ButtonSubgroup, in LidCloseAction, ac);
        if (error != ERROR_SUCCESS)
        {
            Log.Error($"Lid policy: writing AC lid action {ac} to {scheme} failed (error {error})");
            return false;
        }
        error = PowerWriteDCValueIndex(0, in scheme, in ButtonSubgroup, in LidCloseAction, dc);
        if (error != ERROR_SUCCESS)
        {
            Log.Error($"Lid policy: writing DC lid action {dc} to {scheme} failed (error {error})");
            return false;
        }

        var active = ActiveScheme();
        if (active is null)
        {
            Log.Error($"Lid policy: wrote {scheme} but could not tell whether it is active to apply it");
            return false;
        }
        if (active.Value != scheme)
        {
            Log.Info($"Lid policy: wrote AC {ac} / DC {dc} to inactive scheme {scheme}");
            return true;
        }
        var activeScheme = active.Value;
        error = PowerSetActiveScheme(0, in activeScheme);
        if (error != ERROR_SUCCESS)
        {
            Log.Error($"Lid policy: re-applying {scheme} after the write failed (error {error})");
            return false;
        }
        Log.Info($"Lid policy: set AC {ac} / DC {dc} on active scheme {scheme}");
        return true;
    }

    private static unsafe bool ReadHasLid()
    {
        var capabilities = stackalloc byte[SystemPowerCapabilitiesSize];
        var status = CallNtPowerInformation(SystemPowerCapabilities, null, 0, capabilities, SystemPowerCapabilitiesSize);
        if (status != 0)
        {
            // Assume a lid: applying a lid policy on a desktop is harmless,
            // missing it on a laptop is not.
            Log.Warn($"Lid policy: reading power capabilities failed (NTSTATUS 0x{status:X8}); assuming a lid");
            return true;
        }
        var hasLid = capabilities[LidPresentOffset] != 0;
        Log.Info($"Lid policy: lid {(hasLid ? "present" : "absent")}, Modern Standby {(capabilities[AoAcOffset] != 0 ? "yes" : "no")}");
        return hasLid;
    }
}
