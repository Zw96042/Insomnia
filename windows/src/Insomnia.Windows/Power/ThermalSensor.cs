using Insomnia.Core.Logging;
using Insomnia.Core.Logic;
using Insomnia.Core.Platform;
using static Insomnia.Windows.Power.PowerNative;

namespace Insomnia.Windows.Power;

/// <summary>
/// Thermal pressure from the <c>Thermal Zone Information</c> performance
/// counters. Windows has no thermal API for apps; <c>% Passive Limit</c> is
/// how far the firmware's passive cooling is holding the processor back
/// (100 means not at all). The engine polls every 30 s, and only two
/// consecutive throttled samples count as sustained.
/// A zone counts only after it has read 100 at least once: some firmware
/// reports 0 for zones that never use passive cooling, which would otherwise
/// read as permanent heavy throttling and end every session. The cost is
/// that a zone already throttled when Insomnia starts is ignored until it
/// first cools down.
/// </summary>
public sealed class ThermalSensor : IThermalSensor, IDisposable
{
    private const string CounterPath = @"\Thermal Zone Information(*)\% Passive Limit";
    private const double Throttled = 100;
    private const double HeavilyThrottled = 50;

    private readonly object _gate = new();
    private readonly SafePdhQueryHandle? _query;
    private readonly nint _counter;
    private double? _previous;
    private bool _failureLogged;
    private readonly HashSet<string> _trusted = new(StringComparer.Ordinal);

    public ThermalSensor()
    {
        var status = PdhOpenQuery(null, 0, out var query);
        if (status != ERROR_SUCCESS)
        {
            Log.Warn($"Thermal: PdhOpenQuery failed (0x{status:X8}); thermal rules are off");
            query.Dispose();
            return;
        }
        status = PdhAddEnglishCounter(query, CounterPath, 0, out var counter);
        if (status != ERROR_SUCCESS)
        {
            Log.Info($"Thermal: no thermal zone counters (0x{status:X8}); thermal rules are off");
            query.Dispose();
            return;
        }
        // An object with no instances adds fine but never reports a zone.
        if (Sample(query, counter, out var zones) is not { Count: > 0 } initial)
        {
            Log.Info($"Thermal: thermal zone counters report no zones ({zones}); thermal rules are off");
            query.Dispose();
            return;
        }
        Log.Info($"Thermal: watching {zones} thermal zone(s)");
        Trust(initial);
        _query = query;
        _counter = counter;
    }

    public bool IsAvailable => _query is not null;

    public ThermalState Read()
    {
        lock (_gate)
        {
            if (_query is null || _query.IsClosed)
            {
                return ThermalState.Nominal;
            }
            var sample = Sample(_query, _counter, out var zones);
            if (sample is null)
            {
                // No reading is no evidence of throttling; start counting afresh.
                if (!_failureLogged)
                {
                    _failureLogged = true;
                    Log.Warn($"Thermal: sampling failed ({zones} zones read)");
                }
                _previous = null;
                return ThermalState.Nominal;
            }
            _failureLogged = false;
            Trust(sample);
            double? current = null;
            foreach (var (name, value) in sample)
            {
                if (_trusted.Contains(name) && (current is null || value < current))
                {
                    current = value;
                }
            }
            if (current is null)
            {
                _previous = null;
                return ThermalState.Nominal;
            }
            var previous = _previous;
            _previous = current;
            return Classify(previous, current.Value);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _query?.Dispose();
        }
    }

    private void Trust(IReadOnlyDictionary<string, double> sample)
    {
        foreach (var (name, value) in sample)
        {
            if (value >= Throttled && _trusted.Add(name))
            {
                Log.Info($"Thermal: zone {name} reads unthrottled; it now counts");
            }
        }
    }

    /// <summary>Takes the lowest trusted zone so "any zone" throttled counts.</summary>
    private static ThermalState Classify(double? previous, double current)
    {
        if (previous is { } before)
        {
            if (before <= HeavilyThrottled && current <= HeavilyThrottled)
            {
                return ThermalState.Critical;
            }
            if (before < Throttled && current < Throttled)
            {
                return ThermalState.Serious;
            }
        }
        return current < Throttled ? ThermalState.Fair : ThermalState.Nominal;
    }

    /// <summary><c>% Passive Limit</c> by zone instance name, or null when the counters could not be read.</summary>
    private static unsafe Dictionary<string, double>? Sample(SafePdhQueryHandle query, nint counter, out int zones)
    {
        zones = 0;
        var status = PdhCollectQueryData(query);
        if (status != ERROR_SUCCESS)
        {
            Log.Warn($"Thermal: PdhCollectQueryData failed (0x{status:X8})");
            return null;
        }

        uint size = 0;
        status = PdhGetFormattedCounterArray(counter, PDH_FMT_DOUBLE, ref size, out var count, null);
        if (status != PDH_MORE_DATA || size == 0)
        {
            if (status != ERROR_SUCCESS)
            {
                Log.Warn($"Thermal: sizing the counter array failed (0x{status:X8})");
            }
            return null;
        }

        // The buffer holds the items followed by their instance names; the
        // instance count can grow between the two calls, so retry once.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var buffer = new byte[size];
            fixed (byte* bytes = buffer)
            {
                status = PdhGetFormattedCounterArray(counter, PDH_FMT_DOUBLE, ref size, out count, bytes);
                if (status == PDH_MORE_DATA)
                {
                    continue;
                }
                if (status != ERROR_SUCCESS)
                {
                    Log.Warn($"Thermal: reading the counter array failed (0x{status:X8})");
                    return null;
                }
                var items = (PDH_FMT_COUNTERVALUE_ITEM_W*)bytes;
                var values = new Dictionary<string, double>(StringComparer.Ordinal);
                for (var i = 0; i < count; i++)
                {
                    var item = items[i];
                    if (item.CStatus is not (PDH_CSTATUS_VALID_DATA or PDH_CSTATUS_NEW_DATA))
                    {
                        continue;
                    }
                    zones++;
                    var name = item.szName is null ? $"#{i}" : new string(item.szName);
                    values[name] = values.TryGetValue(name, out var existing) ? Math.Min(existing, item.doubleValue) : item.doubleValue;
                }
                return values;
            }
        }
        Log.Warn("Thermal: the counter array kept growing");
        return null;
    }
}
