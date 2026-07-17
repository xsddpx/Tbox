using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Diagnostics;
using Tbox.Core;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Tbox.App;

public sealed partial class MainWindow : Window
{
    private readonly JsonSettingsStore _settingsStore;
    private readonly WindowMessageRouter _messageRouter;
    private readonly AppWindow _appWindow;
    private AppSettings _settings = new();
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

        Title = "Tbox";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        var settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Tbox",
            "settings.json");
        _settingsStore = new JsonSettingsStore(settingsPath);

        var hwnd = WindowNative.GetWindowHandle(this);
        _messageRouter = new WindowMessageRouter();
        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        _appWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Tbox.ico"));
        ResizeForCurrentDisplay(hwnd, windowId);
        ConfigureTitleBar(ElementTheme.Default);
        _appWindow.Closing += (_, args) =>
        {
            if (_isExiting) return;
            args.Cancel = true;
            _appWindow.Hide();
        };

        _trayIcon = new TrayIconService(
            hwnd,
            _messageRouter,
            () => DispatcherQueue.TryEnqueue(() =>
            {
                _appWindow.Show();
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
        ApplyTheme(_settings.AppTheme);
        SelectThemeItem(_settings.AppTheme);

        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _cacheDirectory = CaptureCachePathPolicy.Resolve(
            _settings.ScreenCaptureCacheDirectory,
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            localApplicationData);
        var cacheFallbackMessage = EnsureCaptureCacheDirectory(localApplicationData);
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

        SetFolderToggles(_folderJump.IsEnabled);
        SetScreenPasteToggles(_screenPaste.IsEnabled);
        StartupToggle.IsOn = await StartupManager.IsEnabledAsync();
        FailureNotificationsToggle.IsOn = _settings.ShowFailureNotifications;
        HotkeyButton.Content = _settings.FolderJumpHotkey.DisplayText;
        HomeFolderHotkeyButton.Content = _settings.FolderJumpHotkey.DisplayText;
        GeneralHotkeyButton.Content = _settings.FolderJumpHotkey.DisplayText;
        CacheDirectoryTextBox.Text = _cacheDirectory;
        if (cacheFallbackMessage is not null)
        {
            ShowScreenPasteInfo(cacheFallbackMessage);
        }
        VersionText.Text = $"Tbox {AppVersion.Current}";
        HomeVersionText.Text = AppVersion.Current;
        UpdateStatus();
        SelectNavigationItem("home");
        UpdateResponsiveLayout(PageHost.ActualWidth);
        _initializing = false;
    }

    private string? EnsureCaptureCacheDirectory(string localApplicationData)
    {
        try
        {
            Directory.CreateDirectory(_cacheDirectory);
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            AppLog.Write($"Create capture cache directory failed: {exception}");
            var fallback = CaptureCachePathPolicy.GetFallbackPath(localApplicationData);
            _cacheDirectory = fallback;
            try
            {
                Directory.CreateDirectory(fallback);
            }
            catch (Exception fallbackException)
            {
                AppLog.Write($"Create fallback capture cache directory failed: {fallbackException}");
            }

            return $"无法使用首选截图缓存文件夹，本次运行已改用 {_cacheDirectory}。";
        }
    }

    internal void ShowSettings()
    {
        _appWindow.Show();
        Activate();
    }

    private void ResizeForCurrentDisplay(IntPtr hwnd, WindowId windowId)
    {
        var scale = Math.Max(1d, NativeMethods.GetDpiForWindow(hwnd) / 96d);
        var displayArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary);
        var workArea = displayArea.WorkArea;
        var margin = (int)Math.Round(32 * scale);
        var desiredWidth = (int)Math.Round(1180 * scale);
        var desiredHeight = (int)Math.Round(820 * scale);
        var width = Math.Max(1, Math.Min(desiredWidth, workArea.Width - margin));
        var height = Math.Max(1, Math.Min(desiredHeight, workArea.Height - margin));
        _appWindow.Resize(new Windows.Graphics.SizeInt32(width, height));
    }

    private void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var tag = (args.SelectedItemContainer as NavigationViewItem)?.Tag?.ToString();
        ShowPage(tag);
        if (RootNavigation.DisplayMode != NavigationViewDisplayMode.Expanded)
        {
            RootNavigation.IsPaneOpen = false;
        }
    }

    private void ShowPage(string? tag)
    {
        HomePanel.Visibility = tag == "home" ? Visibility.Visible : Visibility.Collapsed;
        GeneralPanel.Visibility = tag == "general" ? Visibility.Visible : Visibility.Collapsed;
        FolderJumpPanel.Visibility = tag == "folder-jump" ? Visibility.Visible : Visibility.Collapsed;
        ScreenPastePanel.Visibility = tag == "screen-paste" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SelectNavigationItem(string tag)
    {
        var item = RootNavigation.MenuItems
            .OfType<NavigationViewItem>()
            .FirstOrDefault(candidate => string.Equals(candidate.Tag?.ToString(), tag, StringComparison.Ordinal));
        if (item is not null)
        {
            RootNavigation.SelectedItem = item;
            ShowPage(tag);
        }
    }

    private void OnPaneToggleClicked(object sender, RoutedEventArgs e)
    {
        RootNavigation.IsPaneOpen = !RootNavigation.IsPaneOpen;
    }

    private void OnNavigationDisplayModeChanged(NavigationView sender, NavigationViewDisplayModeChangedEventArgs args)
    {
        var expanded = args.DisplayMode == NavigationViewDisplayMode.Expanded;
        PaneToggleButton.Visibility = expanded
            ? Visibility.Collapsed
            : Visibility.Visible;
        if (expanded)
        {
            RootNavigation.IsPaneOpen = true;
        }
    }

    private void OnSearchQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var query = args.QueryText.Trim();
        if (query.Length == 0) return;

        var target = query.Contains("截图", StringComparison.OrdinalIgnoreCase) ||
                     query.Contains("贴图", StringComparison.OrdinalIgnoreCase)
            ? "screen-paste"
            : query.Contains("文件", StringComparison.OrdinalIgnoreCase) ||
              query.Contains("目录", StringComparison.OrdinalIgnoreCase)
                ? "folder-jump"
                : query.Contains("主题", StringComparison.OrdinalIgnoreCase) ||
                  query.Contains("启动", StringComparison.OrdinalIgnoreCase) ||
                  query.Contains("语言", StringComparison.OrdinalIgnoreCase) ||
                  query.Contains("快捷键", StringComparison.OrdinalIgnoreCase) ||
                  query.Contains("更新", StringComparison.OrdinalIgnoreCase) ||
                  query.Contains("常规", StringComparison.OrdinalIgnoreCase)
                    ? "general"
                    : "home";

        SelectNavigationItem(target);
        sender.Text = string.Empty;
    }

    private void OnPageHostSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateResponsiveLayout(e.NewSize.Width);
    }

    private void UpdateResponsiveLayout(double width)
    {
        var compactTitle = RootGrid.ActualWidth < 720;
        AppTitleBar.ColumnDefinitions[0].Width = new GridLength(compactTitle ? 76 : 240);
        TitleBrandText.Visibility = compactTitle ? Visibility.Collapsed : Visibility.Visible;

        var stackedHome = width < 720;
        HomeMainGrid.ColumnDefinitions[1].Width = stackedHome ? new GridLength(0) : new GridLength(340);
        HomeMainGrid.RowDefinitions[1].Height = stackedHome ? GridLength.Auto : new GridLength(0);
        Grid.SetColumn(HomeUtilitiesCard, stackedHome ? 0 : 1);
        Grid.SetRow(HomeUtilitiesCard, stackedHome ? 1 : 0);
        Grid.SetColumn(HomeSignals, stackedHome ? 0 : 1);
        Grid.SetRow(HomeSignals, stackedHome ? 1 : 0);
        HomeSignals.HorizontalAlignment = stackedHome ? HorizontalAlignment.Left : HorizontalAlignment.Right;

        var stackedActions = width < 560;
        ConfigureQuickAction(StartCaptureButton, 0, stackedActions ? 0 : 0, stackedActions);
        ConfigureQuickAction(PasteClipboardButton, stackedActions ? 0 : 1, stackedActions ? 1 : 0, stackedActions);
        ConfigureQuickAction(OpenFolderToolButton, stackedActions ? 0 : 2, stackedActions ? 2 : 0, stackedActions);
        QuickActionGrid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
        QuickActionGrid.ColumnDefinitions[1].Width = stackedActions ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        QuickActionGrid.ColumnDefinitions[2].Width = stackedActions ? new GridLength(0) : new GridLength(1, GridUnitType.Star);

        var stackedSettings = width < 720;
        ConfigureAdaptiveRow(VersionRowGrid, CheckUpdateButton, stackedSettings, true);
        ConfigureAdaptiveRow(LanguageRowGrid, LanguageComboBox, stackedSettings, true, 250);
        ConfigureAdaptiveRow(ThemeRowGrid, ThemeComboBox, stackedSettings, true, 250);
        ConfigureAdaptiveRow(StartupRowGrid, StartupToggle, stackedSettings, false);
        ConfigureAdaptiveRow(NotificationsRowGrid, FailureNotificationsToggle, stackedSettings, false);
        ConfigureAdaptiveRow(TrayRowGrid, TrayToggle, stackedSettings, false);
        ConfigureAdaptiveRow(ConflictRowGrid, CheckShortcutsButton, stackedSettings, true);
        ConfigureAdaptiveRow(FolderHotkeyRowGrid, GeneralHotkeyButton, stackedSettings, true);
    }

    private static void ConfigureQuickAction(FrameworkElement control, int column, int row, bool stacked)
    {
        Grid.SetColumn(control, column);
        Grid.SetRow(control, row);
        control.HorizontalAlignment = HorizontalAlignment.Stretch;
        control.MinHeight = stacked ? 80 : 112;
    }

    private static void ConfigureAdaptiveRow(
        Grid rowGrid,
        FrameworkElement control,
        bool stacked,
        bool stretchWhenStacked,
        double wideWidth = double.NaN)
    {
        rowGrid.ColumnDefinitions[1].Width = stacked ? new GridLength(0) : GridLength.Auto;
        rowGrid.RowDefinitions[1].Height = stacked ? GridLength.Auto : new GridLength(0);
        Grid.SetColumn(control, stacked ? 0 : 1);
        Grid.SetRow(control, stacked ? 1 : 0);
        control.Margin = stacked ? new Thickness(40, 12, 0, 0) : new Thickness(0);
        control.HorizontalAlignment = stacked && stretchWhenStacked
            ? HorizontalAlignment.Stretch
            : HorizontalAlignment.Right;
        control.Width = stacked && stretchWhenStacked ? double.NaN : wideWidth;
    }

    private async void OnStartCaptureClicked(object sender, RoutedEventArgs e)
    {
        if (_screenPaste?.IsEnabled != true)
        {
            ShowFailure("截图与贴图", "请先启用截图与贴图模块。");
            return;
        }
        await _screenPaste.StartCaptureFromUiAsync();
    }

    private async void OnPasteClipboardClicked(object sender, RoutedEventArgs e)
    {
        if (_screenPaste?.IsEnabled != true)
        {
            ShowFailure("截图与贴图", "请先启用截图与贴图模块。");
            return;
        }
        await _screenPaste.PasteClipboardFromUiAsync();
    }

    private void OnOpenFolderToolClicked(object sender, RoutedEventArgs e) => SelectNavigationItem("folder-jump");

    private async void OnHomeFolderToggled(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        await SetFolderJumpEnabledAsync(HomeFolderToggle.IsOn);
    }

    private async void OnEnabledToggled(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        await SetFolderJumpEnabledAsync(EnabledToggle.IsOn);
    }

    private async Task SetFolderJumpEnabledAsync(bool requested)
    {
        if (_folderJump is null) return;
        try
        {
            await _folderJump.SetEnabledAsync(requested);
            SetFolderToggles(_folderJump.IsEnabled);
            _settings = _settings with { FolderJumpEnabled = _folderJump.IsEnabled };
            await _settingsStore.SaveAsync(_settings);
            UpdateStatus();
        }
        catch (Exception exception)
        {
            AppLog.Write($"Change folder-jump setting failed: {exception}");
            SetFolderToggles(_folderJump.IsEnabled);
            ShowFailure("文件夹跳转", "无法保存文件夹跳转设置。");
        }
    }

    private async void OnHomeScreenPasteToggled(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        await SetScreenPasteEnabledAsync(HomeScreenPasteToggle.IsOn);
    }

    private async void OnScreenPasteToggled(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        await SetScreenPasteEnabledAsync(ScreenPasteToggle.IsOn);
    }

    private async Task SetScreenPasteEnabledAsync(bool requested)
    {
        if (_screenPaste is null) return;
        try
        {
            await _screenPaste.SetEnabledAsync(requested);
            SetScreenPasteToggles(_screenPaste.IsEnabled);
            _settings = _settings with { ScreenPasteEnabled = _screenPaste.IsEnabled };
            await _settingsStore.SaveAsync(_settings);
            UpdateStatus();
        }
        catch (Exception exception)
        {
            AppLog.Write($"Change screen-paste setting failed: {exception}");
            SetScreenPasteToggles(_screenPaste.IsEnabled);
            ShowFailure("截图与贴图", "无法保存截图与贴图设置。");
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

    private async void OnFailureNotificationsToggled(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _settings = _settings with { ShowFailureNotifications = FailureNotificationsToggle.IsOn };
        await _settingsStore.SaveAsync(_settings);
    }

    private async void OnThemeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || ThemeComboBox.SelectedItem is not ComboBoxItem item) return;
        var theme = item.Tag?.ToString() ?? "system";
        ApplyTheme(theme);
        _settings = _settings with { AppTheme = theme };
        await _settingsStore.SaveAsync(_settings);
    }

    private void ApplyTheme(string theme)
    {
        TboxUi.SetThemePreference(theme);
        var elementTheme = TboxUi.RequestedTheme;
        RootGrid.RequestedTheme = elementTheme;
        ConfigureTitleBar(elementTheme);
    }

    private void SelectThemeItem(string theme)
    {
        foreach (var entry in ThemeComboBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(entry.Tag?.ToString(), theme, StringComparison.OrdinalIgnoreCase))
            {
                ThemeComboBox.SelectedItem = entry;
                return;
            }
        }
        ThemeComboBox.SelectedIndex = 0;
    }

    private void ConfigureTitleBar(ElementTheme theme)
    {
        var dark = theme == ElementTheme.Dark ||
                   theme == ElementTheme.Default && Application.Current.RequestedTheme == ApplicationTheme.Dark;
        TboxUi.ConfigureTitleBar(_appWindow, dark);
    }

    private void OnCheckUpdateClicked(object sender, RoutedEventArgs e)
    {
        UpdateInfoBar.Title = "已安装最新版本";
        UpdateInfoBar.Message = $"当前版本 {AppVersion.Current}";
        UpdateInfoBar.Severity = InfoBarSeverity.Success;
        UpdateInfoBar.IsOpen = true;
    }

    private void OnCheckShortcutsClicked(object sender, RoutedEventArgs e)
    {
        UpdateStatus();
        ShowGlobalInfo("快捷键检查完成", ConflictDetailText.Text, InfoBarSeverity.Success);
    }

    private void OnWelcomeClicked(object sender, RoutedEventArgs e)
    {
        SelectNavigationItem("home");
        ShowGlobalInfo("欢迎使用 Tbox", "从主页快速启动截图、贴图和文件夹跳转。", InfoBarSeverity.Informational);
    }

    private void OnWhatsNewClicked(object sender, RoutedEventArgs e)
    {
        ShowGlobalInfo("新增内容", "设置窗口已采用 PowerToys 风格布局，并支持响应式侧栏。", InfoBarSeverity.Informational);
    }

    private void OnFeedbackClicked(object sender, RoutedEventArgs e)
    {
        ShowGlobalInfo("提供反馈", "反馈入口将在后续版本接入；当前可通过项目仓库提交问题。", InfoBarSeverity.Informational);
    }

    private void UpdateStatus()
    {
        var enabledCount = (_folderJump?.IsEnabled == true ? 1 : 0) + (_screenPaste?.IsEnabled == true ? 1 : 0);
        EnabledToolCountText.Text = $"{enabledCount} 个已启用";
        var conflict = _settings.FolderJumpEnabled && _folderJump?.IsEnabled != true ||
                       _settings.ScreenPasteEnabled && _screenPaste?.IsEnabled != true;
        ShortcutConflictText.Text = conflict ? "检测到注册失败" : "未找到冲突";
        ConflictDetailText.Text = conflict
            ? "部分快捷键未能注册，请关闭占用快捷键的应用后重试"
            : "当前未发现与其他应用冲突的快捷方式";
    }

    private void SetFolderToggles(bool value)
    {
        var wasInitializing = _initializing;
        _initializing = true;
        EnabledToggle.IsOn = value;
        HomeFolderToggle.IsOn = value;
        _initializing = wasInitializing;
    }

    private void SetScreenPasteToggles(bool value)
    {
        var wasInitializing = _initializing;
        _initializing = true;
        ScreenPasteToggle.IsOn = value;
        HomeScreenPasteToggle.IsOn = value;
        _initializing = wasInitializing;
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
            if (_settings.ShowFailureNotifications)
            {
                _trayIcon?.ShowWarning(title, message);
            }
            var bar = title == "截图与贴图" ? ScreenPasteStatusBar :
                      title == "文件夹跳转" ? StatusBar : GlobalInfoBar;
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

    private void ShowGlobalInfo(string title, string message, InfoBarSeverity severity)
    {
        GlobalInfoBar.Title = title;
        GlobalInfoBar.Message = message;
        GlobalInfoBar.Severity = severity;
        GlobalInfoBar.IsOpen = true;
    }
}
