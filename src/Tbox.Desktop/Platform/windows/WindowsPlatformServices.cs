#if WINDOWS
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Tbox.Core;
using Windows.ApplicationModel;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace Tbox.Desktop.Platform;

// Only OS integration lives here. Selection, drawing, pins and OCR presentation are shared.
public sealed class WindowsPlatformServices : IPlatformServices
{
    private readonly SemaphoreSlim _recognitionLock = new(1, 1);
    private WindowsHotkeys? _hotkeys;
    public bool HasScreenPermission => true;
    public bool RequestScreenPermission() => true;
    public void OpenScreenPermissionSettings() { }

    public Task<IReadOnlyList<CapturedDisplay>> CaptureAsync(CancellationToken cancellationToken) => Task.Run<IReadOnlyList<CapturedDisplay>>(() =>
    {
        var monitors = new List<nint>();
        MonitorEnum callback = (nint monitor, nint _, ref NativeRect bounds, nint data) => { monitors.Add(monitor); return true; };
        if (!EnumDisplayMonitors(0, 0, callback, 0)) throw Error("无法枚举显示器");
        if (monitors.Count == 0) throw new InvalidOperationException("没有可截图的显示器。");
        return monitors.Select((monitor, index) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = ReadMonitor(monitor);
            var r = info.Monitor;
            return new CapturedDisplay((uint)index, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, Capture(r));
        }).ToArray();
    }, cancellationToken);

    private static byte[] Capture(NativeRect bounds)
    {
        var width = bounds.Right - bounds.Left; var height = bounds.Bottom - bounds.Top;
        var screen = GetDC(0);
        if (screen == 0) throw Error("无法访问屏幕");
        nint memory = 0, bitmap = 0, previous = 0;
        try
        {
            memory = CreateCompatibleDC(screen);
            if (memory == 0) throw Error("无法创建截图缓冲区");
            var info = new BitmapInfo
            {
                Header = new BitmapHeader
                {
                    Size = (uint)Marshal.SizeOf<BitmapHeader>(), Width = width, Height = -height,
                    Planes = 1, BitCount = 32, SizeImage = checked((uint)(width * height * 4))
                }
            };
            bitmap = CreateDIBSection(screen, ref info, 0, out var bits, 0, 0);
            if (bitmap == 0 || bits == 0) throw Error("无法创建截图位图");
            previous = SelectObject(memory, bitmap);
            if (previous == 0 || previous == -1) throw Error("无法选择截图位图");
            if (!BitBlt(memory, 0, 0, width, height, screen, bounds.Left, bounds.Top, 0x00CC0020 | 0x40000000))
                throw Error("截图失败");
            using var image = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            using (var target = image.Lock())
            {
                var row = new byte[checked(width * 4)];
                for (var y = 0; y < height; y++)
                {
                    Marshal.Copy(bits + y * row.Length, row, 0, row.Length);
                    for (var x = 3; x < row.Length; x += 4) row[x] = 255;
                    Marshal.Copy(row, 0, target.Address + y * target.RowBytes, row.Length);
                }
            }
            using var output = new MemoryStream(); image.Save(output); return output.ToArray();
        }
        finally
        {
            if (previous != 0 && previous != -1) SelectObject(memory, previous);
            if (bitmap != 0) DeleteObject(bitmap);
            if (memory != 0) DeleteDC(memory);
            ReleaseDC(0, screen);
        }
    }

    public Task<byte[]> CaptureRegionAsync(DesktopPlacement region, CancellationToken cancellationToken) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (region.Width < 1 || region.Height < 1) throw new ArgumentOutOfRangeException(nameof(region));
        return Capture(new NativeRect { Left = (int)Math.Round(region.Left), Top = (int)Math.Round(region.Top),
            Right = (int)Math.Round(region.Left + region.Width), Bottom = (int)Math.Round(region.Top + region.Height) });
    }, cancellationToken);

    public void PrepareCaptureOverlay(nint handle, bool clickThrough)
    {
        if (!SetWindowDisplayAffinity(handle, 0x11)) throw Error("无法从截图中排除控制窗口");
        if (clickThrough) SetWindowLongPtr(handle, -20, GetWindowLongPtr(handle, -20) | 0x20 | 0x80000);
    }

    public async Task<RecognitionResult> RecognizeAsync(byte[] png, CancellationToken cancellationToken)
    {
        try { _ = Package.Current.Id.Name; }
        catch (Exception error) { throw new InvalidOperationException("Windows 系统 OCR 需要应用身份，请安装 Tbox MSIX 后使用。", error); }
        await _recognitionLock.WaitAsync(cancellationToken);
        try
        {
            var engine = OcrEngine.TryCreateFromUserProfileLanguages()
                ?? throw new InvalidOperationException("请在 Windows 语言设置中安装所需的文字识别语言。");
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(png.AsBuffer()).AsTask(cancellationToken); stream.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken);
            var size = OcrImageScalePolicy.FitWithinMaximumDimension((int)decoder.PixelWidth, (int)decoder.PixelHeight, (int)OcrEngine.MaxImageDimension);
            var transform = new BitmapTransform { ScaledWidth = (uint)size.Width, ScaledHeight = (uint)size.Height, InterpolationMode = Windows.Graphics.Imaging.BitmapInterpolationMode.Fant };
            using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
                transform, ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage).AsTask(cancellationToken);
            var result = await engine.RecognizeAsync(bitmap).AsTask(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var lines = result.Lines.Select(line =>
            {
                if (line.Words.Count == 0) return new RecognizedLine(line.Text, 0, 0, 0, 0);
                var left = line.Words.Min(word => word.BoundingRect.Left);
                var top = line.Words.Min(word => word.BoundingRect.Top);
                var right = line.Words.Max(word => word.BoundingRect.Right);
                var bottom = line.Words.Max(word => word.BoundingRect.Bottom);
                return new RecognizedLine(line.Text, left / size.Width, top / size.Height, (right - left) / size.Width, (bottom - top) / size.Height);
            }).ToArray();
            return new RecognitionResult(string.Join(Environment.NewLine, lines.Select(line => line.Text)), lines);
        }
        finally { _recognitionLock.Release(); }
    }

    public bool RegisterHotkey(HotkeyBinding binding, Action<HotkeyAction> onPressed)
    {
        _hotkeys ??= new WindowsHotkeys(onPressed);
        _hotkeys.Unregister(binding.Action);
        return _hotkeys.Register(binding);
    }
    public void UnregisterHotkey(HotkeyAction action) => _hotkeys?.Unregister(action);

    public void PlaceWindow(nint handle, DesktopPlacement p, bool overlay)
    {
        if (!SetWindowPos(handle, -1, (int)Math.Round(p.Left), (int)Math.Round(p.Top),
            Math.Max(1, (int)Math.Round(p.Width)), Math.Max(1, (int)Math.Round(p.Height)), 0x10)) throw Error("无法定位窗口");
    }

    public void MoveToPointer(nint handle)
    {
        if (!GetCursorPos(out var point)) return;
        var bounds = GetWindowPlacement(handle); var work = ReadMonitor(MonitorFromPoint(point, 2)).Work;
        var ratio = Math.Min(1, Math.Min((work.Right - work.Left) / bounds.Width, (work.Bottom - work.Top) / bounds.Height));
        var width = bounds.Width * ratio; var height = bounds.Height * ratio;
        var left = Math.Clamp(point.X - width / 2, work.Left, Math.Max(work.Left, work.Right - width));
        var top = Math.Clamp(point.Y - height / 2, work.Top, Math.Max(work.Top, work.Bottom - height));
        if (!SetWindowPos(handle, 0, (int)left, (int)top, (int)width, (int)height, 0x04 | 0x10)) throw Error("无法移动窗口");
    }

    public DesktopPlacement GetWindowPlacement(nint handle)
    {
        if (!GetWindowRect(handle, out var bounds)) throw Error("无法读取窗口位置");
        return bounds.Placement;
    }
    public DesktopPlacement GetWorkArea(nint handle) => ReadMonitor(MonitorFromWindow(handle, 2)).Work.Placement;
    public double DesktopUnitsPerDip(nint handle) => Math.Max(96u, GetDpiForWindow(handle)) / 96d;
    public DesktopPoint GetPointerPosition()
    {
        if (!GetCursorPos(out var point)) throw Error("无法读取指针位置");
        return new DesktopPoint(point.X, point.Y);
    }
    public ICaptureTargetSource CreateCaptureTargets() => WindowsCaptureTargets.CreateSnapshot();
    public IRegionRecorder CreateRecorder(RecordingArea area, bool systemAudio, int framesPerSecond) => new WindowsRegionRecorder(area, systemAudio, framesPerSecond);
    internal WindowsFolderPicker FolderPicker { get; } = new();
    public IFolderNavigation CreateFolderNavigation() => new WindowsFolderNavigation(FolderPicker, this);
    public async Task<string?> PickFolderAsync(Window owner, string title) => await Dispatcher.UIThread.InvokeAsync(
        () => FolderPicker.Show(owner.TryGetPlatformHandle()?.Handle ?? 0, title), DispatcherPriority.Background);
    public IStartupRegistration CreateStartupRegistration() => new WindowsStartupRegistration();
    public void Dispose() { _hotkeys?.Dispose(); _hotkeys = null; }
    private static Win32Exception Error(string message) => new(Marshal.GetLastWin32Error(), message);
    private static MonitorInfo ReadMonitor(nint monitor)
    {
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) throw Error("无法读取显示器");
        return info;
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
        public readonly DesktopPlacement Placement => new(Left, Top, Right - Left, Bottom - Top);
    }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public uint Size; public NativeRect Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapHeader
    {
        public uint Size; public int Width, Height; public ushort Planes, BitCount;
        public uint Compression, SizeImage; public int XPelsPerMeter, YPelsPerMeter; public uint ColorsUsed, ColorsImportant;
    }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo { public BitmapHeader Header; public uint Colors; }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate bool MonitorEnum(nint monitor, nint dc, ref NativeRect bounds, nint data);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorEnum callback, nint data);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(NativePoint point, uint flags);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowRect(nint window, out NativeRect bounds);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint GetDC(nint window);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowDisplayAffinity(nint window, uint affinity);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint window, int index, nint value);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint SelectObject(nint dc, nint value);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern bool BitBlt(nint target, int x, int y, int width, int height, nint source, int sourceX, int sourceY, uint operation);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
}
#endif
