using System.Text;
using Insomnia.Core.Engine;
using Insomnia.Core.Logging;
using Insomnia.Core.Logic;
using Insomnia.Core.Model;
using Insomnia.Core.Platform;

namespace Insomnia.App.Demo;

/// <summary>Extra commands a controller offers in the tray menu (the demo's state switches).</summary>
internal interface IDemoControls
{
    /// <summary>Read each time the menu opens, so labels can follow state.</summary>
    IReadOnlyList<(string Label, Action Run)> DemoCommands { get; }
}

/// <summary>
/// A complete in-memory <see cref="ISessionController"/> for
/// <c>Insomnia.exe --demo</c>: real timers and deadlines, plausible delays,
/// fake status details and warnings, and switches in the tray menu to close
/// the lid, inject warnings and make the next start, end or quit fail. It
/// changes nothing on the machine. Status is raised on thread-pool threads,
/// as the real engine raises it.
/// </summary>
public sealed class DemoController : ISessionController, IDemoControls, IDisposable
{
    private static readonly string[] RunningApps =
    [
        "slack.exe", "Discord.exe", "Spotify.exe", "Notion.exe", "Figma.exe",
        "WINWORD.EXE", "Code.exe", "WindowsTerminal.exe", "chrome.exe", "Teams.exe",
    ];

    private readonly object _gate = new();
    private readonly INotifier? _notifier;
    private readonly Timer _deadline;
    private SessionStatus _status = SessionStatus.Initial;
    private Config _config = new();
    private int _warningSerial;
    private bool _failNextStart;
    private bool _incompleteNextEnd;
    private bool _blockNextQuit;

    public DemoController(INotifier? notifier = null)
    {
        _notifier = notifier;
        _deadline = new Timer(_ => OnDeadline(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public SessionStatus Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    public event EventHandler<SessionStatus>? StatusChanged;

    public Config Config
    {
        get
        {
            lock (_gate)
            {
                return _config;
            }
        }
    }

    public IReadOnlyList<(string Label, Action Run)> DemoCommands
    {
        get
        {
            var status = Status;
            return
            [
                (status.LidClosed ? "Open the lid" : "Close the lid", ToggleLid),
                ("Add a warning", () => AddWarning(WarningSeverity.Warning)),
                ("Add an error", () => AddWarning(WarningSeverity.Error)),
                ("Clear warnings", ClearWarnings),
                (status.StartBlockedReason is null ? "Block new sessions" : "Allow new sessions", ToggleStartBlocked),
                (_failNextStart ? "Next start succeeds" : "Fail the next start", () => _failNextStart = !_failNextStart),
                (_incompleteNextEnd ? "Next end restores everything" : "Leave the next end incomplete", () => _incompleteNextEnd = !_incompleteNextEnd),
                (_blockNextQuit ? "Let quit through" : "Block the next quit", () => _blockNextQuit = !_blockNextQuit),
                ("Send a notification", () => _notifier?.Notify("5 minutes left", "The session ends soon. Add time from the tray if the work isn't done.")),
            ];
        }
    }

    public async Task<StartResult> StartAsync(TimeSpan duration)
    {
        lock (_gate)
        {
            if (_status.IsActive)
            {
                return new StartResult(StartOutcome.AlreadyActive);
            }
            if (_status.StartBlockedReason is { } reason)
            {
                return new StartResult(StartOutcome.Refused, reason);
            }
        }
        Publish(s => s with { Phase = SessionPhase.Starting });
        await Task.Delay(450).ConfigureAwait(false);
        if (_failNextStart)
        {
            _failNextStart = false;
            Publish(s => s with { Phase = SessionPhase.Idle, Session = null });
            return new StartResult(StartOutcome.Failed, "Windows refused to change the lid-close action for the Balanced power plan, so nothing was changed. (Demo)");
        }
        Session session;
        lock (_gate)
        {
            session = SessionMath.NewSession(DateTimeOffset.UtcNow, duration, _config.MaxDuration);
        }
        Publish(s => s with { Phase = SessionPhase.Active, Session = session });
        ScheduleDeadline(session);
        Log.Info($"demo: session started until {session.EndsAt:O}");
        return new StartResult(StartOutcome.Started);
    }

    public async Task<bool> ExtendAsync(TimeSpan extra)
    {
        await Task.Delay(250).ConfigureAwait(false);
        Session? extended = null;
        lock (_gate)
        {
            if (_status is { Phase: SessionPhase.Active, Session: { } session })
            {
                extended = SessionMath.Extended(session, extra, DateTimeOffset.UtcNow, _config.MaxDuration);
            }
        }
        if (extended is null)
        {
            return false;
        }
        Publish(s => s with { Session = extended });
        ScheduleDeadline(extended);
        return true;
    }

    public Task<EndOutcome> EndAsync(EndReason reason) => EndCoreAsync(reason);

    public async Task<bool> PrepareToQuitAsync()
    {
        if (_blockNextQuit)
        {
            _blockNextQuit = false;
            Publish(s => s with
            {
                QuitBlocked = true,
                Warnings = [.. s.Warnings, new MenuWarning("demo-quit", WarningSeverity.Error,
                    "Slack is still paused and could not be resumed. Quitting now would leave it frozen. (Demo)",
                    new MenuAction.OpenLogs())],
            });
            return false;
        }
        if (Status.IsActive)
        {
            await EndCoreAsync(EndReason.Quit).ConfigureAwait(false);
        }
        return true;
    }

    public async Task<string?> SaveConfigAsync(Config config)
    {
        ArgumentNullException.ThrowIfNull(config);
        await Task.Delay(120).ConfigureAwait(false);
        if (Encoding.UTF8.GetByteCount(config.HotspotSsid) > 32)
        {
            return "A Wi-Fi network name is at most 32 bytes.";
        }
        if (config.TmuxCommand.Contains('"', StringComparison.Ordinal))
        {
            return "The tmux command can't contain quotes; it is split on spaces.";
        }
        var (normalized, changes) = config.Normalized();
        lock (_gate)
        {
            _config = normalized;
        }
        foreach (var change in changes)
        {
            Log.Info("demo config: " + change);
        }
        return null;
    }

    public async Task PerformAsync(MenuAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        await Task.Delay(400).ConfigureAwait(false);
        Log.Info($"demo: performed \"{action.Label}\"");
        Publish(s =>
        {
            var remaining = s.Warnings.Where(w => w.Action != action).ToList();
            return s with
            {
                QuitBlocked = s.QuitBlocked && remaining.Any(w => w.Id == "demo-quit"),
                Warnings = remaining,
            };
        });
    }

    public StatusDetails ReadDetails()
    {
        // Real reads take a moment (battery report, scheduled task query).
        Thread.Sleep(120);
        SessionStatus status;
        Config config;
        lock (_gate)
        {
            status = _status;
            config = _config;
        }
        var lidClosed = status.LidClosed && status.IsActive;
        var frozen = lidClosed ? PreviewFreeze(config).Explicit : [];
        return new StatusDetails(
            LidClosed: status.LidClosed,
            Battery: status.LidClosed
                ? new BatteryReading(true, false, 63, -9_800)
                : new BatteryReading(true, true, 82, 24_300),
            WifiSsid: "Studio 5G",
            Wifi: WifiConnectivity.Satisfied,
            FrozenApps: frozen,
            DockerFrozen: lidClosed && config.DockerRule,
            PowerSavingOn: lidClosed && config.PowerSavingOnLidClose,
            LastNetworkGap: "6 s at 2:14 PM, 1 tmux pane nudged",
            RecoveryAgent: RecoveryAgentState.Armed);
    }

    public FreezePreview PreviewFreeze(Config config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var listed = new HashSet<string>(config.FreezeList, StringComparer.OrdinalIgnoreCase);
        var agents = new HashSet<string>(config.AgentList, StringComparer.OrdinalIgnoreCase);
        // Display names, as the engine's preview reports them.
        var explicitNow = RunningApps.Where(listed.Contains).Select(DisplayName).ToList();
        var automatic = config.FreezeAllApps
            ? RunningApps.Where(a => !listed.Contains(a) && !agents.Contains(a)).Select(DisplayName).ToList()
            : [];
        return new FreezePreview(explicitNow, automatic);
    }

    public void Dispose() => _deadline.Dispose();

    private async Task<EndOutcome> EndCoreAsync(EndReason reason)
    {
        lock (_gate)
        {
            if (!_status.IsActive)
            {
                return EndOutcome.NotActive;
            }
        }
        _deadline.Change(Timeout.Infinite, Timeout.Infinite);
        Publish(s => s with { Phase = SessionPhase.Ending });
        await Task.Delay(700).ConfigureAwait(false);
        var incomplete = _incompleteNextEnd;
        _incompleteNextEnd = false;
        Publish(s => s with
        {
            Phase = SessionPhase.Idle,
            Session = null,
            Warnings = incomplete
                ? [.. s.Warnings, new MenuWarning("demo-restore", WarningSeverity.Error,
                    "The lid-close action for Balanced couldn't be put back. Insomnia retries every 30 seconds. (Demo)",
                    new MenuAction.OpenLogs())]
                : s.Warnings,
        });
        _notifier?.Notify("Session ended", EndReasonText.Describe(reason));
        Log.Info($"demo: session ended ({reason})");
        return incomplete ? EndOutcome.Incomplete : EndOutcome.Restored;
    }

    private void OnDeadline() => _ = EndCoreAsync(EndReason.Timer);

    private void ScheduleDeadline(Session session)
    {
        var due = session.EndsAt - DateTimeOffset.UtcNow;
        _deadline.Change(due > TimeSpan.Zero ? due : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
    }

    private void ToggleLid()
    {
        Publish(s => s with { LidClosed = !s.LidClosed });
    }

    private void ToggleStartBlocked()
    {
        Publish(s => s with
        {
            StartBlockedReason = s.StartBlockedReason is null
                ? "A session file was set aside after a crash. Open logs to see what happened, then try again. (Demo)"
                : null,
        });
    }

    private void AddWarning(WarningSeverity severity)
    {
        var serial = Interlocked.Increment(ref _warningSerial);
        var warning = severity == WarningSeverity.Error
            ? new MenuWarning($"demo-error-{serial}", severity,
                "Headphones (USB) are still muted from a lid close because they are unplugged. (Demo)",
                new MenuAction.StopWaitingForAudio("demo-device", Guid.NewGuid(), "Headphones (USB)"))
            : new MenuWarning($"demo-warning-{serial}", severity,
                "Chrome is running throttled, so background tabs slow down while the lid is closed. (Demo)",
                new MenuAction.RelaunchBrowser("chrome.exe", "Chrome"));
        Publish(s => s with { Warnings = [.. s.Warnings, warning] });
    }

    private void ClearWarnings() => Publish(s => s with { Warnings = [], QuitBlocked = false });

    private void Publish(Func<SessionStatus, SessionStatus> change)
    {
        SessionStatus next;
        lock (_gate)
        {
            next = change(_status);
            _status = next;
        }
        // Like the engine: raised off the UI thread.
        ThreadPool.QueueUserWorkItem(_ => StatusChanged?.Invoke(this, next));
    }

    private static string DisplayName(string image) =>
        Path.GetFileNameWithoutExtension(image) switch
        {
            "slack" => "Slack",
            "WINWORD" => "Microsoft Word",
            "Teams" => "Microsoft Teams",
            "Code" => "Visual Studio Code",
            "WindowsTerminal" => "Windows Terminal",
            "chrome" => "Google Chrome",
            var other => other,
        };
}
