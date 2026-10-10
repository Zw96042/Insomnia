using Insomnia.Core.Logging;
using Insomnia.Core.Model;
using Insomnia.Core.Platform;

namespace Insomnia.Core.Engine;

/// <summary>What one restore pass achieved.</summary>
public sealed class RestoreReport
{
    /// <summary>Undos that failed; their entries stay journaled for a retry.</summary>
    public List<string> Failures { get; } = [];

    /// <summary>Saved volumes whose device is away. Not a failure: they wait for the device.</summary>
    public List<SavedAudioOutput> WaitingAudio { get; } = [];

    /// <summary>Power mode entries for the power source not in use; restored when it is.</summary>
    public List<PowerModeEntry> WaitingPowerMode { get; } = [];

    /// <summary>What was put back, for the log and the recovery notification.</summary>
    public List<string> Restored { get; } = [];

    /// <summary>A journal write that should have cleared a restored entry failed.</summary>
    public bool JournalWriteFailed { get; set; }

    /// <summary>
    /// Processes whose resume was interrupted and that still read paused.
    /// Resuming them again could release another program's pause, so they
    /// stay journaled until they read running or gone, or the user resumes
    /// them from the menu. Retrying changes nothing for them.
    /// </summary>
    public List<FrozenProcess> NeedsAttention { get; } = [];

    /// <summary>Nothing left at all, as the recovery agent's exit code and uninstall need it.</summary>
    public bool IsComplete => HasNoRetryableProblems && NeedsAttention.Count == 0;

    /// <summary>No failure a retry could fix.</summary>
    public bool HasNoRetryableProblems => Failures.Count == 0 && !JournalWriteFailed;

    public void Merge(RestoreReport other)
    {
        Failures.AddRange(other.Failures);
        WaitingAudio.AddRange(other.WaitingAudio);
        WaitingPowerMode.AddRange(other.WaitingPowerMode);
        NeedsAttention.AddRange(other.NeedsAttention);
        Restored.AddRange(other.Restored);
        JournalWriteFailed |= other.JournalWriteFailed;
    }
}

/// <summary>
/// Undoes journaled changes. The end of a session, a lid open, reconcile at
/// launch and the recovery agent all go through here, so every path applies
/// the same guards: an entry is cleared only after its undo succeeded, and a
/// value is written back only while it still reads what Insomnia set.
/// The caller holds the recovery lock and supplies <c>save</c>, which writes
/// the journal and throws on failure.
/// </summary>
public sealed class Restorer
{
    private readonly PlatformServices _os;

    public Restorer(PlatformServices os)
    {
        _os = os;
    }

    /// <summary>
    /// Everything, in the order that keeps the machine from sleeping halfway:
    /// processes, audio, throttling, power mode, then the lid policy last.
    /// </summary>
    public RestoreReport RestoreAll(RuntimeState state, Action save)
    {
        var report = new RestoreReport();
        report.Merge(RestoreProcesses(state, save, onlyGroup: null));
        report.Merge(RestoreAudio(state, save));
        report.Merge(RestoreThrottling(state, save));
        report.Merge(RestorePowerMode(state, save, releaseOwnership: true));
        report.Merge(RestoreLidPolicy(state, save));
        return report;
    }

    /// <summary>
    /// Resumes journaled processes. Suspend counts are counted, so a resume
    /// is never replayed: each entry is marked <see cref="FrozenProcess.Resuming"/>
    /// in the journal before its resume and removed after it. An entry found
    /// still resuming had its resume run with the removal never recorded, or
    /// never run at all; the two cannot be told apart once another program
    /// may also hold the process suspended, so it is dropped with a failure
    /// line rather than resumed a second time. An unconfirmed entry, journaled
    /// before a suspend whose result was never recorded, is resumed only when
    /// every thread reads suspended; otherwise it was never suspended.
    /// </summary>
    public RestoreReport RestoreProcesses(RuntimeState state, Action save, string? onlyGroup)
    {
        var report = new RestoreReport();
        var entries = state.FrozenProcesses.Where(p => onlyGroup is null || p.Group == onlyGroup).ToList();
        if (entries.Count == 0)
        {
            return report;
        }
        var resumedGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (entry.Resuming)
            {
                var stillThere = _os.Freezer.Inspect(entry.Pid, entry.CreationTime);
                if (stillThere is ProcessState.FullySuspended or ProcessState.Unknown)
                {
                    // Never resumed again automatically; kept until it reads
                    // running or gone, or the user resumes it.
                    report.NeedsAttention.Add(entry);
                    continue;
                }
                Log.Info($"interrupted resume of {entry.Image} ({entry.Pid}) is resolved ({stillThere}); entry dropped");
                state.FrozenProcesses.Remove(entry);
                if (!TrySave(save, report))
                {
                    return report;
                }
                continue;
            }
            if (!entry.Confirmed)
            {
                switch (_os.Freezer.Inspect(entry.Pid, entry.CreationTime))
                {
                    case ProcessState.FullySuspended:
                        break;
                    case ProcessState.Unknown:
                        report.Failures.Add($"Could not check whether {entry.Image} (pid {entry.Pid}) is still paused.");
                        continue;
                    case var other:
                        Log.Info($"unconfirmed freeze entry {entry.Image} ({entry.Pid}) is {other.ToString().ToLowerInvariant()}; dropped");
                        state.FrozenProcesses.Remove(entry);
                        continue;
                }
            }

            // Mark, then resume, then remove: the mark must be on disk first.
            var index = state.FrozenProcesses.IndexOf(entry);
            var marked = entry with { Resuming = true };
            state.FrozenProcesses[index] = marked;
            if (!TrySave(save, report))
            {
                state.FrozenProcesses[state.FrozenProcesses.IndexOf(marked)] = entry;
                // Save the rollback too: a caller that caches failed writes
                // for a retry must cache the entry as it was, not marked.
                TrySave(save, report);
                report.Failures.Add($"Did not resume {entry.Image} (pid {entry.Pid}): the journal could not record the resume first.");
                return report;
            }
            var outcome = _os.Freezer.Resume(entry.Pid, entry.CreationTime);
            switch (outcome)
            {
                case ResumeOutcome.Resumed:
                    resumedGroups.Add(entry.Group);
                    state.FrozenProcesses.Remove(marked);
                    break;
                case ResumeOutcome.Gone:
                    Log.Info($"{entry.Image} ({entry.Pid}) exited while frozen; entry dropped");
                    state.FrozenProcesses.Remove(marked);
                    break;
                default:
                    // Nothing was resumed: put the entry back as it was, so it is retried.
                    state.FrozenProcesses[state.FrozenProcesses.IndexOf(marked)] = entry;
                    report.Failures.Add($"Could not resume {entry.Image} (pid {entry.Pid}): {(outcome == ResumeOutcome.AccessDenied ? "access denied" : "resume failed")}.");
                    Log.Error($"resume {entry.Image} ({entry.Pid}) failed: {outcome}");
                    break;
            }
            if (!TrySave(save, report))
            {
                // The resume happened; the journal still says resuming, which
                // no later run replays. Stop: nothing more is resumed on a
                // journal that cannot record it.
                return report;
            }
        }
        if (!state.FrozenProcesses.Any(p => p.Group == DockerGroup))
        {
            state.DockerFrozen = false;
            TrySave(save, report);
        }
        foreach (var group in resumedGroups)
        {
            report.Restored.Add($"resumed {group}");
        }
        return report;
    }

    /// <summary>The <see cref="FrozenProcess.Group"/> of Docker Desktop's processes.</summary>
    public const string DockerGroup = "docker";

    /// <summary>
    /// Restores each saved output on its own device. A device the user
    /// unmuted meanwhile is left as it is. A device that is away keeps its
    /// entry and waits.
    /// </summary>
    public RestoreReport RestoreAudio(RuntimeState state, Action save)
    {
        var report = new RestoreReport();
        if (state.SavedAudioOutputs.Count == 0)
        {
            return report;
        }
        var changed = false;
        foreach (var saved in state.SavedAudioOutputs.ToList())
        {
            var label = saved.Name ?? saved.DeviceId;
            var status = _os.Audio.Status(saved.DeviceId);
            if (status == AudioDeviceStatus.Away)
            {
                report.WaitingAudio.Add(saved);
                continue;
            }
            // A failed query is not absence: the device may be connected and
            // still muted, so it is retried rather than left to wait.
            var current = status == AudioDeviceStatus.Connected ? _os.Audio.Read(saved.DeviceId) : null;
            if (current is null)
            {
                report.Failures.Add($"Could not read the volume of {label}.");
                continue;
            }
            if (!current.Muted)
            {
                Log.Info($"{label} was unmuted while saved; left as it is");
                state.SavedAudioOutputs.Remove(saved);
                changed = true;
                continue;
            }
            var ok = _os.Audio.SetVolume(saved.DeviceId, saved.Volume) && _os.Audio.SetMute(saved.DeviceId, saved.Muted);
            if (ok)
            {
                state.SavedAudioOutputs.Remove(saved);
                changed = true;
                report.Restored.Add($"restored sound on {label}");
            }
            else
            {
                report.Failures.Add($"Could not restore the volume of {label}.");
                Log.Error($"audio restore failed on {label}");
            }
        }
        if (changed)
        {
            Save(save, report);
        }
        return report;
    }

    /// <summary>Puts each agent process's power-throttling setting back.</summary>
    public RestoreReport RestoreThrottling(RuntimeState state, Action save)
    {
        var report = new RestoreReport();
        if (state.ThrottleOverrides.Count == 0)
        {
            return report;
        }
        foreach (var entry in state.ThrottleOverrides.ToList())
        {
            if (_os.Throttle.Get(entry.Pid, entry.CreationTime) is null)
            {
                // The setting dies with the process; anything else is a failed read.
                if (_os.Freezer.Inspect(entry.Pid, entry.CreationTime) == ProcessState.Gone)
                {
                    state.ThrottleOverrides.Remove(entry);
                }
                else
                {
                    report.Failures.Add($"Could not read power throttling for {entry.Image} (pid {entry.Pid}).");
                }
                continue;
            }
            if (_os.Throttle.Set(entry.Pid, entry.CreationTime, entry.PreviousControlMask, entry.PreviousStateMask))
            {
                state.ThrottleOverrides.Remove(entry);
            }
            else
            {
                report.Failures.Add($"Could not restore power throttling for {entry.Image} (pid {entry.Pid}).");
            }
        }
        if (state.ThrottleOverrides.Count == 0)
        {
            report.Restored.Add("restored power throttling for agent apps");
        }
        Save(save, report);
        return report;
    }

    /// <summary>
    /// Restores the power mode. Windows applies an overlay to the active
    /// source only, so an entry for the other source waits. A source whose
    /// value is not Insomnia's (the user's choice, or a source the write never
    /// reached) is dropped without a write. A value that cannot be read is
    /// not evidence of either, so that entry is kept and reported.
    /// </summary>
    public RestoreReport RestorePowerMode(RuntimeState state, Action save, bool releaseOwnership)
    {
        var report = new RestoreReport();
        if (state.PowerMode.Count == 0 && !(releaseOwnership && (state.PowerSavingSetByUs || state.PowerModeRelinquished.Count > 0)))
        {
            return report;
        }
        if (releaseOwnership)
        {
            state.PowerSavingSetByUs = false;
            state.PowerModeRelinquished.Clear();
        }
        foreach (var entry in state.PowerMode.ToList())
        {
            var (ac, dc) = _os.PowerMode.Read();
            var current = entry.Source == PowerSourceKind.Ac ? ac : dc;
            if (current is not { } value)
            {
                report.Failures.Add($"Could not read the power mode {SourceLabel(entry.Source)} to restore it.");
                continue;
            }
            if (!entry.IsOurs(value))
            {
                if (entry.Confirmed)
                {
                    Log.Info($"power mode {SourceLabel(entry.Source)} was changed to {PowerOverlays.Describe(value)} meanwhile; left as it is");
                }
                state.PowerMode.Remove(entry);
                continue;
            }
            var source = _os.PowerMode.CurrentSource();
            if (source != entry.Source)
            {
                report.WaitingPowerMode.Add(entry);
                continue;
            }
            // The charger can change between choosing the source and the
            // write landing, which would put this source's value on the other
            // one. Journal the other source's value first, as an entry for
            // exactly that write, so the undo of a misplaced write survives a
            // crash. (When the other source already has an entry, its own
            // baseline stands in; a misplaced write there reads as a change
            // since and keeps that source at this value. That needs a charger
            // change inside one call, and is logged.)
            var otherSource = entry.Source == PowerSourceKind.Ac ? PowerSourceKind.Dc : PowerSourceKind.Ac;
            var otherValue = entry.Source == PowerSourceKind.Ac ? dc : ac;
            if (otherValue is not { } ov)
            {
                // Unprotected if the write lands there: wait for a readable value.
                report.Failures.Add($"Could not read the power mode {SourceLabel(otherSource)}, so the one {SourceLabel(entry.Source)} was not restored yet.");
                continue;
            }
            var otherEntry = state.PowerMode.FirstOrDefault(e => e.Source == otherSource);
            PowerModeEntry? guard = null;
            PowerModeEntry? widened = null;
            if (otherEntry is not null)
            {
                // Keep the other source's own baseline, and let it recognize
                // this write as Insomnia's too if it lands there.
                if (!otherEntry.IsOurs(entry.Previous))
                {
                    widened = otherEntry with { AlsoApplied = entry.Previous };
                    state.PowerMode[state.PowerMode.IndexOf(otherEntry)] = widened;
                }
            }
            else if (ov != entry.Previous)
            {
                guard = new PowerModeEntry(otherSource, ov, entry.Previous, Confirmed: false);
                state.PowerMode.Add(guard);
            }
            if ((guard is not null || widened is not null) && !TrySave(save, report))
            {
                if (guard is not null)
                {
                    state.PowerMode.Remove(guard);
                }
                if (widened is not null)
                {
                    state.PowerMode[state.PowerMode.IndexOf(widened)] = otherEntry!;
                }
                TrySave(save, report);
                report.Failures.Add($"Did not restore the power mode {SourceLabel(entry.Source)}: the journal could not record it first.");
                continue;
            }
            var ok = _os.PowerMode.SetForCurrentSource(entry.Previous);
            if (_os.PowerMode.CurrentSource() != entry.Source)
            {
                Log.Warn("the power source changed during a power mode restore; both sources stay journaled and are retried");
                report.Failures.Add("The power source changed while the power mode was being restored; Insomnia retries.");
                continue;
            }
            if (guard is not null)
            {
                state.PowerMode.Remove(guard);
            }
            if (widened is not null && state.PowerMode.IndexOf(widened) is var at and >= 0)
            {
                state.PowerMode[at] = widened with { AlsoApplied = null };
            }
            if (ok)
            {
                state.PowerMode.Remove(entry);
                report.Restored.Add($"power mode {SourceLabel(entry.Source)} back to {PowerOverlays.Describe(entry.Previous)}");
            }
            else
            {
                report.Failures.Add($"Could not switch the power mode {SourceLabel(entry.Source)} back to {PowerOverlays.Describe(entry.Previous)}.");
            }
        }
        Save(save, report);
        return report;
    }

    public static string SourceLabel(PowerSourceKind source) => source == PowerSourceKind.Ac ? "when plugged in" : "on battery";

    /// <summary>
    /// Restores each journaled scheme's lid-close action. A value the user
    /// changed meanwhile is kept; a scheme that no longer exists is dropped.
    /// </summary>
    public RestoreReport RestoreLidPolicy(RuntimeState state, Action save)
    {
        var report = new RestoreReport();
        if (state.LidPolicy.Count == 0)
        {
            return report;
        }
        foreach (var entry in state.LidPolicy.ToList())
        {
            var current = _os.LidPolicy.Read(entry.Scheme);
            if (current.Status == LidReadStatus.SchemeMissing)
            {
                Log.Warn($"power scheme {entry.Scheme} no longer exists; its lid entry is dropped");
                state.LidPolicy.Remove(entry);
                continue;
            }
            if (current.Status == LidReadStatus.Failed)
            {
                report.Failures.Add("Could not read the lid-close action to restore it.");
                continue;
            }
            var ac = current.Ac == entry.Applied ? entry.PreviousAc : current.Ac;
            var dc = current.Dc == entry.Applied ? entry.PreviousDc : current.Dc;
            // Written even when the stored values already match: an earlier
            // write may have stored them but failed to re-apply the active
            // scheme, and only the write re-applies it.
            if (_os.LidPolicy.Write(entry.Scheme, ac, dc))
            {
                state.LidPolicy.Remove(entry);
                report.Restored.Add($"lid-close action back to {LidActions.Describe(ac)} (plugged in) and {LidActions.Describe(dc)} (on battery)");
            }
            else
            {
                report.Failures.Add($"Could not restore the lid-close action ({LidActions.Describe(entry.PreviousAc)} plugged in, {LidActions.Describe(entry.PreviousDc)} on battery). Set it in Control Panel > Power Options > Choose what closing the lid does.");
            }
        }
        Save(save, report);
        return report;
    }

    private static bool TrySave(Action save, RestoreReport report)
    {
        try
        {
            save();
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            report.JournalWriteFailed = true;
            Log.Error("journal write during restore failed", e);
            return false;
        }
    }

    private static void Save(Action save, RestoreReport report)
    {
        try
        {
            save();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            report.JournalWriteFailed = true;
            Log.Error("journal write after restore failed", e);
        }
    }
}
