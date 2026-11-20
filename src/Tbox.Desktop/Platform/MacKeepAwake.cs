using System.Runtime.InteropServices;
using Tbox.Core;

namespace Tbox.Desktop.Platform;

internal sealed class MacKeepAwake(string stateDirectory) : IKeepAwake
{
    private readonly string _legacyMarker = Path.Combine(stateDirectory, "keep-awake-sleep-disabled");
    private readonly SemaphoreSlim _sync = new(1, 1);
    private KeepAwakeRequest _applied = KeepAwakeRequest.None;

    public bool HasLid { get; } = NativeHasLid() != 0;
    public bool LidNeedsAuthorization => true;

    public async Task<KeepAwakeResult> ApplyAsync(KeepAwakeRequest request)
    {
        await _sync.WaitAsync();
        try
        {
            var held = NativeKeepAwake(request.System ? 1 : 0, request.Display ? 1 : 0);
            _applied = new KeepAwakeRequest((held & 1) != 0, (held & 2) != 0, _applied.Lid);
            var wantLid = _applied.System && request.Lid && HasLid;
            var legacyChange = File.Exists(_legacyMarker);
            if (wantLid || legacyChange || _applied.Lid)
            {
                if (NativeLidApproved() == 0)
                {
                    _applied = _applied with { Lid = false };
                    return new KeepAwakeResult(_applied, RequiresLidApproval: true);
                }

                var status = await Task.Run(() => NativeLidApply(wantLid ? 1 : 0, legacyChange ? 1 : 0));
                if (status < 0)
                {
                    var message = wantLid ? "后台组件未能开启合盖唤醒，请重试。" : "后台组件未能确认已恢复睡眠，请检查系统设置。";
                    return new KeepAwakeResult(_applied, message);
                }

                _applied = _applied with { Lid = status == 1 };
                if (legacyChange) File.Delete(_legacyMarker);
            }

            return new KeepAwakeResult(_applied, request.System && !_applied.System ? "系统拒绝保持唤醒。" : null);
        }
        finally { _sync.Release(); }
    }

    public string? RequestLidApproval() =>
        NativeRequestLidApproval() == 0 ? null : "无法注册合盖唤醒后台组件。请从「应用程序」中打开 Tbox 后重试。";

    public void Dispose() => NativeKeepAwake(0, 0);

    [DllImport("TboxNative", EntryPoint = "tbox_keep_awake")] private static extern int NativeKeepAwake(int system, int display);
    [DllImport("TboxNative", EntryPoint = "tbox_has_lid")] private static extern int NativeHasLid();
    [DllImport("TboxNative", EntryPoint = "tbox_lid_sleep_approved")] private static extern int NativeLidApproved();
    [DllImport("TboxNative", EntryPoint = "tbox_lid_sleep_request_approval")] private static extern int NativeRequestLidApproval();
    [DllImport("TboxNative", EntryPoint = "tbox_lid_sleep_apply")] private static extern int NativeLidApply(int hold, int legacyChange);
}
