using Insomnia.Core.Model;
using Insomnia.Core.Platform;

namespace Insomnia.Core.Engine;

public enum SessionPhase
{
    Idle,
    /// <summary>A start is in flight; the UI shows the pending countdown.</summary>
    Starting,
    Active,
    /// <summary>An end is in flight.</summary>
    Ending,
}

public enum WarningSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// A line in the tray menu that needs attention: a failed restore, a journal
/// that refuses writes, a browser running throttled. <see cref="Id"/> is
/// stable for the same condition, so the UI can animate changes.
/// </summary>
public sealed record MenuWarning(string Id, WarningSeverity Severity, string Text, MenuAction? Action = null);

/// <summary>An action offered next to a warning.</summary>
public abstract record MenuAction(string Label)
{
    /// <summary>Drop a saved volume whose device is away, leaving the device as it is.</summary>
    public sealed record StopWaitingForAudio(string DeviceId, Guid SaveId, string DeviceName)
        : MenuAction($"Stop waiting for {DeviceName}");

    /// <summary>Quit the browser and start it again with the occlusion flags.</summary>
    public sealed record RelaunchBrowser(string Image, string DisplayName)
        : MenuAction($"Relaunch {DisplayName} unthrottled");

    /// <summary>
    /// Resume a process whose earlier resume was interrupted. The user
    /// decides: if another program also paused it, this releases that pause.
    /// </summary>
    public sealed record ResumeInterrupted(int Pid, long CreationTime, string Image)
        : MenuAction($"Resume {Image} (pid {Pid})");

    /// <summary>Open the log folder. Handled by the UI.</summary>
    public sealed record OpenLogs()
        : MenuAction("Open logs");
}

/// <summary>Read-only facts for the status section of the tray menu, read on demand.</summary>
public sealed record StatusDetails(
    bool? LidClosed,
    BatteryReading Battery,
    string? WifiSsid,
    WifiConnectivity Wifi,
    IReadOnlyList<string> FrozenApps,
    bool DockerFrozen,
    bool PowerSavingOn,
    string? LastNetworkGap,
    RecoveryAgentState RecoveryAgent);

/// <summary>Immutable snapshot the UI renders. A new one is raised on every change.</summary>
public sealed record SessionStatus(
    SessionPhase Phase,
    Session? Session,
    bool LidClosed,
    IReadOnlyList<MenuWarning> Warnings,
    string? StartBlockedReason,
    bool QuitBlocked)
{
    public static readonly SessionStatus Initial = new(SessionPhase.Idle, null, false, [], null, false);

    public bool IsActive => Phase is SessionPhase.Active or SessionPhase.Ending && Session is not null;
}

public enum StartOutcome
{
    Started,
    /// <summary>A session is already active; the request was ignored.</summary>
    AlreadyActive,
    /// <summary>Refused before anything changed; <see cref="StartResult.Message"/> says why.</summary>
    Refused,
    /// <summary>Something was applied and then undone; the message says what failed.</summary>
    Failed,
}

public sealed record StartResult(StartOutcome Outcome, string? Message = null);

public enum EndOutcome
{
    /// <summary>Journal clean, machine restored. Saved audio for an away device may remain and is named.</summary>
    Restored,
    /// <summary>Some entries could not be undone and stay journaled for a retry.</summary>
    Incomplete,
    /// <summary>No session was active.</summary>
    NotActive,
}

/// <summary>What the "Would freeze now" line in Settings shows.</summary>
public sealed record FreezePreview(IReadOnlyList<string> Explicit, IReadOnlyList<string> Automatic);

/// <summary>The engine as the UI sees it. Every method is safe to call from the UI thread.</summary>
public interface ISessionController
{
    SessionStatus Status { get; }

    /// <summary>Raised on a background thread; marshal to the UI thread before touching controls.</summary>
    event EventHandler<SessionStatus>? StatusChanged;

    Config Config { get; }

    Task<StartResult> StartAsync(TimeSpan duration);

    Task<bool> ExtendAsync(TimeSpan extra);

    Task<EndOutcome> EndAsync(EndReason reason);

    /// <summary>
    /// End any session and report whether quitting now abandons nothing.
    /// False leaves the app running; the status warnings say why.
    /// </summary>
    Task<bool> PrepareToQuitAsync();

    /// <summary>Validate, normalize and persist new settings. Returns an error message, or null.</summary>
    Task<string?> SaveConfigAsync(Config config);

    Task PerformAsync(MenuAction action);

    StatusDetails ReadDetails();

    FreezePreview PreviewFreeze(Config config);
}
