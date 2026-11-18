using System.Runtime.InteropServices;
using System.Text;
using UniversalMeetingRecorder.Core.Settings;

namespace UniversalMeetingRecorder.App.Platform;

/// <summary>
/// Секреты в Windows Credential Manager (Generic credentials) — аналог Keychain на Mac.
/// Target = "{service}:{account}", например "com.monster.UniversalMeetingRecorder.Speakr:apiKey".
/// Видно в «Диспетчер учётных данных → Учётные данные Windows».
/// </summary>
public sealed class CredentialStore : ISecretStore
{
    private const int CredTypeGeneric = 1;
    private const int CredPersistLocalMachine = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, int type, int reservedFlag, out IntPtr credentialPtr);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref Credential credential, int flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern void CredFree(IntPtr buffer);

    private static string Target(string service, string account) =>
        AppPaths.IsDevBuild ? $"{service}.dev:{account}" : $"{service}:{account}";

    public string? Get(string service, string account)
    {
        if (!CredRead(Target(service, account), CredTypeGeneric, 0, out var ptr)) return null;
        try
        {
            var cred = Marshal.PtrToStructure<Credential>(ptr);
            if (cred.CredentialBlobSize == 0 || cred.CredentialBlob == IntPtr.Zero) return "";
            var bytes = new byte[cred.CredentialBlobSize];
            Marshal.Copy(cred.CredentialBlob, bytes, 0, bytes.Length);
            return Encoding.Unicode.GetString(bytes);
        }
        finally
        {
            CredFree(ptr);
        }
    }

    public void Set(string service, string account, string secret)
    {
        var bytes = Encoding.Unicode.GetBytes(secret);
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var cred = new Credential
            {
                Type = CredTypeGeneric,
                TargetName = Target(service, account),
                CredentialBlob = blob,
                CredentialBlobSize = bytes.Length,
                Persist = CredPersistLocalMachine,
                UserName = account,
                Comment = "Universal Meeting Recorder",
            };
            if (!CredWrite(ref cred, 0))
            {
                Core.Util.Log.Error($"CredentialStore: CredWrite не удался, код {Marshal.GetLastWin32Error()}");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(blob);
        }
    }

    public void Delete(string service, string account) => CredDelete(Target(service, account), CredTypeGeneric, 0);
}
