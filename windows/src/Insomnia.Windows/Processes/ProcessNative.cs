using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Insomnia.Windows.Processes;

/// <summary>A kernel handle closed with <c>CloseHandle</c> (process, token, Toolhelp snapshot).</summary>
internal sealed class SafeNativeHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeNativeHandle()
        : base(ownsHandle: true)
    {
    }

    protected override bool ReleaseHandle() => ProcessNative.CloseHandle(handle);
}

/// <summary>
/// Win32 and native API declarations for process work. Struct layouts follow
/// the C headers through sequential layout with pointer-sized fields, so the
/// same declarations are right on x64 and Arm64 (and would be on x86).
/// </summary>
internal static unsafe partial class ProcessNative
{
    public const int ErrorAccessDenied = 5;
    public const int ErrorInvalidParameter = 87;
    public const int ErrorInsufficientBuffer = 122;
    public const int ErrorNoMoreFiles = 18;

    public const uint ProcessTerminate = 0x0001;
    public const uint ProcessSetInformation = 0x0200;
    public const uint ProcessQueryInformation = 0x0400;
    public const uint ProcessSuspendResume = 0x0800;
    public const uint ProcessQueryLimitedInformation = 0x1000;

    public const uint StillActive = 259;

    public const uint Th32csSnapProcess = 0x00000002;

    public const uint TokenQuery = 0x0008;
    public const int TokenUserClass = 1;

    public const int StatusSuccess = 0;
    public const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
    public const int StatusBufferTooSmall = unchecked((int)0xC0000023);
    public const int StatusBufferOverflow = unchecked((int)0x80000005);
    public const int StatusAccessDenied = unchecked((int)0xC0000022);
    public const int StatusProcessIsTerminating = unchecked((int)0xC000010A);
    public const int StatusInvalidCid = unchecked((int)0xC000000B);

    /// <summary><c>SYSTEM_INFORMATION_CLASS.SystemProcessInformation</c>.</summary>
    public const int SystemProcessInformationClass = 5;

    /// <summary><c>PROCESSINFOCLASS.ProcessCommandLineInformation</c> (Windows 8.1 and later).</summary>
    public const int ProcessCommandLineInformationClass = 60;

    /// <summary><c>PROCESS_INFORMATION_CLASS.ProcessPowerThrottling</c>.</summary>
    public const int ProcessPowerThrottlingClass = 4;

    public const uint ProcessPowerThrottlingCurrentVersion = 1;

    /// <summary><c>KTHREAD_STATE.Waiting</c>.</summary>
    public const uint ThreadStateWaiting = 5;

    /// <summary><c>KWAIT_REASON.Suspended</c>.</summary>
    public const uint WaitReasonSuspended = 5;

    public const uint GwOwner = 4;
    public const int GwlExStyle = -20;
    public const long WsExToolWindow = 0x00000080;
    public const long WsExAppWindow = 0x00040000;
    public const uint DwmwaCloaked = 14;

    /// <summary><c>PROCESSENTRY32W</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ProcessEntry32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public nuint th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        public fixed char szExeFile[260];
    }

    /// <summary><c>UNICODE_STRING</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public nint Buffer;
    }

    /// <summary>
    /// <c>SYSTEM_PROCESS_INFORMATION</c> as returned for class 5 (0x100 bytes
    /// on 64-bit). Its <c>SYSTEM_THREAD_INFORMATION</c> array follows it
    /// immediately.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SystemProcessInformation
    {
        public uint NextEntryOffset;
        public uint NumberOfThreads;
        public long WorkingSetPrivateSize;
        public uint HardFaultCount;
        public uint NumberOfThreadsHighWatermark;
        public ulong CycleTime;
        public long CreateTime;
        public long UserTime;
        public long KernelTime;
        public UnicodeString ImageName;
        public int BasePriority;
        public nint UniqueProcessId;
        public nint InheritedFromUniqueProcessId;
        public uint HandleCount;
        public uint SessionId;
        public nuint UniqueProcessKey;
        public nuint PeakVirtualSize;
        public nuint VirtualSize;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
        public nuint PrivatePageCount;
        public long ReadOperationCount;
        public long WriteOperationCount;
        public long OtherOperationCount;
        public long ReadTransferCount;
        public long WriteTransferCount;
        public long OtherTransferCount;
    }

    /// <summary><c>SYSTEM_THREAD_INFORMATION</c> (0x50 bytes on 64-bit).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SystemThreadInformation
    {
        public long KernelTime;
        public long UserTime;
        public long CreateTime;
        public uint WaitTime;
        public nint StartAddress;
        public nint UniqueProcess;
        public nint UniqueThread;
        public int Priority;
        public int BasePriority;
        public uint ContextSwitches;
        public uint ThreadState;
        public uint WaitReason;
    }

    /// <summary><c>PROCESS_POWER_THROTTLING_STATE</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ProcessPowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial SafeNativeHandle CreateToolhelp32Snapshot(uint flags, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool Process32FirstW(SafeNativeHandle snapshot, ProcessEntry32W* entry);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool Process32NextW(SafeNativeHandle snapshot, ProcessEntry32W* entry);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial SafeNativeHandle OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetProcessTimes(SafeNativeHandle process, out long creationTime, out long exitTime, out long kernelTime, out long userTime);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetExitCodeProcess(SafeNativeHandle process, out uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool QueryFullProcessImageNameW(SafeNativeHandle process, uint flags, char* exeName, ref uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ProcessIdToSessionId(uint processId, out uint sessionId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetProcessInformation(SafeNativeHandle process, int informationClass, void* information, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetProcessInformation(SafeNativeHandle process, int informationClass, void* information, uint size);

    [LibraryImport("kernel32.dll")]
    public static partial nint LocalFree(nint memory);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool OpenProcessToken(SafeNativeHandle process, uint desiredAccess, out SafeNativeHandle token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetTokenInformation(SafeNativeHandle token, int informationClass, void* information, uint length, out uint returnLength);

    [LibraryImport("ntdll.dll")]
    public static partial int NtSuspendProcess(SafeNativeHandle process);

    [LibraryImport("ntdll.dll")]
    public static partial int NtResumeProcess(SafeNativeHandle process);

    [LibraryImport("ntdll.dll")]
    public static partial int NtQuerySystemInformation(int informationClass, void* information, uint length, out uint returnLength);

    [LibraryImport("ntdll.dll")]
    public static partial int NtQueryInformationProcess(SafeNativeHandle process, int informationClass, void* information, uint length, out uint returnLength);

    [LibraryImport("shell32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CommandLineToArgvW(string commandLine, out int argumentCount);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnumWindows(delegate* unmanaged<nint, nint, int> callback, nint parameter);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(nint window);

    [LibraryImport("user32.dll")]
    public static partial nint GetWindow(nint window, uint command);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static partial nint GetWindowLongPtr64(nint window, int index);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW")]
    public static partial int GetWindowLong32(nint window, int index);

    [LibraryImport("user32.dll")]
    public static partial int InternalGetWindowText(nint window, char* text, int maxCount);

    [LibraryImport("user32.dll")]
    public static partial uint GetWindowThreadProcessId(nint window, out uint processId);

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmGetWindowAttribute(nint window, uint attribute, void* value, uint size);

    /// <summary><c>GetWindowLongPtrW</c> exists only in 64-bit user32; 32-bit code calls <c>GetWindowLongW</c>.</summary>
    public static long GetWindowExStyle(nint window) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(window, GwlExStyle) : GetWindowLong32(window, GwlExStyle);
}
