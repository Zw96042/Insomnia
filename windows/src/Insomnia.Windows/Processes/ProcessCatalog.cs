using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Insomnia.Core.Logging;
using Insomnia.Core.Platform;
using static System.FormattableString;

namespace Insomnia.Windows.Processes;

/// <summary>
/// Running processes for the freeze planner, the Docker rule and the browser
/// check. The Toolhelp snapshot gives every pid, parent and image name even
/// for processes we cannot open; identity, path and ownership come from a
/// limited-query handle on each process. A process that cannot be opened is
/// still listed (creation time 0, not ours), so parent-child chains stay
/// intact while the planner skips it.
/// </summary>
public sealed class ProcessCatalog : IProcessCatalog
{
    private readonly SecurityIdentifier? _userSid;
    private readonly uint? _sessionId;

    // File descriptions never change for a given executable path, and reading
    // the version resource is the slowest part of a snapshot.
    private readonly ConcurrentDictionary<string, string?> _descriptions = new(StringComparer.OrdinalIgnoreCase);

    public ProcessCatalog()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            _userSid = identity.User;
        }
        catch (Exception ex)
        {
            Log.Error("process catalog: could not read the current user's SID; no process will count as ours", ex);
        }
        if (ProcessNative.ProcessIdToSessionId((uint)Environment.ProcessId, out var session))
        {
            _sessionId = session;
        }
        else
        {
            Log.Error(Invariant($"process catalog: ProcessIdToSessionId failed with error {Marshal.GetLastPInvokeError()}; no process will count as ours"));
        }
        WindowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    }

    public int SelfPid { get; } = Environment.ProcessId;

    public string WindowsDirectory { get; }

    public unsafe IReadOnlyList<ProcessInfo> Snapshot()
    {
        // Everything below must describe processes that existed when the
        // enumeration began. A process created later reuses a pid from the
        // snapshot, and must not inherit that entry's image name or window.
        var snapshotTime = DateTime.UtcNow.ToFileTimeUtc();
        var withWindows = new HashSet<int>();
        foreach (var window in TopLevelWindows.Enumerate())
        {
            if (window.AppearsOnTaskbar)
            {
                withWindows.Add(window.Pid);
            }
        }

        using var snapshot = ProcessNative.CreateToolhelp32Snapshot(ProcessNative.Th32csSnapProcess, 0);
        if (snapshot.IsInvalid)
        {
            Log.Error(Invariant($"process catalog: CreateToolhelp32Snapshot failed with error {Marshal.GetLastPInvokeError()}"));
            return [];
        }

        var result = new List<ProcessInfo>(512);
        var entry = new ProcessNative.ProcessEntry32W { dwSize = (uint)sizeof(ProcessNative.ProcessEntry32W) };
        if (!ProcessNative.Process32FirstW(snapshot, &entry))
        {
            Log.Error(Invariant($"process catalog: Process32FirstW failed with error {Marshal.GetLastPInvokeError()}"));
            return [];
        }
        do
        {
            var pid = (int)entry.th32ProcessID;
            // Pid 0 is the idle pseudo-process, not something that can run code.
            if (pid != 0)
            {
                var image = new string(entry.szExeFile);
                result.Add(Describe(pid, (int)entry.th32ParentProcessID, image, withWindows.Contains(pid), snapshotTime));
            }
            entry.dwSize = (uint)sizeof(ProcessNative.ProcessEntry32W);
        }
        while (ProcessNative.Process32NextW(snapshot, &entry));

        var last = Marshal.GetLastPInvokeError();
        if (last != ProcessNative.ErrorNoMoreFiles)
        {
            Log.Warn(Invariant($"process catalog: Process32NextW stopped with error {last}; the snapshot may be partial"));
        }
        return result;
    }

    private ProcessInfo Describe(int pid, int parentPid, string image, bool hasWindow, long snapshotTime)
    {
        var fallbackName = Path.GetFileNameWithoutExtension(image);
        var unreadable = new ProcessInfo(pid, parentPid, 0, image, null, fallbackName, false, hasWindow);
        using var handle = ProcessNative.OpenProcess(ProcessNative.ProcessQueryLimitedInformation, inheritHandle: false, (uint)pid);
        if (handle.IsInvalid || !ProcessNative.GetProcessTimes(handle, out var created, out _, out _, out _))
        {
            return unreadable;
        }
        if (created > snapshotTime)
        {
            // The pid was reused after the enumeration began: this handle is
            // a different process from the snapshot's entry. CreationTime 0
            // makes the planner skip it.
            return unreadable;
        }
        var path = ImagePath(handle);
        if (path is not null && !string.Equals(Path.GetFileName(path), image, StringComparison.OrdinalIgnoreCase))
        {
            return unreadable;
        }
        var displayName = (path is null ? null : DescriptionFor(path)) ?? fallbackName;
        return new ProcessInfo(pid, parentPid, created, image, path, displayName, IsCurrentUser(pid, handle), hasWindow);
    }

    private static unsafe string? ImagePath(SafeNativeHandle handle)
    {
        const int common = 1024;
        var small = stackalloc char[common];
        var size = (uint)common;
        if (ProcessNative.QueryFullProcessImageNameW(handle, 0, small, ref size))
        {
            return new string(small, 0, (int)size);
        }
        if (Marshal.GetLastPInvokeError() != ProcessNative.ErrorInsufficientBuffer)
        {
            return null;
        }
        // Long paths can reach the NT limit of 32 Ki characters.
        const int capacity = 32768;
        var large = (char*)NativeMemory.Alloc(capacity * sizeof(char));
        try
        {
            size = capacity;
            return ProcessNative.QueryFullProcessImageNameW(handle, 0, large, ref size) ? new string(large, 0, (int)size) : null;
        }
        finally
        {
            NativeMemory.Free(large);
        }
    }

    /// <summary>Same user (token SID) and same logon session as Insomnia.</summary>
    private unsafe bool IsCurrentUser(int pid, SafeNativeHandle process)
    {
        if (_userSid is null || _sessionId is not { } ourSession)
        {
            return false;
        }
        if (!ProcessNative.ProcessIdToSessionId((uint)pid, out var session) || session != ourSession)
        {
            return false;
        }
        if (!ProcessNative.OpenProcessToken(process, ProcessNative.TokenQuery, out var token))
        {
            token.Dispose();
            return false;
        }
        using (token)
        {
            // TOKEN_USER plus the largest possible SID (68 bytes) fits easily.
            const int size = 256;
            var buffer = stackalloc byte[size];
            if (!ProcessNative.GetTokenInformation(token, ProcessNative.TokenUserClass, buffer, size, out _))
            {
                return false;
            }
            // TOKEN_USER starts with SID_AND_ATTRIBUTES, whose first field is the PSID.
            var sid = *(nint*)buffer;
            return sid != 0 && new SecurityIdentifier(sid).Equals(_userSid);
        }
    }

    private string? DescriptionFor(string path) =>
        _descriptions.GetOrAdd(path, static p =>
        {
            try
            {
                var description = FileVersionInfo.GetVersionInfo(p).FileDescription?.Trim();
                return string.IsNullOrEmpty(description) ? null : description;
            }
            catch (Exception)
            {
                // Unreadable or missing version resource: the image name stands in.
                return null;
            }
        });
}
