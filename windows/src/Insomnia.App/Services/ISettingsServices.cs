using Insomnia.Core.Platform;

namespace Insomnia.App.Services;

/// <summary>A running app offered by "Add running app…": its executable name and what to call it.</summary>
public sealed record RunningApp(string Image, string DisplayName);

/// <summary>
/// Operations the Settings window and the tray menu need beyond
/// <see cref="Insomnia.Core.Engine.ISessionController"/>: things that are not
/// part of <c>config.json</c>. The integrator implements this over
/// <see cref="IStartupRegistration"/>, <see cref="ICredentialStore"/> and the
/// data folder; <see cref="DemoSettingsServices"/> is the in-memory stand-in.
/// </summary>
/// <remarks>
/// Every member except <see cref="RunningApps"/> is called on the UI thread
/// and must return quickly (registry and Credential Manager calls are fine). Methods that return
/// <c>string?</c> return an error message for the user, or null on success,
/// and must not throw for expected failures. The UI catches and logs any
/// exception anyway and shows its message inline.
/// </remarks>
public interface ISettingsServices
{
    /// <summary>What Windows has on file for launch at login, re-read whenever Settings shows it.</summary>
    StartupState ReadStartup();

    /// <summary>Register or remove launch at login. Returns an error message, or null.</summary>
    string? SetStartup(bool enabled);

    /// <summary>True when Credential Manager holds a hotspot password for <paramref name="ssid"/>. Never returns the password.</summary>
    bool HasHotspotPassword(string ssid);

    /// <summary>Store the hotspot password for <paramref name="ssid"/>. Returns an error message, or null.</summary>
    string? SaveHotspotPassword(string ssid, string password);

    /// <summary>Delete the stored hotspot password for <paramref name="ssid"/>. Returns an error message, or null (also when none was stored).</summary>
    string? ClearHotspotPassword(string ssid);

    /// <summary>Folder holding <c>insomnia.log</c> and <c>handoffs.log</c>, opened by "Open logs".</summary>
    string LogsFolder { get; }

    /// <summary>
    /// Apps of the current user with a visible taskbar window, one entry per
    /// image name, for the "Add running app…" pickers. Called on a background
    /// thread, since it snapshots processes. (<see cref="Insomnia.Core.Engine.FreezePreview"/>
    /// carries display names only, so it cannot fill a list of executables.)
    /// </summary>
    IReadOnlyList<RunningApp> RunningApps();
}
