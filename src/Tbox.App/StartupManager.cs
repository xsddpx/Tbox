using Microsoft.Win32;
using Windows.ApplicationModel;

namespace Tbox.App;

internal static class StartupManager
{
    private const string StartupTaskId = "TboxStartupTask";
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Tbox";

    internal static async Task<bool> IsEnabledAsync()
    {
        if (!IsPackaged()) return IsLegacyRegistryEnabled();

        var startupTask = await StartupTask.GetAsync(StartupTaskId);
        if (IsLegacyRegistryEnabled())
        {
            RemoveLegacyRegistryValue();
            if (startupTask.State == StartupTaskState.Disabled)
            {
                return await startupTask.RequestEnableAsync() == StartupTaskState.Enabled;
            }
        }
        return startupTask.State == StartupTaskState.Enabled;
    }

    internal static async Task<bool> SetEnabledAsync(bool enabled)
    {
        if (IsPackaged())
        {
            RemoveLegacyRegistryValue();
            var startupTask = await StartupTask.GetAsync(StartupTaskId);
            if (enabled)
            {
                return startupTask.State == StartupTaskState.Enabled ||
                       await startupTask.RequestEnableAsync() == StartupTaskState.Enabled;
            }

            startupTask.Disable();
            return false;
        }

        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
        if (enabled)
        {
            key.SetValue(ValueName, $"\"{Environment.ProcessPath}\"");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        return enabled;
    }

    private static bool IsPackaged()
    {
        try
        {
            return !string.IsNullOrWhiteSpace(Package.Current.Id.FamilyName);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsLegacyRegistryEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        return key?.GetValue(ValueName) is string;
    }

    private static void RemoveLegacyRegistryValue()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
