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
    private readonly IFolderNavigation _folders;
    private readonly IStartupRegistration _startup;
    private readonly HotkeyConfiguration _hotkeys;
    private readonly SemaphoreSlim _moduleChanges = new(1);
    private readonly List<PinnedWindow> _pins = [];
    private CaptureSession? _capture;
    private LongCaptureSession? _longCapture;
    private RecordingControlWindow? _recording;
    private readonly List<RecordingReviewWindow> _reviews = [];
    private bool _quitRequested;
    private OcrWindow? _ocr;
    private AppSettings _settings = new();
    private CaptureCache _cache;
    private bool _capturing;
    private bool _pinsHidden;
    private bool _screenPermissionRequested;
    public IPlatformServices Platform { get; }
    public CaptureHistory CaptureHistory { get; } = new();
    public MainWindow Main { get; set; } = null!;
    public bool IsQuitting { get; private set; }
    public string CacheDirectory => _cache.DirectoryPath;
    public AppSettings Settings => _settings;
    public string FolderDirectory => _folders.CurrentDirectory;
    public bool HasFolderPermission => _folders.HasPermission;
    public bool ScreenPermissionRequested => _screenPermissionRequested;
    public StartupState Startup { get; private set; } = new(false);
    public bool HasHotkeyConflict => _hotkeys.ActiveMask != ((_settings.ScreenPasteEnabled ? 7 : 0) | (_settings.FolderJumpEnabled ? 8 : 0));
    public event Action<int>? ThemeLoaded;

    public DesktopController(IPlatformServices platform, IClassicDesktopStyleApplicationLifetime lifetime, string? settingsPath = null)
    {
        Platform = platform; _lifetime = lifetime;
        _folders = platform.CreateFolderNavigation();
        _startup = platform.CreateStartupRegistration();
        _hotkeys = new HotkeyConfiguration(mask => platform.RegisterHotkeys(OnHotkey, mask));
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _settingsStore = new JsonSettingsStore(settingsPath ?? Path.Combine(local, "Tbox", "desktop-settings.json"));
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
            var hotkeys = _hotkeys.TryChange(_settings.ScreenPasteEnabled, _settings.FolderJumpEnabled);
            if (_settings.FolderJumpEnabled) _folders.Start();
            Main.SetStatus(hotkeys ? "已就绪" : "快捷键被占用；可从窗口或菜单栏使用所有功能。");
            Main.Refresh();
            await RefreshStartupAsync();
            var location = await Task.Run(() => CacheLocation.Prepare(_cache.DirectoryPath,
                CaptureCachePathPolicy.GetFallbackPath(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))));
            _cache = new CaptureCache(location.Path);
            if (location.IsFallback) Main.SetStatus("首选缓存目录不可写，本次运行使用本地应用数据目录；原设置和已有文件保持不变。");
            Main.Refresh();
            PruneCache();
            RecordingWorkspace.CleanupExpired();
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
        catch (Exception exception)
        {
            Tbox.App.AppLog.Write(exception.ToString()); Main.SetStatus(exception.Message);
            if (_settings.ShowFailureNotifications && !IsQuitting) FailureNotificationWindow.ShowFailure(this, exception.Message);
        }
    }

    private void OnHotkey(int id) => Dispatcher.UIThread.Post(() =>
    {
        if (id == 1) Run(CaptureAsync);
        if (id == 2) Run(PinClipboardAsync);
        if (id == 3) TogglePins();
        if (id == 4) Run(JumpFolderAsync);
    });

    public void RequestFolderPermission() { _folders.RequestPermission(); Main.Refresh(); }
    public async Task JumpFolderAsync()
    {
        if (!_settings.FolderJumpEnabled) return;
        var result = await _folders.JumpAsync(_stop.Token);
        Main.SetStatus(result.Success ? "文件对话框已跳转。" : result.Message ?? "文件夹跳转未完成。");
        Main.Refresh();
    }

    public async Task SetModulesAsync(bool? requestedCapture, bool? requestedFolders)
    {
        await _moduleChanges.WaitAsync(_stop.Token);
        try
        {
            var capture = requestedCapture ?? _settings.ScreenPasteEnabled;
            var folders = requestedFolders ?? _settings.FolderJumpEnabled;
            if (!capture && _settings.ScreenPasteEnabled)
            {
                foreach (var review in _reviews.ToArray()) if (!await review.RequestCloseAsync()) return;
                if (_recording is { } recording) await recording.CancelAsync();
            }
            if (!_hotkeys.TryChange(capture, folders))
            { Main.SetStatus("快捷键被其他应用占用，模块设置未更改。"); return; }
            var previous = _settings;
            try
            {
                if (folders) _folders.Start(); else _folders.Stop();
                var updated = _settings with { ScreenPasteEnabled = capture, FolderJumpEnabled = folders };
                await _settingsStore.SaveAsync(updated, _stop.Token); _settings = updated;
            }
            catch
            {
                _hotkeys.TryChange(previous.ScreenPasteEnabled, previous.FolderJumpEnabled);
                if (previous.FolderJumpEnabled) _folders.Start(); else _folders.Stop();
                throw;
            }
            if (!capture)
            {
                _capture?.Cancel(); _longCapture?.Stop(); _ocr?.Close();
                foreach (var pin in _pins.ToArray()) pin.Close();
            }
            Main.SetStatus("模块设置已保存。");
        }
        finally { _moduleChanges.Release(); Main.Refresh(); }
    }

    public void ShowMain() { Main.Show(); Main.Activate(); Main.Refresh(); }
    public void RequestPermission()
    {
        if (Platform.HasScreenPermission) { Main.Refresh(); return; }
        // This is the only permission-requesting UI action. Repeated clicks open settings;
        // capture hotkeys, activation, diagnostics and retries must never request access.
        if (_screenPermissionRequested) Platform.OpenScreenPermissionSettings();
        else
        {
            _screenPermissionRequested = true;
            Platform.RequestScreenPermission();
        }
        Main.SetStatus("在系统设置中允许 Tbox；若已开启但仍不可截图，请退出并重新打开应用。");
        Main.Refresh();
    }

    public async Task CaptureAsync()
    {
        if (!_settings.ScreenPasteEnabled || _capturing || _capture is not null || _longCapture is not null || _recording is not null || _quitRequested) return;
        if (!Platform.HasScreenPermission)
        {
            ShowMain();
            Main.SetStatus("截图尚未获授权。请点击“允许屏幕录制”或“打开屏幕录制设置”；贴图和已有图片识别仍可使用。");
            return;
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
        if (!_settings.ScreenPasteEnabled) return;
        using var picture = await ReadClipboardAsync(allowText: true);
        await PinPictureAsync(picture);
    }

    public async Task PinPictureAsync(Picture picture)
    {
        if (!_settings.ScreenPasteEnabled) return;
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

    internal async Task StartRecordingAsync(RecordingArea area)
    {
        if (_recording is not null || _quitRequested) return;
        _capturing = true; Main.Hide();
        try
        {
            await Task.Delay(150, _stop.Token);
            if (IsQuitting) return;
            var session = new RecordingControlWindow(this, area);
            _recording = session;
            session.Released += () => { if (ReferenceEquals(_recording, session)) _recording = null; };
            session.Show(); CaptureHistory.Remember(area.Bounds);
        }
        finally { _capturing = false; }
    }

    internal void ShowRecordingReview(RecordingWorkspace workspace, RecordingArea area, RecordingOutput output, TimeSpan duration)
    {
        var review = new RecordingReviewWindow(this, workspace, area, output, duration);
        _reviews.Add(review); review.Released += () => _reviews.Remove(review); review.Show();
    }

    private void Pin(Picture picture, DesktopPlacement? placement = null, IReadOnlyList<AnnotationCommand>? annotations = null)
    {
        if (!_settings.ScreenPasteEnabled || IsQuitting) { picture.Dispose(); return; }
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
        if (!_settings.ScreenPasteEnabled) return;
        using var picture = await ReadClipboardAsync(allowText: false);
        ShowOcr(picture, false);
    }

    public async Task OpenImageForOcrAsync()
    {
        if (!_settings.ScreenPasteEnabled) return;
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
        if (!_settings.ScreenPasteEnabled || IsQuitting) return;
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
        var sources = new List<Func<Task<Picture?>>>
        {
            async () =>
            {
                using var bitmap = await clipboard.TryGetBitmapAsync();
                if (bitmap is null) return null;
                using var output = new MemoryStream(); bitmap.Save(output);
                return new Picture(output.ToArray());
            },
            async () =>
            {
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
                    catch (Exception error) when (ClipboardReader.IsUnavailableFormat(error)) { }
                }
                return null;
            }
        };
        if (allowText) sources.Add(async () => ClipboardPinContentPolicy.NormalizeText(await clipboard.TryGetTextAsync()) is { } text ? RenderText(text) : null);
        if (await ClipboardReader.ReadAsync(sources, _stop.Token) is { } result) return result;
        throw new InvalidOperationException(allowText ? "剪贴板中没有可用图片、图片文件或文字。" : "剪贴板中没有可用图片。请先复制图片。");
    }

    public static Picture RenderText(string text)
    {
        text = ClipboardPinContentPolicy.NormalizeText(text) ?? throw new ArgumentException("不能创建空白文字贴图。", nameof(text));
        if (text.Length > 50_000)
        {
            var end = char.IsHighSurrogate(text[49_999]) ? 49_999 : 50_000;
            text = text[..end] + "…";
        }
        var control = new Border
        {
            Background = Brushes.White, Padding = new Thickness(24), Width = 640,
            Child = new TextBlock { Text = text, FontSize = 18, Foreground = Brushes.Black, TextWrapping = TextWrapping.Wrap,
                MaxHeight = 4048, TextTrimming = TextTrimming.CharacterEllipsis }
        };
        control.Measure(new Size(640, double.PositiveInfinity));
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
        await UpdateSettingsAsync(settings => settings with { AppTheme = index switch { 1 => "light", 2 => "dark", _ => "system" } });
        ApplyTheme(index);
    }

    private async Task UpdateSettingsAsync(Func<AppSettings, AppSettings> update)
    {
        await _moduleChanges.WaitAsync(_stop.Token);
        try
        {
            var updated = update(_settings);
            await _settingsStore.SaveAsync(updated, _stop.Token);
            _settings = updated;
        }
        finally { _moduleChanges.Release(); Main.Refresh(); }
    }

    public async Task SetFailureNotificationsAsync(bool enabled)
    {
        await UpdateSettingsAsync(settings => settings with { ShowFailureNotifications = enabled });
        if (!enabled) FailureNotificationWindow.Dismiss();
    }

    public async Task RefreshStartupAsync()
    {
        await _moduleChanges.WaitAsync(_stop.Token);
        try { Startup = await _startup.ReadAsync(); }
        finally { _moduleChanges.Release(); Main.Refresh(); }
    }

    public async Task SetStartupAsync(bool enabled)
    {
        await _moduleChanges.WaitAsync(_stop.Token);
        try
        {
            Startup = await _startup.SetEnabledAsync(enabled);
            var updated = _settings with { StartWithWindows = Startup.Enabled };
            await _settingsStore.SaveAsync(updated, _stop.Token); _settings = updated;
            Main.SetStatus(Startup.Message ?? (Startup.Enabled ? "已开启登录时启动。" : "已关闭登录时启动。"));
        }
        catch
        {
            try { Startup = await _startup.ReadAsync(); } catch { }
            throw;
        }
        finally { _moduleChanges.Release(); Main.Refresh(); }
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
        var cache = CreateCache(path);
        await Task.Run(() => CacheLocation.VerifyWritable(cache.DirectoryPath));
        await UpdateSettingsAsync(settings => settings with { ScreenCaptureCacheDirectory = path });
        _cache = cache;
        Main.Refresh();
    }

    public void Quit() => Run(QuitAsync);
    public async Task QuitAsync()
    {
        if (_quitRequested) return;
        _quitRequested = true;
        try
        {
            foreach (var review in _reviews.ToArray()) if (!await review.RequestCloseAsync()) return;
            if (_recording is { } recording) await recording.CancelAsync();
            IsQuitting = true; _lifetime.Shutdown();
        }
        finally { _quitRequested = false; }
    }
    public void Dispose()
    {
        IsQuitting = true; _stop.Cancel();
        _capture?.Cancel(); _longCapture?.Stop(); _ocr?.Close();
        foreach (var pin in _pins.ToArray()) pin.Close();
        _folders.Dispose();
        Platform.Dispose();
    }
}
