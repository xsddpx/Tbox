using System.Diagnostics;
using System.Runtime.InteropServices;
using Tbox.Core;

namespace Tbox.Desktop.Platform;

// macOS has no per-plan lid action. `pmset disablesleep 1` keeps a closed MacBook awake and needs the administrator
// password both ways. It outlives the process, so a marker file records that Tbox turned it on.
internal sealed class MacKeepAwake(string stateDirectory) : IKeepAwake
{
    private readonly string _marker = Path.Combine(stateDirectory, "keep-awake-sleep-disabled");
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
            if (request.System && !_applied.System)
                return new KeepAwakeResult(_applied, "系统拒绝保持唤醒。");

            var wantLid = request.System && request.Lid && HasLid;
            var sleepDisabled = await SleepDisabledAsync();
            if (wantLid && !sleepDisabled)
            {
                if (await SetSleepDisabledAsync(true) is { } refused) return new KeepAwakeResult(_applied with { Lid = false }, refused);
                File.WriteAllText(_marker, "");
            }
            // Only undo what Tbox did: without the marker the setting belongs to the user or another app.
            else if (!wantLid && File.Exists(_marker))
            {
                // Cancelled: sleep stays disabled, so report it as still in effect.
                if (sleepDisabled && await SetSleepDisabledAsync(false) is { } kept) return new KeepAwakeResult(_applied = _applied with { Lid = true }, kept);
                File.Delete(_marker);
            }
            _applied = _applied with { Lid = wantLid };
            return new KeepAwakeResult(_applied);
        }
        finally { _sync.Release(); }
    }

    // Null on success; otherwise why it did not change. Cancelling the password prompt is not an error.
    private static async Task<string?> SetSleepDisabledAsync(bool disabled)
    {
        var script = $"do shell script \"/usr/bin/pmset -a disablesleep {(disabled ? 1 : 0)}\" with administrator privileges " +
            "with prompt \"Tbox 需要管理员密码来更改合上盖子时的睡眠设置。\"";
        var (code, _, error) = await RunAsync("/usr/bin/osascript", "-e", script);
        if (code == 0) return null;
        // -128 is userCanceledErr.
        if (error.Contains("-128")) return disabled ? "已取消授权，合上盖子时仍会睡眠。" : "已取消授权，合上盖子时仍不会睡眠。下次启动 Tbox 时会再次请求恢复。";
        AppLog.Write("pmset disablesleep failed: " + error);
        return "管理员授权失败，合上盖子时的睡眠设置未更改。";
    }

    private static async Task<bool> SleepDisabledAsync()
    {
        var (code, output, _) = await RunAsync("/usr/bin/pmset", "-g");
        return code == 0 && output.Split('\n').Any(line =>
            line.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries) is ["SleepDisabled", "1", ..]);
    }

    private static async Task<(int Code, string Output, string Error)> RunAsync(string file, params string[] arguments)
    {
        var info = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new IOException($"无法启动 {file}。");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await output, await error);
    }

    // The assertions end with the process. Changing the lid setting needs a password prompt, which Dispose cannot show;
    // a normal quit restores it first (DesktopController.QuitAsync). Otherwise the marker stays and the next start
    // keeps or restores it according to the saved settings.
    public void Dispose() => NativeKeepAwake(0, 0);

    [DllImport("TboxNative", EntryPoint = "tbox_keep_awake")] private static extern int NativeKeepAwake(int system, int display);
    [DllImport("TboxNative", EntryPoint = "tbox_has_lid")] private static extern int NativeHasLid();
}
