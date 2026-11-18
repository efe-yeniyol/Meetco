using System.ComponentModel;
using System.Runtime.InteropServices;
using Meetco.Core.Settings;

namespace MeetcoApp.Platform;

/// <summary>
/// Stores the OpenAI API key in Windows Credential Manager (a generic credential for the
/// current user). Meetco never reads keys from environment variables or files.
/// </summary>
public sealed class CredentialManagerKeyStore : IApiKeyStore
{
    private const string Target = "Meetco/OpenAI-API-Key";

    public string? Load()
    {
        if (!NativeMethods.CredRead(Target, NativeMethods.CRED_TYPE_GENERIC, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == NativeMethods.ERROR_NOT_FOUND) return null;
            throw new Win32Exception(error, "Could not read the API key from Windows Credential Manager.");
        }
        try
        {
            var credential = Marshal.PtrToStructure<NativeMethods.CREDENTIAL>(pointer);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0) return null;
            return Marshal.PtrToStringUni(credential.CredentialBlob, (int)credential.CredentialBlobSize / 2);
        }
        finally
        {
            NativeMethods.CredFree(pointer);
        }
    }

    public void Save(string key)
    {
        var target = Marshal.StringToCoTaskMemUni(Target);
        var user = Marshal.StringToCoTaskMemUni("OpenAI API key");
        var blob = Marshal.StringToCoTaskMemUni(key);
        try
        {
            var credential = new NativeMethods.CREDENTIAL
            {
                Type = NativeMethods.CRED_TYPE_GENERIC,
                TargetName = target,
                UserName = user,
                CredentialBlob = blob,
                CredentialBlobSize = (uint)(key.Length * 2),
                Persist = NativeMethods.CRED_PERSIST_LOCAL_MACHINE,
            };
            if (!NativeMethods.CredWrite(ref credential, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not save the API key to Windows Credential Manager.");
            }
        }
        finally
        {
            // Overwrite the key copy before releasing the memory.
            for (var offset = 0; offset < key.Length * 2; offset++) Marshal.WriteByte(blob, offset, 0);
            Marshal.FreeCoTaskMem(blob);
            Marshal.FreeCoTaskMem(user);
            Marshal.FreeCoTaskMem(target);
        }
    }

    public void Delete()
    {
        if (NativeMethods.CredDelete(Target, NativeMethods.CRED_TYPE_GENERIC, 0)) return;
        var error = Marshal.GetLastWin32Error();
        if (error != NativeMethods.ERROR_NOT_FOUND)
        {
            throw new Win32Exception(error, "Could not delete the API key from Windows Credential Manager.");
        }
    }
}
