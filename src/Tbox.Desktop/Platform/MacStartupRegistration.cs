using System.Runtime.InteropServices;

namespace Tbox.Desktop.Platform;

internal sealed class MacStartupRegistration : IStartupRegistration
{
    public Task<StartupState> ReadAsync() => Task.Run(() => MacPlatformServices.ReadJson<StartupState>(NativeStatus()));
    public Task<StartupState> SetEnabledAsync(bool enabled) => Task.Run(() => MacPlatformServices.ReadJson<StartupState>(NativeSetEnabled(enabled ? 1 : 0)));
    [DllImport("TboxNative", EntryPoint = "tbox_startup_status")] private static extern nint NativeStatus();
    [DllImport("TboxNative", EntryPoint = "tbox_startup_set")] private static extern nint NativeSetEnabled(int enabled);
}
