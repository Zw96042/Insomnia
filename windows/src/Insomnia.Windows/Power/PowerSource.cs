using System.Runtime.InteropServices;
using Insomnia.Core.Logging;
using Insomnia.Core.Platform;
using static Insomnia.Windows.Power.PowerNative;

namespace Insomnia.Windows.Power;

/// <summary>
/// Battery level and charger state from <c>GetSystemPowerStatus</c>, and the
/// charge rate from the WinRT aggregate battery report, which is the only
/// API that gives a rate without opening battery devices.
/// </summary>
public sealed class PowerSource : IPowerSource
{
    private const byte NoSystemBattery = 128;
    private const byte Unknown = 255;

    private bool _rateFailureLogged;

    public BatteryReading Read()
    {
        if (!GetSystemPowerStatus(out var status))
        {
            Log.Warn($"Power source: GetSystemPowerStatus failed (error {Marshal.GetLastPInvokeError()})");
            return new BatteryReading(null, null, null, null);
        }

        bool? hasBattery = status.BatteryFlag switch
        {
            Unknown => null,
            _ when (status.BatteryFlag & NoSystemBattery) != 0 => false,
            _ => true,
        };
        bool? onAc = status.ACLineStatus switch
        {
            0 => false,
            1 => true,
            _ => null,
        };
        int? percent = hasBattery == false || status.BatteryLifePercent == Unknown
            ? null
            : Math.Min((int)status.BatteryLifePercent, 100);
        var rate = hasBattery == false ? null : ChargeRate();
        return new BatteryReading(hasBattery, onAc, percent, rate);
    }

    private int? ChargeRate()
    {
        try
        {
            var report = global::Windows.Devices.Power.Battery.AggregateBattery.GetReport();
            _rateFailureLogged = false;
            return report.ChargeRateInMilliwatts;
        }
        catch (Exception ex)
        {
            // Polled every 30 s; log the first failure of a run, not each one.
            if (!_rateFailureLogged)
            {
                _rateFailureLogged = true;
                Log.Warn($"Power source: battery report failed: {ex.GetType().Name}: {ex.Message}");
            }
            return null;
        }
    }
}
