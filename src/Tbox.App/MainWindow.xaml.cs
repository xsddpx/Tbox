using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Tbox.Core;
using WinRT.Interop;

namespace Tbox.App;

public sealed partial class MainWindow : Window
{
    private readonly JsonSettingsStore _settingsStore;
    private AppSettings _settings = new();
    private readonly WindowMessageRouter _messageRouter;
    private FolderJumpModule? _folderJump;
    private TrayIconService? _trayIcon;
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
                _trayIcon?.Dispose();
                _messageRouter.Dispose();
                Close();
            }));
    }

    public async Task InitializeAsync()
    {
        _settings = await _settingsStore.LoadAsync();
        var hwnd = WindowNative.GetWindowHandle(this);
        _folderJump = new FolderJumpModule(_messageRouter.Handle, _messageRouter, ShowFailure);
        await _folderJump.SetEnabledAsync(_settings.FolderJumpEnabled);
        EnabledToggle.IsOn = _folderJump.IsEnabled;
        StartupToggle.IsOn = StartupManager.IsEnabled;
        HotkeyButton.Content = _settings.FolderJumpHotkey.DisplayText;
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
    }

    private async void OnEnabledToggled(object sender, RoutedEventArgs e)
    {
        if (_initializing || _folderJump is null) return;
        await _folderJump.SetEnabledAsync(EnabledToggle.IsOn);
        EnabledToggle.IsOn = _folderJump.IsEnabled;
        _settings = _settings with { FolderJumpEnabled = _folderJump.IsEnabled };
        await _settingsStore.SaveAsync(_settings);
        UpdateStatus();
    }

    private async void OnStartupToggled(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        try
        {
            StartupManager.SetEnabled(StartupToggle.IsOn);
            _settings = _settings with { StartWithWindows = StartupToggle.IsOn };
            await _settingsStore.SaveAsync(_settings);
        }
        catch (UnauthorizedAccessException)
        {
            StartupToggle.IsOn = StartupManager.IsEnabled;
            ShowFailure("无法修改开机启动设置。");
        }
    }

    private void UpdateStatus() => OverviewStatus.Text = _folderJump?.IsEnabled == true ? "已启用" : "已关闭";

    private void ShowFailure(string message)
    {
        _trayIcon?.ShowWarning(message);
        DispatcherQueue.TryEnqueue(() =>
        {
            StatusBar.Severity = InfoBarSeverity.Warning;
            StatusBar.Title = "文件夹跳转";
            StatusBar.Message = message;
            StatusBar.IsOpen = true;
        });
    }
}
