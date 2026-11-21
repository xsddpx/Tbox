#if WINDOWS
using System.Runtime.InteropServices;
using Avalonia.Headless.XUnit;
using Tbox.Desktop.Platform;
using Xunit;

namespace Tbox.Desktop.Tests;

public sealed class WindowsWindowTopmostTests
{
    [AvaloniaFact]
    public void AWindowWithAnExistingTopmostOwnedWindowCanBePinnedAndUnpinned()
    {
        var main = CreateWindowEx(0, "Static", "主窗口", 0x90000000, 0, 0, 320, 180, 0, 0, 0, 0);
        Assert.NotEqual(nint.Zero, main);
        var existing = CreateWindowEx(8, "Static", "原有置顶窗口", 0x80000000, 0, 0, 80, 50, main, 0, 0, 0);
        var normal = CreateWindowEx(0, "Static", "普通子窗口", 0x80000000, 0, 0, 80, 50, main, 0, 0, 0);
        try
        {
            Assert.NotEqual(nint.Zero, existing);
            Assert.NotEqual(nint.Zero, normal);
            Assert.False(IsTopmost(main));
            Assert.True(IsTopmost(existing));
            Assert.False(IsTopmost(normal));
            using var topmost = new WindowsWindowTopmost();
            topmost.SetBorderVisible(false);
            topmost.ToggleWindow(main);
            Assert.Equal((long)main, Assert.Single(topmost.Windows).Id);
            Assert.True(IsTopmost(main));
            Assert.True(IsTopmost(existing));
            topmost.ToggleWindow(main);
            Assert.Empty(topmost.Windows);
            Assert.False(IsTopmost(main));
            Assert.True(IsTopmost(existing));
            Assert.False(IsTopmost(normal));
        }
        finally
        {
            DestroyWindow(normal);
            DestroyWindow(existing);
            DestroyWindow(main);
        }
    }

    private static bool IsTopmost(nint window) => (GetWindowLongPtr(window, -20) & 8) != 0;
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode)] private static extern nint CreateWindowEx(uint exStyle, string name, string title, uint style, int x, int y, int width, int height, nint owner, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
}
#endif
