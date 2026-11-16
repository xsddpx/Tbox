using System.Runtime.InteropServices;
using System.Text.Json;
using Tbox.Core.ModelServices;

namespace Tbox.Desktop.Platform;

internal sealed class MacModelCredentialStore : IModelCredentialStore
{
    public Task<string?> ReadAsync(Guid id, CancellationToken ct = default) => Execute(() =>
    {
        var value = Read(Get(id.ToString("D"))).GetProperty("value");
        return value.ValueKind == JsonValueKind.Null ? null : value.GetString();
    }, ct);
    public Task DeleteAsync(Guid id, CancellationToken ct = default) => Execute(() => { Read(Delete(id.ToString("D"))); return true; }, ct);

    // Once a native write/delete begins, report its actual outcome rather than claiming it was cancelled.
    private static Task<T> Execute<T>(Func<T> operation, CancellationToken ct) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        try { return operation(); }
        catch (ModelServiceException) { throw; }
        catch { throw new ModelServiceException(ModelFailure.Credentials, "无法访问系统钥匙串，请确认钥匙串已解锁并允许 Tbox 访问。"); }
    }, ct);

    private static JsonElement Read(nint pointer)
    {
        if (pointer == 0) throw Error();
        try
        {
            using var json = JsonDocument.Parse(Marshal.PtrToStringUTF8(pointer) ?? "{}");
            if (json.RootElement.TryGetProperty("error", out _)) throw Error();
            return json.RootElement.Clone();
        }
        finally { Free(pointer); }
    }
    private static ModelServiceException Error() => new(ModelFailure.Credentials, "钥匙串操作未完成，请解锁或允许 Tbox 访问。");
    [DllImport("TboxNative", EntryPoint = "tbox_model_secret_get")] private static extern nint Get([MarshalAs(UnmanagedType.LPUTF8Str)] string id);
    [DllImport("TboxNative", EntryPoint = "tbox_model_secret_delete")] private static extern nint Delete([MarshalAs(UnmanagedType.LPUTF8Str)] string id);
    [DllImport("TboxNative", EntryPoint = "tbox_free")] private static extern void Free(nint pointer);
}
