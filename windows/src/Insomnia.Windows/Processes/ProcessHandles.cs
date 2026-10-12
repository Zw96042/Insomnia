using System.Runtime.InteropServices;
using Insomnia.Core.Logging;
using static System.FormattableString;

namespace Insomnia.Windows.Processes;

internal enum OpenOutcome
{
    Opened,
    /// <summary>The process exited, or the pid now names a different process.</summary>
    Gone,
    AccessDenied,
    Failed,
}

/// <summary>
/// Opens processes by pid and proves identity on the handle itself: the
/// creation time read through the handle must equal the recorded one, and
/// the process must not have exited. Every later call acts through that same
/// handle, so a pid reused between the check and the action is never touched.
/// </summary>
internal static class ProcessHandles
{
    public static OpenOutcome OpenVerified(int pid, long creationTime, uint access, string purpose, out SafeNativeHandle? handle)
    {
        handle = null;
        if (pid <= 0)
        {
            return OpenOutcome.Gone;
        }
        var opened = ProcessNative.OpenProcess(access, inheritHandle: false, (uint)pid);
        if (opened.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            opened.Dispose();
            switch (error)
            {
                case ProcessNative.ErrorInvalidParameter:
                    return OpenOutcome.Gone;
                case ProcessNative.ErrorAccessDenied:
                    Log.Warn(Invariant($"{purpose}: access denied opening pid {pid}"));
                    return OpenOutcome.AccessDenied;
                default:
                    Log.Error(Invariant($"{purpose}: OpenProcess({pid}) failed with error {error}"));
                    return OpenOutcome.Failed;
            }
        }
        if (!ProcessNative.GetProcessTimes(opened, out var created, out _, out _, out _))
        {
            var error = Marshal.GetLastPInvokeError();
            opened.Dispose();
            Log.Error(Invariant($"{purpose}: GetProcessTimes({pid}) failed with error {error}"));
            return OpenOutcome.Failed;
        }
        if (created != creationTime)
        {
            opened.Dispose();
            return OpenOutcome.Gone;
        }
        if (!ProcessNative.GetExitCodeProcess(opened, out var exitCode))
        {
            var error = Marshal.GetLastPInvokeError();
            opened.Dispose();
            Log.Error(Invariant($"{purpose}: GetExitCodeProcess({pid}) failed with error {error}"));
            return OpenOutcome.Failed;
        }
        if (exitCode != ProcessNative.StillActive)
        {
            opened.Dispose();
            return OpenOutcome.Gone;
        }
        handle = opened;
        return OpenOutcome.Opened;
    }

    /// <summary>True while the process with this identity still runs.</summary>
    public static bool IsAlive(int pid, long creationTime)
    {
        var outcome = OpenVerified(pid, creationTime, ProcessNative.ProcessQueryLimitedInformation, "liveness check", out var handle);
        handle?.Dispose();
        // A process we may not inspect is not known to be gone.
        return outcome is OpenOutcome.Opened or OpenOutcome.AccessDenied or OpenOutcome.Failed;
    }

    /// <summary>
    /// The process's command line, read through
    /// <c>NtQueryInformationProcess(ProcessCommandLineInformation)</c>, which
    /// needs only limited query access. Null when it could not be read.
    /// </summary>
    public static unsafe string? ReadCommandLine(int pid, long creationTime)
    {
        if (OpenVerified(pid, creationTime, ProcessNative.ProcessQueryLimitedInformation, "command line", out var handle) != OpenOutcome.Opened)
        {
            return null;
        }
        using (handle)
        {
            var size = 4096u;
            for (var attempt = 0; attempt < 4; attempt++)
            {
                var buffer = NativeMemory.Alloc(size);
                try
                {
                    var status = ProcessNative.NtQueryInformationProcess(handle!, ProcessNative.ProcessCommandLineInformationClass, buffer, size, out var needed);
                    if (status is ProcessNative.StatusInfoLengthMismatch or ProcessNative.StatusBufferTooSmall or ProcessNative.StatusBufferOverflow)
                    {
                        size = Math.Max(needed, size * 2);
                        continue;
                    }
                    if (status < 0)
                    {
                        Log.Warn(Invariant($"command line: NtQueryInformationProcess({pid}) failed with 0x{status:X8}"));
                        return null;
                    }
                    var text = (ProcessNative.UnicodeString*)buffer;
                    if (text->Buffer == 0 || text->Length == 0)
                    {
                        return null;
                    }
                    return new string((char*)text->Buffer, 0, text->Length / sizeof(char));
                }
                finally
                {
                    NativeMemory.Free(buffer);
                }
            }
            Log.Warn(Invariant($"command line: pid {pid} kept growing past {size} bytes"));
            return null;
        }
    }

    /// <summary>
    /// Splits a command line the way the target process's own runtime does
    /// (<c>CommandLineToArgvW</c>). Element 0 is the program. Null on failure.
    /// </summary>
    public static unsafe string[]? SplitCommandLine(string commandLine)
    {
        // CommandLineToArgvW answers an empty string with the calling
        // program's path, which would be wrong here.
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return null;
        }
        var argv = ProcessNative.CommandLineToArgvW(commandLine, out var count);
        if (argv == 0)
        {
            Log.Warn(Invariant($"command line: CommandLineToArgvW failed with error {Marshal.GetLastPInvokeError()}"));
            return null;
        }
        try
        {
            var result = new string[count];
            var items = (char**)argv;
            for (var i = 0; i < count; i++)
            {
                result[i] = new string(items[i]);
            }
            return result;
        }
        finally
        {
            ProcessNative.LocalFree(argv);
        }
    }
}
