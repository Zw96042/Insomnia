using System.Runtime.InteropServices;
using System.Security;
using Insomnia.Core.Logging;
using Insomnia.Core.Model;
using Insomnia.Core.Platform;
using Microsoft.Win32;
using static Insomnia.Windows.Power.PowerNative;

namespace Insomnia.Windows.Power;

/// <summary>
/// The power mode overlay (the Settings "Power mode" slider). The overlay
/// functions are exported by powrprof.dll but not in every build's import
/// library, so they are bound at run time and their absence reads as
/// unsupported rather than failing to load the assembly.
/// </summary>
/// <remarks>
/// Windows may accept an overlay only on the Balanced scheme, and may accept
/// the call without applying it, so a set is verified by reading back.
/// </remarks>
public sealed unsafe class PowerMode : IPowerMode
{
    private const string SchemesKey = @"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes";
    private const string AcValue = "ActiveOverlayAcPowerScheme";
    private const string DcValue = "ActiveOverlayDcPowerScheme";

    private readonly delegate* unmanaged<Guid*, uint> _getEffective;
    private readonly delegate* unmanaged<Guid, uint> _setActive;

    public PowerMode()
    {
        if (!NativeLibrary.TryLoad("powrprof.dll", typeof(PowerMode).Assembly, DllImportSearchPath.System32, out var library))
        {
            Log.Warn("Power mode: powrprof.dll did not load; power mode is unsupported");
            return;
        }
        if (NativeLibrary.TryGetExport(library, "PowerGetEffectiveOverlayScheme", out var get)
            && NativeLibrary.TryGetExport(library, "PowerSetActiveOverlayScheme", out var set))
        {
            _getEffective = (delegate* unmanaged<Guid*, uint>)get;
            _setActive = (delegate* unmanaged<Guid, uint>)set;
        }
        else
        {
            Log.Info("Power mode: overlay functions are not exported on this build; power mode is unsupported");
        }
        // powrprof.dll stays loaded for the process (the P/Invokes use it too).
    }

    public bool IsSupported => Effective() is not null;

    public PowerSourceKind? CurrentSource()
    {
        if (!GetSystemPowerStatus(out var status))
        {
            Log.Warn($"Power mode: GetSystemPowerStatus failed (error {Marshal.GetLastPInvokeError()})");
            return null;
        }
        return status.ACLineStatus switch
        {
            0 => PowerSourceKind.Dc,
            1 => PowerSourceKind.Ac,
            _ => null,
        };
    }

    public (Guid? Ac, Guid? Dc) Read()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(SchemesKey, writable: false);
            if (key is null)
            {
                Log.Warn($@"Power mode: HKLM\{SchemesKey} is missing");
                return (null, null);
            }
            return (ReadOverlay(key, AcValue), ReadOverlay(key, DcValue));
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            Log.Warn($"Power mode: reading the overlays failed: {ex.Message}");
            return (null, null);
        }
    }

    public bool SetForCurrentSource(Guid overlay)
    {
        if (_setActive == null)
        {
            Log.Warn("Power mode: cannot set an overlay on this build");
            return false;
        }
        var error = _setActive(overlay);
        if (error != ERROR_SUCCESS)
        {
            Log.Error($"Power mode: Windows refused {PowerOverlays.Describe(overlay)} (error {error})");
            return false;
        }

        var source = CurrentSource();
        var (ac, dc) = Read();
        var recorded = source switch
        {
            PowerSourceKind.Ac => ac,
            PowerSourceKind.Dc => dc,
            _ => null,
        };
        if (recorded == overlay || Effective() == overlay)
        {
            Log.Info($"Power mode: set {PowerOverlays.Describe(overlay)} for {source?.ToString() ?? "the current source"}");
            return true;
        }
        Log.Error($"Power mode: Windows accepted {PowerOverlays.Describe(overlay)} but did not apply it (the active scheme may not support overlays)");
        return false;
    }

    private Guid? Effective()
    {
        if (_getEffective == null)
        {
            return null;
        }
        Guid overlay;
        var error = _getEffective(&overlay);
        return error == ERROR_SUCCESS ? overlay : null;
    }

    /// <summary>A missing value means no overlay, which is Balanced.</summary>
    private static Guid? ReadOverlay(RegistryKey key, string name)
    {
        var value = key.GetValue(name);
        if (value is null)
        {
            return PowerOverlays.Balanced;
        }
        if (value is string text && Guid.TryParse(text, out var overlay))
        {
            return overlay;
        }
        Log.Warn($"Power mode: {name} has an unexpected value");
        return null;
    }
}
