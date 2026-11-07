#if WINDOWS
using Microsoft.Win32;
using Windows.ApplicationModel;

namespace Tbox.Desktop.Platform;

internal sealed class WindowsStartupRegistration : IStartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Tbox.Desktop";
    private static string Command => $"\"{Environment.ProcessPath}\"";
    private static bool IsPackaged
    {
        get { try { return !string.IsNullOrEmpty(Package.Current.Id.FamilyName); } catch { return false; } }
    }
    public async Task<StartupState> ReadAsync()
    {
        if (IsPackaged) return Describe((await StartupTask.GetAsync("TboxStartupTask")).State);
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return new(string.Equals(key?.GetValue(ValueName) as string, Command, StringComparison.OrdinalIgnoreCase));
    }
    public async Task<StartupState> SetEnabledAsync(bool enabled)
    {
        if (IsPackaged)
        {
            var task = await StartupTask.GetAsync("TboxStartupTask");
            if (enabled) return Describe(await task.RequestEnableAsync());
            task.Disable(); return Describe(task.State);
        }
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled) key.SetValue(ValueName, Command);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
        return await ReadAsync();
    }
    private static StartupState Describe(StartupTaskState state) => new(
        state is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy,
        state is StartupTaskState.DisabledByUser or StartupTaskState.DisabledByPolicy,
        state is StartupTaskState.DisabledByUser or StartupTaskState.DisabledByPolicy
            ? "开机启动已被系统关闭，请在 Windows 设置或任务管理器中检查启动应用。" : null);
}
#endif
