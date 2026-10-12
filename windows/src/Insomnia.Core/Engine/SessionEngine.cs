using System.Globalization;
using Insomnia.Core.Logging;
using Insomnia.Core.Logic;
using Insomnia.Core.Model;
using Insomnia.Core.Platform;
using Insomnia.Core.Store;

namespace Insomnia.Core.Engine;

/// <summary>Timings, overridable in tests.</summary>
public sealed record EngineOptions
{
    /// <summary>Lid changes are acted on once they have held this long.</summary>
    public TimeSpan LidDebounce { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Delay before a refused or incomplete transaction is tried again.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long an ordinary transaction waits for the recovery lock.</summary>
    public TimeSpan LockWait { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long a session end waits for the recovery lock.</summary>
    public TimeSpan EndLockWait { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Limit for each <c>docker ps</c> probe.</summary>
    public TimeSpan DockerProbeTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Thermal poll, battery re-read and session file check during a session.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The "minutes left" reminder fires this long before the end.</summary>
    public TimeSpan ReminderLead { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Lock wait at launch reconcile, which runs again after <see cref="RetryDelay"/> when busy.</summary>
    public TimeSpan ReconcileLockWait { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// The session engine: starts, extends and ends sessions, runs the lid-close
/// actions and the battery and thermal rules, reconciles at launch, and
/// retries anything left incomplete. Every transaction runs on one serial
/// queue, holds the recovery lock, and reads the journal from disk first, so
/// the journal, not memory, decides what is undone.
/// </summary>
public sealed class SessionEngine : ISessionController, IDisposable
{
    private readonly PlatformServices _os;
    private readonly JournalStore _store;
    private readonly RecoveryLock _lock;
    private readonly EngineOptions _options;
    private readonly TimeProvider _time;
    private readonly Restorer _restorer;
    private readonly SerialQueue _queue = new();
    private readonly NetworkFailover _failover;

    // Guarded by _gate: read by the UI thread, written by the queue and timers.
    private readonly object _gate = new();
    private Config _config;
    private Session? _session;
    private SessionPhase _phase = SessionPhase.Idle;
    private bool _lidClosed;
    private readonly Dictionary<string, MenuWarning> _warnings = new(StringComparer.Ordinal);
    private string? _startBlocked;
    private IReadOnlyList<string> _frozenApps = [];
    private bool _dockerFrozen;
    private bool _powerSavingOn;
    private SessionStatus _status = SessionStatus.Initial;

    // Owned by the queue.
    private RuntimeState? _owedState;

    // The journal text this process last read or wrote (null: no file), and
    // the text that was on disk when the owed write failed. An owed write is
    // replayed only while the disk still holds that text: once another
    // process (the recovery agent) has changed the journal, the cached state
    // is stale and replaying it could redo an undo.
    private string? _lastDisk;
    private string? _owedBase;
    private int _batteryMisses;
    private PowerSavingCause? _lastCause;
    private bool _displaysOff;

    private bool _reconciled;
    private bool _sessionRemovalOwed;
    private int _retryCount;

    // Lid sequencing: numbered as events arrive, before their actions queue.
    private long _lidSeq;
    private CancellationTokenSource _lidCts = new();
    private bool? _rawLid;
    private ITimer? _lidDebounce;

    private ITimer? _deadlineTimer;
    private ITimer? _reminderTimer;
    private ITimer? _pollTimer;
    private ITimer? _retryTimer;
    private bool _disposed;

    public SessionEngine(PlatformServices os, JournalStore store, RecoveryLock recoveryLock, EngineOptions? options = null, TimeProvider? time = null)
    {
        _os = os;
        _store = store;
        _lock = recoveryLock;
        _options = options ?? new EngineOptions();
        _time = time ?? TimeProvider.System;
        _restorer = new Restorer(os);
        _config = LoadConfig();
        _failover = new NetworkFailover(os, _time, () => Config, new RotatingLog(store.Paths.HandoffsLog), OnFailoverUpdate);

        _os.PowerEvents.LidChanged += OnLidChanged;
        _os.PowerEvents.PowerSourceChanged += OnPowerSourceChanged;
        _os.PowerEvents.ActiveSchemeChanged += OnActiveSchemeChanged;
        _os.PowerEvents.Resumed += OnResumed;
        _os.Audio.DevicesChanged += OnAudioDevicesChanged;
    }

    public event EventHandler<SessionStatus>? StatusChanged;

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

    private DateTimeOffset Now => _time.GetUtcNow();

    /// <summary>Completes once every transaction queued so far has run. For tests.</summary>
    internal Task IdleAsync() => _queue.Run(() => Task.CompletedTask);

    // MARK: Launch

    /// <summary>
    /// Reconcile at launch: restore a journal left by a session that is gone,
    /// or resume a session that is still valid. Retries after
    /// <see cref="EngineOptions.RetryDelay"/> while the lock is busy or the
    /// journal unreadable.
    /// </summary>
    public Task InitializeAsync() => _queue.Run(ReconcileCore);

    private async Task ReconcileCore()
    {
        await Task.CompletedTask.ConfigureAwait(false);
        if (_reconciled)
        {
            return;
        }
        var opened = OpenTxn(_options.ReconcileLockWait);
        if (opened.Txn is null)
        {
            Log.Warn($"reconcile refused: {opened.Refusal}");
            ScheduleRetry();
            return;
        }
        using var txn = opened.Txn;
        var read = _store.ReadSession();
        if (read is SessionRead.Inaccessible inaccessible)
        {
            // It may hold a live session: neither resume nor restore yet.
            Log.Warn($"reconcile postponed: {inaccessible.Reason}");
            SetWarning("session-read", WarningSeverity.Warning, $"{inaccessible.Reason}. Insomnia tries again in {(int)_options.RetryDelay.TotalSeconds} s.");
            Publish();
            ScheduleRetry();
            return;
        }
        ClearWarning("session-read");
        _reconciled = true;
        ClearWarning("lock-busy");
        HealLaunchAtLogin();

        Session? valid = null;
        switch (read)
        {
            case SessionRead.Valid v when !v.Session.IsExpired(Now):
                valid = v.Session;
                break;
            case SessionRead.Valid v:
                Log.Info($"reconcile: session ended at {Json.FormatDate(v.Session.EndsAt)} while Insomnia was not running");
                break;
            case SessionRead.Unreadable u:
                SetAsideUnreadableSession(u.Reason);
                break;
        }

        if (valid is null)
        {
            var report = _restorer.RestoreAll(txn.State, txn.SaveOrThrow);
            if (read is SessionRead.Valid)
            {
                TryRemoveSession();
            }
            UpdateSessionFacts(txn.State);
            ApplyRestoreOutcome(report, announce: report.Restored.Count > 0 ? "Restored settings left by the last session" : null);
            Publish();
            return;
        }

        // A valid session survives the relaunch: arm the agent before taking
        // the machine back.
        var (agent, agentError) = _os.RecoveryAgent.Arm();
        if (agent != RecoveryAgentState.Armed)
        {
            Log.Error($"reconcile: recovery agent not armed ({agent}): {agentError}");
            SetWarning("agent", WarningSeverity.Error, $"The recovery agent could not be registered: {agentError ?? agent.ToString()}");
            var report = _restorer.RestoreAll(txn.State, txn.SaveOrThrow);
            TryRemoveSession();
            ApplyRestoreOutcome(report, announce: null);
            _os.Notifier.Notify("Session ended", EndReasonText.Describe(EndReason.RecoveryUnavailable));
            Publish();
            return;
        }
        ClearWarning("agent");

        if (!ApplyLidPolicy(txn) || !_os.KeepAwake.Acquire(KeepAwakeReason(valid)))
        {
            var report = _restorer.RestoreAll(txn.State, txn.SaveOrThrow);
            TryRemoveSession();
            _os.KeepAwake.Release();
            ApplyRestoreOutcome(report, announce: null);
            _os.Notifier.Notify("Session ended", EndReasonText.Describe(EndReason.RecoveryUnavailable));
            Publish();
            return;
        }

        var lidClosed = _os.PowerEvents.LidClosed == true;
        lock (_gate)
        {
            _session = valid;
            _phase = SessionPhase.Active;
            _lidClosed = lidClosed;
        }
        Log.Info($"reconcile: resumed session ending {Json.FormatDate(valid.EndsAt)}{(lidClosed ? " with the lid closed" : "")}");
        if (!lidClosed)
        {
            // The lid is open: undo whatever an earlier close left in place.
            var report = new RestoreReport();
            report.Merge(_restorer.RestoreProcesses(txn.State, txn.SaveOrThrow, onlyGroup: null));
            report.Merge(_restorer.RestoreAudio(txn.State, txn.SaveOrThrow));
            ApplyRestoreOutcome(report, announce: null);
        }
        var end = EvaluateFloors(txn, countMiss: true);
        UpdateSessionFacts(txn.State);
        StartSessionTimers(valid);
        _failover.Start();
        Publish();
        if (end is { } reason)
        {
            _ = EndAsync(reason);
        }
    }

    /// <summary>
    /// With launch at login on but no registration on file (the value was
    /// removed outside Insomnia, or the app moved), register again. A
    /// registration turned off in Task Manager is the user's choice and is
    /// left alone.
    /// </summary>
    private void HealLaunchAtLogin()
    {
        if (!Config.LaunchAtLogin || _os.Startup.Read() != StartupState.NotRegistered)
        {
            return;
        }
        var error = _os.Startup.Set(true);
        Log.Info(error is null ? "launch at login registered again" : $"launch at login could not be registered again: {error}");
    }

    private void SetAsideUnreadableSession(string reason)
    {
        Log.Error($"reconcile: {reason}");
        try
        {
            var aside = _store.SetAsideSession(Now);
            Log.Info($"unreadable session.json moved to {aside}");
            _os.Notifier.Notify("Session file could not be read", $"Insomnia treated it as ended and moved it to {aside}.");
            ClearWarning("session-aside");
            lock (_gate)
            {
                _startBlocked = null;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Error("could not move the unreadable session.json aside", e);
            var text = $"session.json could not be read and could not be moved aside. Remove {_store.Paths.Session} or move it out of the folder.";
            SetWarning("session-aside", WarningSeverity.Error, text, new MenuAction.OpenLogs());
            lock (_gate)
            {
                _startBlocked = text;
            }
        }
    }

    // MARK: Start, extend

    public Task<StartResult> StartAsync(TimeSpan duration) => _queue.Run(async () =>
    {
        await Task.CompletedTask.ConfigureAwait(false);
        lock (_gate)
        {
            if (_session is not null)
            {
                return new StartResult(StartOutcome.AlreadyActive);
            }
            if (_startBlocked is { } blocked)
            {
                return new StartResult(StartOutcome.Refused, blocked);
            }
            _phase = SessionPhase.Starting;
        }
        Publish();
        try
        {
            return StartCore(duration);
        }
        finally
        {
            lock (_gate)
            {
                if (_phase == SessionPhase.Starting)
                {
                    _phase = _session is null ? SessionPhase.Idle : SessionPhase.Active;
                }
            }
            Publish();
        }
    });

    private StartResult StartCore(TimeSpan duration)
    {
        if (!_reconciled)
        {
            return new StartResult(StartOutcome.Refused, "Insomnia is still checking the last session. Try again in a moment.");
        }
        var opened = OpenTxn(_options.LockWait);
        if (opened.Txn is null)
        {
            return new StartResult(StartOutcome.Refused, opened.Refusal);
        }
        using var txn = opened.Txn;

        switch (_store.ReadSession())
        {
            case SessionRead.Unreadable unreadable:
                return new StartResult(StartOutcome.Refused, $"{unreadable.Reason}. Remove {_store.Paths.Session} or move it out of the folder.");
            case SessionRead.Inaccessible inaccessible:
                return new StartResult(StartOutcome.Refused, $"{inaccessible.Reason}. Try again in a moment.");
        }

        var (agent, agentError) = _os.RecoveryAgent.Arm();
        if (agent != RecoveryAgentState.Armed)
        {
            Log.Error($"start refused: recovery agent {agent}: {agentError}");
            SetWarning("agent", WarningSeverity.Error, $"The recovery agent could not be registered: {agentError ?? agent.ToString()}");
            return new StartResult(StartOutcome.Refused, $"The recovery agent could not be registered, so no session was started. {agentError}".Trim());
        }
        ClearWarning("agent");

        var config = Config;
        var session = SessionMath.NewSession(Now, duration, config.MaxDuration);
        try
        {
            _store.WriteSession(session);
            _sessionRemovalOwed = false;
            ClearWarning("session-delete");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Error("start refused: session.json could not be written", e);
            return new StartResult(StartOutcome.Refused, $"session.json could not be written: {e.Message}");
        }

        if (!ApplyLidPolicy(txn))
        {
            return Unwind(txn, "The lid-close action could not be changed, so the session did not start.");
        }
        if (!_os.KeepAwake.Acquire(KeepAwakeReason(session)))
        {
            return Unwind(txn, "Windows refused the keep-awake request, so the session did not start.");
        }
        if (config.DisableThrottlingForAgents)
        {
            ExemptAgents(txn, config);
        }

        var lidClosed = _os.PowerEvents.LidClosed == true;
        lock (_gate)
        {
            _session = session;
            _phase = SessionPhase.Active;
            _lidClosed = lidClosed;
            _warnings.Keys.Where(k => k.StartsWith("browser-result:", StringComparison.Ordinal)).ToList().ForEach(k => _warnings.Remove(k));
        }
        _batteryMisses = 0;
        _lastCause = null;
        _displaysOff = false;
        Log.Info($"session started: {SessionMath.FormatRemaining(session.EndsAt - session.StartedAt)}, ends {Json.FormatDate(session.EndsAt)}{(lidClosed ? " (lid closed)" : "")}");

        var end = EvaluateFloors(txn, countMiss: true);
        UpdateSessionFacts(txn.State);
        StartSessionTimers(session);
        ScanBrowsers();
        _failover.Start();
        Publish();
        if (end is { } reason)
        {
            _ = EndAsync(reason);
        }
        return new StartResult(StartOutcome.Started);
    }

    /// <summary>Undo a start that got part of the way, from the journal, like an end.</summary>
    private StartResult Unwind(Txn txn, string message)
    {
        Log.Error($"start failed: {message}");
        var report = _restorer.RestoreAll(txn.State, txn.SaveOrThrow);
        _os.KeepAwake.Release();
        TryRemoveSession();
        ApplyRestoreOutcome(report, announce: null);
        return new StartResult(StartOutcome.Failed, report.IsComplete ? message : $"{message} Some changes could not be undone yet; Insomnia will retry.");
    }

    public Task<bool> ExtendAsync(TimeSpan extra) => _queue.Run(async () =>
    {
        await Task.CompletedTask.ConfigureAwait(false);
        Session? current;
        lock (_gate)
        {
            current = _session;
        }
        if (current is null)
        {
            return false;
        }
        var opened = OpenTxn(_options.LockWait);
        if (opened.Txn is null)
        {
            SetWarning("extend", WarningSeverity.Warning, $"The session could not be extended: {opened.Refusal}");
            Publish();
            return false;
        }
        using var txn = opened.Txn;
        // The recovery agent may have ended this session while the extend
        // waited for the lock. Extend only the session still on disk; one
        // that merely passed its deadline restarts from now, as upstream.
        var onDiskRead = _store.ReadSession();
        if (onDiskRead is SessionRead.Inaccessible inaccessible)
        {
            SetWarning("extend", WarningSeverity.Warning, $"The session could not be extended right now: {inaccessible.Reason}.");
            Publish();
            return false;
        }
        if (onDiskRead is not SessionRead.Valid onDisk || onDisk.Session != current)
        {
            Log.Warn("extend refused: the session on disk is gone or not this one");
            SetWarning("extend", WarningSeverity.Warning, "The session had already ended, so it was not extended. Start a new one.");
            Publish();
            _ = EndAsync(EndReason.Recovery);
            return false;
        }
        if (current.IsExpired(Now))
        {
            // Past its deadline the session may have been restored already
            // (a recovery run that could not remove the file): extending it
            // restarts it, so redo what a start does before the deadline moves.
            var (agent, agentError) = _os.RecoveryAgent.Arm();
            if (agent != RecoveryAgentState.Armed || !ApplyLidPolicy(txn) || !_os.KeepAwake.Acquire(KeepAwakeReason(current)))
            {
                Log.Warn($"extend of an expired session refused: {agentError ?? "the lid action or keep-awake could not be applied"}");
                SetWarning("extend", WarningSeverity.Warning, "The session had already ended and could not be restarted. Start a new one.");
                Publish();
                _ = EndAsync(EndReason.Timer);
                return false;
            }
        }
        var extended = SessionMath.Extended(current, extra, Now, Config.MaxDuration);
        try
        {
            _store.WriteSession(extended);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Error("extend failed: session.json could not be written", e);
            SetWarning("extend", WarningSeverity.Warning, $"The session could not be extended: {e.Message}");
            Publish();
            return false;
        }
        ClearWarning("extend");
        _os.KeepAwake.Acquire(KeepAwakeReason(extended));
        lock (_gate)
        {
            _session = extended;
        }
        Log.Info($"session extended to {Json.FormatDate(extended.EndsAt)}");
        StartSessionTimers(extended);
        Publish();
        return true;
    });

    // MARK: End

    public Task<EndOutcome> EndAsync(EndReason reason) => _queue.Run(async () =>
    {
        await Task.CompletedTask.ConfigureAwait(false);
        return EndCore(reason);
    });

    private EndOutcome EndCore(EndReason reason)
    {
        Session? session;
        lock (_gate)
        {
            session = _session;
            if (session is null)
            {
                return EndOutcome.NotActive;
            }
            if (reason == EndReason.Timer && !session.IsExpired(Now))
            {
                // Queued before an extension that ran first: this deadline
                // no longer applies.
                session = null;
            }
            else
            {
                _phase = SessionPhase.Ending;
            }
        }
        if (session is null)
        {
            Log.Info("a timer end was queued for a deadline an extension replaced; ignored");
            StartSessionTimers(Status.Session!);
            return EndOutcome.NotActive;
        }
        Publish();

        // Make any queued lid close stale and stop a Docker probe and a
        // browser relaunch's wait.
        CancelLidWork();
        lock (_gate)
        {
            _browserWait.Cancel();
            _browserWait.Dispose();
            _browserWait = new CancellationTokenSource();
        }
        _failover.Stop();
        if (_displaysOff)
        {
            _os.Display.WakeDisplays();
            _displaysOff = false;
        }

        var opened = OpenTxn(_options.EndLockWait);
        if (opened.Txn is null)
        {
            Log.Warn($"end ({reason}) refused: {opened.Refusal}");
            SetWarning("end", WarningSeverity.Error, $"The session could not end yet: {opened.Refusal} Insomnia retries in {(int)_options.RetryDelay.TotalSeconds} s.");
            lock (_gate)
            {
                _phase = SessionPhase.Active;
            }
            ScheduleRetry();
            Publish();
            return EndOutcome.Incomplete;
        }
        RestoreReport report;
        bool removed;
        using (var txn = opened.Txn)
        {
            report = _restorer.RestoreAll(txn.State, txn.SaveOrThrow);
            removed = TryRemoveSession();
            UpdateSessionFacts(txn.State);
        }
        if (!removed)
        {
            report.Failures.Add("session.json could not be removed.");
        }
        _os.KeepAwake.Release();
        StopSessionTimers();
        lock (_gate)
        {
            _session = null;
            _phase = SessionPhase.Idle;
            _lidClosed = false;
        }
        ClearWarning("end");
        ApplyRestoreOutcome(report, announce: null);
        Log.Info($"session ended ({reason}): {(report.IsComplete ? "restored" : "incomplete")}");
        NotifyEnd(reason, report);
        Publish();
        return report.IsComplete ? EndOutcome.Restored : EndOutcome.Incomplete;
    }

    private void NotifyEnd(EndReason reason, RestoreReport report)
    {
        var body = EndReasonText.Describe(reason);
        if (report.WaitingAudio.Count > 0)
        {
            body += $" Still muted until it reconnects: {string.Join(", ", report.WaitingAudio.Select(a => a.Name ?? a.DeviceId))}.";
        }
        if (!report.IsComplete)
        {
            body += " Some settings could not be restored yet; Insomnia and its recovery agent keep trying. See the menu for details.";
            _os.Notifier.Notify("Restore incomplete", body);
            return;
        }
        if (reason != EndReason.Quit)
        {
            _os.Notifier.Notify("Session ended", body);
        }
    }

    public async Task<bool> PrepareToQuitAsync()
    {
        if (Status.Session is not null)
        {
            await EndAsync(EndReason.Quit).ConfigureAwait(false);
        }
        return await _queue.Run(async () =>
        {
            await Task.CompletedTask.ConfigureAwait(false);
            if (Status.Session is not null)
            {
                SetWarning("quit", WarningSeverity.Error, "Insomnia could not end the session, so it is still running. See the lines above.");
                Publish();
                return false;
            }
            if (_sessionRemovalOwed && !TryRemoveSession())
            {
                // The ended session is still on disk with a future deadline: a
                // relaunch would resume it. Stay until it is removed.
                SetWarning("quit", WarningSeverity.Error, $"The ended session could not be removed from disk yet, so Insomnia stays open to retry. Delete {_store.Paths.Session} to quit now.");
                Publish();
                return false;
            }
            // The recovery agent can undo everything the app can, so quitting
            // with entries left is safe only while it is armed.
            var dirty = _store.ReadState() is not StateRead.Valid { State.IsCleanExceptWaitingAudio: true } || _owedState is not null;
            if (dirty && _os.RecoveryAgent.Check() != RecoveryAgentState.Armed)
            {
                SetWarning("quit", WarningSeverity.Error, "Some settings are not restored yet and the recovery agent is not registered, so Insomnia stays open to retry.");
                Publish();
                return false;
            }
            ClearWarning("quit");
            Log.Info(dirty ? "quitting with entries left for the recovery agent" : "quitting");
            return true;
        }).ConfigureAwait(false);
    }

    // MARK: Lid

    private void OnLidChanged(object? sender, bool closed)
    {
        lock (_gate)
        {
            _rawLid = closed;
            _lidDebounce?.Dispose();
            _lidDebounce = _time.CreateTimer(_ => LidSettled(), null, _options.LidDebounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void LidSettled()
    {
        bool closed;
        long seq;
        CancellationToken token;
        lock (_gate)
        {
            if (_rawLid is not { } raw || _session is null || _disposed)
            {
                if (_rawLid is { } r)
                {
                    _lidClosed = r && _session is not null;
                }
                return;
            }
            closed = raw;
            seq = ++_lidSeq;
            if (!closed)
            {
                // An open makes every earlier close stale and stops a probe now,
                // not when the open's own turn comes.
                _lidCts.Cancel();
                _lidCts.Dispose();
                _lidCts = new CancellationTokenSource();
            }
            token = _lidCts.Token;
        }
        Log.Info($"lid {(closed ? "closed" : "opened")} (event {seq})");
        _ = _queue.Run(() => closed ? LidCloseCore(seq, token) : LidOpenCore(seq));
    }

    private void CancelLidWork()
    {
        lock (_gate)
        {
            _lidSeq++;
            _lidCts.Cancel();
            _lidCts.Dispose();
            _lidCts = new CancellationTokenSource();
        }
    }

    private bool IsStale(long seq)
    {
        lock (_gate)
        {
            return seq != _lidSeq || _session is null;
        }
    }

    private async Task LidCloseCore(long seq, CancellationToken token)
    {
        if (IsStale(seq))
        {
            Log.Info($"lid close (event {seq}) is stale; skipped");
            return;
        }
        var opened = OpenTxn(_options.LockWait);
        if (opened.Txn is null)
        {
            Log.Warn($"lid close refused: {opened.Refusal}");
            SetWarning("lid", WarningSeverity.Warning, $"Lid-close actions did not run: {opened.Refusal}");
            Publish();
            return;
        }
        using var txn = opened.Txn;
        ClearWarning("lid");
        var config = Config;
        lock (_gate)
        {
            _lidClosed = true;
        }
        Publish();

        if (config.MuteOnLidClose)
        {
            MuteDefaultOutput(txn);
        }
        FreezeApps(txn, config);
        if (config.DockerRule)
        {
            await FreezeDockerIfIdle(txn, token).ConfigureAwait(false);
        }
        if (IsStale(seq))
        {
            UpdateSessionFacts(txn.State);
            Publish();
            return;
        }
        var end = EvaluateFloors(txn, countMiss: false);
        if (config.DisplaysOffOnLidClose)
        {
            _os.Display.RequestDisplaysOff();
            _displaysOff = true;
        }
        UpdateSessionFacts(txn.State);
        Publish();
        if (end is { } reason)
        {
            _ = EndAsync(reason);
        }
    }

    private async Task LidOpenCore(long seq)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        if (IsStale(seq))
        {
            return;
        }
        lock (_gate)
        {
            _lidClosed = false;
        }
        if (_displaysOff)
        {
            _os.Display.WakeDisplays();
            _displaysOff = false;
        }
        var opened = OpenTxn(_options.LockWait);
        if (opened.Txn is null)
        {
            Log.Warn($"lid open refused: {opened.Refusal}");
            SetWarning("lid", WarningSeverity.Warning, $"Lid-open restore did not run: {opened.Refusal} Insomnia retries in {(int)_options.RetryDelay.TotalSeconds} s.");
            ScheduleRetry();
            Publish();
            return;
        }
        using var txn = opened.Txn;
        ClearWarning("lid");
        var report = new RestoreReport();
        report.Merge(_restorer.RestoreProcesses(txn.State, txn.SaveOrThrow, onlyGroup: null));
        report.Merge(_restorer.RestoreAudio(txn.State, txn.SaveOrThrow));
        var end = EvaluateFloors(txn, countMiss: false);
        UpdateSessionFacts(txn.State);
        ApplyRestoreOutcome(report, announce: null);
        Publish();
        if (end is { } reason)
        {
            _ = EndAsync(reason);
        }
    }

    private void MuteDefaultOutput(Txn txn)
    {
        var device = _os.Audio.DefaultOutput();
        if (device is null)
        {
            return;
        }
        if (device.Muted)
        {
            Log.Info($"{device.Name} is already muted; nothing saved");
            return;
        }
        if (txn.State.SavedAudioOutputs.Any(a => a.DeviceId == device.Id))
        {
            _os.Audio.SetMute(device.Id, true);
            return;
        }
        var entry = new SavedAudioOutput(device.Id, device.Name, device.Volume, device.Muted, Guid.NewGuid());
        txn.State.SavedAudioOutputs.Add(entry);
        if (!txn.Save())
        {
            txn.State.SavedAudioOutputs.Remove(entry);
            _owedState = txn.State.Clone();
            Log.Error($"mute skipped: the journal could not record {device.Name}");
            return;
        }
        if (!_os.Audio.SetMute(device.Id, true))
        {
            Log.Error($"mute failed on {device.Name}");
            txn.State.SavedAudioOutputs.Remove(entry);
            txn.Save();
            return;
        }
        Log.Info($"muted {device.Name} (volume {device.Volume.ToString("0.00", CultureInfo.InvariantCulture)} saved)");
    }

    private void FreezeApps(Txn txn, Config config)
    {
        var snapshot = _os.Processes.Snapshot();
        var plan = FreezePlanner.Plan(snapshot, config, _os.Processes.SelfPid, _os.Processes.WindowsDirectory);
        var automatic = plan.Where(g => g.Origin == FreezeOrigin.Automatic).Select(g => g.DisplayName).ToList();
        if (automatic.Count > 0)
        {
            Log.Info($"automatic freeze candidates: {string.Join(", ", automatic)}");
        }
        var failures = new List<string>();
        foreach (var group in plan)
        {
            var already = new HashSet<(int, long)>(txn.State.FrozenProcesses.Select(p => (p.Pid, p.CreationTime)));
            var entries = group.Processes
                .Where(p => !already.Contains((p.Pid, p.CreationTime)))
                .Select(p => new FrozenProcess(p.Pid, p.CreationTime, p.Image, group.Image, Confirmed: false))
                .ToList();
            if (entries.Count == 0)
            {
                continue;
            }
            txn.State.FrozenProcesses.AddRange(entries);
            if (!txn.Save())
            {
                // Nothing is suspended without a journal entry.
                foreach (var e in entries)
                {
                    txn.State.FrozenProcesses.Remove(e);
                }
                _owedState = txn.State.Clone();
                failures.Add($"{group.DisplayName} (journal write failed)");
                continue;
            }
            var suspended = 0;
            foreach (var entry in entries)
            {
                var outcome = _os.Freezer.Suspend(entry.Pid, entry.CreationTime);
                var index = txn.State.FrozenProcesses.IndexOf(entry);
                if (outcome == SuspendOutcome.Suspended)
                {
                    txn.State.FrozenProcesses[index] = entry with { Confirmed = true };
                    suspended++;
                }
                else
                {
                    txn.State.FrozenProcesses.RemoveAt(index);
                    if (outcome != SuspendOutcome.Gone)
                    {
                        Log.Warn($"could not suspend {entry.Image} ({entry.Pid}): {outcome}");
                    }
                }
            }
            if (suspended == 0)
            {
                failures.Add(group.DisplayName);
            }
            else
            {
                Log.Info($"froze {group.DisplayName}: {suspended} process(es)");
            }
            // A failed confirm leaves unconfirmed entries on disk, which
            // recovery resumes once it sees every thread suspended.
            txn.Save();
        }
        if (failures.Count > 0)
        {
            SetWarning("freeze", WarningSeverity.Warning, $"Could not pause: {string.Join(", ", failures)}.");
        }
        else
        {
            ClearWarning("freeze");
        }
    }

    private async Task FreezeDockerIfIdle(Txn txn, CancellationToken token)
    {
        if (txn.State.DockerFrozen)
        {
            return;
        }
        var processes = _os.Docker.DesktopProcesses().Where(p => p.CreationTime != 0).ToList();
        if (processes.Count == 0)
        {
            return;
        }
        var first = await _os.Docker.ProbeAsync(_options.DockerProbeTimeout, token).ConfigureAwait(false);
        if (token.IsCancellationRequested)
        {
            Log.Info("docker rule: lid opened during the first check; Docker left running");
            return;
        }
        Log.Info(first switch
        {
            DockerProbeResult.NoContainers => "docker rule: first check found no running container",
            DockerProbeResult.Busy => "docker rule: first check found containers running; Docker left running",
            _ => "docker rule: first check failed; Docker left running",
        });
        if (first != DockerProbeResult.NoContainers)
        {
            return;
        }
        var entries = processes.Select(p => new FrozenProcess(p.Pid, p.CreationTime, p.Image, Restorer.DockerGroup, Confirmed: false)).ToList();
        txn.State.FrozenProcesses.AddRange(entries);
        if (!txn.Save())
        {
            foreach (var e in entries)
            {
                txn.State.FrozenProcesses.Remove(e);
            }
            _owedState = txn.State.Clone();
            Log.Error("docker rule: the journal could not record Docker's processes; Docker left running");
            return;
        }
        var second = await _os.Docker.ProbeAsync(_options.DockerProbeTimeout, token).ConfigureAwait(false);
        if (token.IsCancellationRequested || second != DockerProbeResult.NoContainers)
        {
            Log.Info(token.IsCancellationRequested
                ? "docker rule: lid opened during the second check; Docker left running"
                : second == DockerProbeResult.Busy
                    ? "docker rule: second check found containers running; Docker left running"
                    : "docker rule: second check failed; Docker left running");
            foreach (var e in entries)
            {
                txn.State.FrozenProcesses.Remove(e);
            }
            txn.Save();
            return;
        }
        Log.Info("docker rule: second check found no running container");
        var suspended = 0;
        foreach (var entry in entries)
        {
            var index = txn.State.FrozenProcesses.IndexOf(entry);
            if (_os.Freezer.Suspend(entry.Pid, entry.CreationTime) == SuspendOutcome.Suspended)
            {
                txn.State.FrozenProcesses[index] = entry with { Confirmed = true };
                suspended++;
            }
            else
            {
                txn.State.FrozenProcesses.RemoveAt(index);
            }
        }
        txn.State.DockerFrozen = suspended > 0;
        txn.Save();
        Log.Info($"docker rule: paused Docker Desktop ({suspended} process(es))");
    }

    // MARK: Lid policy

    /// <summary>
    /// Sets the active scheme's lid-close action to Do nothing, journaling
    /// the previous values first. A scheme already at Do nothing for both
    /// sources needs no entry.
    /// </summary>
    private bool ApplyLidPolicy(Txn txn)
    {
        if (!_os.LidPolicy.HasLid)
        {
            return true;
        }
        if (_os.LidPolicy.ActiveScheme() is not { } scheme)
        {
            Log.Error("the active power scheme could not be read");
            return false;
        }
        var values = _os.LidPolicy.Read(scheme);
        if (values.Status != LidReadStatus.Ok)
        {
            Log.Error($"the lid-close action of scheme {scheme} could not be read");
            return false;
        }
        if (values.Ac == LidActions.DoNothing && values.Dc == LidActions.DoNothing)
        {
            return true;
        }
        var existing = txn.State.LidPolicy.FirstOrDefault(e => e.Scheme == scheme);
        if (existing is null)
        {
            txn.State.LidPolicy.Add(new LidPolicyEntry(scheme, values.Ac, values.Dc));
            if (!txn.Save())
            {
                txn.State.LidPolicy.RemoveAll(e => e.Scheme == scheme);
                _owedState = txn.State.Clone();
                Log.Error("the journal could not record the lid-close action; it was not changed");
                return false;
            }
        }
        if (!_os.LidPolicy.Write(scheme, LidActions.DoNothing, LidActions.DoNothing))
        {
            Log.Error($"writing Do nothing to scheme {scheme} failed");
            return false;
        }
        Log.Info($"lid-close action set to Do nothing (was {LidActions.Describe(values.Ac)} plugged in, {LidActions.Describe(values.Dc)} on battery)");
        return true;
    }

    private void OnActiveSchemeChanged(object? sender, EventArgs e)
    {
        if (Status.Session is null)
        {
            return;
        }
        _ = _queue.Run(async () =>
        {
            await Task.CompletedTask.ConfigureAwait(false);
            if (Status.Session is null)
            {
                return;
            }
            var opened = OpenTxn(_options.LockWait);
            if (opened.Txn is null)
            {
                ScheduleRetry();
                return;
            }
            using var txn = opened.Txn;
            if (!ApplyLidPolicy(txn))
            {
                SetWarning("lid-policy", WarningSeverity.Error, "The new power plan's lid-close action could not be set to Do nothing. Closing the lid may sleep the PC.");
            }
            else
            {
                ClearWarning("lid-policy");
            }
            // The overlay belongs to the scheme too; re-apply it if wanted.
            var end = EvaluateFloors(txn, countMiss: false);
            UpdateSessionFacts(txn.State);
            Publish();
            if (end is { } reason)
            {
                _ = EndAsync(reason);
            }
        });
    }

    // MARK: Throttling

    private void ExemptAgents(Txn txn, Config config)
    {
        var agents = new HashSet<string>(config.AgentList, StringComparer.OrdinalIgnoreCase);
        var targets = _os.Processes.Snapshot().Where(p => p.IsCurrentUser && p.CreationTime != 0 && agents.Contains(p.Image)).ToList();
        foreach (var p in targets)
        {
            if (txn.State.ThrottleOverrides.Any(o => o.Pid == p.Pid && o.CreationTime == p.CreationTime))
            {
                continue;
            }
            if (_os.Throttle.Get(p.Pid, p.CreationTime) is not { } previous)
            {
                continue;
            }
            // Exempt: take control of execution-speed throttling and turn it off.
            const uint ExecutionSpeed = 0x1;
            var control = previous.ControlMask | ExecutionSpeed;
            var state = previous.StateMask & ~ExecutionSpeed;
            if (control == previous.ControlMask && state == previous.StateMask)
            {
                continue;
            }
            var entry = new ThrottleOverride(p.Pid, p.CreationTime, p.Image, previous.ControlMask, previous.StateMask);
            txn.State.ThrottleOverrides.Add(entry);
            if (!txn.Save())
            {
                txn.State.ThrottleOverrides.Remove(entry);
                _owedState = txn.State.Clone();
                Log.Error("the journal could not record a throttling override; agents left as they are");
                return;
            }
            if (!_os.Throttle.Set(p.Pid, p.CreationTime, control, state))
            {
                txn.State.ThrottleOverrides.Remove(entry);
                txn.Save();
            }
        }
        if (txn.State.ThrottleOverrides.Count > 0)
        {
            Log.Info($"power throttling turned off for {txn.State.ThrottleOverrides.Count} agent process(es)");
        }
    }

    // MARK: Floors

    /// <summary>
    /// Applies the battery, thermal and lid rules inside a transaction and
    /// returns an end reason when the session must end. The caller queues
    /// that end after its own transaction, never inside it.
    /// </summary>
    private EndReason? EvaluateFloors(Txn txn, bool countMiss)
    {
        Session? session;
        bool lidClosed;
        lock (_gate)
        {
            session = _session;
            lidClosed = _lidClosed;
        }
        if (session is null)
        {
            return null;
        }
        var config = Config;
        var reading = _os.PowerSource.Read();
        BatteryStatus battery;
        if (reading.HasBattery == false)
        {
            battery = new BatteryStatus.NoBattery();
            _batteryMisses = 0;
        }
        else if (reading.Percent is { } percent)
        {
            battery = new BatteryStatus.Level(percent);
            _batteryMisses = 0;
        }
        else
        {
            if (countMiss)
            {
                _batteryMisses++;
            }
            battery = new BatteryStatus.Unreadable(_batteryMisses);
            Log.Warn($"battery level unreadable ({_batteryMisses} in a row)");
        }
        // An unknown source counts as battery: ending beats holding sleep
        // with no floor on a battery that may be draining.
        var onAc = reading.OnAc ?? false;
        var thermal = config.ThermalRules && _os.Thermal.IsAvailable ? _os.Thermal.Read() : ThermalState.Nominal;

        var cause = FloorRules.Cause(battery.Percent, onAc, thermal, lidClosed, config);
        if (txn.State.PowerSavingSetByUs && cause is { } holding)
        {
            _lastCause = holding;
        }
        foreach (var action in FloorRules.Evaluate(battery, onAc, thermal, lidClosed, txn.State.PowerSavingSetByUs, config))
        {
            switch (action)
            {
                case FloorAction.EndSession end:
                    Log.Warn($"floor rule ends the session: {end.Reason}");
                    if (end.Reason == EndReason.BatteryFloor)
                    {
                        _os.Notifier.Notify("Battery floor reached", $"Battery at {battery.Percent}%, below the {config.EndFloor}% end floor. The session is ending.");
                    }
                    else if (end.Reason == EndReason.BatteryUnreadable)
                    {
                        _os.Notifier.Notify("Battery unreadable", "The battery level could not be read twice in a row, so the session is ending.");
                    }
                    else if (end.Reason == EndReason.ThermalCritical)
                    {
                        _os.Notifier.Notify("Too hot", "The PC has stayed heavily throttled by heat. The session is ending.");
                    }
                    return end.Reason;
                case FloorAction.EnablePowerSaving enable:
                    txn.State.PowerSavingSetByUs = true;
                    _lastCause = enable.Cause;
                    ApplyPowerSaving(txn);
                    if (enable.Cause == PowerSavingCause.Battery)
                    {
                        _os.Notifier.Notify("Power saving on", $"Battery at {battery.Percent}%, below the {config.LowPowerFloor}% floor.");
                    }
                    else if (enable.Cause == PowerSavingCause.Thermal)
                    {
                        _os.Notifier.Notify("Power saving on", "The PC is being throttled by heat. Power saving is on until it cools down.");
                    }
                    else
                    {
                        Log.Info("power saving on (lid closed)");
                    }
                    break;
                case FloorAction.DisablePowerSaving:
                    var report = _restorer.RestorePowerMode(txn.State, txn.SaveOrThrow, releaseOwnership: true);
                    ApplyRestoreOutcome(report, announce: null);
                    if (_lastCause == PowerSavingCause.Lid)
                    {
                        Log.Info("power saving off (lid opened)");
                    }
                    else
                    {
                        _os.Notifier.Notify("Power saving off", onAc ? "Charger connected." : "Back above the floor.");
                    }
                    _lastCause = null;
                    break;
            }
        }
        if (txn.State.PowerSavingSetByUs && cause is not null)
        {
            // Still wanted: make sure the current source has it, for example
            // after the charger was unplugged.
            ApplyPowerSaving(txn);
        }
        if (battery is BatteryStatus.Unreadable)
        {
            EnsurePollTimer();
        }
        return null;
    }

    /// <summary>
    /// Switches the current source to Best power efficiency. Both sources'
    /// values are read first and journaled before the write, because the
    /// charger can change between choosing the source and the write landing;
    /// with either value unreadable nothing is changed, since a guessed
    /// baseline would later be "restored" over the user's real mode. A source
    /// the user moved off Insomnia's value is theirs (persisted in
    /// <see cref="RuntimeState.PowerModeRelinquished"/>) until power saving
    /// is released.
    /// </summary>
    private void ApplyPowerSaving(Txn txn)
    {
        var state = txn.State;
        if (!_os.PowerMode.IsSupported)
        {
            SetWarning("power-saving", WarningSeverity.Info, "Power saving is not available with this power plan or Windows version.");
            txn.Save();
            return;
        }
        if (_os.PowerMode.CurrentSource() is not { } source || state.PowerModeRelinquished.Contains(source))
        {
            txn.Save();
            return;
        }
        var (ac, dc) = _os.PowerMode.Read();
        if (ac is not { } acValue || dc is not { } dcValue)
        {
            Log.Warn("power saving not switched on: the current power modes could not be read");
            txn.Save();
            return;
        }
        Guid ValueOf(PowerSourceKind s) => s == PowerSourceKind.Ac ? acValue : dcValue;

        // Settle unconfirmed entries first. One whose source reads what it
        // would have applied was written and only its confirmation was lost
        // (a failed read, a crash): confirm it and keep its baseline. Any
        // other value means it was never written, or was changed since.
        foreach (var pending in state.PowerMode.Where(e => !e.Confirmed).ToList())
        {
            if (pending.IsOurs(ValueOf(pending.Source)))
            {
                state.PowerMode[state.PowerMode.IndexOf(pending)] = pending with { Confirmed = true };
            }
            else
            {
                state.PowerMode.Remove(pending);
            }
        }

        var existing = state.PowerMode.FirstOrDefault(e => e.Source == source);
        if (existing is not null && ValueOf(source) != PowerOverlays.BestPowerEfficiency && existing.IsOurs(ValueOf(source)))
        {
            // Insomnia's value, but not efficiency: a restore's write that a
            // charger change landed here. Keep the baseline and apply
            // efficiency over it.
            var retargeted = existing with { Applied = PowerOverlays.BestPowerEfficiency, AlsoApplied = ValueOf(source), Confirmed = false };
            state.PowerMode[state.PowerMode.IndexOf(existing)] = retargeted;
            if (txn.Save() && _os.PowerMode.SetForCurrentSource(PowerOverlays.BestPowerEfficiency)
                && _os.PowerMode.Read() is var check && (source == PowerSourceKind.Ac ? check.Ac : check.Dc) == PowerOverlays.BestPowerEfficiency)
            {
                state.PowerMode[state.PowerMode.IndexOf(retargeted)] = retargeted with { Confirmed = true, AlsoApplied = null };
            }
            txn.Save();
            return;
        }
        if (existing is not null)
        {
            if (!existing.IsOurs(ValueOf(source)))
            {
                Log.Info($"power mode {Restorer.SourceLabel(source)} was changed to {PowerOverlays.Describe(ValueOf(source))} by someone else; left as it is until power saving is released");
                state.PowerMode.Remove(existing);
                state.PowerModeRelinquished.Add(source);
            }
            txn.Save();
            return;
        }
        if (ValueOf(source) == PowerOverlays.BestPowerEfficiency)
        {
            txn.Save();
            return;
        }

        // Journal a baseline for every source the write could reach. That
        // includes a relinquished one: Insomnia does not take it back on
        // purpose, but a charger change can still land this write there.
        var added = new List<PowerModeEntry>();
        foreach (var s in new[] { PowerSourceKind.Ac, PowerSourceKind.Dc })
        {
            if (state.PowerMode.Any(e => e.Source == s) || ValueOf(s) == PowerOverlays.BestPowerEfficiency)
            {
                continue;
            }
            var baseline = new PowerModeEntry(s, ValueOf(s), PowerOverlays.BestPowerEfficiency, Confirmed: false);
            state.PowerMode.Add(baseline);
            added.Add(baseline);
        }
        if (!txn.Save())
        {
            foreach (var e in added)
            {
                state.PowerMode.Remove(e);
            }
            _owedState = state.Clone();
            Log.Error("power saving not switched on: the journal could not record the current modes");
            return;
        }

        var ok = _os.PowerMode.SetForCurrentSource(PowerOverlays.BestPowerEfficiency);
        var after = _os.PowerMode.Read();
        // Confirm what reads applied; a source that still reads its baseline
        // was not touched. An unreadable source keeps its unconfirmed entry,
        // since the write may have reached it.
        foreach (var s in new[] { PowerSourceKind.Ac, PowerSourceKind.Dc })
        {
            var entry = state.PowerMode.FirstOrDefault(e => e.Source == s && !e.Confirmed);
            if (entry is null)
            {
                continue;
            }
            var now = s == PowerSourceKind.Ac ? after.Ac : after.Dc;
            if (now == PowerOverlays.BestPowerEfficiency)
            {
                state.PowerMode[state.PowerMode.IndexOf(entry)] = entry with { Confirmed = true };
            }
            else if (now == entry.Previous && s != _os.PowerMode.CurrentSource())
            {
                // The other source, untouched: no undo needed for it.
                state.PowerMode.Remove(entry);
            }
        }
        txn.Save();
        if (!ok && state.PowerMode.All(e => !e.Confirmed))
        {
            SetWarning("power-saving", WarningSeverity.Info, "Windows refused to switch to Best power efficiency with this power plan.");
            return;
        }
        ClearWarning("power-saving");
        Log.Info($"power mode {Restorer.SourceLabel(source)} set to Best power efficiency (was {PowerOverlays.Describe(ValueOf(source))})");
    }

    private void OnPowerSourceChanged(object? sender, EventArgs e) => RunFloorCheck(countMiss: true, retryPending: true);

    private void OnResumed(object? sender, EventArgs e)
    {
        Log.Warn("the system resumed from sleep");
        if (Status.Session is { } session)
        {
            _os.KeepAwake.Acquire(KeepAwakeReason(session));
            RunFloorCheck(countMiss: false, retryPending: true);
        }
    }

    private void RunFloorCheck(bool countMiss, bool retryPending)
    {
        _ = _queue.Run(async () =>
        {
            await Task.CompletedTask.ConfigureAwait(false);
            if (Status.Session is null)
            {
                if (retryPending)
                {
                    RetryPendingCore();
                }
                return;
            }
            var opened = OpenTxn(_options.LockWait);
            if (opened.Txn is null)
            {
                ScheduleRetry();
                return;
            }
            EndReason? end;
            using (var txn = opened.Txn)
            {
                end = EvaluateFloors(txn, countMiss);
                if (retryPending && txn.State.PowerMode.Count > 0 && !txn.State.PowerSavingSetByUs)
                {
                    ApplyRestoreOutcome(_restorer.RestorePowerMode(txn.State, txn.SaveOrThrow, releaseOwnership: false), announce: null);
                }
                UpdateSessionFacts(txn.State);
            }
            Publish();
            if (end is { } reason)
            {
                _ = EndAsync(reason);
            }
        });
    }

    // MARK: Timers

    private void StartSessionTimers(Session session)
    {
        lock (_gate)
        {
            _deadlineTimer?.Dispose();
            var due = session.EndsAt - Now;
            _deadlineTimer = _time.CreateTimer(_ => OnDeadline(), null, due < TimeSpan.Zero ? TimeSpan.Zero : due, Timeout.InfiniteTimeSpan);

            _reminderTimer?.Dispose();
            _reminderTimer = null;
            var remindAt = session.EndsAt - _options.ReminderLead;
            if (remindAt > Now)
            {
                _reminderTimer = _time.CreateTimer(_ => OnReminder(session.EndsAt), null, remindAt - Now, Timeout.InfiniteTimeSpan);
            }

            _pollTimer ??= _time.CreateTimer(_ => OnPoll(), null, _options.PollInterval, _options.PollInterval);
        }
    }

    private void EnsurePollTimer()
    {
        lock (_gate)
        {
            _pollTimer ??= _time.CreateTimer(_ => OnPoll(), null, _options.PollInterval, _options.PollInterval);
        }
    }

    private void StopSessionTimers()
    {
        lock (_gate)
        {
            _deadlineTimer?.Dispose();
            _deadlineTimer = null;
            _reminderTimer?.Dispose();
            _reminderTimer = null;
            _pollTimer?.Dispose();
            _pollTimer = null;
        }
    }

    private void OnDeadline()
    {
        var session = Status.Session;
        if (session is null)
        {
            return;
        }
        if (!session.IsExpired(Now))
        {
            // The clock moved (sleep, a time change): wait for the real deadline.
            StartSessionTimers(session);
            return;
        }
        _ = EndAsync(EndReason.Timer);
    }

    private void OnReminder(DateTimeOffset endsAt)
    {
        var session = Status.Session;
        if (session is null || session.EndsAt != endsAt)
        {
            return;
        }
        var local = endsAt.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);
        var minutes = (int)Math.Round(_options.ReminderLead.TotalMinutes);
        Log.Info($"reminder: {minutes} minutes left");
        _os.Notifier.Notify($"{minutes} minutes left", $"Sleep is allowed again at {local}. Extend from the Insomnia tray icon.");
    }

    /// <summary>
    /// Every poll interval during a session: thermal and battery re-read, and
    /// a check that session.json still holds this session. If the recovery
    /// agent or a person removed it, the session is over.
    /// </summary>
    private void OnPoll()
    {
        var session = Status.Session;
        if (session is null)
        {
            StopSessionTimers();
            return;
        }
        if (Status.Phase == SessionPhase.Active && _store.ReadSession() is SessionRead.Missing)
        {
            Log.Warn("session.json disappeared during the session; ending");
            _ = EndAsync(EndReason.Recovery);
            return;
        }
        RunFloorCheck(countMiss: true, retryPending: false);
    }

    private void ScheduleRetry()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _retryTimer?.Dispose();
            _retryTimer = _time.CreateTimer(_ => OnRetry(), null, _options.RetryDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnRetry()
    {
        _ = _queue.Run(async () =>
        {
            if (!_reconciled)
            {
                await ReconcileCore().ConfigureAwait(false);
                return;
            }
            Session? session;
            SessionPhase phase;
            lock (_gate)
            {
                session = _session;
                phase = _phase;
            }
            if (session is not null && (phase == SessionPhase.Active && (session.IsExpired(Now) || _warnings.ContainsKey("end"))))
            {
                EndCore(session.IsExpired(Now) ? EndReason.Timer : EndReason.User);
                return;
            }
            RetryPendingCore();
        });
    }

    /// <summary>
    /// Retries what an earlier end, lid open or launch left: failed restores,
    /// owed journal writes, and saved audio waiting for its device. Outside a
    /// session everything is restored; during one, only what the lid state
    /// allows.
    /// </summary>
    private void RetryPendingCore()
    {
        if (!_reconciled)
        {
            // Until launch reconcile has read session.json, a session there
            // may be live: restoring now could undo it. Reconcile goes first.
            ScheduleRetry();
            return;
        }
        var opened = OpenTxn(_options.LockWait);
        if (opened.Txn is null)
        {
            ScheduleRetry();
            return;
        }
        RestoreReport report;
        using (var txn = opened.Txn)
        {
            bool active;
            bool lidClosed;
            lock (_gate)
            {
                active = _session is not null;
                lidClosed = _lidClosed;
            }
            if (!active)
            {
                report = _restorer.RestoreAll(txn.State, txn.SaveOrThrow);
            }
            else
            {
                report = new RestoreReport();
                if (!lidClosed)
                {
                    report.Merge(_restorer.RestoreProcesses(txn.State, txn.SaveOrThrow, onlyGroup: null));
                    report.Merge(_restorer.RestoreAudio(txn.State, txn.SaveOrThrow));
                }
                if (!txn.State.PowerSavingSetByUs)
                {
                    report.Merge(_restorer.RestorePowerMode(txn.State, txn.SaveOrThrow, releaseOwnership: false));
                }
            }
            if (_owedState is not null)
            {
                txn.Save();
            }
            if (_sessionRemovalOwed && !active && !TryRemoveSession())
            {
                report.Failures.Add("session.json could not be removed.");
            }
            UpdateSessionFacts(txn.State);
        }
        ApplyRestoreOutcome(report, announce: null);
        Publish();
    }

    // MARK: Audio devices

    private void OnAudioDevicesChanged(object? sender, EventArgs e)
    {
        bool waiting;
        lock (_gate)
        {
            waiting = _warnings.Keys.Any(k => k.StartsWith("audio:", StringComparison.Ordinal)) || _warnings.ContainsKey("restore");
        }
        if (!waiting)
        {
            return;
        }
        _ = _queue.Run(async () =>
        {
            await Task.CompletedTask.ConfigureAwait(false);
            if (!_reconciled)
            {
                return;
            }
            bool lidClosed;
            lock (_gate)
            {
                lidClosed = _session is not null && _lidClosed;
            }
            if (lidClosed)
            {
                // The next lid open restores it.
                return;
            }
            var opened = OpenTxn(_options.LockWait);
            if (opened.Txn is null)
            {
                ScheduleRetry();
                return;
            }
            RestoreReport report;
            using (var txn = opened.Txn)
            {
                report = _restorer.RestoreAudio(txn.State, txn.SaveOrThrow);
            }
            ApplyRestoreOutcome(report, announce: null, audioOnly: true);
            Publish();
        });
    }

    // MARK: Menu actions

    public Task PerformAsync(MenuAction action) => action switch
    {
        MenuAction.StopWaitingForAudio stop => StopWaitingAsync(stop),
        MenuAction.RelaunchBrowser relaunch => RelaunchBrowserAsync(relaunch),
        MenuAction.ResumeInterrupted resume => ResumeInterruptedAsync(resume),
        _ => Task.CompletedTask,
    };

    /// <summary>The user's explicit resume of a process whose earlier resume was interrupted.</summary>
    private Task ResumeInterruptedAsync(MenuAction.ResumeInterrupted resume) => _queue.Run(async () =>
    {
        await Task.CompletedTask.ConfigureAwait(false);
        var opened = OpenTxn(_options.LockWait);
        if (opened.Txn is null)
        {
            return;
        }
        using var txn = opened.Txn;
        var entry = txn.State.FrozenProcesses.FirstOrDefault(p => p.Resuming && p.Pid == resume.Pid && p.CreationTime == resume.CreationTime);
        if (entry is not null)
        {
            var outcome = _os.Freezer.Resume(entry.Pid, entry.CreationTime);
            Log.Info($"resume of {entry.Image} ({entry.Pid}) at the user's request: {outcome}");
            if (outcome is ResumeOutcome.Resumed or ResumeOutcome.Gone)
            {
                txn.State.FrozenProcesses.Remove(entry);
                txn.Save();
            }
            else
            {
                SetWarning("resume-failed", WarningSeverity.Warning, $"{entry.Image} could not be resumed ({outcome}).");
            }
        }
        UpdateSessionFacts(txn.State);
        Publish();
    });

    /// <summary>
    /// Drops a saved volume, leaving the device as it is. Nothing is dropped
    /// when the device reads connected by then, or when the journal on disk
    /// holds a later save for it than the one the menu showed.
    /// </summary>
    private Task StopWaitingAsync(MenuAction.StopWaitingForAudio stop) => _queue.Run(async () =>
    {
        await Task.CompletedTask.ConfigureAwait(false);
        var opened = OpenTxn(_options.LockWait);
        if (opened.Txn is null)
        {
            return;
        }
        using var txn = opened.Txn;
        var entry = txn.State.SavedAudioOutputs.FirstOrDefault(a => a.DeviceId == stop.DeviceId && a.SaveId == stop.SaveId);
        if (entry is null || _os.Audio.Status(stop.DeviceId) == AudioDeviceStatus.Connected)
        {
            Log.Info($"stop waiting for {stop.DeviceName}: nothing dropped (reconnected or saved again)");
        }
        else
        {
            txn.State.SavedAudioOutputs.Remove(entry);
            txn.Save();
            Log.Info($"stopped waiting for {stop.DeviceName}; its saved volume was dropped");
        }
        ClearWarning("audio:" + stop.DeviceId);
        UpdateSessionFacts(txn.State);
        Publish();
    });

    /// <summary>How long a relaunch waits for the user to quit the browser.</summary>
    public static readonly TimeSpan BrowserQuitWait = TimeSpan.FromMinutes(2);

    private CancellationTokenSource _browserWait = new();

    /// <summary>
    /// Asks the user to quit the browser from its own menu, which asks about
    /// unsaved pages and saves every window, then reopens it unthrottled with
    /// those windows. Insomnia never closes the browser itself. A session
    /// that ends during the wait cancels it, and nothing is reported.
    /// </summary>
    private async Task RelaunchBrowserAsync(MenuAction.RelaunchBrowser relaunch)
    {
        var key = "browser-result:" + relaunch.Image;
        CancellationToken token;
        lock (_gate)
        {
            token = _browserWait.Token;
        }
        var minutes = (int)BrowserQuitWait.TotalMinutes;
        SetWarning(key, WarningSeverity.Info, $"Quit {relaunch.DisplayName} from its own menu (Exit) within {minutes} minutes. Insomnia then reopens it unthrottled, with your windows.");
        Publish();
        _os.Notifier.Notify($"Quit {relaunch.DisplayName} to continue", $"Exit {relaunch.DisplayName} from its menu. Insomnia reopens it unthrottled, with your windows. Private windows are not restored.");

        var result = await _os.Browsers.RelaunchAfterExitAsync(relaunch.Image, BrowserQuitWait, token).ConfigureAwait(false);
        Log.Info($"relaunch {relaunch.DisplayName}: {result}");
        if (result is BrowserRelaunchResult.Relaunched or BrowserRelaunchResult.Cancelled)
        {
            ClearWarning(key);
        }
        else
        {
            var text = result switch
            {
                BrowserRelaunchResult.NotRunning => $"{relaunch.DisplayName} was not running.",
                BrowserRelaunchResult.ArgumentsUnreadable => $"{relaunch.DisplayName} was not relaunched: its command line could not be read.",
                BrowserRelaunchResult.StillRunning => $"{relaunch.DisplayName} was not relaunched: it was still running after {minutes} minutes. If it keeps running in the background (startup boost or background apps), turn that off in its settings and try again.",
                BrowserRelaunchResult.LaunchFailed => $"{relaunch.DisplayName} quit but did not start again. Open it yourself.",
                _ => $"{relaunch.DisplayName} was not relaunched.",
            };
            SetWarning(key, WarningSeverity.Warning, text);
            _os.Notifier.Notify("Browser not relaunched", text);
        }
        ScanBrowsers();
        Publish();
    }

    private void ScanBrowsers()
    {
        IReadOnlyList<BrowserInstance> browsers;
        try
        {
            browsers = _os.Browsers.Scan();
        }
        catch (Exception e)
        {
            Log.Error("browser scan failed", e);
            return;
        }
        lock (_gate)
        {
            foreach (var key in _warnings.Keys.Where(k => k.StartsWith("browser:", StringComparison.Ordinal)).ToList())
            {
                _warnings.Remove(key);
            }
            if (_session is null)
            {
                return;
            }
            foreach (var group in browsers.Where(b => !b.Unthrottled).GroupBy(b => b.Image, StringComparer.OrdinalIgnoreCase))
            {
                var first = group.First();
                _warnings["browser:" + first.Image] = new MenuWarning(
                    "browser:" + first.Image,
                    WarningSeverity.Info,
                    $"{first.DisplayName} throttles its windows while the lid is closed.",
                    new MenuAction.RelaunchBrowser(first.Image, first.DisplayName));
            }
        }
    }

    // MARK: Settings

    public Task<string?> SaveConfigAsync(Config config) => _queue.Run(async () =>
    {
        await Task.CompletedTask.ConfigureAwait(false);
        var (normalized, changes) = config.Normalized();
        foreach (var change in changes)
        {
            Log.Info($"settings: {change}");
        }
        var previous = Config;
        try
        {
            _store.WriteConfig(normalized);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Error("config.json could not be written", e);
            return $"Settings could not be saved: {e.Message}";
        }
        lock (_gate)
        {
            _config = normalized;
        }
        ClearWarning("config");
        // Settings registers launch at login itself and saves the flag only
        // once Windows accepted it; the engine just keeps the flag.
        if (normalized.HotspotSsid != previous.HotspotSsid && normalized.HotspotSsid.Length > 0)
        {
            _ = _os.Wifi.RequestAccessAsync();
        }
        Publish();
        if (Status.Session is not null)
        {
            RunFloorCheck(countMiss: false, retryPending: false);
        }
        return null;
    });

    private Config LoadConfig()
    {
        var (config, problem) = _store.ReadConfig(out var missing);
        if (problem is not null)
        {
            Log.Error(problem);
            SetWarning("config", WarningSeverity.Warning, $"Settings could not be read, so the defaults are in use until you save: {problem}");
            return config.Normalized().Config;
        }
        var (normalized, changes) = config.Normalized();
        foreach (var change in changes)
        {
            Log.Info($"config: {change}");
        }
        if (changes.Count > 0 || missing)
        {
            try
            {
                _store.WriteConfig(normalized);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.Error("config.json could not be written back", e);
            }
        }
        return normalized;
    }

    // MARK: Reads for the UI

    public StatusDetails ReadDetails()
    {
        ScanBrowsers();
        BatteryReading battery;
        WifiConnectivity wifi;
        string? ssid;
        RecoveryAgentState agent;
        try
        {
            battery = _os.PowerSource.Read();
            wifi = _os.Wifi.Connectivity();
            ssid = wifi == WifiConnectivity.Satisfied ? _os.Wifi.CurrentSsid() : null;
            agent = _os.RecoveryAgent.Check();
        }
        catch (Exception e)
        {
            Log.Error("status read failed", e);
            battery = new BatteryReading(null, null, null, null);
            wifi = WifiConnectivity.NoAdapter;
            ssid = null;
            agent = RecoveryAgentState.Unknown;
        }
        Publish();
        // Read outside our lock: the failover takes its own lock and calls
        // back into the engine while holding none.
        var gap = _failover.LastGap;
        lock (_gate)
        {
            return new StatusDetails(
                _os.PowerEvents.LidClosed,
                battery,
                ssid,
                wifi,
                _frozenApps,
                _dockerFrozen,
                _powerSavingOn,
                gap,
                agent);
        }
    }

    public FreezePreview PreviewFreeze(Config config)
    {
        var plan = FreezePlanner.Plan(_os.Processes.Snapshot(), config, _os.Processes.SelfPid, _os.Processes.WindowsDirectory);
        var (explicitNames, automatic) = FreezePlanner.Preview(plan);
        return new FreezePreview(explicitNames, automatic);
    }

    // MARK: Failover

    private void OnFailoverUpdate(FailoverEvent update)
    {
        switch (update)
        {
            case FailoverEvent.PasswordMissing missing:
                SetWarning("hotspot", WarningSeverity.Warning, $"No password is saved for the hotspot \"{missing.Ssid}\". Enter it in Settings > Network.");
                break;
            case FailoverEvent.Recovered:
                ClearWarning("hotspot");
                break;
        }
        Publish();
    }

    // MARK: Status

    private void UpdateSessionFacts(RuntimeState state)
    {
        lock (_gate)
        {
            // Interrupted resumes are shown from the journal itself, so the
            // line stays exactly as long as the entry does.
            foreach (var key in _warnings.Keys.Where(k => k.StartsWith("interrupted:", StringComparison.Ordinal)).ToList())
            {
                _warnings.Remove(key);
            }
            foreach (var p in state.FrozenProcesses.Where(p => p.Resuming))
            {
                var id = $"interrupted:{p.Pid}:{p.CreationTime}";
                _warnings[id] = new MenuWarning(
                    id,
                    WarningSeverity.Warning,
                    $"{p.Image} may still be paused: Insomnia's resume of it was interrupted. Resume it here unless another program paused it too.",
                    new MenuAction.ResumeInterrupted(p.Pid, p.CreationTime, p.Image));
            }
            _frozenApps = state.FrozenProcesses
                .Where(p => p.Group != Restorer.DockerGroup)
                .Select(p => p.Group)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            _dockerFrozen = state.DockerFrozen;
            _powerSavingOn = state.PowerSavingSetByUs && state.PowerMode.Count > 0;
        }
    }

    /// <summary>
    /// Turns a restore report into menu lines, and schedules a retry while
    /// anything failed or a write is owed. Audio waiting for its device gets
    /// a line with "Stop waiting" but no retry: the device-change event
    /// brings it back.
    /// </summary>
    private void ApplyRestoreOutcome(RestoreReport report, string? announce, bool audioOnly = false)
    {
        foreach (var line in report.Restored)
        {
            Log.Info($"restored: {line}");
        }
        lock (_gate)
        {
            foreach (var key in _warnings.Keys.Where(k => k.StartsWith("audio:", StringComparison.Ordinal)).ToList())
            {
                _warnings.Remove(key);
            }
            foreach (var waiting in report.WaitingAudio)
            {
                var name = waiting.Name ?? "an audio device";
                _warnings["audio:" + waiting.DeviceId] = new MenuWarning(
                    "audio:" + waiting.DeviceId,
                    WarningSeverity.Info,
                    $"{name} is still muted. Insomnia restores its volume when it reconnects.",
                    new MenuAction.StopWaitingForAudio(waiting.DeviceId, waiting.SaveId, name));
            }
            if (!audioOnly || report.Failures.Count > 0)
            {
                if (report.HasNoRetryableProblems)
                {
                    _warnings.Remove("restore");
                    _retryCount = 0;
                }
                else
                {
                    var reasons = report.Failures.ToList();
                    if (report.JournalWriteFailed)
                    {
                        reasons.Add("state.json could not be written; do not quit until it can be.");
                    }
                    _warnings["restore"] = new MenuWarning(
                        "restore",
                        WarningSeverity.Error,
                        $"Restore incomplete. {string.Join(" ", reasons)} Insomnia retries every {(int)_options.RetryDelay.TotalSeconds} s.",
                        new MenuAction.OpenLogs());
                }
            }
        }
        if (!report.HasNoRetryableProblems || _owedState is not null)
        {
            _retryCount++;
            ScheduleRetry();
        }
        if (announce is not null && report.Restored.Count > 0)
        {
            _os.Notifier.Notify(announce, string.Join("; ", report.Restored) + ".");
        }
    }

    private void SetWarning(string id, WarningSeverity severity, string text, MenuAction? action = null)
    {
        lock (_gate)
        {
            SetWarningLocked(id, severity, text, action);
        }
    }

    private void SetWarningLocked(string id, WarningSeverity severity, string text, MenuAction? action = null) =>
        _warnings[id] = new MenuWarning(id, severity, text, action);

    private void ClearWarning(string id)
    {
        lock (_gate)
        {
            _warnings.Remove(id);
        }
    }

    private void Publish()
    {
        SessionStatus status;
        lock (_gate)
        {
            status = PublishLocked();
        }
        StatusChanged?.Invoke(this, status);
    }

    private SessionStatus PublishLocked()
    {
        var order = new Dictionary<WarningSeverity, int> { [WarningSeverity.Error] = 0, [WarningSeverity.Warning] = 1, [WarningSeverity.Info] = 2 };
        var warnings = _warnings.Values.OrderBy(w => order[w.Severity]).ThenBy(w => w.Id, StringComparer.Ordinal).ToList();
        _status = new SessionStatus(
            _session is null ? (_phase == SessionPhase.Starting ? SessionPhase.Starting : SessionPhase.Idle) : _phase,
            _session,
            _session is not null && _lidClosed,
            warnings,
            _startBlocked,
            _session is not null);
        return _status;
    }

    // MARK: Transactions

    private sealed record Opened(Txn? Txn, string? Refusal);

    /// <summary>
    /// Takes the recovery lock and reads the journal. A write this process
    /// still owes goes first, since it is the latest truth; while it keeps
    /// failing, the owed state stands in for the disk.
    /// </summary>
    private Opened OpenTxn(TimeSpan wait)
    {
        var handle = _lock.TryAcquire(wait);
        if (handle is null)
        {
            const string busy = "Another Insomnia process (probably the recovery agent) is busy.";
            SetWarning("lock-busy", WarningSeverity.Warning, busy);
            return new Opened(null, busy);
        }
        ClearWarning("lock-busy");
        var read = _store.ReadState(out var raw);
        RuntimeState state;
        if (_owedState is { } owed && read is StateRead.Valid && raw == _owedBase)
        {
            try
            {
                _lastDisk = _store.WriteState(owed);
                _owedState = null;
                ClearWarning("journal-write");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.Error("owed journal write failed again", e);
            }
            state = owed.Clone();
        }
        else
        {
            if (_owedState is not null && read is StateRead.Valid)
            {
                // Only a successful read proves another process changed the
                // journal. While it cannot be read, the owed write is kept.
                Log.Warn("state.json changed on disk since a journal write failed here; the disk wins and the owed write is dropped");
                _owedState = null;
                ClearWarning("journal-write");
            }
            switch (read)
            {
                case StateRead.Valid v:
                    state = v.State;
                    _lastDisk = raw;
                    ClearWarning("journal");
                    break;
                case StateRead.Unreadable u:
                    handle.Dispose();
                    var text = $"{u.Reason}. Insomnia changes nothing until it can read it. Fix or remove {_store.Paths.State}.";
                    SetWarning("journal", WarningSeverity.Error, text, new MenuAction.OpenLogs());
                    ScheduleRetry();
                    return new Opened(null, text);
                default:
                    handle.Dispose();
                    return new Opened(null, "state.json could not be read.");
            }
        }
        return new Opened(new Txn(this, handle, state), null);
    }

    private sealed class Txn : IDisposable
    {
        private readonly SessionEngine _engine;
        private readonly IDisposable _handle;

        public Txn(SessionEngine engine, IDisposable handle, RuntimeState state)
        {
            _engine = engine;
            _handle = handle;
            State = state;
        }

        public RuntimeState State { get; }

        /// <summary>Writes the journal. On failure the state is owed and written first by the next transaction.</summary>
        public bool Save()
        {
            try
            {
                _engine._lastDisk = _engine._store.WriteState(State);
                _engine._owedState = null;
                _engine.ClearWarning("journal-write");
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.Error("state.json write failed", e);
                if (_engine._owedState is null)
                {
                    _engine._owedBase = _engine._lastDisk;
                }
                _engine._owedState = State.Clone();
                _engine.SetWarning("journal-write", WarningSeverity.Error, $"state.json could not be written ({e.Message}). Insomnia retries; do not quit until it can.");
                return false;
            }
        }

        public void SaveOrThrow()
        {
            if (!Save())
            {
                throw new IOException("state.json write failed");
            }
        }

        public void Dispose() => _handle.Dispose();
    }

    /// <summary>
    /// Removes session.json. When it cannot be deleted, it is overwritten
    /// with an already-expired session, which launch reconcile and the
    /// recovery agent both read as ended, so an ended session can never be
    /// resumed. When neither works, the removal is owed and retried, and the
    /// caller reports the end as incomplete.
    /// </summary>
    private bool TryRemoveSession()
    {
        try
        {
            _store.DeleteSession();
            _sessionRemovalOwed = false;
            ClearWarning("session-delete");
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Error("session.json could not be deleted", e);
        }
        try
        {
            var past = SessionMath.WholeSeconds(Now).AddSeconds(-1);
            _store.WriteSession(new Session(past, past));
            // An expired session is an ended one everywhere it is read (launch
            // reconcile and the recovery agent delete it), so nothing is owed.
            Log.Warn("session.json was marked expired instead of deleted");
            _sessionRemovalOwed = false;
            ClearWarning("session-delete");
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Error("session.json could not be marked expired either", e);
        }
        _sessionRemovalOwed = true;
        SetWarning("session-delete", WarningSeverity.Error, $"session.json could not be removed. Delete {_store.Paths.Session} before starting Insomnia again; Insomnia retries every {(int)_options.RetryDelay.TotalSeconds} s.");
        ScheduleRetry();
        return false;
    }

    private static string KeepAwakeReason(Session session) =>
        $"Insomnia session until {session.EndsAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)}";

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _lidDebounce?.Dispose();
            _retryTimer?.Dispose();
            _lidCts.Cancel();
            _lidCts.Dispose();
            _browserWait.Cancel();
            _browserWait.Dispose();
        }
        StopSessionTimers();
        _failover.Dispose();
        _os.PowerEvents.LidChanged -= OnLidChanged;
        _os.PowerEvents.PowerSourceChanged -= OnPowerSourceChanged;
        _os.PowerEvents.ActiveSchemeChanged -= OnActiveSchemeChanged;
        _os.PowerEvents.Resumed -= OnResumed;
        _os.Audio.DevicesChanged -= OnAudioDevicesChanged;
        _queue.Dispose();
    }
}
