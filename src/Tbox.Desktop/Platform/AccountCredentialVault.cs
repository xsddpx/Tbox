using System.Runtime.InteropServices;
using System.Text.Json;

namespace Tbox.Desktop.Platform;

internal interface IAccountCredentialVault
{
    Task<string?> ReadAsync(CancellationToken ct);
    Task WriteAsync(string value, CancellationToken ct);
}

internal sealed class AccountCredentialVault : IAccountCredentialVault
{
    private const string WindowsTarget = "Tbox/Account/v1";

    public Task<string?> ReadAsync(CancellationToken ct) => Task.Run(() =>
        OperatingSystem.IsWindows() ? WindowsModelCredentialStore.ReadCredential(WindowsTarget)
            : ReadMac(GetMac()).GetProperty("value").GetString(), ct);

    public Task WriteAsync(string value, CancellationToken ct) => Task.Run(() =>
    {
        if (OperatingSystem.IsWindows()) WindowsModelCredentialStore.WriteCredential(WindowsTarget, value);
        else ReadMac(SetMac(value));
    }, ct);

    private static JsonElement ReadMac(nint pointer)
    {
        if (pointer == 0) throw new IOException("无法读取 Tbox 账号钥匙串。");
        try
        {
            using var content = JsonDocument.Parse(Marshal.PtrToStringUTF8(pointer) ?? "{}");
            if (content.RootElement.TryGetProperty("error", out _))
                throw new IOException("Tbox 账号钥匙串不可用，请解锁后重试。");
            return content.RootElement.Clone();
        }
        finally { FreeMac(pointer); }
    }

    [DllImport("TboxNative", EntryPoint = "tbox_account_secret_get")] private static extern nint GetMac();
    [DllImport("TboxNative", EntryPoint = "tbox_account_secret_set")]
    private static extern nint SetMac([MarshalAs(UnmanagedType.LPUTF8Str)] string value);
    [DllImport("TboxNative", EntryPoint = "tbox_free")] private static extern void FreeMac(nint pointer);
}
