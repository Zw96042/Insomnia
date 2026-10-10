using System.Drawing;
using System.Windows.Forms;
using Insomnia.App.Composition;
using Insomnia.Core.Engine;
using Insomnia.Core.Logging;
using Insomnia.Core.Platform;
using Insomnia.Core.Store;

namespace Insomnia.App.Recovery;

/// <summary>
/// Command-line entry points that run without the tray app:
/// <c>--recover</c> (the scheduled task, every minute), and
/// <c>--arm-agent</c> / <c>--remove-agent</c> (the install scripts).
/// </summary>
public static class RecoveryEntry
{
    /// <summary><c>--recover</c>: one recovery run. Exit codes are <see cref="RecoveryExit"/>.</summary>
    public static int Run(string[] args)
    {
        // The common case, every minute: no journal and no session, or a
        // session still running. Decide that from the files alone before
        // starting audio, Wi-Fi and window threads. The runner checks again
        // under the lock, so a stale answer here only costs one more minute.
        var paths = Paths.Default();
        var store = new JournalStore(paths);
        var nothingJournaled = store.ReadState(out var raw) is StateRead.Valid && raw is null;
        var session = store.ReadSession();
        if (nothingJournaled && (session is SessionRead.Missing
            || (session is SessionRead.Valid valid && !valid.Session.IsExpired(DateTimeOffset.UtcNow))))
        {
            return RecoveryExit.Ok;
        }

        ConfigureLogging("recover");
        var notifier = new TransientNotifier();
        try
        {
            using var host = new WindowsHost(notifier, new NoPowerEvents());
            return new RecoveryRunner(host.Services, host.Store, host.Lock).Run();
        }
        catch (Exception e)
        {
            Log.Error("recovery run failed", e);
            return RecoveryExit.Incomplete;
        }
        finally
        {
            notifier.Flush();
        }
    }

    /// <summary><c>--arm-agent</c>: register the recovery task for this executable. 0 when armed.</summary>
    public static int ArmAgent()
    {
        ConfigureLogging("install");
        if (Paths.Default().IsOverride)
        {
            // The task always recovers the default folder; arming it for a
            // session kept elsewhere would leave that session unrecoverable.
            Log.Error("--arm-agent refused: INSOMNIA_HOME is set");
            return 1;
        }
        var (state, error) = NewAgent().Arm();
        if (state != RecoveryAgentState.Armed)
        {
            Log.Error($"--arm-agent: {state}: {error}");
            return 1;
        }
        return 0;
    }

    /// <summary><c>--remove-agent</c>: delete the recovery task. 0 when it is gone.</summary>
    public static int RemoveAgent()
    {
        ConfigureLogging("uninstall");
        var agent = NewAgent();
        return agent.Remove() || agent.Check() == RecoveryAgentState.Missing ? 0 : 1;
    }

    private static Insomnia.Windows.SystemServices.RecoveryAgentTask NewAgent() =>
        new(Environment.ProcessPath!);

    private static void ConfigureLogging(string tag)
    {
        try
        {
            var paths = Paths.Default();
            paths.EnsureFolders();
            Log.Configure(paths.InsomniaLog, tag);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"log folder unavailable: {e.Message}");
        }
    }

    /// <summary>The recovery agent never watches power events; it runs once and exits.</summary>
    private sealed class NoPowerEvents : IPowerEvents
    {
        public bool? LidClosed => null;

        public event EventHandler<bool>? LidChanged { add { } remove { } }

        public event EventHandler? PowerSourceChanged { add { } remove { } }

        public event EventHandler? ActiveSchemeChanged { add { } remove { } }

        public event EventHandler? Resumed { add { } remove { } }
    }

    /// <summary>
    /// Notifications for a process with no tray icon of its own: the
    /// notifications are collected during the run and shown at the end from
    /// a temporary tray icon, kept alive long enough for Windows to raise the
    /// toast. Only runs that restored something notify, so this is rare.
    /// </summary>
    private sealed class TransientNotifier : INotifier
    {
        private readonly List<(string Title, string Body)> _pending = [];

        public void Notify(string title, string body)
        {
            Log.Info($"notify: {title}: {body}");
            lock (_pending)
            {
                _pending.Add((title, body));
            }
        }

        public void Flush()
        {
            (string Title, string Body)[] notes;
            lock (_pending)
            {
                notes = [.. _pending];
                _pending.Clear();
            }
            if (notes.Length == 0)
            {
                return;
            }
            try
            {
                using var icon = new NotifyIcon { Icon = SystemIcons.Information, Text = "Insomnia recovery", Visible = true };
                foreach (var (title, body) in notes)
                {
                    icon.ShowBalloonTip(10_000, title, body.Length > 255 ? body[..252] + "..." : body, ToolTipIcon.Info);
                    var until = DateTime.UtcNow.AddSeconds(4);
                    while (DateTime.UtcNow < until)
                    {
                        Application.DoEvents();
                        Thread.Sleep(50);
                    }
                }
            }
            catch (Exception e)
            {
                Log.Error("recovery notification failed", e);
            }
        }
    }
}
