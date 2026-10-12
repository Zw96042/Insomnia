using System.Diagnostics;
using System.Runtime.InteropServices;
using Insomnia.Core.Logging;
using Insomnia.Core.Platform;
using static System.FormattableString;

namespace Insomnia.Windows.Processes;

/// <summary>
/// Whole-process suspend and resume through <c>NtSuspendProcess</c> and
/// <c>NtResumeProcess</c>. Both work per thread suspend count: a suspend adds
/// one to every thread and a resume takes one away, so a process somebody
/// else also suspended stays suspended for them. Identity is checked on the
/// same handle the call goes through (see <see cref="ProcessHandles"/>).
/// </summary>
public sealed class ProcessFreezer : IProcessFreezer
{
    private const uint Access = ProcessNative.ProcessSuspendResume | ProcessNative.ProcessQueryLimitedInformation;

    // Start large enough for a busy desktop so the common case is one call.
    private const uint InitialSnapshotBytes = 1024 * 1024;

    public SuspendOutcome Suspend(int pid, long creationTime)
    {
        if (pid == Environment.ProcessId)
        {
            Log.Error("freezer: refusing to suspend Insomnia itself");
            return SuspendOutcome.Failed;
        }
        switch (ProcessHandles.OpenVerified(pid, creationTime, Access, "suspend", out var handle))
        {
            case OpenOutcome.Gone:
                return SuspendOutcome.Gone;
            case OpenOutcome.AccessDenied:
                return SuspendOutcome.AccessDenied;
            case OpenOutcome.Failed:
                return SuspendOutcome.Failed;
        }
        using (handle)
        {
            var status = ProcessNative.NtSuspendProcess(handle!);
            return Classify(status, "NtSuspendProcess", pid) switch
            {
                OpenOutcome.Opened => SuspendOutcome.Suspended,
                OpenOutcome.Gone => SuspendOutcome.Gone,
                OpenOutcome.AccessDenied => SuspendOutcome.AccessDenied,
                _ => SuspendOutcome.Failed,
            };
        }
    }

    public ResumeOutcome Resume(int pid, long creationTime)
    {
        switch (ProcessHandles.OpenVerified(pid, creationTime, Access, "resume", out var handle))
        {
            case OpenOutcome.Gone:
                return ResumeOutcome.Gone;
            case OpenOutcome.AccessDenied:
                return ResumeOutcome.AccessDenied;
            case OpenOutcome.Failed:
                return ResumeOutcome.Failed;
        }
        using (handle)
        {
            var status = ProcessNative.NtResumeProcess(handle!);
            return Classify(status, "NtResumeProcess", pid) switch
            {
                OpenOutcome.Opened => ResumeOutcome.Resumed,
                OpenOutcome.Gone => ResumeOutcome.Gone,
                OpenOutcome.AccessDenied => ResumeOutcome.AccessDenied,
                _ => ResumeOutcome.Failed,
            };
        }
    }

    /// <summary>
    /// Reads the system process list rather than opening the process, so it
    /// needs no access to the process itself. A thread counts as suspended
    /// when it is Waiting with wait reason Suspended.
    /// </summary>
    public unsafe ProcessState Inspect(int pid, long creationTime)
    {
        if (pid <= 0)
        {
            return ProcessState.Gone;
        }
        Debug.Assert(
            IntPtr.Size != 8
            || (sizeof(ProcessNative.SystemProcessInformation) == 0x100 && sizeof(ProcessNative.SystemThreadInformation) == 0x50),
            "SYSTEM_PROCESS_INFORMATION / SYSTEM_THREAD_INFORMATION layout mismatch");

        var size = InitialSnapshotBytes;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var buffer = (byte*)NativeMemory.Alloc(size);
            try
            {
                var status = ProcessNative.NtQuerySystemInformation(ProcessNative.SystemProcessInformationClass, buffer, size, out var needed);
                if (status == ProcessNative.StatusInfoLengthMismatch)
                {
                    // The list grows between calls; leave headroom.
                    size = Math.Max(needed, size) + (256 * 1024);
                    continue;
                }
                if (status < 0)
                {
                    Log.Error(Invariant($"freezer: NtQuerySystemInformation failed with 0x{status:X8}"));
                    return ProcessState.Unknown;
                }
                return FindAndInspect(buffer, needed == 0 ? size : Math.Min(needed, size), pid, creationTime);
            }
            finally
            {
                NativeMemory.Free(buffer);
            }
        }
        Log.Error("freezer: the system process list kept outgrowing the buffer");
        return ProcessState.Unknown;
    }

    private static unsafe ProcessState FindAndInspect(byte* buffer, uint length, int pid, long creationTime)
    {
        var processSize = (uint)sizeof(ProcessNative.SystemProcessInformation);
        var threadSize = (uint)sizeof(ProcessNative.SystemThreadInformation);
        uint offset = 0;
        while (offset + processSize <= length)
        {
            var process = (ProcessNative.SystemProcessInformation*)(buffer + offset);
            if (process->UniqueProcessId == pid)
            {
                if (process->CreateTime != creationTime)
                {
                    // The pid now names a different process.
                    return ProcessState.Gone;
                }
                var threads = process->NumberOfThreads;
                // A process with no threads is exiting; there is nothing to resume.
                if (threads == 0)
                {
                    return ProcessState.Gone;
                }
                if (offset + processSize + ((ulong)threads * threadSize) > length)
                {
                    Log.Error(Invariant($"freezer: the thread list of {pid} runs past the snapshot"));
                    return ProcessState.Unknown;
                }
                var thread = (ProcessNative.SystemThreadInformation*)(buffer + offset + processSize);
                for (var i = 0; i < threads; i++)
                {
                    if (thread[i].ThreadState != ProcessNative.ThreadStateWaiting || thread[i].WaitReason != ProcessNative.WaitReasonSuspended)
                    {
                        return ProcessState.Running;
                    }
                }
                return ProcessState.FullySuspended;
            }
            if (process->NextEntryOffset == 0)
            {
                break;
            }
            offset += process->NextEntryOffset;
        }
        return ProcessState.Gone;
    }

    /// <summary>Maps an NTSTATUS from suspend or resume; Opened stands for success.</summary>
    private static OpenOutcome Classify(int status, string call, int pid)
    {
        switch (status)
        {
            case >= 0:
                return OpenOutcome.Opened;
            case ProcessNative.StatusProcessIsTerminating:
            case ProcessNative.StatusInvalidCid:
                return OpenOutcome.Gone;
            case ProcessNative.StatusAccessDenied:
                Log.Warn(Invariant($"freezer: {call}({pid}) was denied"));
                return OpenOutcome.AccessDenied;
            default:
                Log.Error(Invariant($"freezer: {call}({pid}) failed with 0x{status:X8}"));
                return OpenOutcome.Failed;
        }
    }
}
