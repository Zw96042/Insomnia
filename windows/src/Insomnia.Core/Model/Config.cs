using System.Text.Json.Serialization;

namespace Insomnia.Core.Model;

/// <summary>
/// User settings, persisted as <c>config.json</c>. Every property has a
/// default, so a file written by an older build, or edited by hand with keys
/// missing, loads with the missing values filled in.
/// </summary>
public sealed record Config
{
    // Session

    /// <summary>Preset durations in seconds, offered in the flyout.</summary>
    public List<double> Presets { get; init; } = [.. DefaultPresets];

    /// <summary>Duration started when Enter is pressed with every field empty.</summary>
    public double DefaultPreset { get; init; } = 4 * 3600;

    /// <summary>Hard ceiling on a session, extensions included. 30 days.</summary>
    public double MaxDuration { get; init; } = 30 * 24 * 3600;

    // Lid-close actions

    /// <summary>
    /// Executable names (for example <c>slack.exe</c>) suspended while the
    /// lid is closed. Matched case-insensitively against the image name.
    /// An entry here overrides the built-in protected set, never the hard
    /// deny list.
    /// </summary>
    public List<string> FreezeList { get; init; } = [.. DefaultFreezeList];

    /// <summary>
    /// Also suspend every other app with a visible top-level window that is
    /// not an agent app, a Windows component, Docker Desktop or built-in
    /// protected. Off by default: only the freeze list is suspended.
    /// </summary>
    public bool FreezeAllApps { get; init; }

    /// <summary>
    /// Suspend Docker Desktop on lid close when <c>docker ps -q</c> is empty
    /// on two checks, the second right before the suspend. Off by default.
    /// </summary>
    public bool DockerRule { get; init; }

    /// <summary>Mute the default output on lid close and restore it on open.</summary>
    public bool MuteOnLidClose { get; init; } = true;

    /// <summary>Ask every display to power off on lid close, and wake them on open.</summary>
    public bool DisplaysOffOnLidClose { get; init; } = true;

    /// <summary>
    /// Switch Windows to its "Best power efficiency" power mode while the lid
    /// is closed. With sleep held off, a closed laptop otherwise keeps running
    /// at full speed.
    /// </summary>
    public bool PowerSavingOnLidClose { get; init; } = true;

    // Agent apps

    /// <summary>
    /// Executable names of the apps doing the long-running work. They are
    /// never suspended, and with <see cref="DisableThrottlingForAgents"/> on
    /// they are exempted from Windows power throttling for the session.
    /// </summary>
    public List<string> AgentList { get; init; } = [.. DefaultAgentList];

    /// <summary>
    /// Turn Windows power throttling (EcoQoS) off for running agent processes
    /// while a session runs, and put each process's previous setting back at
    /// the end. Off: Insomnia never changes another process's settings.
    /// </summary>
    public bool DisableThrottlingForAgents { get; init; }

    // Battery and thermal floors

    /// <summary>Battery percentage below which power saving is switched on.</summary>
    public int LowPowerFloor { get; init; } = 40;

    /// <summary>
    /// Battery percentage below which the session ends. 0 turns the battery
    /// end off. Otherwise kept below <see cref="LowPowerFloor"/>.
    /// </summary>
    public int EndFloor { get; init; } = 10;

    /// <summary>Apply the thermal rules (power saving when throttling, end when critical).</summary>
    public bool ThermalRules { get; init; } = true;

    // Network failover

    /// <summary>SSID joined when Wi-Fi has been down for 5 s during a session. Empty: off.</summary>
    public string HotspotSsid { get; init; } = "";

    /// <summary>Outage length in seconds after which tmux panes are nudged.</summary>
    public double NudgeThreshold { get; init; } = 90;

    /// <summary>tmux targets as <c>session:window.pane</c>.</summary>
    public List<string> TmuxTargets { get; init; } = [];

    /// <summary>Press Enter after typing <c>continue</c> into a marked pane.</summary>
    public bool TmuxNudgePressesEnter { get; init; }

    /// <summary>
    /// Command that runs tmux, split on spaces before the tmux arguments.
    /// The default reaches tmux inside the default WSL distribution.
    /// </summary>
    public string TmuxCommand { get; init; } = "wsl.exe -e tmux";

    // App

    /// <summary>Start Insomnia when you sign in to Windows.</summary>
    public bool LaunchAtLogin { get; init; }

    public static readonly IReadOnlyList<double> DefaultPresets =
    [
        30 * 60,
        1 * 3600,
        2 * 3600,
        4 * 3600,
        8 * 3600,
        12 * 3600,
        24 * 3600,
        3 * 24 * 3600,
    ];

    /// <summary>Chat apps that burn battery in the background.</summary>
    public static readonly IReadOnlyList<string> DefaultFreezeList =
    [
        "slack.exe",
        "WhatsApp.exe",       // WhatsApp (Microsoft Store, UWP shell)
        "WhatsApp.Root.exe",  // WhatsApp (Microsoft Store, WebView2 build)
        "Discord.exe",
    ];

    /// <summary>
    /// Default agent list. Image names are matched case-insensitively, so the
    /// casing here is cosmetic. Edit the list in Settings.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultAgentList =
    [
        "T3 Code.exe",
        "Conductor.exe",
        "WindowsTerminal.exe",
        "wezterm-gui.exe",
        "alacritty.exe",
        "Warp.exe",
        "chrome.exe",
        "msedge.exe",
        "brave.exe",
        "Docker Desktop.exe",
        "Code.exe",
        "Cursor.exe",
        "Windsurf.exe",
        "Zed.exe",
        "Antigravity.exe",
        "claude.exe",
        "ChatGPT.exe",
        "Codex.exe",
        "tailscale-ipn.exe",
        "LM Studio.exe",
        "ollama app.exe",
        "ollama.exe",
    ];

    /// <summary>Settings step for both floors; the floors are kept at least one step apart.</summary>
    public const int FloorStep = 5;

    /// <summary>Highest end floor Settings offers: one step below a 100% power-saving floor.</summary>
    public const int MaxEndFloor = 100 - FloorStep;

    /// <summary>A non-zero end floor sits below the power-saving floor.</summary>
    [JsonIgnore]
    public bool FloorsAreOrdered => EndFloor == 0 || EndFloor < LowPowerFloor;

    /// <summary>
    /// Sets the end floor (0 to <see cref="MaxEndFloor"/>) and raises the
    /// power-saving floor one step above it when it would otherwise not be.
    /// </summary>
    public Config WithEndFloor(int value)
    {
        var end = Math.Clamp(value, 0, MaxEndFloor);
        var low = LowPowerFloor;
        if (end > 0 && low <= end)
        {
            low = Math.Min(end + FloorStep, 100);
        }
        return this with { EndFloor = end, LowPowerFloor = low };
    }

    /// <summary>
    /// Sets the power-saving floor (0 to 100) and lowers the end floor one
    /// step below it when it would otherwise not be. An end floor pushed to 0
    /// is off.
    /// </summary>
    public Config WithLowPowerFloor(int value)
    {
        var low = Math.Clamp(value, 0, 100);
        var end = EndFloor;
        if (end > 0 && end >= low)
        {
            end = Math.Max(low - FloorStep, 0);
        }
        return this with { LowPowerFloor = low, EndFloor = end };
    }

    /// <summary>
    /// Clamps every value to its range and restores the floor order by
    /// raising the power-saving floor, never by lowering the end the user
    /// asked for. Returns the corrected config and a log line describing each
    /// change, or no lines when nothing changed. Run on a config.json that
    /// Settings did not write.
    /// </summary>
    public (Config Config, IReadOnlyList<string> Changes) Normalized()
    {
        var changes = new List<string>();
        var low = Math.Clamp(LowPowerFloor, 0, 100);
        var end = Math.Clamp(EndFloor, 0, MaxEndFloor);
        if (end > 0 && end >= low)
        {
            low = Math.Min(end + FloorStep, 100);
        }
        if (low != LowPowerFloor || end != EndFloor)
        {
            changes.Add($"battery floors corrected: lowPowerFloor {LowPowerFloor} -> {low}, endFloor {EndFloor} -> {end}");
        }

        var max = MaxDuration;
        if (!double.IsFinite(max) || max < Logic.SessionMath.MinimumDuration || max > 30 * 24 * 3600)
        {
            max = 30 * 24 * 3600;
            changes.Add($"maxDuration {MaxDuration} out of range, reset to 30 days");
        }

        var presets = (Presets ?? []).Where(p => double.IsFinite(p) && p >= Logic.SessionMath.MinimumDuration && p <= max).Distinct().Order().ToList();
        if (presets.Count == 0)
        {
            presets = [.. DefaultPresets];
        }
        if (Presets is null || !presets.SequenceEqual(Presets))
        {
            changes.Add("presets cleaned (duplicates, out-of-range values or an empty list)");
        }

        var preset = DefaultPreset;
        if (!double.IsFinite(preset) || preset < Logic.SessionMath.MinimumDuration || preset > max)
        {
            preset = Math.Min(4 * 3600, max);
            changes.Add($"defaultPreset {DefaultPreset} out of range, reset to {preset}");
        }

        var nudge = NudgeThreshold;
        if (!double.IsFinite(nudge) || nudge < 0)
        {
            nudge = 90;
            changes.Add($"nudgeThreshold {NudgeThreshold} out of range, reset to 90");
        }

        var normalized = this with
        {
            LowPowerFloor = low,
            EndFloor = end,
            MaxDuration = max,
            Presets = presets,
            DefaultPreset = preset,
            NudgeThreshold = nudge,
            FreezeList = CleanNames(FreezeList),
            AgentList = CleanNames(AgentList),
            TmuxTargets = (TmuxTargets ?? []).Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.Ordinal).ToList(),
            HotspotSsid = HotspotSsid ?? "",
            TmuxCommand = string.IsNullOrWhiteSpace(TmuxCommand) ? "wsl.exe -e tmux" : TmuxCommand.Trim(),
        };
        return (normalized, changes);
    }

    private static List<string> CleanNames(List<string>? names) =>
        (names ?? []).Select(n => n.Trim()).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public bool Equals(Config? other) =>
        other is not null
        && Presets.SequenceEqual(other.Presets)
        && DefaultPreset.Equals(other.DefaultPreset)
        && MaxDuration.Equals(other.MaxDuration)
        && FreezeList.SequenceEqual(other.FreezeList)
        && FreezeAllApps == other.FreezeAllApps
        && DockerRule == other.DockerRule
        && MuteOnLidClose == other.MuteOnLidClose
        && DisplaysOffOnLidClose == other.DisplaysOffOnLidClose
        && PowerSavingOnLidClose == other.PowerSavingOnLidClose
        && AgentList.SequenceEqual(other.AgentList)
        && DisableThrottlingForAgents == other.DisableThrottlingForAgents
        && LowPowerFloor == other.LowPowerFloor
        && EndFloor == other.EndFloor
        && ThermalRules == other.ThermalRules
        && HotspotSsid == other.HotspotSsid
        && NudgeThreshold.Equals(other.NudgeThreshold)
        && TmuxTargets.SequenceEqual(other.TmuxTargets)
        && TmuxNudgePressesEnter == other.TmuxNudgePressesEnter
        && TmuxCommand == other.TmuxCommand
        && LaunchAtLogin == other.LaunchAtLogin;

    public override int GetHashCode() => HashCode.Combine(DefaultPreset, FreezeAllApps, LowPowerFloor, EndFloor, HotspotSsid, LaunchAtLogin);
}
