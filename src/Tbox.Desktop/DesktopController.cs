using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
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
    private readonly string _settingsPath;
    private readonly string? _legacySettingsPath;
    private readonly CancellationTokenSource _stop = new();
    private readonly IFolderNavigation _folders;
    private readonly FolderJumpBarSession _folderBar;
    private readonly IStartupRegistration _startup;
    private readonly HotkeyConfiguration _hotkeys;
    private bool _recordingShortcut;
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
    private bool _cachePickerOpen;
    private bool _pinsHidden;
    private bool _screenPermissionRequested;
    private bool _capturePermissionNeeded;
    public IPlatformServices Platform { get; }
    public CaptureHistory CaptureHistory { get; } = new();
    // The one main window. It stays hidden until opened from the tray, a second launch or app reopen.
    public MainWindow Main { get; set; } = null!;
    public bool IsQuitting { get; private set; }
    internal bool HasOpenWorkForUpdate => _capturing || _capture is not null || _longCapture is not null || _recording is not null ||
        _pins.Count != 0 || _reviews.Count != 0 || _lifetime.Windows.Any(window =>
            window != Main && window is not (FailureNotificationWindow or FolderJumpBarWindow));
    public string CacheDirectory => _cache.DirectoryPath;
    public AppSettings Settings => _settings;
    public string FolderDirectory => _folders.CurrentDirectory;
    public bool HasFolderPermission => _folders.HasPermission;
    public bool ScreenPermissionRequested => _screenPermissionRequested;
    // Set only after a capture attempt found no permission, so startup never shows an unrequested prompt.
    public bool CapturePermissionNeeded => _capturePermissionNeeded && !Platform.HasScreenPermission;
    public int PinCount => _pins.Count;
    public bool PinsHidden => _pinsHidden;
    public StartupState Startup { get; private set; } = new(false);
    public bool HasHotkeyConflict => HasConflictIn(capture: true) || HasConflictIn(capture: false);
    // Capture, pin and toggle belong to the capture page; folder jump to its own page.
    public bool HasConflictIn(bool capture) => _hotkeys.Conflicts.Any(action => (action != HotkeyAction.Folder) == capture);
    public bool HasHotkeyConflictFor(HotkeyAction action) => _hotkeys.Conflicts.Contains(action);
    public Shortcut ShortcutFor(HotkeyAction action) => ShortcutPolicy.Effective(_settings.Hotkeys, action, OperatingSystem.IsMacOS());
    public bool IsDefaultShortcut(HotkeyAction action) => ShortcutFor(action) == ShortcutPolicy.Default(action, OperatingSystem.IsMacOS());
    // Raised after a shortcut changes so labels (tray menu, buttons, hints) can follow.
    public event Action? ShortcutsChanged;

    public DesktopController(IPlatformServices platform, IClassicDesktopStyleApplicationLifetime lifetime, string? settingsPath = null)
    {
        Platform = platform; _lifetime = lifetime;
        _folders = platform.CreateFolderNavigation();
        _folderBar = new(this, _folders, _stop.Token);
        _startup = platform.CreateStartupRegistration();
        _hotkeys = new HotkeyConfiguration(binding => platform.RegisterHotkey(binding, OnHotkey), platform.UnregisterHotkey);
        // Tooltips and labels across the windows read the shortcuts from this controller's settings.
        ShortcutLabels.Bindings = () => _settings.Hotkeys;
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _settingsPath = settingsPath ?? Path.Combine(local, "Tbox", "desktop-settings.json");
        _legacySettingsPath = settingsPath is null && OperatingSystem.IsWindows() ? Path.Combine(local, "Tbox", "settings.json") : null;
        _settingsStore = new JsonSettingsStore(_settingsPath);
        _cache = CreateCache(null);
    }

    public async Task InitializeAsync(string[] args)
    {
        var migrationNotice = _legacySettingsPath is null || args.Contains("--smoke-test") ? null
            : await SettingsMigration.ImportIfMissingAsync(_settingsPath, _legacySettingsPath, _stop.Token);
        _settings = await _settingsStore.LoadAsync(_stop.Token);
        _cache = CreateCache(_settings.ScreenCaptureCacheDirectory);
        if (!args.Contains("--smoke-test"))
        {
            ApplyHotkeys(_settings);
            ShortcutsChanged?.Invoke();
            if (_settings.FolderJumpEnabled) _folders.Start();
            _folderBar.Sync();
            if (HasHotkeyConflict) Main.SetStatus("部分快捷键被其他应用占用，可在主窗口修改或直接启动工具。");
            Main.Refresh();
            await RefreshStartupAsync();
            var location = await Task.Run(() => CacheLocation.Prepare(_cache.DirectoryPath,
                CaptureCachePathPolicy.GetFallbackPath(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))));
            _cache = new CaptureCache(location.Path);
            if (location.IsFallback) Main.SetStatus("首选缓存目录不可写，本次运行使用本地应用数据目录；原设置和已有文件保持不变。");
            Main.Refresh();
            PruneCache();
            RecordingWorkspace.CleanupExpired();
            if (migrationNotice is not null) Main.SetStatus(migrationNotice);
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
            var message = string.IsNullOrWhiteSpace(exception.Message) ? "操作遇到异常，请重试。" : exception.Message;
            AppLog.Write(exception.ToString()); Main.SetStatus(message);
            if (_settings.ShowFailureNotifications && !IsQuitting) FailureNotificationWindow.ShowFailure(this, message);
        }
    }

    private void OnHotkey(HotkeyAction action) => Dispatcher.UIThread.Post(() =>
    {
        if (_recordingShortcut) return;
        if (action == HotkeyAction.Capture) Run(CaptureAsync);
        if (action == HotkeyAction.Pin) Run(PinClipboardAsync);
        if (action == HotkeyAction.Toggle) TogglePins();
        if (action == HotkeyAction.Folder) Run(JumpFolderAsync);
    }, DispatcherPriority.Send);

    // Every shortcut whose module is on, at its saved or default combination.
    private void ApplyHotkeys(AppSettings settings)
    {
        if (_recordingShortcut) return;
        _hotkeys.Apply(ShortcutPolicy.Actions
            .Where(action => action == HotkeyAction.Folder ? settings.FolderJumpEnabled : settings.ScreenPasteEnabled)
            .Select(action => new HotkeyBinding(action, ShortcutPolicy.Effective(settings.Hotkeys, action, OperatingSystem.IsMacOS()))));
    }

    // Let the recorder receive combinations that Tbox would otherwise handle globally.
    internal bool TryPauseHotkeys()
    {
        if (_recordingShortcut) return false;
        _recordingShortcut = true;
        _hotkeys.Apply([]);
        return true;
    }

    internal void ResumeHotkeys()
    {
        _recordingShortcut = false;
        if (IsQuitting) return;
        ApplyHotkeys(_settings);
        Main.Refresh();
    }

    // Saves a new combination (null = platform default). Returns the reason when it cannot be used; the old shortcut stays.
    public async Task<string?> SetShortcutAsync(HotkeyAction action, Shortcut? shortcut)
    {
        var mac = OperatingSystem.IsMacOS();
        var target = shortcut ?? ShortcutPolicy.Default(action, mac);
        if (ShortcutPolicy.Validate(target, mac) is { } invalid) return invalid;
        await _moduleChanges.WaitAsync(_stop.Token);
        try
        {
            foreach (var other in ShortcutPolicy.Actions)
                if (other != action && ShortcutFor(other) == target)
                    return $"已被“{ShortcutPolicy.Name(other)}”使用。";

            var previous = _settings;
            var enabled = action == HotkeyAction.Folder ? previous.FolderJumpEnabled : previous.ScreenPasteEnabled;
            if (!_hotkeys.TryChange(action, target, enabled && !_recordingShortcut ? ShortcutFor(action) : null))
                return $"{target.Display(mac)} 已被其他应用占用，请换一个组合。";
            if (!enabled) _hotkeys.Release(action);
            // Store only the overrides; a default choice clears the entry so platform defaults keep applying.
            var saved = (previous.Hotkeys ?? new HotkeyBindings()).With(action, target == ShortcutPolicy.Default(action, mac) ? null : target);
            var updated = previous with { Hotkeys = saved == new HotkeyBindings() ? null : saved };
            try { await _settingsStore.SaveAsync(updated, _stop.Token); _settings = updated; }
            catch { ApplyHotkeys(previous); throw; }
            ShortcutsChanged?.Invoke();
            return null;
        }
        finally { _moduleChanges.Release(); Main.Refresh(); }
    }

    public void RequestFolderPermission() { _folders.RequestPermission(); Main.Refresh(); }
    public async Task JumpFolderAsync()
    {
        if (!_settings.FolderJumpEnabled) return;
        var shortcut = ShortcutFor(HotkeyAction.Folder);
        if (await _folderBar.TryJumpAsync(shortcut)) return;
        var result = await _folders.JumpAsync(shortcut, _stop.Token);
        await ReportFolderJumpAsync(result, _folders.CurrentDirectory);
    }

    internal async Task ReportFolderJumpAsync(FolderNavigationResult result, string path)
    {
        var message = result.Success ? "文件对话框已跳转。" : result.Message ?? "文件夹跳转未完成。";
        Main.SetStatus(message);
        if (!result.Success && (!Main.IsVisible || _cachePickerOpen) && _settings.ShowFailureNotifications && !IsQuitting)
            FailureNotificationWindow.ShowFailure(this, message);
        if (!result.Success) { Main.Refresh(); return; }
        try
        {
            await UpdateSettingsAsync(settings => settings with { RecentFolderJumps = FolderJumpList.Remember(settings.RecentFolderJumps, path) });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            AppLog.Write("Folder jump history could not be saved: " + error.GetType().Name);
        }
    }

    public async Task SetFolderJumpBarAsync(bool enabled)
    {
        await UpdateSettingsAsync(settings => settings with { ShowFolderJumpBar = enabled });
        _folderBar.Sync();
    }

    internal Task ToggleFavoriteFolderAsync(string path) => UpdateSettingsAsync(settings => settings with
    {
        FavoriteFolders = FolderJumpList.ToggleFavorite(settings.FavoriteFolders, path)
    });

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
            // A shortcut another application holds no longer blocks the module switch; the page shows the conflict instead.
            var previous = _settings;
            try
            {
                var updated = _settings with { ScreenPasteEnabled = capture, FolderJumpEnabled = folders };
                ApplyHotkeys(updated);
                if (folders) _folders.Start(); else _folders.Stop();
                await _settingsStore.SaveAsync(updated, _stop.Token); _settings = updated;
            }
            catch
            {
                ApplyHotkeys(previous);
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
        finally { _moduleChanges.Release(); Main.Refresh(); _folderBar.Sync(); }
    }

    // Tray click, app reopen and a second launch all bring the main window to the front.
    // A hidden window reopens on 截图; one that is already open keeps its page unless a page is requested.
    public void ShowMain() => ShowMain(null);
    internal void ShowMain(MainPage? page)
    {
        if (IsQuitting) return;
        if (!Main.IsVisible) Main.Navigate(page ?? MainPage.Capture);
        else if (page is { } requested) Main.Navigate(requested);
        Main.Show();
        if (Main.WindowState == WindowState.Minimized) Main.WindowState = WindowState.Normal;
        Main.Activate(); Main.Refresh();
    }
    public void RequestPermission()
    {
        if (Platform.HasScreenPermission) { _capturePermissionNeeded = false; Main.Refresh(); return; }
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

    public Task CaptureAsync() => CaptureAsync(null);
    public Task RecordRegionAsync() => CaptureAsync(CaptureAction.Record);
    public Task LongCaptureAsync() => CaptureAsync(CaptureAction.Long);

    internal async Task CaptureAsync(CaptureAction? preset)
    {
        if (!_settings.ScreenPasteEnabled || _capturing || _capture is not null || _longCapture is not null || _recording is not null || _quitRequested) return;
        if (!Platform.HasScreenPermission)
        {
            // Explain on the capture page; only its button may request access.
            _capturePermissionNeeded = true;
            ShowMain(MainPage.Capture);
            return;
        }
        _capturing = true;
        var timer = Stopwatch.StartNew();
        var restore = (preset is CaptureAction.Long or CaptureAction.Record) && Main.IsVisible;
        if (restore) Main.Hide();
        try
        {
            // Only a window we just hid needs time to leave the compositor's next capture.
            // Ordinary hotkey/button captures preserve the main window and start immediately.
            if (restore) await Task.Delay(150, _stop.Token);
            // Snapshot external targets before overlays exist, alongside screen capture, not on the input thread.
            var targetsTask = Task.Run(Platform.CreateCaptureTargets, _stop.Token);
            var displaysTask = Platform.CaptureAsync(_stop.Token);
            await Task.WhenAll(targetsTask, displaysTask);
            var displays = await displaysTask;
            var capturedMs = timer.Elapsed.TotalMilliseconds;
            if (IsQuitting) return;
            _capture = new CaptureSession(this, displays, cancelled =>
            {
                _capture = null;
                if (restore && cancelled && !IsQuitting) ShowMain();
            }, preset, await targetsTask);
            _capture.Show();
            AppLog.Write($"Capture ready: acquisition={capturedMs:F1}ms, overlay-shown={timer.Elapsed.TotalMilliseconds:F1}ms, displays={displays.Count}");
        }
        catch { if (restore) ShowMain(); throw; }
        finally { _capturing = false; }
    }

    public async Task CompleteCaptureAsync(Picture picture, DesktopPlacement placement, CaptureAction action,
        IReadOnlyList<AnnotationCommand>? annotations = null)
    {
        using (picture)
        {
            Picture? pinned = null;
            if (action == CaptureAction.Pin) { pinned = picture.Clone(); Pin(pinned, placement, annotations); }
            if (action == CaptureAction.Copy) await CopyAsync(picture);
            if (action == CaptureAction.Ocr) ShowOcr(picture, false);
            await CacheAsync(picture, pinned);
            if (action == CaptureAction.Save && !await SaveAsync(picture, Main)) return;
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
        var pinned = picture.Clone();
        Pin(pinned);
        await CacheAsync(picture, pinned);
    }

    // Recent captures already live in the cache, so pinning one reuses that file instead of saving a copy.
    public async Task PinFileAsync(string path)
    {
        if (!_settings.ScreenPasteEnabled) return;
        var picture = await LoadCachedAsync(path);
        picture.CachePath = path;
        Pin(picture);
    }

    public async Task CopyFileAsync(string path)
    {
        using var picture = await LoadCachedAsync(path);
        await CopyAsync(picture);
        if (!Main.IsVisible) FailureNotificationWindow.ShowInfo(this, "已复制截图。");
    }

    // A recent capture opens in the image preview; the preview owns and releases the loaded picture.
    public async Task OpenCachedAsync(string path) => ShowImageResult(await LoadCachedAsync(path), false);

    private async Task<Picture> LoadCachedAsync(string path)
    {
        var full = Path.GetFullPath(path);
        if (!string.Equals(Path.GetDirectoryName(full), CacheDirectory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("只能打开截图缓存目录中的图片。");
        try { return new Picture(await File.ReadAllBytesAsync(full, _stop.Token)); }
        catch (FileNotFoundException) { Main.Refresh(); throw new InvalidOperationException("这张截图已被清理或移动。"); }
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
        if (_pins.Count == 0) { Notify("还没有贴图。先截图或复制图片、文字后创建贴图。"); return; }
        _pinsHidden = !_pinsHidden;
        foreach (var pin in _pins.ToArray())
        {
            if (_pinsHidden) pin.Hide(); else pin.Show();
        }
    }

    // Status for actions started from a hotkey or the tray menu: shown in the main window when open, otherwise briefly.
    private void Notify(string message)
    {
        Main.SetStatus(message);
        if (!Main.IsVisible && !IsQuitting) FailureNotificationWindow.ShowInfo(this, message);
    }

    // Dialogs attach to the main window when it is open, otherwise to the open OCR window.
    private Window DialogOwner => Main.IsVisible ? Main : _ocr is { IsVisible: true } ocr ? ocr : Main;

    public async Task OcrClipboardAsync()
    {
        if (!_settings.ScreenPasteEnabled) return;
        using var picture = await ReadClipboardAsync(allowText: false);
        ShowOcr(picture, false);
    }

    public async Task OpenImageForOcrAsync()
    {
        if (!_settings.ScreenPasteEnabled) return;
        var paths = await DialogOwner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
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
        _ocr.Show();
        if (_ocr.WindowState == WindowState.Minimized) _ocr.WindowState = WindowState.Normal;
        _ocr.Activate();
        _ocr.Recognize(new Picture(picture.Png));
    }

    public async Task CopyAsync(Picture picture)
    {
        var clipboard = Main.Clipboard ?? throw new InvalidOperationException("系统剪贴板不可用。");
        var transfer = new ClipboardImageTransfer(picture);
        try { await clipboard.SetDataAsync(transfer); }
        catch { transfer.Dispose(); throw; }
        // Ownership belongs to the clipboard after SetDataAsync. In particular,
        // neither this method nor Picture.Dispose may release its deferred image.
        if (OperatingSystem.IsWindows()) await clipboard.FlushAsync();
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
                return Picture.FromBitmap(bitmap);
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
                        return Picture.FromBitmap(image);
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
        return Picture.FromBitmap(image);
    }

    private async Task CacheAsync(Picture picture, Picture? pinned = null)
    {
        var png = await Task.Run(() => picture.Png, _stop.Token);
        var cache = _cache;
        try { picture.CachePath = await Task.Run(() => cache.SaveAsync(png, _stop.Token), _stop.Token); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _cache = new CaptureCache(CaptureCachePathPolicy.GetFallbackPath(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)));
            cache = _cache;
            picture.CachePath = await Task.Run(() => cache.SaveAsync(png, _stop.Token), _stop.Token);
            Main.SetStatus("原缓存目录不可写，本次运行使用应用数据目录。");
        }
        if (pinned is not null) pinned.CachePath = picture.CachePath;
        // Protect the image about to become a pin as well as all currently visible/hidden pins.
        _cache.Prune(_pins.Select(pin => pin.Picture.CachePath ?? "").Append(picture.CachePath));
        if (Main.IsVisible) Main.Refresh();
    }

    private static CaptureCache CreateCache(string? path) => new(CaptureCachePathPolicy.Resolve(path,
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)));
    internal void UseCacheDirectoryForTests(string path) => _cache = new CaptureCache(path);

    private void PruneCache()
    {
        try { _cache.Prune(_pins.Select(pin => pin.Picture.CachePath ?? "")); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Main.SetStatus("缓存清理暂时失败，下次启动会重试。"); }
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
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // A passive refresh must not interrupt a file picker or abort app initialization.
            AppLog.Write("Startup status refresh failed: " + error);
        }
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

    public void OpenCache()
    {
        Directory.CreateDirectory(CacheDirectory);
        Process.Start(new ProcessStartInfo(CacheDirectory) { UseShellExecute = true });
    }

    public async Task ChooseCacheAsync()
    {
        if (_cachePickerOpen) return;
        string? path;
        _cachePickerOpen = true;
        try { path = await Platform.PickFolderAsync(Main, "选择截图缓存目录"); }
        finally { _cachePickerOpen = false; }
        if (path is null) return;
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
        _folderBar.Dispose();
        _capture?.Cancel(); _longCapture?.Stop(); _ocr?.Close();
        foreach (var pin in _pins.ToArray()) pin.Close();
        _folders.Dispose();
        Platform.Dispose();
    }
}
