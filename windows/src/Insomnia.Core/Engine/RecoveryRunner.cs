using Insomnia.Core.Logging;
using Insomnia.Core.Platform;
using Insomnia.Core.Store;

namespace Insomnia.Core.Engine;

/// <summary>Exit codes of <c>Insomnia.exe --recover</c>.</summary>
public static class RecoveryExit
{
    /// <summary>Nothing to do, a valid session is running, or everything was restored.</summary>
    public const int Ok = 0;

    /// <summary>Something could not be restored; it stays journaled for the next run.</summary>
    public const int Incomplete = 1;

    /// <summary>The recovery lock was busy; the next run tries again.</summary>
    public const int Busy = 2;

    /// <summary>The journal could not be read; nothing was changed.</summary>
    public const int JournalUnreadable = 3;

    /// <summary>
    /// Everything that can be restored now was, but a power mode change for
    /// the power source not in use waits until that source is active again.
    /// Uninstall must not remove the agent while this holds.
    /// </summary>
    public const int WaitingForPowerSource = 4;
}

/// <summary>
/// The recovery agent's single run. It honors a valid session's deadline and
/// does nothing while one runs. Once the session file is missing, expired or
/// unreadable, it restores the journal, removes the session file, and says
/// so in a notification. It is the same code the app uses to end a session,
/// so it restores everything the app can, audio included.
/// </summary>
public sealed class RecoveryRunner
{
    private readonly PlatformServices _os;
    private readonly JournalStore _store;
    private readonly RecoveryLock _lock;
    private readonly TimeProvider _time;

    public RecoveryRunner(PlatformServices os, JournalStore store, RecoveryLock recoveryLock, TimeProvider? time = null)
    {
        _os = os;
        _store = store;
        _lock = recoveryLock;
        _time = time ?? TimeProvider.System;
    }

    public int Run()
    {
        using var handle = _lock.TryAcquire(TimeSpan.FromSeconds(2));
        if (handle is null)
        {
            // The app or an earlier run is mid-transaction. Quietly retry next minute.
            return RecoveryExit.Busy;
        }

        var now = _time.GetUtcNow();
        var session = _store.ReadSession();
        if (session is SessionRead.Valid valid && !valid.Session.IsExpired(now))
        {
            return RecoveryExit.Ok;
        }
        if (session is SessionRead.Inaccessible inaccessible)
        {
            // It may be a live session held open by another program: undoing
            // its changes could sleep a running session. Try next minute.
            Log.Warn($"recovery: {inaccessible.Reason}; trying again next run");
            return RecoveryExit.Busy;
        }

        var stateRead = _store.ReadState();
        if (stateRead is StateRead.Unreadable unreadable)
        {
            Log.Error($"recovery: {unreadable.Reason}; nothing changed");
            return RecoveryExit.JournalUnreadable;
        }
        var state = ((StateRead.Valid)stateRead).State;

        if (session is SessionRead.Missing && state.IsCleanExceptWaitingAudio
            && state.SavedAudioOutputs.All(a => _os.Audio.Status(a.DeviceId) == AudioDeviceStatus.Away))
        {
            // The common case, every minute: nothing to do, or only saved
            // volumes whose devices are confirmed away. A device whose state
            // cannot be read may be connected and muted, so it gets a run.
            ForgetNotice();
            return RecoveryExit.Ok;
        }

        Log.Info(session switch
        {
            SessionRead.Valid v => $"recovery: session ended at {Json.FormatDate(v.Session.EndsAt)}; restoring",
            SessionRead.Unreadable u => $"recovery: {u.Reason}; treating it as ended",
            _ => "recovery: no session but the journal has entries; restoring",
        });

        // The session goes first. Once it is gone, nothing (an extension
        // waiting in the app, a relaunch) can take it for a live session
        // whose lid action still holds; and if this run stops halfway, the
        // journal entries alone bring the next run back here.
        switch (session)
        {
            case SessionRead.Valid:
                try
                {
                    _store.DeleteSession();
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    Log.Error("recovery: session.json could not be deleted", e);
                }
                break;
            case SessionRead.Unreadable:
                try
                {
                    var aside = _store.SetAsideSession(now);
                    Log.Info($"recovery: unreadable session.json moved to {aside}");
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    Log.Error("recovery: unreadable session.json could not be moved aside", e);
                }
                break;
        }

        var report = new Restorer(_os).RestoreAll(state, () => _store.WriteState(state));
        foreach (var line in report.Restored)
        {
            Log.Info($"recovery restored: {line}");
        }
        foreach (var failure in report.Failures)
        {
            Log.Error($"recovery failed: {failure}");
        }

        foreach (var p in report.NeedsAttention)
        {
            Log.Warn($"recovery: {p.Image} ({p.Pid}) may still be paused after an interrupted resume; left for the user");
        }
        if (report.Restored.Count > 0)
        {
            _os.Notifier.Notify("Sleep settings restored", $"Insomnia was not running when its session ended, so the recovery agent restored: {string.Join("; ", report.Restored)}.");
        }
        if (!report.IsComplete)
        {
            var problems = report.Failures.Concat(report.NeedsAttention.Select(p =>
                $"{p.Image} (pid {p.Pid}) may still be paused after an interrupted resume; open Insomnia to resume it."));
            NotifyOnce("Restore incomplete", $"{string.Join(" ", problems)} The agent tries again every minute.");
        }
        else
        {
            ForgetNotice();
        }
        if (!report.IsComplete)
        {
            return RecoveryExit.Incomplete;
        }
        if (report.WaitingPowerMode.Count > 0)
        {
            Log.Info($"recovery: the power mode for {string.Join(" and ", report.WaitingPowerMode.Select(e => e.Source == Model.PowerSourceKind.Ac ? "plugged in" : "on battery"))} is restored when that power source is in use");
            return RecoveryExit.WaitingForPowerSource;
        }
        return RecoveryExit.Ok;
    }

    /// <summary>
    /// The agent runs every minute; a failure that lasts would otherwise post
    /// the same notification each time. It is posted again only when the
    /// message changes, and once the journal is clean the record goes.
    /// </summary>
    private void NotifyOnce(string title, string body)
    {
        var path = _store.Paths.RecoveryNotice;
        try
        {
            if (File.ReadAllText(path) == body)
            {
                return;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        _os.Notifier.Notify(title, body);
        try
        {
            JournalStore.AtomicWrite(path, body);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"recovery: could not record the notification: {e.Message}");
        }
    }

    private void ForgetNotice()
    {
        try
        {
            File.Delete(_store.Paths.RecoveryNotice);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
