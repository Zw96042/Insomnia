using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Insomnia.Windows.Power;

/// <summary>
/// Win32 declarations for the power, window and display code. Structs are
/// blittable and laid out to match the Windows headers on x64 and Arm64 (and
/// x86, where the pointer-sized fields shrink accordingly).
/// </summary>
internal static unsafe partial class PowerNative
{
    // Window messages and styles.
    public const uint WM_DESTROY = 0x0002;
    public const uint WM_SYSCOMMAND = 0x0112;
    public const uint WM_POWERBROADCAST = 0x0218;
    public const uint WM_APP = 0x8000;
    public const nint HWND_MESSAGE = -3;
    public const nint HWND_BROADCAST = 0xFFFF;
    public const uint WS_POPUP = 0x80000000;
    public const uint WS_EX_TOOLWINDOW = 0x00000080;
    public const uint WS_EX_NOACTIVATE = 0x08000000;

    // WM_POWERBROADCAST event codes.
    public const uint PBT_APMSUSPEND = 0x0004;
    public const uint PBT_APMRESUMESUSPEND = 0x0007;
    public const uint PBT_APMPOWERSTATUSCHANGE = 0x000A;
    public const uint PBT_APMRESUMEAUTOMATIC = 0x0012;
    public const uint PBT_POWERSETTINGCHANGE = 0x8013;
    public const uint DEVICE_NOTIFY_WINDOW_HANDLE = 0;

    // Display power.
    public const nuint SC_MONITORPOWER = 0xF170;
    public const uint SMTO_ABORTIFHUNG = 0x0002;
    public const uint INPUT_MOUSE = 0;
    public const uint MOUSEEVENTF_MOVE = 0x0001;

    // Execution state.
    public const uint ES_SYSTEM_REQUIRED = 0x00000001;
    public const uint ES_DISPLAY_REQUIRED = 0x00000002;
    public const uint ES_CONTINUOUS = 0x80000000;

    // Power requests.
    public const uint POWER_REQUEST_CONTEXT_VERSION = 0;
    public const uint POWER_REQUEST_CONTEXT_SIMPLE_STRING = 0x1;
    public const int PowerRequestSystemRequired = 1;

    // CallNtPowerInformation.
    public const int SystemPowerCapabilities = 4;

    /// <summary>sizeof(SYSTEM_POWER_CAPABILITIES): 32 bytes of BOOLEAN/UCHAR fields, three BATTERY_REPORTING_SCALE, five SYSTEM_POWER_STATE.</summary>
    public const int SystemPowerCapabilitiesSize = 76;

    public const int LidPresentOffset = 2;
    public const int AoAcOffset = 20;

    // PDH.
    public const uint PDH_FMT_DOUBLE = 0x00000200;
    public const uint PDH_MORE_DATA = 0x800007D2;
    public const uint PDH_CSTATUS_VALID_DATA = 0x00000000;
    public const uint PDH_CSTATUS_NEW_DATA = 0x00000001;

    public const uint ERROR_SUCCESS = 0;
    public const uint ERROR_FILE_NOT_FOUND = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public delegate* unmanaged<nint, uint, nuint, nint, nint> lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public char* lpszMenuName;
        public char* lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public nint hwnd;
        public uint message;
        public nuint wParam;
        public nint lParam;
        public uint time;
        public POINT pt;
        public uint lPrivate;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public nint dwExtraInfo;
    }

    /// <summary>INPUT with only the mouse arm of the union, which is also the union's largest member.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public MOUSEINPUT mi;
    }

    /// <summary>
    /// REASON_CONTEXT. The union after Flags is either a string pointer or the
    /// Detailed struct (HMODULE, ULONG, ULONG, LPWSTR*); the padding fields
    /// give it the Detailed size on both 32 and 64 bit.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct REASON_CONTEXT
    {
        public uint Version;
        public uint Flags;
        public char* SimpleReasonString;
        public uint Reserved1;
        public uint Reserved2;
        public nint Reserved3;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    /// <summary>PDH_FMT_COUNTERVALUE_ITEM_W with the double arm of the value union.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PDH_FMT_COUNTERVALUE_ITEM_W
    {
        public char* szName;
        public uint CStatus;
        public double doubleValue;
    }

    // kernel32

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial nint GetModuleHandleW(string? lpModuleName);

    [LibraryImport("kernel32.dll")]
    public static partial uint SetThreadExecutionState(uint esFlags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial SafePowerRequestHandle PowerCreateRequest(REASON_CONTEXT* context);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PowerSetRequest(SafePowerRequestHandle powerRequest, int requestType);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PowerClearRequest(SafePowerRequestHandle powerRequest, int requestType);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    [LibraryImport("kernel32.dll")]
    public static partial nint LocalFree(nint hMem);

    // user32

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial ushort RegisterClassExW(WNDCLASSEXW* wndClass);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterClassW(string className, nint hInstance);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial nint CreateWindowExW(
        uint exStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        nint parent,
        nint menu,
        nint instance,
        nint param);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    public static partial nint DefWindowProcW(nint hwnd, uint msg, nuint wParam, nint lParam);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial int GetMessageW(MSG* msg, nint hwnd, uint filterMin, uint filterMax);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TranslateMessage(MSG* msg);

    [LibraryImport("user32.dll")]
    public static partial nint DispatchMessageW(MSG* msg);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostMessageW(nint hwnd, uint msg, nuint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    public static partial void PostQuitMessage(int exitCode);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial nint SendMessageTimeoutW(
        nint hwnd,
        uint msg,
        nuint wParam,
        nint lParam,
        uint flags,
        uint timeoutMilliseconds,
        out nuint result);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial uint SendInput(uint count, INPUT* inputs, int size);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial nint RegisterPowerSettingNotification(nint recipient, in Guid powerSettingGuid, uint flags);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterPowerSettingNotification(nint handle);

    // powrprof

    [LibraryImport("powrprof.dll")]
    public static partial int CallNtPowerInformation(int informationLevel, void* inputBuffer, uint inputBufferLength, void* outputBuffer, uint outputBufferLength);

    [LibraryImport("powrprof.dll")]
    public static partial uint PowerGetActiveScheme(nint userRootPowerKey, out nint activePolicyGuid);

    [LibraryImport("powrprof.dll")]
    public static partial uint PowerSetActiveScheme(nint userRootPowerKey, in Guid schemeGuid);

    [LibraryImport("powrprof.dll")]
    public static partial uint PowerReadACValueIndex(nint rootPowerKey, in Guid schemeGuid, in Guid subGroupGuid, in Guid settingGuid, out uint value);

    [LibraryImport("powrprof.dll")]
    public static partial uint PowerReadDCValueIndex(nint rootPowerKey, in Guid schemeGuid, in Guid subGroupGuid, in Guid settingGuid, out uint value);

    [LibraryImport("powrprof.dll")]
    public static partial uint PowerWriteACValueIndex(nint rootPowerKey, in Guid schemeGuid, in Guid subGroupGuid, in Guid settingGuid, uint value);

    [LibraryImport("powrprof.dll")]
    public static partial uint PowerWriteDCValueIndex(nint rootPowerKey, in Guid schemeGuid, in Guid subGroupGuid, in Guid settingGuid, uint value);

    // pdh

    [LibraryImport("pdh.dll", EntryPoint = "PdhOpenQueryW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint PdhOpenQuery(string? dataSource, nuint userData, out SafePdhQueryHandle query);

    [LibraryImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint PdhAddEnglishCounter(SafePdhQueryHandle query, string counterPath, nuint userData, out nint counter);

    [LibraryImport("pdh.dll")]
    public static partial uint PdhCollectQueryData(SafePdhQueryHandle query);

    [LibraryImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW")]
    public static partial uint PdhGetFormattedCounterArray(nint counter, uint format, ref uint bufferSize, out uint itemCount, void* items);

    [LibraryImport("pdh.dll")]
    public static partial uint PdhCloseQuery(nint query);
}

/// <summary>A power request handle; closing it drops any request still set on it.</summary>
internal sealed class SafePowerRequestHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafePowerRequestHandle()
        : base(ownsHandle: true)
    {
    }

    protected override bool ReleaseHandle() => PowerNative.CloseHandle(handle);
}

/// <summary>A PDH query; closing it also frees every counter added to it.</summary>
internal sealed class SafePdhQueryHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafePdhQueryHandle()
        : base(ownsHandle: true)
    {
    }

    protected override bool ReleaseHandle() => PowerNative.PdhCloseQuery(handle) == PowerNative.ERROR_SUCCESS;
}
