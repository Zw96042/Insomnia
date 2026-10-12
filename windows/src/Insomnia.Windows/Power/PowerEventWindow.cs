using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Insomnia.Core.Logging;
using Insomnia.Core.Platform;
using static Insomnia.Windows.Power.PowerNative;

namespace Insomnia.Windows.Power;

/// <summary>
/// Power events from a hidden top-level window on a dedicated thread.
/// Windows delivers lid, power source and scheme changes only to a window or
/// a service, so this owns one rather than borrowing the UI's dispatcher. It
/// is a hidden top-level window rather than a message-only one because
/// suspend and resume (<c>PBT_APMRESUME*</c>) arrive only as broadcasts, which
/// message-only windows never receive.
/// </summary>
/// <remarks>
/// The window starts in the constructor. Windows answers each
/// <c>RegisterPowerSettingNotification</c> with the setting's current value,
/// so <see cref="LidClosed"/> fills in within moments of construction and
/// <see cref="LidChanged"/> fires for that initial report; a subscriber that
/// attaches later reads <see cref="LidClosed"/> instead. The initial power
/// source, battery and scheme reports only seed the last-known values:
/// <see cref="PowerSourceChanged"/> and <see cref="ActiveSchemeChanged"/>
/// fire for real changes, so re-applying the same scheme (which Windows also
/// reports) does not loop back into the engine. Events are raised on the
/// window thread; a throwing subscriber is logged and does not stop others.
/// </remarks>
public sealed class PowerEventWindow : IPowerEvents, IDisposable
{
    private static readonly Guid LidSwitchStateChange = new("BA3E0F4D-B817-4094-A2D1-D56379E6A0F3");
    private static readonly Guid AcDcPowerSource = new("5D3E9A59-E9D5-4B00-A6BD-FF34FF516548");
    private static readonly Guid BatteryPercentageRemaining = new("A7AD8041-B45A-4CAE-87A3-EECBB468A9E1");
    private static readonly Guid ActivePowerScheme = new("31F9F286-5084-42FE-B720-2B0264993763");

    private static readonly Guid[] Settings = [LidSwitchStateChange, AcDcPowerSource, BatteryPercentageRemaining, ActivePowerScheme];

    private readonly WindowThread _window;

    // Only touched on the window thread.
    private readonly List<nint> _registrations = [];
    private uint? _powerSource;
    private uint? _batteryPercent;
    private Guid? _scheme;
    private bool _resumeRaised;

    // -1 unknown, 0 open, 1 closed; read from any thread.
    private int _lid = -1;

    public PowerEventWindow()
    {
        _window = new WindowThread("PowerEvents", messageOnly: false, OnMessage, Register, Unregister);
        IsRunning = _window.Start();
        if (!IsRunning)
        {
            Log.Error("Power events: no window, so lid, power source and resume events will not arrive");
        }
    }

    /// <summary>False when the window could not be created; no events will arrive.</summary>
    public bool IsRunning { get; }

    public bool? LidClosed => Volatile.Read(ref _lid) switch
    {
        0 => false,
        1 => true,
        _ => null,
    };

    public event EventHandler<bool>? LidChanged;

    public event EventHandler? PowerSourceChanged;

    public event EventHandler? ActiveSchemeChanged;

    public event EventHandler? Resumed;

    public void Dispose() => _window.Dispose();

    private void Register(nint hwnd)
    {
        foreach (var setting in Settings)
        {
            var handle = RegisterPowerSettingNotification(hwnd, in setting, DEVICE_NOTIFY_WINDOW_HANDLE);
            if (handle == 0)
            {
                Log.Warn($"Power events: RegisterPowerSettingNotification({setting}) failed (error {Marshal.GetLastPInvokeError()})");
                continue;
            }
            _registrations.Add(handle);
        }
    }

    private void Unregister(nint hwnd)
    {
        foreach (var handle in _registrations)
        {
            if (!UnregisterPowerSettingNotification(handle))
            {
                Log.Warn($"Power events: UnregisterPowerSettingNotification failed (error {Marshal.GetLastPInvokeError()})");
            }
        }
        _registrations.Clear();
    }

    private nint? OnMessage(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        if (message != WM_POWERBROADCAST)
        {
            return null;
        }
        switch ((uint)wParam)
        {
            case PBT_POWERSETTINGCHANGE:
                OnSettingChange(lParam);
                break;
            case PBT_APMSUSPEND:
                _resumeRaised = false;
                break;
            case PBT_APMRESUMEAUTOMATIC:
            case PBT_APMRESUMESUSPEND:
                // A user-initiated wake sends both; report the resume once.
                if (!_resumeRaised)
                {
                    _resumeRaised = true;
                    Raise(Resumed, nameof(Resumed));
                }
                break;
            case PBT_APMPOWERSTATUSCHANGE:
                Raise(PowerSourceChanged, nameof(PowerSourceChanged));
                break;
        }
        return 1;
    }

    /// <summary>Parses POWERBROADCAST_SETTING: GUID PowerSetting, DWORD DataLength, then the data.</summary>
    private unsafe void OnSettingChange(nint lParam)
    {
        if (lParam == 0)
        {
            return;
        }
        var bytes = (byte*)lParam;
        var setting = Unsafe.ReadUnaligned<Guid>(bytes);
        var length = Unsafe.ReadUnaligned<uint>(bytes + 16);
        var data = bytes + 20;

        if (setting == ActivePowerScheme)
        {
            if (length < 16)
            {
                return;
            }
            var scheme = Unsafe.ReadUnaligned<Guid>(data);
            var previous = _scheme;
            _scheme = scheme;
            if (previous.HasValue && previous.Value != scheme)
            {
                Log.Info($"Power events: active scheme changed to {scheme}");
                Raise(ActiveSchemeChanged, nameof(ActiveSchemeChanged));
            }
            return;
        }

        if (length < 4)
        {
            return;
        }
        var value = Unsafe.ReadUnaligned<uint>(data);
        if (setting == LidSwitchStateChange)
        {
            var closed = value == 0;
            Volatile.Write(ref _lid, closed ? 1 : 0);
            var handlers = LidChanged;
            if (handlers is null)
            {
                return;
            }
            foreach (var handler in handlers.GetInvocationList().Cast<EventHandler<bool>>())
            {
                try
                {
                    handler(this, closed);
                }
                catch (Exception ex)
                {
                    Log.Error("Power events: LidChanged subscriber failed", ex);
                }
            }
        }
        else if (setting == AcDcPowerSource)
        {
            var previous = _powerSource;
            _powerSource = value;
            if (previous.HasValue && previous.Value != value)
            {
                Raise(PowerSourceChanged, nameof(PowerSourceChanged));
            }
        }
        else if (setting == BatteryPercentageRemaining)
        {
            var previous = _batteryPercent;
            _batteryPercent = value;
            if (previous.HasValue && previous.Value != value)
            {
                Raise(PowerSourceChanged, nameof(PowerSourceChanged));
            }
        }
    }

    private void Raise(EventHandler? handlers, string name)
    {
        if (handlers is null)
        {
            return;
        }
        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler>())
        {
            try
            {
                handler(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                Log.Error($"Power events: {name} subscriber failed", ex);
            }
        }
    }
}
