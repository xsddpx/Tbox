using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Tbox.Core;
using Tbox.Desktop;
using Tbox.Desktop.Platform;
using Tbox.Desktop.Views;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;

namespace Tbox.ClipboardSmoke;

// Verifies on a real Windows desktop that an image copied through Tbox's clipboard path reaches other
// applications as a usable colour image. Regression: the shared Avalonia path published a 1 bpp, all-black
// CF_BITMAP, which the Windows clipboard history and the emoji panel preferred, so they showed a black tile.
internal static class Program
{
    private const int Width = 8;
    private const int Height = 6;
    private const uint CfDib = 8;
    private const uint CfBitmap = 2;

    [STAThread]
    private static int Main()
    {
        var pixels = BuildPixels();
        var lifetime = new ClassicDesktopStyleApplicationLifetime { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        AppBuilder.Configure<Application>().UsePlatformDetect().SetupWithLifetime(lifetime);
        var result = new TaskCompletionSource<Exception?>();
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                var window = new Window { Width = 160, Height = 100 };
                window.Show();
                var png = EncodePng(pixels);
                using (var platform = new WindowsPlatformServices())
                {
                    if (!platform.TryWriteClipboardImage(png, pixels, Width, Height))
                        throw new InvalidOperationException("Windows 平台未发布剪贴板图片。");
                }
                VerifyFormats();
                await VerifyWindowsClipboardApiAsync(pixels);
                await VerifyClipboardPinsAsync(lifetime, pixels);
                window.Close();
                Console.WriteLine($"OK 剪贴板图片为彩色 {Width}x{Height}，系统剪贴板 API 读到的像素一致。");
                result.SetResult(null);
            }
            catch (Exception error) { result.SetResult(error); }
            finally { lifetime.Shutdown(); }
        });
        lifetime.Start([]);
        var failure = result.Task.GetAwaiter().GetResult();
        if (failure is null) return 0;
        Console.Error.WriteLine(failure);
        return 1;
    }

    private static async Task VerifyClipboardPinsAsync(ClassicDesktopStyleApplicationLifetime lifetime, byte[] expected)
    {
        var root = Path.Combine(Path.GetTempPath(), "tbox-pin-smoke-" + Guid.NewGuid().ToString("N"));
        var settingsFile = Path.Combine(root, "settings.json");
        try
        {
            await new JsonSettingsStore(settingsFile).SaveAsync(new AppSettings
            {
                FolderJumpEnabled = false,
                ScreenCaptureCacheDirectory = Path.Combine(root, "captures")
            });
            using var controller = new DesktopController(new WindowsPlatformServices(), lifetime, settingsFile);
            Application.Current!.Styles.Add(DesignTokens.CreateTheme());
            controller.Main = new MainWindow(controller);
            await controller.InitializeAsync([]);
            var shortcut = controller.ShortcutFor(HotkeyAction.Pin);
            if (ShortcutPolicy.WindowsKeyCode(shortcut.Key) != 0x76 || controller.HasHotkeyConflictFor(HotkeyAction.Pin))
                throw new InvalidOperationException("本轮未注册默认 F7 贴图快捷键。");

            var hotkeyWindow = FindWindowEx(new IntPtr(-3), IntPtr.Zero, null, "Tbox hotkeys");
            GetWindowThreadProcessId(hotkeyWindow, out var processId);
            if (hotkeyWindow == IntPtr.Zero || processId != (uint)Environment.ProcessId)
                throw new InvalidOperationException("未找到本轮的 Windows 快捷键消息窗口。");

            async Task<PinnedWindow> PinWithF7Async()
            {
                // Deliver the registered Windows message without relying on physical keyboard focus on the runner.
                if (!PostMessage(hotkeyWindow, 0x0312, (nint)HotkeyAction.Pin, 0x76 << 16))
                    throw new InvalidOperationException("无法递交 F7 快捷键消息。");
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                while (true)
                {
                    var pin = lifetime.Windows.OfType<PinnedWindow>().SingleOrDefault();
                    if (pin?.Picture.CachePath is not null) return pin;
                    await Task.Delay(25, timeout.Token);
                }
            }

            var image = await PinWithF7Async();
            if (!image.Picture.GetPixels().SequenceEqual(expected))
                throw new InvalidOperationException("F7 贴图未保留 Windows 剪贴板中的彩色像素。");
            image.Close();

            const string text = "Tbox clipboard fixture";
            var data = new DataPackage();
            data.SetText(text);
            Clipboard.SetContent(data);
            Clipboard.Flush();
            var textPin = await PinWithF7Async();
            using var expectedCard = DesktopController.RenderText(text);
            if (!textPin.Picture.GetPixels().SequenceEqual(expectedCard.GetPixels()))
                throw new InvalidOperationException("F7 未把纯文本创建为贴图。");
            textPin.Close();
            controller.Main.Close();
            Console.WriteLine("OK Windows F7 快捷键消息经应用入口把系统剪贴板的彩色图片和纯文本创建为贴图。");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [DllImport("user32.dll", EntryPoint = "FindWindowExW", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? className, string title);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    private static extern bool PostMessage(IntPtr window, uint message, nint wParam, nint lParam);

    private static byte[] EncodePng(byte[] pixels)
    {
        var bitmap = new WriteableBitmap(new PixelSize(Width, Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using (var target = bitmap.Lock())
            for (var y = 0; y < Height; y++) Marshal.Copy(pixels, y * Width * 4, target.Address + y * target.RowBytes, Width * 4);
        using var stream = new MemoryStream();
        bitmap.Save(stream);
        bitmap.Dispose();
        return stream.ToArray();
    }

    private static void VerifyFormats()
    {
        if (!OpenClipboard(IntPtr.Zero)) throw new InvalidOperationException("无法打开剪贴板。");
        try
        {
            var pngFormat = RegisterClipboardFormat("PNG");
            var pngMimeFormat = RegisterClipboardFormat("image/png");
            if (GetClipboardData(CfDib) == IntPtr.Zero) throw new InvalidOperationException("剪贴板缺少 CF_DIB。");
            if (pngFormat == 0 || GetClipboardData(pngFormat) == IntPtr.Zero) throw new InvalidOperationException("剪贴板缺少 PNG。");
            if (pngMimeFormat == 0 || GetClipboardData(pngMimeFormat) == IntPtr.Zero) throw new InvalidOperationException("剪贴板缺少 image/png。");
            // Windows derives CF_BITMAP from CF_DIB itself; it must be a colour bitmap that shows the source.
            var bitmapHandle = GetClipboardData(CfBitmap);
            if (bitmapHandle == IntPtr.Zero) throw new InvalidOperationException("剪贴板缺少 CF_BITMAP：" + DescribeFormats());
            var bitmap = new Bitmap();
            if (GetObject(bitmapHandle, Marshal.SizeOf<Bitmap>(), ref bitmap) == 0)
                throw new InvalidOperationException("无法读取剪贴板位图。");
            if (bitmap.bmWidth != Width || bitmap.bmHeight != Height)
                throw new InvalidOperationException($"剪贴板位图为 {bitmap.bmWidth}x{bitmap.bmHeight}，期望 {Width}x{Height}。");
            // A bitmap compatible with a memory DC is 1 bpp: other applications then read a black image.
            if (bitmap.bmBitsPixel < 24)
                throw new InvalidOperationException($"剪贴板位图只有 {bitmap.bmBitsPixel} bpp，其他应用会读到黑白或全黑图像。");

            var dib = ReadAll(GetClipboardData(CfDib));
            if (dib.Length < 40) throw new InvalidOperationException("CF_DIB 数据过短。");
            var width = BitConverter.ToInt32(dib, 4);
            var height = BitConverter.ToInt32(dib, 8);
            var bitCount = BitConverter.ToInt16(dib, 14);
            if (width != Width || height != Height || bitCount != 32 || height < 0)
                throw new InvalidOperationException($"CF_DIB 为 {width}x{height} {bitCount}bpp（height={height}），期望 {Width}x{Height} 32bpp 自下而上。");
        }
        finally { CloseClipboard(); }
    }

    private static string DescribeFormats()
    {
        var parts = new List<string>();
        uint format = 0;
        while ((format = EnumClipboardFormats(format)) != 0)
        {
            var name = new System.Text.StringBuilder(128);
            var length = GetClipboardFormatName(format, name, name.Capacity);
            parts.Add($"{format}:{(length > 0 ? name.ToString() : "<predefined>")}");
        }
        return string.Join(", ", parts);
    }

    private static byte[] ReadAll(IntPtr handle)
    {
        var size = (int)GlobalSize(handle);
        var source = GlobalLock(handle);
        if (source == IntPtr.Zero) throw new InvalidOperationException("无法读取剪贴板数据。");
        try
        {
            var data = new byte[size];
            Marshal.Copy(source, data, 0, size);
            return data;
        }
        finally { GlobalUnlock(handle); }
    }

    // The clipboard history and the emoji panel decode the clipboard through the WinRT data transfer API.
    private static async Task VerifyWindowsClipboardApiAsync(byte[] expected)
    {
        DataPackageView? view = null;
        for (var attempt = 0; attempt < 20 && view is null; attempt++)
        {
            try { view = Clipboard.GetContent(); }
            catch (Exception) { await Task.Delay(100); }
        }
        if (view is null) throw new InvalidOperationException("无法读取系统剪贴板。");
        var reference = await view.GetBitmapAsync();
        using var stream = await reference.OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(stream);
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        if (bitmap.PixelWidth != Width || bitmap.PixelHeight != Height)
            throw new InvalidOperationException($"系统剪贴板读到的尺寸为 {bitmap.PixelWidth}x{bitmap.PixelHeight}。");
        var decoded = new byte[Width * Height * 4];
        bitmap.CopyToBuffer(decoded.AsBuffer());
        for (var index = 0; index < decoded.Length; index += 4)
        {
            if (decoded[index] == expected[index] && decoded[index + 1] == expected[index + 1] &&
                decoded[index + 2] == expected[index + 2]) continue;
            throw new InvalidOperationException(
                $"系统剪贴板读到的像素 {index / 4} 为 B{decoded[index]}G{decoded[index + 1]}R{decoded[index + 2]}，" +
                $"期望 B{expected[index]}G{expected[index + 1]}R{expected[index + 2]}。");
        }
    }

    private static byte[] BuildPixels()
    {
        var pixels = new byte[Width * Height * 4];
        for (var y = 0; y < Height; y++)
        for (var x = 0; x < Width; x++)
        {
            var index = (y * Width + x) * 4;
            pixels[index] = (byte)(10 + x * 20);
            pixels[index + 1] = (byte)(30 + y * 30);
            pixels[index + 2] = (byte)(200 - x * 10);
            pixels[index + 3] = 255;
        }
        return pixels;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetClipboardData(uint format);
    [DllImport("user32.dll", EntryPoint = "RegisterClipboardFormatW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterClipboardFormat(string name);
    [DllImport("user32.dll")] private static extern uint EnumClipboardFormats(uint format);
    [DllImport("user32.dll", EntryPoint = "GetClipboardFormatNameW", CharSet = CharSet.Unicode)]
    private static extern int GetClipboardFormatName(uint format, System.Text.StringBuilder name, int max);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalUnlock(IntPtr memory);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern UIntPtr GlobalSize(IntPtr memory);
    [DllImport("gdi32.dll")] private static extern int GetObject(IntPtr handle, int size, ref Bitmap bitmap);

    [StructLayout(LayoutKind.Sequential)]
    private struct Bitmap { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public IntPtr bmBits; }
}
