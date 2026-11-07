using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Tbox.Core;
using Tbox.Desktop.Models;
using Tbox.Desktop.Platform;
using Tbox.Desktop.Views;

namespace Tbox.Desktop;

public sealed class DesktopController : IDisposable
{
    private readonly IClassicDesktopStyleApplicationLifetime _lifetime;
    private readonly JsonSettingsStore _settingsStore;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<PinnedWindow> _pins = [];
    private CaptureSession? _capture;
    private LongCaptureSession? _longCapture;
    private OcrWindow? _ocr;
    private AppSettings _settings = new();
    private CaptureCache _cache;
    private bool _capturing;
    private bool _pinsHidden;
    public IPlatformServices Platform { get; }
    public CaptureHistory CaptureHistory { get; } = new();
    public MainWindow Main { get; set; } = null!;
    public bool IsQuitting { get; private set; }
    public string CacheDirectory => _cache.DirectoryPath;
    public event Action<int>? ThemeLoaded;

    public DesktopController(IPlatformServices platform, IClassicDesktopStyleApplicationLifetime lifetime)
    {
        Platform = platform; _lifetime = lifetime;
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _settingsStore = new JsonSettingsStore(Path.Combine(local, "Tbox", "desktop-settings.json"));
        _cache = CreateCache(null);
    }

    public async Task InitializeAsync(string[] args)
    {
        _settings = await _settingsStore.LoadAsync(_stop.Token);
        _cache = CreateCache(_settings.ScreenCaptureCacheDirectory);
        var index = _settings.AppTheme switch { "light" => 1, "dark" => 2, _ => 0 };
        ApplyTheme(index); ThemeLoaded?.Invoke(index);
        if (!args.Contains("--smoke-test"))
        {
            var hotkeys = Platform.RegisterHotkeys(id => Dispatcher.UIThread.Post(() =>
            {
                if (id == 1) Run(CaptureAsync);
                if (id == 2) Run(PinClipboardAsync);
                if (id == 3) TogglePins();
            }));
            Main.SetStatus(hotkeys ? "已就绪" : "快捷键被占用；可从窗口或菜单栏使用所有功能。");
            Main.Refresh();
            PruneCache();
        }
        else
        {
            await SmokeTest.RunAsync(this, args);
        }
    }

    public async void Run(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception exception) { Main.SetStatus(exception.Message); ShowMain(); }
    }

    public void ShowMain() { Main.Show(); Main.Activate(); Main.Refresh(); }
    public void RequestPermission() { Platform.RequestScreenPermission(); Main.Refresh(); }

    public async Task CaptureAsync()
    {
        if (_capturing || _capture is not null || _longCapture is not null) return;
        if (!Platform.HasScreenPermission)
        {
            RequestPermission();
            if (!Platform.HasScreenPermission)
                throw new InvalidOperationException("请在系统设置中允许 Tbox 屏幕录制，再退出并重新打开 Tbox。");
        }
        _capturing = true;
        var restore = Main.IsVisible;
        Main.Hide();
        try
        {
            await Task.Delay(150, _stop.Token);
            var displays = await Platform.CaptureAsync(_stop.Token);
            if (IsQuitting) return;
            _capture = new CaptureSession(this, displays, cancelled =>
            {
                _capture = null;
                if (restore && cancelled && !IsQuitting) ShowMain();
            });
            _capture.Show();
        }
        catch { if (restore) ShowMain(); throw; }
        finally { _capturing = false; }
    }

    public async Task CompleteCaptureAsync(Picture picture, DesktopPlacement placement, CaptureAction action,
        IReadOnlyList<AnnotationCommand>? annotations = null)
    {
        using (picture)
        {
            await CacheAsync(picture);
            if (action == CaptureAction.Pin) Pin(new Picture(picture.Png) { CachePath = picture.CachePath }, placement, annotations);
            if (action == CaptureAction.Copy) await CopyAsync(picture);
            if (action == CaptureAction.Save && !await SaveAsync(picture, Main)) return;
            if (action == CaptureAction.Ocr) ShowOcr(picture, false);
            CaptureHistory.Remember(placement);
        }
    }

    public async Task PinClipboardAsync()
    {
        using var picture = await ReadClipboardAsync(allowText: true);
        await PinPictureAsync(picture);
    }

    public async Task PinPictureAsync(Picture picture)
    {
        await CacheAsync(picture);
        Pin(new Picture(picture.Png) { CachePath = picture.CachePath });
    }

    internal async Task StartLongCaptureAsync(DesktopPlacement region)
    {
        _capturing = true;
        try
        {
            await Task.Delay(150, _stop.Token);
            if (IsQuitting) return;
            _longCapture?.Stop();
            var session = new LongCaptureSession(this, region, () => _longCapture = null);
            _longCapture = session;
            try { session.Start(); }
            catch { session.Stop(); throw; }
        }
        finally { _capturing = false; }
    }

    public void ShowImageResult(Picture picture, bool fullScreen) => new ImageResultWindow(this, picture, fullScreen).Show();

    private void Pin(Picture picture, DesktopPlacement? placement = null, IReadOnlyList<AnnotationCommand>? annotations = null)
    {
        var pin = new PinnedWindow(this, picture);
        foreach (var command in annotations ?? []) pin.Editor.Document.Commit(command);
        _pins.Add(pin);
        pin.Closed += (_, _) => { _pins.Remove(pin); PruneCache(); };
        _pinsHidden = false;
        pin.Show();
        var handle = pin.TryGetPlatformHandle()!.Handle;
        if (placement is not null) Platform.PlaceWindow(handle, placement, false);
        else Platform.MoveToPointer(handle);
        pin.Activate();
    }

    public void TogglePins()
    {
        if (_pins.Count == 0) { Main.SetStatus("还没有贴图。先截图或复制图片、文字后创建贴图。"); return; }
        _pinsHidden = !_pinsHidden;
        foreach (var pin in _pins.ToArray())
        {
            if (_pinsHidden) pin.Hide(); else pin.Show();
        }
    }

    public async Task OcrClipboardAsync()
    {
        using var picture = await ReadClipboardAsync(allowText: false);
        ShowOcr(picture, false);
    }

    public async Task OpenImageForOcrAsync()
    {
        var paths = await Main.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择要识别的图片", AllowMultiple = false, FileTypeFilter = [FilePickerFileTypes.ImageAll]
        });
        if (paths.Count == 0) return;
        await using var input = await paths[0].OpenReadAsync();
        using var buffer = new MemoryStream();
        await input.CopyToAsync(buffer, _stop.Token);
        using var picture = new Picture(buffer.ToArray());
        ShowOcr(picture, false);
    }

    public void ShowOcr(Picture picture, bool topmost)
    {
        if (_ocr is null)
        {
            _ocr = new OcrWindow(this);
            _ocr.Closed += (_, _) => _ocr = null;
        }
        _ocr.Topmost = topmost;
        _ocr.Show(); _ocr.Activate();
        _ocr.Recognize(new Picture(picture.Png));
    }

    public async Task CopyAsync(Picture picture)
    {
        await (Main.Clipboard ?? throw new InvalidOperationException("系统剪贴板不可用。")).SetBitmapAsync(picture.Bitmap);
        Main.SetStatus("图片已复制。");
    }

    public async Task<bool> SaveAsync(Picture picture, Window owner)
    {
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "保存图片", SuggestedFileName = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".png",
            DefaultExtension = "png", FileTypeChoices = [FilePickerFileTypes.ImagePng], ShowOverwritePrompt = true
        });
        if (file is null) return false;
        await using var output = await file.OpenWriteAsync();
        output.SetLength(0);
        await output.WriteAsync(picture.Png, _stop.Token);
        Main.SetStatus("图片已保存。");
        return true;
    }

    public async Task<Picture> ReadClipboardAsync(bool allowText)
    {
        var clipboard = Main.Clipboard ?? throw new InvalidOperationException("系统剪贴板不可用。");
        Bitmap? candidate = null;
        try { candidate = await clipboard.TryGetBitmapAsync(); }
        catch (Exception error) when (error is ArgumentException or IOException or NotSupportedException or InvalidOperationException) { }
        using var bitmap = candidate;
        if (bitmap is not null)
        {
            using var output = new MemoryStream(); bitmap.Save(output);
            return new Picture(output.ToArray());
        }
        var files = await clipboard.TryGetFilesAsync();
        foreach (var file in files?.OfType<IStorageFile>() ?? [])
        {
            try
            {
                await using var stream = await file.OpenReadAsync();
                using var image = new Bitmap(stream);
                using var output = new MemoryStream(); image.Save(output);
                return new Picture(output.ToArray());
            }
            catch (Exception e) when (e is ArgumentException or IOException or NotSupportedException) { }
        }
        if (allowText && ClipboardPinContentPolicy.NormalizeText(await clipboard.TryGetTextAsync()) is { } text)
            return RenderText(text);
        throw new InvalidOperationException(allowText ? "剪贴板中没有可用图片、图片文件或文字。" : "剪贴板中没有可用图片。请先复制图片。");
    }

    public static Picture RenderText(string text)
    {
        if (text.Length > 50_000) throw new InvalidOperationException("文字较长，请分段创建贴图（每段最多 50000 字）。");
        var control = new Border
        {
            Background = Brushes.White, Padding = new Thickness(24), Width = 640,
            Child = new TextBlock { Text = text, FontSize = 18, Foreground = Brushes.Black, TextWrapping = TextWrapping.Wrap }
        };
        control.Measure(new Size(640, double.PositiveInfinity));
        if (control.DesiredSize.Height > 12000) throw new InvalidOperationException("文字贴图过高，请分段创建。");
        control.Arrange(new Rect(control.DesiredSize));
        using var image = new RenderTargetBitmap(new PixelSize(1280, (int)Math.Ceiling(control.DesiredSize.Height * 2)), new Vector(192, 192));
        image.Render(control);
        using var output = new MemoryStream(); image.Save(output);
        return new Picture(output.ToArray());
    }

    private async Task CacheAsync(Picture picture)
    {
        try { picture.CachePath = await _cache.SaveAsync(picture.Png, _stop.Token); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _cache = new CaptureCache(CaptureCachePathPolicy.GetFallbackPath(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)));
            picture.CachePath = await _cache.SaveAsync(picture.Png, _stop.Token);
            Main.SetStatus("原缓存目录不可写，本次运行使用应用数据目录。");
        }
        // Protect the image about to become a pin as well as all currently visible/hidden pins.
        _cache.Prune(_pins.Select(pin => pin.Picture.CachePath ?? "").Append(picture.CachePath));
        Main.Refresh();
    }

    private static CaptureCache CreateCache(string? path) => new(CaptureCachePathPolicy.Resolve(path,
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)));

    private void PruneCache()
    {
        try { _cache.Prune(_pins.Select(pin => pin.Picture.CachePath ?? "")); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Main.SetStatus("缓存清理暂时失败，下次启动会重试。"); }
    }

    public async Task ChangeThemeAsync(int index)
    {
        ApplyTheme(index);
        _settings = _settings with { AppTheme = index switch { 1 => "light", 2 => "dark", _ => "system" } };
        await _settingsStore.SaveAsync(_settings, _stop.Token);
    }

    private static void ApplyTheme(int index) => Application.Current!.RequestedThemeVariant =
        index switch { 1 => ThemeVariant.Light, 2 => ThemeVariant.Dark, _ => ThemeVariant.Default };

    public void OpenCache()
    {
        Directory.CreateDirectory(CacheDirectory);
        Process.Start(new ProcessStartInfo(CacheDirectory) { UseShellExecute = true });
    }

    public async Task ChooseCacheAsync()
    {
        var folders = await Main.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "选择截图缓存目录", AllowMultiple = false });
        if (folders.Count == 0 || folders[0].TryGetLocalPath() is not { } path) return;
        _cache = CreateCache(path);
        _settings = _settings with { ScreenCaptureCacheDirectory = path };
        await _settingsStore.SaveAsync(_settings, _stop.Token);
        Main.Refresh();
    }

    public void Quit() { IsQuitting = true; _lifetime.Shutdown(); }
    public void Dispose()
    {
        IsQuitting = true; _stop.Cancel();
        _capture?.Cancel(); _longCapture?.Stop(); _ocr?.Close();
        foreach (var pin in _pins.ToArray()) pin.Close();
        Platform.Dispose();
    }
}
