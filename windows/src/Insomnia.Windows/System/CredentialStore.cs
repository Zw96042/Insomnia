using System.ComponentModel;
using System.Runtime.InteropServices;
using Insomnia.Core.Logging;
using Insomnia.Core.Platform;
using static Insomnia.Windows.SystemServices.SystemNative;

namespace Insomnia.Windows.SystemServices;

/// <summary>
/// Hotspot passwords in Windows Credential Manager as generic credentials
/// (<c>Insomnia/hotspot/&lt;SSID&gt;</c>), which Windows protects with the
/// user's DPAPI keys. Passwords are never logged; the blob is written
/// straight from the string and the native copy is zeroed before it is freed.
/// </summary>
public sealed class CredentialStore : ICredentialStore
{
    private const string TargetPrefix = "Insomnia/hotspot/";

    public unsafe string? ReadHotspotPassword(string ssid)
    {
        var target = Target(ssid);
        if (!CredRead(target, CRED_TYPE_GENERIC, 0, out var pointer))
        {
            var error = Marshal.GetLastPInvokeError();
            if (error != ERROR_NOT_FOUND)
            {
                Log.Warn($"Credentials: reading the hotspot password failed ({Message(error)})");
            }
            return null;
        }
        try
        {
            var credential = (CREDENTIALW*)pointer;
            var size = (int)credential->CredentialBlobSize;
            if (credential->CredentialBlob == null || size == 0)
            {
                return null;
            }
            if (size % sizeof(char) != 0)
            {
                Log.Warn("Credentials: the stored hotspot password is not UTF-16 text; ignoring it");
                return null;
            }
            return new string((char*)credential->CredentialBlob, 0, size / sizeof(char));
        }
        finally
        {
            var credential = (CREDENTIALW*)pointer;
            if (credential->CredentialBlob != null)
            {
                new Span<byte>(credential->CredentialBlob, (int)credential->CredentialBlobSize).Clear();
            }
            CredFree(pointer);
        }
    }

    public unsafe string? WriteHotspotPassword(string ssid, string password)
    {
        var size = password.Length * sizeof(char);
        if (size > CRED_MAX_CREDENTIAL_BLOB_SIZE)
        {
            return "The password is too long to store.";
        }
        var target = Target(ssid);
        bool ok;
        int error;
        fixed (char* targetName = target)
        fixed (char* userName = ssid)
        fixed (char* blob = password)
        {
            var credential = new CREDENTIALW
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = targetName,
                UserName = userName,
                CredentialBlobSize = (uint)size,
                CredentialBlob = (byte*)blob,
                Persist = CRED_PERSIST_LOCAL_MACHINE,
            };
            ok = CredWrite(&credential, 0);
            error = ok ? 0 : Marshal.GetLastPInvokeError();
        }
        if (!ok)
        {
            Log.Error($"Credentials: saving the hotspot password failed ({Message(error)})");
            return $"Windows could not save the password: {Message(error)}";
        }
        Log.Info("Credentials: saved the hotspot password");
        return null;
    }

    public string? DeleteHotspotPassword(string ssid)
    {
        if (CredDelete(Target(ssid), CRED_TYPE_GENERIC, 0))
        {
            Log.Info("Credentials: deleted the hotspot password");
            return null;
        }
        var error = Marshal.GetLastPInvokeError();
        if (error == ERROR_NOT_FOUND)
        {
            return null;
        }
        Log.Error($"Credentials: deleting the hotspot password failed ({Message(error)})");
        return $"Windows could not delete the password: {Message(error)}";
    }

    private static string Target(string ssid) => TargetPrefix + ssid;

    private static string Message(int error) => $"error {error}: {new Win32Exception(error).Message}";
}
