using System.Security;
using Insomnia.Core.Logging;
using Insomnia.Core.Platform;
using Microsoft.Win32;

namespace Insomnia.Windows.SystemServices;

/// <summary>
/// Launch at login through the per-user Run key. Task Manager's Startup apps
/// switch does not touch the Run value: it records its own flag under
/// <c>StartupApproved\Run</c>, which only the user should change, so it is
/// read and reported but never written.
/// </summary>
public sealed class StartupRegistration : IStartupRegistration
{
    public const string ValueName = "Insomnia";

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string TurnedOffMessage = "Insomnia is registered, but it is turned off in Task Manager > Startup apps. Turn it on there to start at sign-in.";

    private readonly string _executable;

    /// <param name="executablePath">The executable to start at login; normally the running app's own path.</param>
    public StartupRegistration(string executablePath)
    {
        _executable = Path.GetFullPath(executablePath);
    }

    public StartupState Read()
    {
        try
        {
            using (var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: false))
            {
                if (run?.GetValue(ValueName) is not string command || !IsThisExecutable(command))
                {
                    return StartupState.NotRegistered;
                }
            }
            return IsTurnedOffByUser() ? StartupState.DisabledByUser : StartupState.Enabled;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            Log.Warn($"Startup: reading the Run key failed: {ex.Message}");
            return StartupState.NotRegistered;
        }
    }

    public string? Set(bool enabled)
    {
        try
        {
            using var run = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (enabled)
            {
                run.SetValue(ValueName, "\"" + _executable + "\"", RegistryValueKind.String);
                Log.Info($"Startup: registered {_executable}");
                return IsTurnedOffByUser() ? TurnedOffMessage : null;
            }
            run.DeleteValue(ValueName, throwOnMissingValue: false);
            Log.Info("Startup: unregistered");
            return null;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            Log.Error($"Startup: {(enabled ? "registering" : "unregistering")} failed: {ex.Message}");
            return $"Windows did not allow the change: {ex.Message}";
        }
    }

    /// <summary>
    /// Task Manager writes a 12-byte value whose first byte is even when the
    /// entry is enabled (0x02) and odd when the user turned it off (0x03).
    /// </summary>
    private static bool IsTurnedOffByUser()
    {
        using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: false);
        return approved?.GetValue(ValueName) is byte[] { Length: > 0 } flags && (flags[0] & 1) == 1;
    }

    /// <summary>The Run value is a command line; its program is the quoted or leading path.</summary>
    private bool IsThisExecutable(string command)
    {
        var text = command.Trim();
        string path;
        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            path = end > 0 ? text[1..end] : text[1..];
        }
        else
        {
            path = text;
        }
        try
        {
            path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
        return string.Equals(path, _executable, StringComparison.OrdinalIgnoreCase);
    }
}
