using System.Runtime.InteropServices;

namespace Insomnia.Windows.SystemServices;

/// <summary>Win32 declarations for Credential Manager and OEM code page text.</summary>
internal static unsafe partial class SystemNative
{
    public const uint CRED_TYPE_GENERIC = 1;
    public const uint CRED_PERSIST_LOCAL_MACHINE = 2;

    /// <summary>CRED_MAX_CREDENTIAL_BLOB_SIZE (5 * 512).</summary>
    public const int CRED_MAX_CREDENTIAL_BLOB_SIZE = 2560;

    public const int ERROR_NOT_FOUND = 1168;
    public const uint CP_OEMCP = 1;

    /// <summary>CREDENTIALW. FILETIME is two DWORDs, so it is 4-byte aligned.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct CREDENTIALW
    {
        public uint Flags;
        public uint Type;
        public char* TargetName;
        public char* Comment;
        public uint LastWrittenLow;
        public uint LastWrittenHigh;
        public uint CredentialBlobSize;
        public byte* CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public nint Attributes;
        public char* TargetAlias;
        public char* UserName;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CredWrite(CREDENTIALW* credential, uint flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredReadW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CredRead(string targetName, uint type, uint flags, out nint credential);

    [LibraryImport("advapi32.dll", EntryPoint = "CredDeleteW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CredDelete(string targetName, uint type, uint flags);

    [LibraryImport("advapi32.dll")]
    public static partial void CredFree(nint buffer);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial int MultiByteToWideChar(uint codePage, uint flags, byte* multiByte, int multiByteLength, char* wideChar, int wideCharLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial int WideCharToMultiByte(uint codePage, uint flags, char* wideChar, int wideCharLength, byte* multiByte, int multiByteLength, nint defaultChar, nint usedDefaultChar);

    /// <summary>Decodes console-tool output, which is in the OEM code page when redirected.</summary>
    public static string FromOem(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return string.Empty;
        }
        fixed (byte* source = bytes)
        {
            var length = MultiByteToWideChar(CP_OEMCP, 0, source, bytes.Length, null, 0);
            if (length <= 0)
            {
                return string.Empty;
            }
            var buffer = new char[length];
            fixed (char* target = buffer)
            {
                length = MultiByteToWideChar(CP_OEMCP, 0, source, bytes.Length, target, length);
            }
            return length <= 0 ? string.Empty : new string(buffer, 0, length);
        }
    }

    /// <summary>The text as it reads after a trip through the OEM code page (unmappable characters become its default character).</summary>
    public static string ThroughOem(string text)
    {
        if (text.Length == 0)
        {
            return text;
        }
        fixed (char* source = text)
        {
            var length = WideCharToMultiByte(CP_OEMCP, 0, source, text.Length, null, 0, 0, 0);
            if (length <= 0)
            {
                return text;
            }
            var bytes = new byte[length];
            fixed (byte* target = bytes)
            {
                length = WideCharToMultiByte(CP_OEMCP, 0, source, text.Length, target, length, 0, 0);
            }
            return length <= 0 ? text : FromOem(bytes.AsSpan(0, length));
        }
    }
}
