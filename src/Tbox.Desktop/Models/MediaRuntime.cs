using LibVLCSharp.Shared;
using System.Runtime.InteropServices;

namespace Tbox.Desktop.Models;

internal static class MediaRuntime
{
    private static readonly object Gate = new();
    private static bool _initialized;
    public static LibVLC Create()
    {
        lock (Gate)
        {
            if (!_initialized)
            {
                var custom = Environment.GetEnvironmentVariable("TBOX_VLC_DIRECTORY");
                if (OperatingSystem.IsMacOS())
                {
                    var root = custom ?? Path.Combine(AppContext.BaseDirectory, "ThirdParty", "VLC");
                    if (!Directory.Exists(root)) throw new DirectoryNotFoundException("录屏回放组件缺失，仍可保存母版。");
                    // .NET's Unix environment cache is not libc's environment, which LibVLC reads.
                    if (SetEnvironment("VLC_PLUGIN_PATH", Path.Combine(root, "plugins"), 1) != 0)
                        throw new InvalidOperationException("无法设置回放组件目录。");
                    LibVLCSharp.Shared.Core.Initialize(Path.Combine(root, "lib"));
                }
                else LibVLCSharp.Shared.Core.Initialize(custom);
                _initialized = true;
            }
        }
        return new LibVLC("--no-video-title-show", "--no-metadata-network-access");
    }

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "setenv")]
    private static extern int SetEnvironment([MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value, int overwrite);
}
