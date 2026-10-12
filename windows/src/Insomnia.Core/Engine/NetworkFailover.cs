using System.Globalization;
using Insomnia.Core.Logging;
using Insomnia.Core.Logic;
using Insomnia.Core.Model;
using Insomnia.Core.Platform;

namespace Insomnia.Core.Engine;

/// <summary>What the failover tells the engine.</summary>
public abstract record FailoverEvent
{
    private FailoverEvent() { }

    /// <summary>A join was skipped because Credential Manager has no password for the SSID.</summary>
    public sealed record PasswordMissing(string Ssid) : FailoverEvent;

    /// <summary>Wi-Fi is back.</summary>
    public sealed record Recovered(TimeSpan Gap) : FailoverEvent;
}

/// <summary>
/// During a session, watches Wi-Fi connectivity. After 5 s without it, joins
/// the configured hotspot, retrying with backoff (5, 10, 20, 30 s, then every
/// 30 s) until Wi-Fi is back or the session ends. Every outage is logged to
/// <c>handoffs.log</c>, and an outage longer than the nudge threshold nudges
/// the marked tmux panes.
/// </summary>
public sealed class NetworkFailover : IDisposable
{
    private static readonly TimeSpan[] Backoff =
    [
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(20),
        TimeSpan.FromSeconds(30),
    ];

    private readonly PlatformServices _os;
    private readonly TimeProvider _time;
    private readonly Func<Config> _config;
    private readonly RotatingLog _handoffs;
    private readonly Action<FailoverEvent> _report;
    private readonly object _gate = new();

    private bool _running;
    private DateTimeOffset? _outageStart;
    private int _attempt;
    private bool _passwordWarned;
    private ITimer? _joinTimer;
    private CancellationTokenSource? _joinCts;
    private string? _lastGap;

    public NetworkFailover(PlatformServices os, TimeProvider time, Func<Config> config, RotatingLog handoffs, Action<FailoverEvent> report)
    {
        _os = os;
        _time = time;
        _config = config;
        _handoffs = handoffs;
        _report = report;
    }

    /// <summary>"2m 10s at 14:02", or null before the first outage.</summary>
    public string? LastGap
    {
        get
        {
            lock (_gate)
            {
                return _lastGap;
            }
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_running)
            {
                return;
            }
            _running = true;
            _outageStart = null;
            _attempt = 0;
            _passwordWarned = false;
        }
        _os.Wifi.ConnectivityChanged += OnConnectivityChanged;
        if (_config().HotspotSsid.Length > 0)
        {
            _ = RequestAccess();
        }
        Evaluate();
    }

    public void Stop()
    {
        _os.Wifi.ConnectivityChanged -= OnConnectivityChanged;
        lock (_gate)
        {
            _running = false;
            _outageStart = null;
            CancelJoinLocked();
        }
    }

    private async Task RequestAccess()
    {
        try
        {
            if (!await _os.Wifi.RequestAccessAsync().ConfigureAwait(false))
            {
                Log.Warn("Wi-Fi access (location) not allowed; hotspot joins will fail until it is");
            }
        }
        catch (Exception e)
        {
            Log.Error("Wi-Fi access request failed", e);
        }
    }

    private void OnConnectivityChanged(object? sender, EventArgs e) => Evaluate();

    private void Evaluate()
    {
        WifiConnectivity state;
        try
        {
            state = _os.Wifi.Connectivity();
        }
        catch (Exception e)
        {
            Log.Error("Wi-Fi connectivity read failed", e);
            return;
        }
        var now = _time.GetUtcNow();
        // Callbacks into the engine run after the lock is released: the
        // engine takes its own lock and may read LastGap, which takes ours.
        (DateTimeOffset Start, TimeSpan Gap)? recovered = null;
        lock (_gate)
        {
            if (!_running)
            {
                return;
            }
            if (state == WifiConnectivity.Unsatisfied && _outageStart is null)
            {
                _outageStart = now;
                _attempt = 0;
                Log.Info("Wi-Fi lost");
                ScheduleJoinLocked(Backoff[0]);
            }
            else if (state == WifiConnectivity.Satisfied && _outageStart is { } start)
            {
                _outageStart = null;
                CancelJoinLocked();
                var gap = now - start;
                _lastGap = $"{FormatGap(gap)} at {start.ToLocalTime().ToString("t", CultureInfo.CurrentCulture)}";
                _passwordWarned = false;
                recovered = (start, gap);
            }
        }
        if (recovered is { } r)
        {
            _ = Recovered(r.Start, now, r.Gap);
        }
    }

    private async Task Recovered(DateTimeOffset start, DateTimeOffset end, TimeSpan gap)
    {
        _handoffs.Write(string.Create(CultureInfo.InvariantCulture,
            $"{start.ToLocalTime():yyyy-MM-dd HH:mm:ss} -> {end.ToLocalTime():HH:mm:ss}  gap {FormatGap(gap)}  ssid {_os.Wifi.CurrentSsid() ?? "?"}"));
        Log.Info($"Wi-Fi back after {FormatGap(gap)}");
        _report(new FailoverEvent.Recovered(gap));
        var config = _config();
        if (gap.TotalSeconds < config.NudgeThreshold)
        {
            return;
        }
        var nudged = await TmuxNudge.RunAsync(_os.Commands, config, CancellationToken.None).ConfigureAwait(false);
        var panes = nudged == 1 ? "1 tmux pane" : $"{nudged} tmux panes";
        _os.Notifier.Notify("Network back", $"Network was down {FormatGap(gap)}. Nudged {panes}. Check GUI agents.");
    }

    private void ScheduleJoinLocked(TimeSpan delay)
    {
        _joinTimer?.Dispose();
        _joinTimer = _time.CreateTimer(_ => _ = JoinAttempt(), null, delay, Timeout.InfiniteTimeSpan);
    }

    private void CancelJoinLocked()
    {
        _joinTimer?.Dispose();
        _joinTimer = null;
        _joinCts?.Cancel();
        _joinCts?.Dispose();
        _joinCts = null;
    }

    private async Task JoinAttempt()
    {
        var ssid = _config().HotspotSsid;
        CancellationToken token;
        lock (_gate)
        {
            if (!_running || _outageStart is null)
            {
                return;
            }
            if (ssid.Length == 0)
            {
                return;
            }
            _joinCts?.Dispose();
            _joinCts = new CancellationTokenSource();
            token = _joinCts.Token;
        }
        var password = _os.Credentials.ReadHotspotPassword(ssid);
        // A read can wait behind a Settings save: check everything again.
        var sameSsid = _config().HotspotSsid == ssid;
        bool stillWanted;
        lock (_gate)
        {
            stillWanted = sameSsid && _running && _outageStart is not null && !token.IsCancellationRequested;
        }
        if (!stillWanted)
        {
            return;
        }
        if (password is null)
        {
            bool warn;
            lock (_gate)
            {
                warn = !_passwordWarned;
                _passwordWarned = true;
            }
            Log.Warn($"hotspot join skipped: no password saved for {ssid}");
            if (warn)
            {
                _report(new FailoverEvent.PasswordMissing(ssid));
                _os.Notifier.Notify("Hotspot password missing", $"Wi-Fi is down and no password is saved for \"{ssid}\". Enter it in Insomnia's Settings > Network.");
            }
            ScheduleNext();
            return;
        }
        HotspotJoinResult result;
        try
        {
            result = await _os.Wifi.JoinAsync(ssid, password, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception e)
        {
            Log.Error("hotspot join failed", e);
            result = HotspotJoinResult.Failed;
        }
        Log.Info($"hotspot join {ssid}: {result}");
        if (result != HotspotJoinResult.Joined)
        {
            ScheduleNext();
        }
    }

    private void ScheduleNext()
    {
        lock (_gate)
        {
            if (!_running || _outageStart is null)
            {
                return;
            }
            _attempt++;
            ScheduleJoinLocked(Backoff[Math.Min(_attempt, Backoff.Length - 1)]);
        }
    }

    /// <summary>"2m 10s", "45s", "1h 3m".</summary>
    public static string FormatGap(TimeSpan gap)
    {
        var total = (long)Math.Max(0, Math.Round(gap.TotalSeconds));
        if (total < 60)
        {
            return $"{total}s";
        }
        if (total < 3600)
        {
            return total % 60 == 0 ? $"{total / 60}m" : $"{total / 60}m {total % 60}s";
        }
        return SessionMath.FormatRemaining(TimeSpan.FromSeconds(total));
    }

    public void Dispose()
    {
        Stop();
    }
}

/// <summary>
/// Types <c>continue</c> into tmux panes the user marked with the pane option
/// <c>@insomnia-nudge on</c>, after a long network outage. An unmarked pane
/// is skipped. tmux runs through <see cref="Config.TmuxCommand"/>, by default
/// inside WSL.
/// </summary>
public static class TmuxNudge
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>Returns how many panes were nudged.</summary>
    public static async Task<int> RunAsync(ICommandRunner runner, Config config, CancellationToken cancellationToken)
    {
        if (config.TmuxTargets.Count == 0)
        {
            return 0;
        }
        var parts = config.TmuxCommand.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return 0;
        }
        var file = parts[0];
        var prefix = parts.Skip(1).ToList();

        Task<CommandResult> Tmux(params string[] args) =>
            runner.RunAsync(file, [.. prefix, .. args], Timeout, cancellationToken);

        var nudged = 0;
        foreach (var target in config.TmuxTargets)
        {
            var resolved = await Tmux("display-message", "-p", "-t", target, "#{pane_id}").ConfigureAwait(false);
            var pane = resolved.StandardOutput.Trim();
            if (resolved.ExitCode != 0 || !IsPaneId(pane))
            {
                Log.Warn($"tmux nudge: {target} did not resolve to a pane");
                continue;
            }
            // Pane scope only, without -A, so a session or window option never counts.
            var mark = await Tmux("show-options", "-qpv", "-t", pane, "@insomnia-nudge").ConfigureAwait(false);
            if (mark.ExitCode != 0 || mark.StandardOutput.Trim() != "on")
            {
                Log.Info($"tmux nudge: {target} ({pane}) is not marked; skipped");
                continue;
            }
            var sent = await Tmux("send-keys", "-t", pane, "-l", "continue").ConfigureAwait(false);
            if (sent.ExitCode != 0)
            {
                Log.Warn($"tmux nudge: send-keys to {target} failed");
                continue;
            }
            if (config.TmuxNudgePressesEnter)
            {
                await Tmux("send-keys", "-t", pane, "Enter").ConfigureAwait(false);
            }
            Log.Info($"tmux nudge: nudged {target} ({pane})");
            nudged++;
        }
        return nudged;
    }

    internal static bool IsPaneId(string text) =>
        text.Length > 1 && text[0] == '%' && text[1..].All(char.IsAsciiDigit);
}
