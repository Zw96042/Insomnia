using System.Runtime.InteropServices;
using Insomnia.Core.Logging;
using Insomnia.Core.Platform;
using static System.FormattableString;

namespace Insomnia.Windows.Devices;

/// <summary>
/// Output endpoints through Core Audio. Device ids are endpoint ids, which
/// stay the same across reconnects, so lid open restores the device lid
/// close muted even if the default output changed in between.
/// </summary>
/// <remarks>
/// The enumerator is created on a dedicated MTA thread. Core Audio objects
/// are free-threaded, so an enumerator living in the MTA can be called from
/// the engine's thread-pool threads directly; one created on the UI's STA
/// thread would route every call through that thread instead. Every COM
/// object obtained per call is released before the call returns.
/// </remarks>
public sealed class AudioControl : IAudioControl, IDisposable
{
    /// <summary>Name used when the endpoint's friendly name cannot be read.</summary>
    private const string UnnamedDevice = "Audio output";

    private readonly IMMDeviceEnumerator? _enumerator;
    private readonly NotificationClient? _client;
    private int _disposed;

    public AudioControl()
    {
        (_enumerator, var error) = CreateEnumeratorInMta();
        if (_enumerator is null)
        {
            Log.Error($"audio: could not create the device enumerator: {error}");
            return;
        }
        var client = new NotificationClient(this);
        var hr = _enumerator.RegisterEndpointNotificationCallback(client);
        if (hr < 0)
        {
            Log.Error(Invariant($"audio: registering for device changes failed with 0x{hr:X8}; reconnects will be noticed only on the next check"));
            return;
        }
        _client = client;
    }

    public event EventHandler? DevicesChanged;

    public AudioEndpoint? DefaultOutput()
    {
        var enumerator = Enumerator();
        if (enumerator is null)
        {
            return null;
        }
        var hr = enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Console, out var device);
        if (hr == AudioNative.ENotFound)
        {
            return null;
        }
        if (hr < 0 || device is null)
        {
            Log.Error(Invariant($"audio: reading the default output failed with 0x{hr:X8}"));
            return null;
        }
        try
        {
            hr = device.GetId(out var id);
            if (hr < 0 || string.IsNullOrEmpty(id))
            {
                Log.Error(Invariant($"audio: reading the default output's id failed with 0x{hr:X8}"));
                return null;
            }
            return Describe(device, id);
        }
        finally
        {
            Release(device);
        }
    }

    public AudioDeviceStatus Status(string deviceId)
    {
        ArgumentException.ThrowIfNullOrEmpty(deviceId);
        var enumerator = Enumerator();
        if (enumerator is null)
        {
            return AudioDeviceStatus.Unknown;
        }
        var lookup = enumerator.GetDevice(deviceId, out var device);
        if (lookup == AudioNative.ENotFound)
        {
            // Windows no longer knows the endpoint (its driver was removed):
            // confirmed absent, so its saved volume waits like an unplugged one.
            Release(device);
            return AudioDeviceStatus.Away;
        }
        if (lookup < 0 || device is null)
        {
            Release(device);
            Log.Error(Invariant($"audio: looking up {deviceId} failed with 0x{lookup:X8}"));
            return AudioDeviceStatus.Unknown;
        }
        try
        {
            var hr = device.GetState(out var state);
            if (hr < 0)
            {
                Log.Error(Invariant($"audio: reading the state of {deviceId} failed with 0x{hr:X8}"));
                return AudioDeviceStatus.Unknown;
            }
            return state switch
            {
                AudioNative.DeviceStateActive => AudioDeviceStatus.Connected,
                AudioNative.DeviceStateDisabled or AudioNative.DeviceStateNotPresent or AudioNative.DeviceStateUnplugged => AudioDeviceStatus.Away,
                _ => AudioDeviceStatus.Unknown,
            };
        }
        finally
        {
            Release(device);
        }
    }

    public AudioEndpoint? Read(string deviceId)
    {
        ArgumentException.ThrowIfNullOrEmpty(deviceId);
        var device = OpenActive(deviceId);
        if (device is null)
        {
            return null;
        }
        try
        {
            return Describe(device, deviceId);
        }
        finally
        {
            Release(device);
        }
    }

    public bool SetMute(string deviceId, bool muted)
    {
        ArgumentException.ThrowIfNullOrEmpty(deviceId);
        return WithVolume(deviceId, "set mute", volume =>
        {
            var context = Guid.Empty;
            return volume.SetMute(muted, ref context);
        });
    }

    public bool SetVolume(string deviceId, float volume)
    {
        ArgumentException.ThrowIfNullOrEmpty(deviceId);
        if (float.IsNaN(volume))
        {
            Log.Error($"audio: refusing to set {deviceId} to a volume that is not a number");
            return false;
        }
        var level = Math.Clamp(volume, 0f, 1f);
        return WithVolume(deviceId, "set volume", endpoint =>
        {
            var context = Guid.Empty;
            return endpoint.SetMasterVolumeLevelScalar(level, ref context);
        });
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        if (_enumerator is null)
        {
            return;
        }
        if (_client is not null)
        {
            var hr = _enumerator.UnregisterEndpointNotificationCallback(_client);
            if (hr < 0)
            {
                Log.Warn(Invariant($"audio: unregistering for device changes failed with 0x{hr:X8}"));
            }
        }
        Marshal.FinalReleaseComObject(_enumerator);
    }

    private void RaiseDevicesChanged()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }
        // Core Audio forbids blocking or calling back into it from inside a
        // notification, so subscribers run on the thread pool instead.
        ThreadPool.QueueUserWorkItem(static self => self.DevicesChanged?.Invoke(self, EventArgs.Empty), this, preferLocal: false);
    }

    private IMMDeviceEnumerator? Enumerator()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _enumerator;
    }

    /// <summary>The device with this endpoint id, or null when Windows does not know it.</summary>
    private IMMDevice? Open(string deviceId)
    {
        var enumerator = Enumerator();
        if (enumerator is null)
        {
            return null;
        }
        var hr = enumerator.GetDevice(deviceId, out var device);
        if (hr == AudioNative.ENotFound)
        {
            Release(device);
            return null;
        }
        if (hr < 0 || device is null)
        {
            Release(device);
            Log.Error(Invariant($"audio: looking up {deviceId} failed with 0x{hr:X8}"));
            return null;
        }
        return device;
    }

    /// <summary>The device when it is present and active, else null.</summary>
    private IMMDevice? OpenActive(string deviceId)
    {
        var device = Open(deviceId);
        if (device is null)
        {
            return null;
        }
        if (device.GetState(out var state) >= 0 && state == AudioNative.DeviceStateActive)
        {
            return device;
        }
        Release(device);
        return null;
    }

    private AudioEndpoint? Describe(IMMDevice device, string id)
    {
        var volume = ActivateVolume(device, id);
        if (volume is null)
        {
            return null;
        }
        try
        {
            var hr = volume.GetMasterVolumeLevelScalar(out var level);
            if (hr < 0)
            {
                Log.Error(Invariant($"audio: reading the volume of {id} failed with 0x{hr:X8}"));
                return null;
            }
            hr = volume.GetMute(out var muted);
            if (hr < 0)
            {
                Log.Error(Invariant($"audio: reading the mute state of {id} failed with 0x{hr:X8}"));
                return null;
            }
            return new AudioEndpoint(id, FriendlyName(device) ?? UnnamedDevice, level, muted);
        }
        finally
        {
            Release(volume);
        }
    }

    private bool WithVolume(string deviceId, string what, Func<IAudioEndpointVolume, int> action)
    {
        var device = OpenActive(deviceId);
        if (device is null)
        {
            Log.Warn($"audio: cannot {what} on {deviceId}: it is not connected");
            return false;
        }
        try
        {
            var volume = ActivateVolume(device, deviceId);
            if (volume is null)
            {
                return false;
            }
            try
            {
                var hr = action(volume);
                if (hr < 0)
                {
                    Log.Error(Invariant($"audio: {what} on {deviceId} failed with 0x{hr:X8}"));
                    return false;
                }
                return true;
            }
            finally
            {
                Release(volume);
            }
        }
        finally
        {
            Release(device);
        }
    }

    private static IAudioEndpointVolume? ActivateVolume(IMMDevice device, string id)
    {
        var iid = AudioNative.IidAudioEndpointVolume;
        var hr = device.Activate(ref iid, AudioNative.ClsctxAll, 0, out var instance);
        if (hr < 0 || instance is null)
        {
            Release(instance);
            Log.Error(Invariant($"audio: activating the volume control of {id} failed with 0x{hr:X8}"));
            return null;
        }
        // The RCW was activated for this interface, so the cast is a
        // QueryInterface that succeeds; both names refer to the same RCW.
        return (IAudioEndpointVolume)instance;
    }

    private static string? FriendlyName(IMMDevice device)
    {
        if (device.OpenPropertyStore(AudioNative.StgmRead, out var store) < 0 || store is null)
        {
            Release(store);
            return null;
        }
        try
        {
            var key = AudioNative.DeviceFriendlyName;
            if (store.GetValue(ref key, out var value) < 0)
            {
                return null;
            }
            try
            {
                return value.ValueType == AudioNative.VtLpwstr && value.Value != 0 ? Marshal.PtrToStringUni(value.Value) : null;
            }
            finally
            {
                _ = AudioNative.PropVariantClear(ref value);
            }
        }
        finally
        {
            Release(store);
        }
    }

    /// <summary>
    /// Drops the one reference this call's marshalling added. Not a final
    /// release: the runtime shares one wrapper per COM identity, and another
    /// call may hold the same object.
    /// </summary>
    private static void Release(object? comObject)
    {
        if (comObject is not null && Marshal.IsComObject(comObject))
        {
            Marshal.ReleaseComObject(comObject);
        }
    }

    private static (IMMDeviceEnumerator? Enumerator, string? Error) CreateEnumeratorInMta()
    {
        IMMDeviceEnumerator? enumerator = null;
        string? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCoClass();
            }
            catch (Exception ex)
            {
                error = $"{ex.GetType().Name}: {ex.Message}";
            }
        })
        {
            IsBackground = true,
            Name = "Insomnia audio setup",
        };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        thread.Join();
        return (enumerator, error);
    }

    /// <summary>
    /// Receives endpoint notifications. Only render-side default changes
    /// matter; property changes (names, formats) never affect a restore.
    /// </summary>
    private sealed class NotificationClient : IMMNotificationClient
    {
        private readonly AudioControl _owner;

        public NotificationClient(AudioControl owner)
        {
            _owner = owner;
        }

        public int OnDeviceStateChanged(string deviceId, uint newState)
        {
            _owner.RaiseDevicesChanged();
            return AudioNative.SOk;
        }

        public int OnDeviceAdded(string deviceId)
        {
            _owner.RaiseDevicesChanged();
            return AudioNative.SOk;
        }

        public int OnDeviceRemoved(string deviceId)
        {
            _owner.RaiseDevicesChanged();
            return AudioNative.SOk;
        }

        public int OnDefaultDeviceChanged(EDataFlow flow, ERole role, string? defaultDeviceId)
        {
            if (flow == EDataFlow.Render)
            {
                _owner.RaiseDevicesChanged();
            }
            return AudioNative.SOk;
        }

        public int OnPropertyValueChanged(string deviceId, PropertyKey key) => AudioNative.SOk;
    }
}
