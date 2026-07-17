using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Diagnostics;
using Tbox.Core;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Tbox.App;

public sealed partial class MainWindow : Window
{
    private readonly JsonSettingsStore _settingsStore;
    private AppSettings _settings = new();
    private readonly WindowMessageRouter _messageRouter;
    private FolderJumpModule? _folderJump;
    private ScreenPasteModule? _screenPaste;
    private TrayIconService? _trayIcon;
    private string _cacheDirectory = string.Empty;
    private bool _initializing = true;
    private bool _isExiting;

    public MainWindow()
    {
        try
        {
            InitializeComponent();
        }
        catch (Exception exception)
        {
            var logDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Tbox");
            Directory.CreateDirectory(logDirectory);
            File.WriteAllText(Path.Combine(logDirectory, "startup-error.log"), exception.ToString());
            throw;
        }
        VersionText.Text = $"版本 {AppVersion.Current}";
        Title = "Tbox";
        var settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Tbox",
            "settings.json");
        _settingsStore = new JsonSettingsStore(settingsPath);

        var hwnd = WindowNative.GetWindowHandle(this);
        _messageRouter = new WindowMessageRouter();
        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        appWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Tbox.ico"));
        appWindow.Resize(new Windows.Graphics.SizeInt32(1040, 720));
        appWindow.Closing += (_, args) =>
        {
            if (_isExiting)
            {
                return;
            }

            args.Cancel = true;
            appWindow.Hide();
        };
        _trayIcon = new TrayIconService(hwnd, _messageRouter,
            () => DispatcherQueue.TryEnqueue(() =>
            {
                appWindow.Show();
                Activate();
            }),
            () => DispatcherQueue.TryEnqueue(() =>
            {
                _isExiting = true;
                _folderJump?.Dispose();
                _screenPaste?.Dispose();
                _trayIcon?.Dispose();
                _messageRouter.Dispose();
                Close();
            }));
    }

    public async Task InitializeAsync()
    {
        _settings = await _settingsStore.LoadAsync();
        _cacheDirectory = CaptureCachePathPolicy.Resolve(
            _settings.ScreenCaptureCacheDirectory,
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        var hwnd = WindowNative.GetWindowHandle(this);
        _folderJump = new FolderJumpModule(
            _messageRouter.Handle,
            _messageRouter,
            message => ShowFailure("文件夹跳转", message));
        await _folderJump.SetEnabledAsync(_settings.FolderJumpEnabled);
        _screenPaste = new ScreenPasteModule(
            _messageRouter.Handle,
            _messageRouter,
            DispatcherQueue,
            _cacheDirectory,
            message => ShowFailure("截图与贴图", message));
        await _screenPaste.SetEnabledAsync(_settings.ScreenPasteEnabled);
        EnabledToggle.IsOn = _folderJump.IsEnabled;
        ScreenPasteToggle.IsOn = _screenPaste.IsEnabled;
        StartupToggle.IsOn = await StartupManager.IsEnabledAsync();
        HotkeyButton.Content = _settings.FolderJumpHotkey.DisplayText;
        CacheDirectoryTextBox.Text = _cacheDirectory;
        UpdateStatus();
        RootNavigation.SelectedItem = RootNavigation.MenuItems[0];
        _initializing = false;
    }

    internal void ShowSettings()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        AppWindow.GetFromWindowId(windowId).Show();
        Activate();
    }

    private void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var tag = (args.SelectedItemContainer as NavigationViewItem)?.Tag?.ToString();
        OverviewPanel.Visibility = tag == "overview" ? Visibility.Visible : Visibility.Collapsed;
        FolderJumpPanel.Visibility = tag == "folder-jump" ? Visibility.Visible : Visibility.Collapsed;
        ScreenPastePanel.Visibility = tag == "screen-paste" ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnScreenPasteToggled(object sender, RoutedEventArgs e)
    {
        if (_initializing || _screenPaste is null) return;
        try
        {
            await _screenPaste.SetEnabledAsync(ScreenPasteToggle.IsOn);
            SetToggleSilently(ScreenPasteToggle, _screenPaste.IsEnabled);
            _settings = _settings with { ScreenPasteEnabled = _screenPaste.IsEnabled };
            await _settingsStore.SaveAsync(_settings);
            UpdateStatus();
        }
        catch (Exception exception)
        {
            AppLog.Write($"Change screen-paste setting failed: {exception}");
            SetToggleSilently(ScreenPasteToggle, _screenPaste.IsEnabled);
            ShowFailure("截图与贴图", "无法保存截图与贴图设置。");
        }
    }

    private async void OnEnabledToggled(object sender, RoutedEventArgs e)
    {
        if (_initializing || _folderJump is null) return;
        try
        {
            await _folderJump.SetEnabledAsync(EnabledToggle.IsOn);
            SetToggleSilently(EnabledToggle, _folderJump.IsEnabled);
            _settings = _settings with { FolderJumpEnabled = _folderJump.IsEnabled };
            await _settingsStore.SaveAsync(_settings);
            UpdateStatus();
        }
        catch (Exception exception)
        {
            AppLog.Write($"Change folder-jump setting failed: {exception}");
            SetToggleSilently(EnabledToggle, _folderJump.IsEnabled);
            ShowFailure("文件夹跳转", "无法保存文件夹跳转设置。");
        }
    }

    private async void OnChangeCacheFolderClicked(object sender, RoutedEventArgs e)
    {
        if (_screenPaste is null) return;
        try
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;

            Directory.CreateDirectory(folder.Path);
            _screenPaste.SetCacheDirectory(folder.Path);
            _cacheDirectory = _screenPaste.CacheDirectory;
            CacheDirectoryTextBox.Text = _cacheDirectory;
            _settings = _settings with { ScreenCaptureCacheDirectory = _cacheDirectory };
            await _settingsStore.SaveAsync(_settings);
            ShowScreenPasteInfo("缓存文件夹已更新；之后的截图会保存到新位置。");
        }
        catch (Exception exception)
        {
            AppLog.Write($"Change capture cache directory failed: {exception}");
            ShowFailure("截图与贴图", "无法使用所选缓存文件夹。");
        }
    }

    private void OnOpenCacheFolderClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_cacheDirectory);
            Process.Start(new ProcessStartInfo(_cacheDirectory) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            AppLog.Write($"Open capture cache directory failed: {exception}");
            ShowFailure("截图与贴图", "无法打开截图缓存文件夹。");
        }
    }

    private async void OnStartupToggled(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        try
        {
            var requested = StartupToggle.IsOn;
            var enabled = await StartupManager.SetEnabledAsync(requested);
            SetToggleSilently(StartupToggle, enabled);
            _settings = _settings with { StartWithWindows = enabled };
            await _settingsStore.SaveAsync(_settings);
            if (requested && !enabled)
            {
                ShowFailure("开机启动", "系统或用户设置阻止了开机启动，请在任务管理器中允许 Tbox。");
            }
        }
        catch (Exception exception)
        {
            AppLog.Write($"Change startup setting failed: {exception}");
            try { SetToggleSilently(StartupToggle, await StartupManager.IsEnabledAsync()); }
            catch { SetToggleSilently(StartupToggle, false); }
            ShowFailure("开机启动", "无法修改开机启动设置。");
        }
    }

    private void UpdateStatus()
    {
        OverviewStatus.Text = _folderJump?.IsEnabled == true ? "已启用" : "已关闭";
        ScreenPasteOverviewStatus.Text = _screenPaste?.IsEnabled == true ? "已启用" : "已关闭";
    }

    private void SetToggleSilently(ToggleSwitch toggle, bool value)
    {
        var wasInitializing = _initializing;
        _initializing = true;
        toggle.IsOn = value;
        _initializing = wasInitializing;
    }

    private void ShowFailure(string title, string message)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _trayIcon?.ShowWarning(title, message);
            var bar = title == "截图与贴图" ? ScreenPasteStatusBar : StatusBar;
            bar.Severity = InfoBarSeverity.Warning;
            bar.Title = title;
            bar.Message = message;
            bar.IsOpen = true;
        });
    }

    private void ShowScreenPasteInfo(string message)
    {
        ScreenPasteStatusBar.Severity = InfoBarSeverity.Success;
        ScreenPasteStatusBar.Title = "截图与贴图";
        ScreenPasteStatusBar.Message = message;
        ScreenPasteStatusBar.IsOpen = true;
    }
}
