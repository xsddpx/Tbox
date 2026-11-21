using System.Runtime.InteropServices;
using Tbox.Core.ModelServices;

namespace Tbox.Desktop.Platform;

internal sealed class WindowsModelCredentialStore : IModelCredentialStore
{
    private const int NotFound = 1168;
    private static string Target(Guid id) => "Tbox/Models/" + id.ToString("N");
    public Task<string?> ReadAsync(Guid id, CancellationToken ct = default) => Execute(() => ReadCredential(Target(id)), ct);

    internal static string? ReadCredential(string target)
    {
        if (!CredRead(target, 1, 0, out var pointer)) { if (Marshal.GetLastWin32Error() == NotFound) return null; throw Error(); }
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            return Marshal.PtrToStringUni(credential.Blob, checked((int)credential.BlobSize / 2));
        }
        finally { CredFree(pointer); }
    }

    internal static void WriteCredential(string target, string value)
    {
        var pointer = Marshal.StringToCoTaskMemUni(value);
        try
        {
            var credential = new Credential
            {
                Type = 1, TargetName = target, UserName = "Tbox",
                Blob = pointer, BlobSize = checked((uint)value.Length * 2), Persist = 2
            };
            if (!CredWrite(ref credential, 0)) throw Error();
        }
        finally { Marshal.ZeroFreeCoTaskMemUnicode(pointer); }
    }

    public Task DeleteAsync(Guid id, CancellationToken ct = default) => Execute(() =>
    {
        if (!CredDelete(Target(id), 1, 0) && Marshal.GetLastWin32Error() != NotFound) throw Error();
        return true;
    }, ct);
    private static Task<T> Execute<T>(Func<T> operation, CancellationToken ct) => Task.Run(() => { ct.ThrowIfCancellationRequested(); return operation(); }, ct);
    private static ModelServiceException Error() => new(ModelFailure.Credentials, "无法访问 Windows 凭据管理器，请确认当前用户已登录。");
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags, Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint BlobSize;
        public nint Blob;
        public uint Persist, AttributeCount;
        public nint Attributes;
        public string? TargetAlias;
        public string UserName;
    }
    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint flags, out nint credential);
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);
    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref Credential credential, uint flags);
    [DllImport("advapi32.dll")] private static extern void CredFree(nint credential);
}
