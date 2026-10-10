using Insomnia.Core.Model;
using Insomnia.Core.Platform;

namespace Insomnia.Core.Logic;

/// <summary>Where a group in the freeze plan came from.</summary>
public enum FreezeOrigin
{
    /// <summary>The user's freeze list.</summary>
    Explicit,
    /// <summary>"Freeze every other app".</summary>
    Automatic,
}

/// <summary>One app to suspend: its processes and their helpers.</summary>
public sealed record FreezeGroup(string Image, string DisplayName, FreezeOrigin Origin, IReadOnlyList<ProcessInfo> Processes);

/// <summary>
/// Decides which processes a lid close suspends. Pure: it reads a process
/// snapshot and the settings, and changes nothing.
/// </summary>
public static class FreezePlanner
{
    /// <summary>
    /// Image names that can never be frozen, from either scope: the Windows
    /// shell and its hosts that live outside <c>%WINDIR%</c>, Insomnia itself,
    /// and Docker Desktop (the Docker rule handles it).
    /// </summary>
    public static readonly IReadOnlySet<string> HardDenylist = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Insomnia.exe",
        "explorer.exe",
        "Docker Desktop.exe",
        "msedgewebview2.exe", // shared WebView2 runtime; freezing it stalls unrelated apps
        "SecurityHealthSystray.exe",
        "OneDrive.exe",       // pausing a sync engine mid-transfer risks conflicts
    };

    /// <summary>
    /// Built-in protected set: editors and agent hosts, terminals, browsers,
    /// VPN and local model runtimes, password managers, local databases, and
    /// meeting, recording and dictation apps. The automatic scope leaves them
    /// alone even when they are not on the agent list. An explicit freeze-list
    /// entry overrides this set; the hard deny list does not. Code level and
    /// not persisted, so new entries reach existing installs.
    /// </summary>
    public static readonly IReadOnlySet<string> BuiltInProtected = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // Editors and agent hosts
        "Code.exe", "Code - Insiders.exe", "VSCodium.exe", "Cursor.exe", "Windsurf.exe", "Zed.exe",
        "Antigravity.exe", "studio64.exe", "sublime_text.exe", "devenv.exe", "notepad++.exe",
        "claude.exe", "ChatGPT.exe", "Codex.exe", "Conductor.exe", "T3 Code.exe",
        // Terminals
        "WindowsTerminal.exe", "wt.exe", "OpenConsole.exe", "wezterm-gui.exe", "alacritty.exe",
        "Tabby.exe", "Hyper.exe", "Warp.exe", "mintty.exe", "ConEmu64.exe", "ConEmu.exe",
        // Browsers
        "chrome.exe", "chromium.exe", "msedge.exe", "brave.exe", "vivaldi.exe", "opera.exe",
        "firefox.exe", "zen.exe", "arc.exe", "floorp.exe", "librewolf.exe",
        // VPN, local models, containers
        "tailscale-ipn.exe", "tailscaled.exe", "LM Studio.exe", "ollama.exe", "ollama app.exe",
        "Docker Desktop.exe", "Rancher Desktop.exe", "Podman Desktop.exe",
        // Password managers
        "1Password.exe", "Bitwarden.exe", "KeePassXC.exe", "KeePass.exe",
        // Local databases
        "postgres.exe", "pgAdmin4.exe", "mysqld.exe", "mongod.exe", "redis-server.exe",
        // Meeting, recording and dictation apps: freezing one ends the call,
        // the recording or the notes when the lid closes.
        "Zoom.exe", "ms-teams.exe", "Teams.exe", "Webex.exe", "CiscoCollabHost.exe", "atmgr.exe",
        "Wispr Flow.exe", "Granola.exe", "Otter.exe", "obs64.exe", "obs32.exe", "Loom.exe",
        "Discord Voice.exe", "Skype.exe", "Slack Huddle.exe",
    };

    /// <summary>
    /// Path segments that protect every app installed under them, the way
    /// upstream protects every JetBrains IDE by bundle-id prefix.
    /// </summary>
    public static readonly IReadOnlyList<string> ProtectedPathSegments =
    [
        @"\JetBrains\",
        @"\Zoom\",
    ];

    public static bool IsBuiltInProtected(ProcessInfo process) =>
        BuiltInProtected.Contains(process.Image)
        || (process.Path is { } path && ProtectedPathSegments.Any(s => path.Contains(s, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// Builds the plan. Order: the explicit list first, in its own order, then
    /// the automatic candidates by display name, de-duplicated.
    /// </summary>
    public static IReadOnlyList<FreezeGroup> Plan(IReadOnlyList<ProcessInfo> snapshot, Config config, int selfPid, string windowsDirectory)
    {
        var agents = new HashSet<string>(config.AgentList, StringComparer.OrdinalIgnoreCase);
        var windowsPrefix = windowsDirectory.TrimEnd('\\') + "\\";

        bool Eligible(ProcessInfo p) =>
            p.Pid != selfPid
            && p.Pid > 4
            && p.IsCurrentUser
            && p.CreationTime != 0
            && !HardDenylist.Contains(p.Image)
            && !agents.Contains(p.Image)
            && !(p.Path is { } path && path.StartsWith(windowsPrefix, StringComparison.OrdinalIgnoreCase));

        var groups = new List<FreezeGroup>();
        var claimed = new HashSet<int>();

        void AddGroup(string image, FreezeOrigin origin, bool allowProtected)
        {
            var roots = snapshot.Where(p => string.Equals(p.Image, image, StringComparison.OrdinalIgnoreCase) && Eligible(p)).ToList();
            if (roots.Count == 0)
            {
                return;
            }
            if (!allowProtected && roots.Any(IsBuiltInProtected))
            {
                return;
            }
            var members = CollectTree(snapshot, roots, p => Eligible(p) && (!IsBuiltInProtected(p) || string.Equals(p.Image, image, StringComparison.OrdinalIgnoreCase)))
                .Where(p => claimed.Add(p.Pid))
                .OrderBy(p => p.CreationTime)
                .ToList();
            if (members.Count == 0)
            {
                return;
            }
            var display = roots.FirstOrDefault(r => r.HasVisibleWindow)?.DisplayName ?? roots[0].DisplayName;
            groups.Add(new FreezeGroup(roots[0].Image, display, origin, members));
        }

        foreach (var image in config.FreezeList)
        {
            AddGroup(image, FreezeOrigin.Explicit, allowProtected: true);
        }

        if (config.FreezeAllApps)
        {
            var candidates = snapshot
                .Where(p => p.HasVisibleWindow && Eligible(p) && !IsBuiltInProtected(p) && !claimed.Contains(p.Pid))
                .GroupBy(p => p.Image, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            foreach (var candidate in candidates)
            {
                // A helper window of an app already claimed is part of that app.
                if (claimed.Contains(candidate.Pid))
                {
                    continue;
                }
                AddGroup(candidate.Image, FreezeOrigin.Automatic, allowProtected: false);
            }
        }

        return groups;
    }

    /// <summary>
    /// Every root plus every descendant reachable by parent pid. A child must
    /// be created after its parent, which rules out a parent pid that was
    /// reused by an unrelated, later process.
    /// </summary>
    internal static IEnumerable<ProcessInfo> CollectTree(IReadOnlyList<ProcessInfo> snapshot, IEnumerable<ProcessInfo> roots, Func<ProcessInfo, bool> include)
    {
        var byParent = snapshot.ToLookup(p => p.ParentPid);
        var seen = new HashSet<int>();
        var queue = new Queue<ProcessInfo>();
        foreach (var root in roots)
        {
            if (seen.Add(root.Pid))
            {
                queue.Enqueue(root);
            }
        }
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            yield return current;
            foreach (var child in byParent[current.Pid])
            {
                if (child.Pid == current.Pid || child.CreationTime < current.CreationTime || !include(child))
                {
                    continue;
                }
                if (seen.Add(child.Pid))
                {
                    queue.Enqueue(child);
                }
            }
        }
    }

    /// <summary>The "Would freeze now" preview: display names by scope.</summary>
    public static (IReadOnlyList<string> Explicit, IReadOnlyList<string> Automatic) Preview(IReadOnlyList<FreezeGroup> plan) =>
        (plan.Where(g => g.Origin == FreezeOrigin.Explicit).Select(g => g.DisplayName).ToList(),
         plan.Where(g => g.Origin == FreezeOrigin.Automatic).Select(g => g.DisplayName).ToList());
}
