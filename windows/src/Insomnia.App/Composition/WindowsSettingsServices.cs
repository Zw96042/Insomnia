using Insomnia.App.Services;
using Insomnia.Core.Platform;

namespace Insomnia.App.Composition;

/// <summary><see cref="ISettingsServices"/> over the registry, Credential Manager and the process list.</summary>
internal sealed class WindowsSettingsServices : ISettingsServices
{
    private readonly WindowsHost _host;

    public WindowsSettingsServices(WindowsHost host)
    {
        _host = host;
    }

    public string LogsFolder => _host.Paths.Logs;

    public StartupState ReadStartup() => _host.Startup.Read();

    public string? SetStartup(bool enabled) => _host.Startup.Set(enabled);

    public bool HasHotspotPassword(string ssid) =>
        !string.IsNullOrEmpty(ssid) && _host.Credentials.ReadHotspotPassword(ssid) is not null;

    public string? SaveHotspotPassword(string ssid, string password)
    {
        if (string.IsNullOrWhiteSpace(ssid))
        {
            return "Enter the hotspot's name (SSID) first.";
        }
        var error = _host.Credentials.WriteHotspotPassword(ssid, password);
        if (error is null)
        {
            // Windows 11 needs location access before Wi-Fi scans return SSIDs;
            // ask now, while the user is here, not in the middle of an outage.
            _ = _host.Services.Wifi.RequestAccessAsync();
        }
        return error;
    }

    public string? ClearHotspotPassword(string ssid) =>
        string.IsNullOrEmpty(ssid) ? null : _host.Credentials.DeleteHotspotPassword(ssid);

    public IReadOnlyList<RunningApp> RunningApps()
    {
        var self = _host.Catalog.SelfPid;
        var windows = _host.Catalog.WindowsDirectory.TrimEnd('\\') + "\\";
        return _host.Catalog.Snapshot()
            .Where(p => p.HasVisibleWindow && p.IsCurrentUser && p.Pid != self
                && !(p.Path?.StartsWith(windows, StringComparison.OrdinalIgnoreCase) ?? false))
            .GroupBy(p => p.Image, StringComparer.OrdinalIgnoreCase)
            .Select(g => new RunningApp(g.First().Image, g.First().DisplayName))
            .OrderBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }
}
