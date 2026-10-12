using Insomnia.Core.Platform;

namespace Insomnia.App.Services;

/// <summary>
/// In-memory <see cref="ISettingsServices"/> for <c>--demo</c>: nothing is
/// written to the registry or Credential Manager. Launch at login cycles
/// through every state so each Settings note can be seen: the first switch-on
/// reports "turned off in Task Manager", the next one succeeds.
/// </summary>
public sealed class DemoSettingsServices : ISettingsServices
{
    private readonly Dictionary<string, string> _passwords = new(StringComparer.Ordinal);
    private StartupState _startup = StartupState.NotRegistered;
    private bool _showedTaskManagerNote;

    public DemoSettingsServices()
    {
        LogsFolder = Path.Combine(Path.GetTempPath(), "Insomnia-demo", "Logs");
    }

    public string LogsFolder { get; }

    public IReadOnlyList<RunningApp> RunningApps() =>
    [
        new("slack.exe", "Slack"),
        new("Discord.exe", "Discord"),
        new("Spotify.exe", "Spotify"),
        new("Notion.exe", "Notion"),
        new("Figma.exe", "Figma"),
        new("WINWORD.EXE", "Microsoft Word"),
        new("ms-teams.exe", "Microsoft Teams"),
        new("Code.exe", "Visual Studio Code"),
        new("WindowsTerminal.exe", "Windows Terminal"),
        new("chrome.exe", "Google Chrome"),
    ];

    public StartupState ReadStartup() => _startup;

    public string? SetStartup(bool enabled)
    {
        if (!enabled)
        {
            _startup = StartupState.NotRegistered;
            return null;
        }
        if (!_showedTaskManagerNote)
        {
            _showedTaskManagerNote = true;
            _startup = StartupState.DisabledByUser;
            return null;
        }
        _startup = StartupState.Enabled;
        return null;
    }

    public bool HasHotspotPassword(string ssid) => _passwords.ContainsKey(ssid);

    public string? SaveHotspotPassword(string ssid, string password)
    {
        if (string.IsNullOrWhiteSpace(ssid))
        {
            return "Enter the hotspot name first.";
        }
        if (password.Length is < 8 or > 63)
        {
            return "Wi-Fi passwords are 8 to 63 characters.";
        }
        _passwords[ssid] = password;
        return null;
    }

    public string? ClearHotspotPassword(string ssid)
    {
        _passwords.Remove(ssid);
        return null;
    }
}
