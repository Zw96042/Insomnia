using System.Net;
using System.Net.NetworkInformation;
using Insomnia.Core.Logging;
using Insomnia.Core.Platform;
using Windows.Devices.WiFi;
using Windows.Networking.Connectivity;
using Windows.Security.Credentials;
using WinRtNetworkInformation = Windows.Networking.Connectivity.NetworkInformation;

namespace Insomnia.Windows.Network;

/// <summary>
/// Wi-Fi state and the hotspot join. Connectivity comes from the adapters'
/// operational state and gateways (cheap, no permission needed); the SSID and
/// the scan come from WinRT, which on Windows 11 needs location access before
/// it reveals SSIDs.
/// </summary>
public sealed class Wifi : IWifi, IDisposable
{
    private int _disposed;

    public Wifi()
    {
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
    }

    public event EventHandler? ConnectivityChanged;

    public WifiConnectivity Connectivity()
    {
        NetworkInterface[] interfaces;
        try
        {
            interfaces = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException ex)
        {
            Log.Error("wifi: could not list network interfaces", ex);
            return WifiConnectivity.Unsatisfied;
        }

        var anyWireless = false;
        foreach (var adapter in interfaces)
        {
            if (adapter.NetworkInterfaceType != NetworkInterfaceType.Wireless80211)
            {
                continue;
            }
            anyWireless = true;
            if (adapter.OperationalStatus == OperationalStatus.Up && HasGateway(adapter))
            {
                return WifiConnectivity.Satisfied;
            }
        }
        return anyWireless ? WifiConnectivity.Unsatisfied : WifiConnectivity.NoAdapter;
    }

    public string? CurrentSsid()
    {
        try
        {
            // The internet profile is the one traffic uses when it is Wi-Fi;
            // otherwise any connected Wi-Fi profile (a hotspot without
            // internet access still has an SSID worth reporting).
            var internet = WinRtNetworkInformation.GetInternetConnectionProfile();
            var ssid = ConnectedSsid(internet);
            if (ssid is not null)
            {
                return ssid;
            }
            foreach (var profile in WinRtNetworkInformation.GetConnectionProfiles())
            {
                ssid = ConnectedSsid(profile);
                if (ssid is not null)
                {
                    return ssid;
                }
            }
            return null;
        }
        catch (Exception ex)
        {
            Log.Warn($"wifi: could not read the current SSID: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    public async Task<bool> RequestAccessAsync()
    {
        try
        {
            var access = await WiFiAdapter.RequestAccessAsync().AsTask().ConfigureAwait(false);
            if (access != WiFiAccessStatus.Allowed)
            {
                Log.Warn($"wifi: access to the Wi-Fi adapter is {access}");
            }
            return access == WiFiAccessStatus.Allowed;
        }
        catch (Exception ex)
        {
            Log.Error("wifi: requesting Wi-Fi access failed", ex);
            return false;
        }
    }

    public async Task<HotspotJoinResult> JoinAsync(string ssid, string password, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(ssid);
        ArgumentNullException.ThrowIfNull(password);
        try
        {
            var access = await WiFiAdapter.RequestAccessAsync().AsTask(cancellationToken).ConfigureAwait(false);
            if (access != WiFiAccessStatus.Allowed)
            {
                Log.Warn($"wifi: cannot join {ssid}: access to the Wi-Fi adapter is {access}");
                return HotspotJoinResult.AccessDenied;
            }

            var adapters = await WiFiAdapter.FindAllAdaptersAsync().AsTask(cancellationToken).ConfigureAwait(false);
            if (adapters.Count == 0)
            {
                Log.Warn($"wifi: cannot join {ssid}: no Wi-Fi adapter");
                return HotspotJoinResult.Failed;
            }
            var adapter = adapters[0];

            await adapter.ScanAsync().AsTask(cancellationToken).ConfigureAwait(false);
            // A hotspot can answer from more than one BSSID; take the strongest.
            var network = adapter.NetworkReport.AvailableNetworks
                .Where(n => string.Equals(n.Ssid, ssid, StringComparison.Ordinal))
                .OrderByDescending(n => n.NetworkRssiInDecibelMilliwatts)
                .FirstOrDefault();
            if (network is null)
            {
                Log.Info($"wifi: {ssid} not found in the scan");
                return HotspotJoinResult.NotFound;
            }
            cancellationToken.ThrowIfCancellationRequested();

            WiFiConnectionResult result;
            if (password.Length == 0)
            {
                result = await adapter.ConnectAsync(network, WiFiReconnectionKind.Automatic).AsTask(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var credential = new PasswordCredential { Password = password };
                result = await adapter.ConnectAsync(network, WiFiReconnectionKind.Automatic, credential).AsTask(cancellationToken).ConfigureAwait(false);
            }

            var outcome = result.ConnectionStatus switch
            {
                WiFiConnectionStatus.Success => HotspotJoinResult.Joined,
                WiFiConnectionStatus.AccessRevoked => HotspotJoinResult.AccessDenied,
                WiFiConnectionStatus.InvalidCredential => HotspotJoinResult.BadPassword,
                WiFiConnectionStatus.NetworkNotAvailable => HotspotJoinResult.NotFound,
                _ => HotspotJoinResult.Failed,
            };
            Log.Info($"wifi: joining {ssid}: {result.ConnectionStatus}");
            return outcome;
        }
        catch (OperationCanceledException)
        {
            Log.Info($"wifi: joining {ssid} was cancelled");
            return HotspotJoinResult.Failed;
        }
        catch (Exception ex)
        {
            // The password is never part of an exception message from these APIs; only the SSID is logged.
            Log.Error($"wifi: joining {ssid} failed", ex);
            return HotspotJoinResult.Failed;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
    }

    private static bool HasGateway(NetworkInterface adapter)
    {
        try
        {
            foreach (var gateway in adapter.GetIPProperties().GatewayAddresses)
            {
                var address = gateway.Address;
                if (!address.Equals(IPAddress.Any) && !address.Equals(IPAddress.IPv6Any) && !address.Equals(IPAddress.None))
                {
                    return true;
                }
            }
        }
        catch (NetworkInformationException ex)
        {
            Log.Warn($"wifi: could not read the gateways of {adapter.Name}: {ex.Message}");
        }
        return false;
    }

    private static string? ConnectedSsid(ConnectionProfile? profile)
    {
        if (profile is null || !profile.IsWlanConnectionProfile
            || profile.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.None)
        {
            return null;
        }
        var ssid = profile.WlanConnectionProfileDetails?.GetConnectedSsid();
        return string.IsNullOrEmpty(ssid) ? null : ssid;
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e) => Raise();

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) => Raise();

    private void Raise()
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            ConnectivityChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
