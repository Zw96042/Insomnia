using Insomnia.Core.Logic;
using Insomnia.Core.Model;

namespace Insomnia.Core.Platform;

// Contracts between the platform-neutral engine and the Windows
// implementations in Insomnia.Windows. Every call is synchronous unless it
// names a timeout; the engine calls them from its own serial queue, never
// from the UI thread. Implementations must not throw for expected failures
// (access denied, a process that exited, a device that went away): they
// report them through their return values. An exception means a bug or an
// unexpected OS failure, and the engine treats it as a failed step.

/// <summary>
/// Holds the system awake (no idle sleep) for as long as it is acquired and
/// the process lives. Windows releases it automatically when the process
/// exits, so it needs no journal entry.
/// </summary>
public interface IKeepAwake
{
    /// <summary>Acquire or refresh the hold. <paramref name="reason"/> shows in <c>powercfg /requests</c>.</summary>
    bool Acquire(string reason);

    void Release();

    bool IsHeld { get; }
}

/// <summary>Lid-close action of the power schemes (<c>SUB_BUTTONS\LIDACTION</c>).</summary>
public interface ILidPolicy
{
    /// <summary>False on a machine without a lid switch: no lid policy applies.</summary>
    bool HasLid { get; }

    /// <summary>The active power scheme, or null when it cannot be read.</summary>
    Guid? ActiveScheme();

    /// <summary>
    /// The scheme's AC and DC lid-close action indexes. A scheme Windows
    /// reports as not existing reads <see cref="LidReadStatus.SchemeMissing"/>;
    /// any other failure reads <see cref="LidReadStatus.Failed"/>, which
    /// proves nothing about the scheme.
    /// </summary>
    LidRead Read(Guid scheme);

    /// <summary>
    /// Write both indexes and re-apply the active scheme so the change takes
    /// effect. Returns false when Windows refused either write.
    /// </summary>
    bool Write(Guid scheme, uint ac, uint dc);
}

public enum LidReadStatus
{
    Ok,
    /// <summary>Windows reports the scheme does not exist (deleted).</summary>
    SchemeMissing,
    /// <summary>The read failed for another reason; the scheme may exist.</summary>
    Failed,
}

/// <summary>A lid-close action read. <see cref="Ac"/> and <see cref="Dc"/> are meaningful only when <see cref="Status"/> is Ok.</summary>
public readonly record struct LidRead(LidReadStatus Status, uint Ac, uint Dc)
{
    public static LidRead Missing => new(LidReadStatus.SchemeMissing, 0, 0);

    public static LidRead Failure => new(LidReadStatus.Failed, 0, 0);
}

/// <summary>Windows power mode overlay ("Best power efficiency" and friends).</summary>
public interface IPowerMode
{
    /// <summary>False when this Windows build has no overlay API or the active scheme does not support overlays.</summary>
    bool IsSupported { get; }

    /// <summary>Which power source is in use now, or null when unknown.</summary>
    PowerSourceKind? CurrentSource();

    /// <summary>
    /// The overlay recorded for each source, read from the registry so the
    /// inactive source can be read too. A null element could not be read.
    /// </summary>
    (Guid? Ac, Guid? Dc) Read();

    /// <summary>Set the overlay for the current source. Returns false when Windows refused it.</summary>
    bool SetForCurrentSource(Guid overlay);
}

/// <summary>A battery reading.</summary>
/// <param name="HasBattery">True with a system battery, false on a desktop, null when that could not be told.</param>
/// <param name="OnAc">True on the charger, false on battery, null when unknown.</param>
/// <param name="Percent">0 to 100, or null when the level could not be read.</param>
/// <param name="ChargeRateMilliwatts">Positive while charging, negative while discharging, null when unknown.</param>
public sealed record BatteryReading(bool? HasBattery, bool? OnAc, int? Percent, int? ChargeRateMilliwatts);

public interface IPowerSource
{
    BatteryReading Read();
}

public interface IThermalSensor
{
    /// <summary>False when the machine exposes no thermal zone counters; the thermal rules then never fire.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Current thermal pressure. Implementations derive it from sustained
    /// passive-cooling limits; one sample of throttling is Fair, not Serious.
    /// </summary>
    ThermalState Read();
}

/// <summary>One running process, as seen by the freeze planner.</summary>
/// <param name="Pid">Process id.</param>
/// <param name="ParentPid">Parent process id as recorded at creation (may name an exited or reused process).</param>
/// <param name="CreationTime">FILETIME, UTC, 100 ns ticks. Identity together with the pid.</param>
/// <param name="Image">Executable file name, for example <c>slack.exe</c>.</param>
/// <param name="Path">Full executable path, or null when it could not be read.</param>
/// <param name="DisplayName">File description from the version resource, or the image name without extension.</param>
/// <param name="IsCurrentUser">Owned by the user running Insomnia, in the same session.</param>
/// <param name="HasVisibleWindow">Owns a visible, unowned top-level window that appears on the taskbar.</param>
public sealed record ProcessInfo(
    int Pid,
    int ParentPid,
    long CreationTime,
    string Image,
    string? Path,
    string DisplayName,
    bool IsCurrentUser,
    bool HasVisibleWindow);

public interface IProcessCatalog
{
    IReadOnlyList<ProcessInfo> Snapshot();

    /// <summary>Pid of the Insomnia process itself.</summary>
    int SelfPid { get; }

    /// <summary>Windows directory, for example <c>C:\Windows</c>; processes under it are never frozen.</summary>
    string WindowsDirectory { get; }
}

public enum SuspendOutcome
{
    Suspended,
    /// <summary>The process exited, or the pid now names a different process.</summary>
    Gone,
    AccessDenied,
    Failed,
}

public enum ResumeOutcome
{
    Resumed,
    /// <summary>The process exited, or the pid now names a different process.</summary>
    Gone,
    AccessDenied,
    Failed,
}

public enum ProcessState
{
    /// <summary>No process with that pid and creation time exists.</summary>
    Gone,
    /// <summary>It exists and at least one thread runs.</summary>
    Running,
    /// <summary>It exists and every thread is suspended.</summary>
    FullySuspended,
    /// <summary>The inspection failed.</summary>
    Unknown,
}

/// <summary>
/// Suspends and resumes whole processes. Every call verifies identity (pid
/// plus creation time) on the same handle it acts through, so a reused pid
/// is never touched. Suspending increments every thread's suspend count and
/// resuming decrements it once, so a process somebody else also suspended
/// stays suspended for them.
/// </summary>
public interface IProcessFreezer
{
    SuspendOutcome Suspend(int pid, long creationTime);

    ResumeOutcome Resume(int pid, long creationTime);

    /// <summary>
    /// Whether the process with that identity still exists, and whether every
    /// one of its threads is suspended. <see cref="ProcessState.Unknown"/>
    /// means the inspection failed and proves nothing either way.
    /// </summary>
    ProcessState Inspect(int pid, long creationTime);
}

/// <summary>Per-process power throttling (EcoQoS), <c>PROCESS_POWER_THROTTLING_STATE</c>.</summary>
public interface IThrottleControl
{
    /// <summary>The process's control and state masks, or null when it is gone or unreadable.</summary>
    (uint ControlMask, uint StateMask)? Get(int pid, long creationTime);

    bool Set(int pid, long creationTime, uint controlMask, uint stateMask);
}

/// <summary>An audio output endpoint.</summary>
public sealed record AudioEndpoint(string Id, string Name, float Volume, bool Muted);

public enum AudioDeviceStatus
{
    /// <summary>Present and active.</summary>
    Connected,
    /// <summary>Unplugged, disabled, not present, or no longer known to Windows: confirmed absent.</summary>
    Away,
    /// <summary>The query failed; the device may be connected.</summary>
    Unknown,
}

public interface IAudioControl
{
    /// <summary>The default console render endpoint, or null when there is none.</summary>
    AudioEndpoint? DefaultOutput();

    AudioDeviceStatus Status(string deviceId);

    /// <summary>The endpoint's current values, or null when it is not connected.</summary>
    AudioEndpoint? Read(string deviceId);

    bool SetMute(string deviceId, bool muted);

    bool SetVolume(string deviceId, float volume);

    /// <summary>Raised on any thread when an endpoint is added, removed or changes state, or the default changes.</summary>
    event EventHandler? DevicesChanged;
}

/// <summary>Best-effort display power. Nothing here is persistent, so nothing is journaled.</summary>
public interface IDisplayControl
{
    void RequestDisplaysOff();

    void WakeDisplays();
}

public enum DockerProbeResult
{
    /// <summary><c>docker ps -q</c> printed nothing and exited 0.</summary>
    NoContainers,
    /// <summary>At least one container is running.</summary>
    Busy,
    /// <summary>The probe failed, timed out or was cancelled.</summary>
    Failed,
}

public interface IDocker
{
    /// <summary>Pids of Docker Desktop's own processes (the front end and its backend services), with identity.</summary>
    IReadOnlyList<ProcessInfo> DesktopProcesses();

    /// <summary>Runs <c>docker ps -q</c> with the timeout. Cancellation kills the probe and returns Failed.</summary>
    Task<DockerProbeResult> ProbeAsync(TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>User-facing notifications (Windows toasts).</summary>
public interface INotifier
{
    void Notify(string title, string body);
}

public enum RecoveryAgentState
{
    /// <summary>Registered, enabled, and pointing at this executable.</summary>
    Armed,
    Missing,
    Disabled,
    /// <summary>Registered for another executable path or with other arguments.</summary>
    Mismatched,
    /// <summary>The task could not be queried.</summary>
    Unknown,
}

/// <summary>The per-user scheduled task that runs <c>Insomnia.exe --recover</c> at logon and every minute.</summary>
public interface IRecoveryAgent
{
    RecoveryAgentState Check();

    /// <summary>Register or repair the task for this executable. Returns the state after the attempt and an error message when not armed.</summary>
    (RecoveryAgentState State, string? Error) Arm();

    /// <summary>Remove the task (uninstall only).</summary>
    bool Remove();
}

public enum StartupState
{
    Enabled,
    /// <summary>Registered but turned off in Task Manager's Startup apps.</summary>
    DisabledByUser,
    NotRegistered,
}

public interface IStartupRegistration
{
    StartupState Read();

    /// <summary>Register or unregister. Returns an error message, or null on success.</summary>
    string? Set(bool enabled);
}

/// <summary>Per-user secret storage (Windows Credential Manager).</summary>
public interface ICredentialStore
{
    /// <summary>The hotspot password for the SSID, or null when none is stored. Throws nothing.</summary>
    string? ReadHotspotPassword(string ssid);

    /// <summary>Returns an error message, or null on success.</summary>
    string? WriteHotspotPassword(string ssid, string password);

    /// <summary>Returns an error message, or null on success (including when nothing was stored).</summary>
    string? DeleteHotspotPassword(string ssid);
}

public enum WifiConnectivity
{
    /// <summary>The Wi-Fi adapter has a connection with a usable route.</summary>
    Satisfied,
    Unsatisfied,
    /// <summary>No Wi-Fi adapter.</summary>
    NoAdapter,
}

public enum HotspotJoinResult
{
    Joined,
    NotFound,
    AccessDenied,
    BadPassword,
    Failed,
}

public interface IWifi
{
    WifiConnectivity Connectivity();

    /// <summary>SSID of the current Wi-Fi connection, or null.</summary>
    string? CurrentSsid();

    /// <summary>Raised on any thread when connectivity may have changed.</summary>
    event EventHandler? ConnectivityChanged;

    /// <summary>
    /// Ask Windows for location access, which it requires before Wi-Fi scans
    /// return SSIDs. Returns true when access is allowed.
    /// </summary>
    Task<bool> RequestAccessAsync();

    /// <summary>Scan for the SSID and join it with the password. The password never appears in process arguments.</summary>
    Task<HotspotJoinResult> JoinAsync(string ssid, string password, CancellationToken cancellationToken);
}

/// <summary>Result of running an external command.</summary>
public sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut);

/// <summary>Runs external commands with a timeout (tmux through WSL, docker).</summary>
public interface ICommandRunner
{
    Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>A Chromium-family browser main process and whether it runs throttled.</summary>
public sealed record BrowserInstance(string Image, string DisplayName, int Pid, long CreationTime, bool Unthrottled);

public enum BrowserRelaunchResult
{
    Relaunched,
    NotRunning,
    /// <summary>The command line could not be read, so nothing was relaunched.</summary>
    ArgumentsUnreadable,
    /// <summary>The browser was still running when the wait ended (the user did not quit it); nothing was launched.</summary>
    StillRunning,
    /// <summary>It exited, but the relaunch did not start.</summary>
    LaunchFailed,
    Cancelled,
}

/// <summary>
/// Chromium throttles windows Windows reports as occluded, which is every
/// window once the lid is closed with no external display.
/// </summary>
public interface IBrowserControl
{
    IReadOnlyList<BrowserInstance> Scan();

    /// <summary>
    /// Read every instance's command line, wait up to <paramref name="wait"/>
    /// for the user to quit the browser from its own menu, then start it
    /// again with its original arguments plus
    /// <c>--disable-backgrounding-occluded-windows</c>,
    /// <c>--disable-renderer-backgrounding</c> and
    /// <c>--restore-last-session</c>. Insomnia never closes the browser
    /// itself: the browser's own exit asks about unsaved pages, and saves
    /// the windows the relaunch restores.
    /// </summary>
    Task<BrowserRelaunchResult> RelaunchAfterExitAsync(string image, TimeSpan wait, CancellationToken cancellationToken);
}

/// <summary>
/// Power events from the OS. Windows delivers them to a window
/// (<c>WM_POWERBROADCAST</c>); implementations raise these on any thread.
/// </summary>
public interface IPowerEvents
{
    /// <summary>The last lid state Windows reported: true closed, false open, null before the first report or without a lid.</summary>
    bool? LidClosed { get; }

    /// <summary>Raised with true for closed and false for open, undebounced.</summary>
    event EventHandler<bool>? LidChanged;

    /// <summary>The AC/DC source or the battery percentage changed.</summary>
    event EventHandler? PowerSourceChanged;

    /// <summary>The active power scheme changed.</summary>
    event EventHandler? ActiveSchemeChanged;

    /// <summary>The system resumed from sleep or hibernation.</summary>
    event EventHandler? Resumed;
}

/// <summary>Everything the engine needs from the OS, in one place.</summary>
public sealed record PlatformServices(
    IPowerEvents PowerEvents,
    IKeepAwake KeepAwake,
    ILidPolicy LidPolicy,
    IPowerMode PowerMode,
    IPowerSource PowerSource,
    IThermalSensor Thermal,
    IProcessCatalog Processes,
    IProcessFreezer Freezer,
    IThrottleControl Throttle,
    IAudioControl Audio,
    IDisplayControl Display,
    IDocker Docker,
    INotifier Notifier,
    IRecoveryAgent RecoveryAgent,
    IStartupRegistration Startup,
    ICredentialStore Credentials,
    IWifi Wifi,
    ICommandRunner Commands,
    IBrowserControl Browsers);
