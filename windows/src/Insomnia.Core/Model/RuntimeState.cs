using System.Text.Json.Serialization;

namespace Insomnia.Core.Model;

/// <summary>
/// The journal, persisted as <c>state.json</c>: everything Insomnia changed
/// and must undo. An entry is written before its change is made and removed
/// only after its undo succeeded, so the journal on disk, never memory, is
/// the source of truth for ending a session, reconcile at launch and the
/// recovery agent.
/// </summary>
public sealed class RuntimeState
{
    /// <summary>Lid-close action overrides, one per power scheme Insomnia changed.</summary>
    public List<LidPolicyEntry> LidPolicy { get; set; } = [];

    /// <summary>
    /// True while Insomnia wants power saving on (a floor, the thermal rule or
    /// a closed lid). The per-source undo lives in <see cref="PowerMode"/>.
    /// </summary>
    public bool PowerSavingSetByUs { get; set; }

    /// <summary>Power mode overlays Insomnia changed, or may have changed, one per power source.</summary>
    public List<PowerModeEntry> PowerMode { get; set; } = [];

    /// <summary>
    /// Power sources whose mode the user moved off Insomnia's value while
    /// Insomnia held it. Insomnia does not take them back until it releases
    /// power saving or a new session starts, also across a relaunch.
    /// </summary>
    public List<PowerSourceKind> PowerModeRelinquished { get; set; } = [];

    /// <summary>Processes Insomnia suspended, or is about to.</summary>
    public List<FrozenProcess> FrozenProcesses { get; set; } = [];

    /// <summary>True while Docker Desktop's processes are suspended by the Docker rule.</summary>
    public bool DockerFrozen { get; set; }

    /// <summary>
    /// Each output device a lid close muted, with what it had. An entry for a
    /// device that is not connected waits for it.
    /// </summary>
    public List<SavedAudioOutput> SavedAudioOutputs { get; set; } = [];

    /// <summary>Power-throttling overrides on agent processes.</summary>
    public List<ThrottleOverride> ThrottleOverrides { get; set; } = [];

    /// <summary>
    /// True when nothing is left to undo except saved audio for devices that
    /// are away. Those can wait days for their device; the menu names them.
    /// </summary>
    [JsonIgnore]
    public bool IsCleanExceptWaitingAudio =>
        LidPolicy.Count == 0
        && !PowerSavingSetByUs
        && PowerMode.Count == 0
        && PowerModeRelinquished.Count == 0
        && FrozenProcesses.Count == 0
        && !DockerFrozen
        && ThrottleOverrides.Count == 0;

    [JsonIgnore]
    public bool IsEmpty => IsCleanExceptWaitingAudio && SavedAudioOutputs.Count == 0;

    public RuntimeState Clone() => new()
    {
        LidPolicy = [.. LidPolicy],
        PowerSavingSetByUs = PowerSavingSetByUs,
        PowerMode = [.. PowerMode],
        PowerModeRelinquished = [.. PowerModeRelinquished],
        FrozenProcesses = [.. FrozenProcesses],
        DockerFrozen = DockerFrozen,
        SavedAudioOutputs = [.. SavedAudioOutputs],
        ThrottleOverrides = [.. ThrottleOverrides],
    };
}

/// <summary>
/// One power scheme whose lid-close action Insomnia set to "Do nothing".
/// Values are the raw <c>LIDACTION</c> indexes (0 do nothing, 1 sleep,
/// 2 hibernate, 3 shut down). Undo writes <see cref="PreviousAc"/> or
/// <see cref="PreviousDc"/> back only while the value still reads
/// <see cref="Applied"/>; a value the user changed meanwhile is theirs.
/// </summary>
public sealed record LidPolicyEntry(Guid Scheme, uint PreviousAc, uint PreviousDc, uint Applied = LidActions.DoNothing);

public static class LidActions
{
    public const uint DoNothing = 0;
    public const uint Sleep = 1;
    public const uint Hibernate = 2;
    public const uint ShutDown = 3;

    public static string Describe(uint value) => value switch
    {
        DoNothing => "Do nothing",
        Sleep => "Sleep",
        Hibernate => "Hibernate",
        ShutDown => "Shut down",
        _ => $"action {value}",
    };
}

/// <summary>Power source a value applies to.</summary>
public enum PowerSourceKind
{
    Ac,
    Dc,
}

/// <summary>
/// The power mode overlay Insomnia set, or may have set, for one power
/// source. Both sources' baselines are journaled before any write, because
/// the charger can be plugged in or out between choosing the source and the
/// write landing. <see cref="Confirmed"/> is true once Insomnia saw the
/// source read <see cref="Applied"/> after its write. Undo writes
/// <see cref="Previous"/> back only while that source still reads
/// <see cref="Applied"/>, and only while that source is the active one,
/// since Windows applies an overlay to the current source only. An entry for
/// the other source waits until it becomes active; one whose source never
/// changed reads its previous value and is dropped.
/// <para>
/// <see cref="AlsoApplied"/> is a second value that counts as Insomnia's: it
/// is journaled before a restore of the other source, whose write a charger
/// change could land here, so this entry still recognizes the source as
/// Insomnia's afterwards and its baseline is not lost.
/// </para>
/// </summary>
public sealed record PowerModeEntry(PowerSourceKind Source, Guid Previous, Guid Applied, bool Confirmed = true, Guid? AlsoApplied = null)
{
    /// <summary>True when <paramref name="value"/> is a value Insomnia may have written to this source.</summary>
    public bool IsOurs(Guid value) => value == Applied || value == AlsoApplied;
}

/// <summary>Well-known power mode overlay GUIDs.</summary>
public static class PowerOverlays
{
    /// <summary>"Best power efficiency".</summary>
    public static readonly Guid BestPowerEfficiency = new("961cc777-2547-4f9d-8174-7d86181b8a7a");

    /// <summary>"Balanced" (no overlay).</summary>
    public static readonly Guid Balanced = Guid.Empty;

    /// <summary>"Best performance".</summary>
    public static readonly Guid BestPerformance = new("ded574b5-45a0-4f42-8737-46345c09c238");

    public static string Describe(Guid overlay) =>
        overlay == BestPowerEfficiency ? "Best power efficiency"
        : overlay == Balanced ? "Balanced"
        : overlay == BestPerformance ? "Best performance"
        : overlay.ToString();
}

/// <summary>
/// A process Insomnia suspended. Identity is the pid plus the process
/// creation time (FILETIME, UTC, 100 ns ticks), which a reused pid never
/// matches. <see cref="Confirmed"/> is false between the journal write and
/// the suspend call returning; such an entry is resumed only when every
/// thread of the process reads suspended, and otherwise dropped.
/// <see cref="Resuming"/> is written before each resume: suspend counts are
/// counted, so a resume must never be replayed, and an entry found still
/// resuming (the resume ran but its removal was never recorded) is not
/// resumed again.
/// </summary>
public sealed record FrozenProcess(int Pid, long CreationTime, string Image, string Group, bool Confirmed, bool Resuming = false);

/// <summary>
/// An output device a lid close muted. <see cref="SaveId"/> is drawn for each
/// save, so "Stop waiting" drops only the save the menu showed.
/// </summary>
public sealed record SavedAudioOutput(string DeviceId, string? Name, float Volume, bool Muted, Guid SaveId);

/// <summary>
/// A power-throttling override on one process. The masks are the process's
/// <c>PROCESS_POWER_THROTTLING_STATE</c> before Insomnia changed it. The
/// setting lives only as long as the process, so a gone process clears the
/// entry.
/// </summary>
public sealed record ThrottleOverride(int Pid, long CreationTime, string Image, uint PreviousControlMask, uint PreviousStateMask);
