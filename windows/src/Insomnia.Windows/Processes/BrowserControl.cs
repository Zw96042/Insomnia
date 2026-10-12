using System.ComponentModel;
using System.Diagnostics;
using Insomnia.Core.Logging;
using Insomnia.Core.Platform;

namespace Insomnia.Windows.Processes;

/// <summary>
/// Chromium-family browsers and their occlusion throttling. The main browser
/// process is the one without a <c>--type=</c> argument; its command line,
/// read from the process itself, says whether both unthrottling flags are on
/// and carries the profile arguments a relaunch must keep.
/// </summary>
public sealed class BrowserControl : IBrowserControl
{
    public const string OccludedFlag = "--disable-backgrounding-occluded-windows";
    public const string RendererFlag = "--disable-renderer-backgrounding";

    /// <summary>
    /// Chromium's protocol-handler launch: everything after it is one URL the
    /// OS passed in. It is not a user setting, and repeating it on relaunch
    /// would open that link again on top of the restored session.
    /// </summary>
    private const string SingleArgumentSwitch = "--single-argument";

    /// <summary>
    /// Switches describing how the closed instance was started rather than
    /// what the user chose: a background start at login (Chrome background
    /// mode, Edge startup boost). Repeated, the relaunch would bring the
    /// browser back with no window.
    /// </summary>
    private static readonly string[] LaunchContextSwitches = ["--no-startup-window", "--win-session-start"];

    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    private static readonly Dictionary<string, string> KnownBrowsers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["chrome.exe"] = "Google Chrome",
        ["msedge.exe"] = "Microsoft Edge",
        ["brave.exe"] = "Brave",
        ["vivaldi.exe"] = "Vivaldi",
        ["opera.exe"] = "Opera",
        ["chromium.exe"] = "Chromium",
        ["arc.exe"] = "Arc",
    };

    private readonly IProcessCatalog _catalog;

    public BrowserControl(IProcessCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _catalog = catalog;
    }

    public IReadOnlyList<BrowserInstance> Scan()
    {
        var result = new List<BrowserInstance>();
        foreach (var main in MainInstances(image: null))
        {
            result.Add(new BrowserInstance(main.Process.Image, main.Name, main.Process.Pid, main.Process.CreationTime, IsUnthrottled(main.Arguments)));
        }
        var throttled = result.Where(b => !b.Unthrottled).Select(b => b.DisplayName).Distinct().ToList();
        if (throttled.Count > 0)
        {
            Log.Info($"browsers: throttled: {string.Join(", ", throttled)}");
        }
        return result;
    }

    public async Task<BrowserRelaunchResult> RelaunchAfterExitAsync(string image, TimeSpan wait, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(image);
        if (!KnownBrowsers.ContainsKey(image))
        {
            Log.Error($"relaunch: {image} is not a known Chromium browser");
            return BrowserRelaunchResult.NotRunning;
        }

        // Every main instance's arguments are read before anything is closed:
        // without them a relaunch could open the wrong profile.
        var instances = new List<MainInstance>();
        foreach (var process in MainCandidates(image))
        {
            var arguments = ReadArguments(process);
            if (arguments is null)
            {
                if (!ProcessHandles.IsAlive(process.Pid, process.CreationTime))
                {
                    continue;
                }
                Log.Error($"relaunch: could not read the command line of {image} (pid {process.Pid}); not closing it");
                return BrowserRelaunchResult.ArgumentsUnreadable;
            }
            if (IsMain(arguments))
            {
                if (process.Path is null)
                {
                    Log.Error($"relaunch: could not read the executable path of {image} (pid {process.Pid}); not closing it");
                    return BrowserRelaunchResult.ArgumentsUnreadable;
                }
                instances.Add(new MainInstance(process, NameFor(image), arguments));
            }
        }
        if (instances.Count == 0)
        {
            Log.Info($"relaunch: {image} is not running");
            return BrowserRelaunchResult.NotRunning;
        }
        if (cancellationToken.IsCancellationRequested)
        {
            return BrowserRelaunchResult.Cancelled;
        }

        // The user quits the browser from its own menu: that exit asks about
        // unsaved pages and saves every window, which closing it from here
        // would not. Wait for it, then relaunch.
        Log.Info($"relaunch: waiting up to {wait.TotalSeconds:0} s for {image} to be quit");

        var quit = await PollAsync(
            wait,
            () => instances.All(i => !ProcessHandles.IsAlive(i.Process.Pid, i.Process.CreationTime)),
            cancellationToken).ConfigureAwait(false);
        if (quit is null)
        {
            Log.Info($"relaunch: cancelled while {image} was closing");
            return BrowserRelaunchResult.Cancelled;
        }
        // An instance that started meanwhile would take the relaunch's
        // arguments as a new-window request and ignore the flags.
        var remaining = MainInstances(image).Select(m => m.Process.Pid).ToList();
        if (quit == false || remaining.Count > 0)
        {
            Log.Info($"relaunch: {image} still running after {wait.TotalSeconds:0} s (pids {string.Join(", ", remaining)}); not launching");
            return BrowserRelaunchResult.StillRunning;
        }

        // One launch per distinct command line: separate main instances use
        // separate user data directories.
        var launches = instances
            .Select(i => (Executable: i.Process.Path!, Arguments: RelaunchArguments(i.Arguments)))
            .DistinctBy(l => l.Executable + "\0" + string.Join("\0", l.Arguments), StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var (executable, arguments) in launches)
        {
            if (!Launch(executable, arguments))
            {
                return BrowserRelaunchResult.LaunchFailed;
            }
        }

        var started = await PollAsync(StartTimeout, () => MainInstances(image).Count > 0, cancellationToken).ConfigureAwait(false);
        if (started is null)
        {
            Log.Info($"relaunch: start check for {image} cancelled after launching; not reporting");
            return BrowserRelaunchResult.Cancelled;
        }
        if (started == false)
        {
            Log.Error($"relaunch: {image} not running {StartTimeout.TotalSeconds:0} s after launching");
            return BrowserRelaunchResult.LaunchFailed;
        }
        Log.Info($"relaunch: {image} relaunched unthrottled");
        return BrowserRelaunchResult.Relaunched;
    }

    /// <summary>True when both flags are present as their own arguments (a value after <c>=</c> still counts).</summary>
    internal static bool IsUnthrottled(IReadOnlyList<string> arguments) =>
        HasSwitch(arguments, OccludedFlag) && HasSwitch(arguments, RendererFlag);

    /// <summary>
    /// Reopens the windows the browser saved when it was closed, whatever its
    /// "On startup" setting says.
    /// </summary>
    public const string RestoreSessionFlag = "--restore-last-session";

    /// <summary>
    /// The original arguments minus the program, both flags, background-start
    /// switches and any protocol-handler URL, with both flags and
    /// <see cref="RestoreSessionFlag"/> appended.
    /// </summary>
    internal static List<string> RelaunchArguments(IReadOnlyList<string> arguments)
    {
        var kept = new List<string>();
        for (var i = 1; i < arguments.Count; i++)
        {
            var argument = arguments[i];
            if (string.Equals(argument, SingleArgumentSwitch, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
            if (IsSwitch(argument, OccludedFlag) || IsSwitch(argument, RendererFlag) || IsSwitch(argument, RestoreSessionFlag)
                || LaunchContextSwitches.Any(name => IsSwitch(argument, name)))
            {
                continue;
            }
            kept.Add(argument);
        }
        kept.Add(OccludedFlag);
        kept.Add(RendererFlag);
        kept.Add(RestoreSessionFlag);
        return kept;
    }

    private static bool IsMain(IReadOnlyList<string> arguments)
    {
        for (var i = 1; i < arguments.Count; i++)
        {
            if (arguments[i].StartsWith("--type=", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        return true;
    }

    private static bool HasSwitch(IReadOnlyList<string> arguments, string name)
    {
        for (var i = 1; i < arguments.Count; i++)
        {
            if (IsSwitch(arguments[i], name))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsSwitch(string argument, string name) =>
        argument.StartsWith(name, StringComparison.OrdinalIgnoreCase)
        && (argument.Length == name.Length || argument[name.Length] == '=');

    private static string NameFor(string image) => KnownBrowsers.TryGetValue(image, out var name) ? name : image;

    /// <summary>
    /// This user's known-browser processes, of one image or of all, that
    /// could be a main process: those whose parent is not a running process
    /// of the same image created before them. Renderers, GPU and utility
    /// processes are children of the main process, so their command lines
    /// need not be read; the <c>--type=</c> test settles the rest.
    /// </summary>
    private List<ProcessInfo> MainCandidates(string? image)
    {
        var browsers = _catalog.Snapshot()
            .Where(p => p.IsCurrentUser
                && (image is null ? KnownBrowsers.ContainsKey(p.Image) : string.Equals(p.Image, image, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var byPid = browsers.ToDictionary(p => p.Pid);
        return browsers
            .Where(p => !(byPid.TryGetValue(p.ParentPid, out var parent)
                && string.Equals(parent.Image, p.Image, StringComparison.OrdinalIgnoreCase)
                && parent.CreationTime <= p.CreationTime))
            .ToList();
    }

    /// <summary>Main browser processes whose command line could be read; others are logged and skipped.</summary>
    private List<MainInstance> MainInstances(string? image)
    {
        var result = new List<MainInstance>();
        foreach (var process in MainCandidates(image))
        {
            var arguments = ReadArguments(process);
            if (arguments is null)
            {
                if (ProcessHandles.IsAlive(process.Pid, process.CreationTime))
                {
                    Log.Warn($"browsers: could not read the command line of {process.Image} (pid {process.Pid})");
                }
                continue;
            }
            if (IsMain(arguments))
            {
                result.Add(new MainInstance(process, NameFor(process.Image), arguments));
            }
        }
        return result;
    }

    private static string[]? ReadArguments(ProcessInfo process)
    {
        var commandLine = ProcessHandles.ReadCommandLine(process.Pid, process.CreationTime);
        return commandLine is null ? null : ProcessHandles.SplitCommandLine(commandLine);
    }

    private static bool Launch(string executable, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(executable) ?? string.Empty,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                Log.Error($"relaunch: {executable} did not start");
                return false;
            }
            Log.Info($"relaunch: started {executable} (pid {process.Id}) with {arguments.Count} argument(s)");
            return true;
        }
        catch (Win32Exception ex)
        {
            Log.Error($"relaunch: could not start {executable}: {ex.Message}");
            return false;
        }
        catch (InvalidOperationException ex)
        {
            Log.Error($"relaunch: could not start {executable}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Checks <paramref name="condition"/> every poll interval until it holds
    /// (true) or the timeout passes (false). Null when cancelled.
    /// </summary>
    private static async Task<bool?> PollAsync(TimeSpan timeout, Func<bool> condition, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return null;
            }
            if (condition())
            {
                return true;
            }
            if (clock.Elapsed >= timeout)
            {
                return false;
            }
            try
            {
                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }
    }

    private sealed record MainInstance(ProcessInfo Process, string Name, string[] Arguments);
}
