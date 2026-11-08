using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Tbox.Desktop.Models;

namespace Tbox.Desktop.Views;

public sealed class MainWindow : Window
{
    private readonly DesktopController _controller;
    private readonly TextBlock _status = Ui.Caption("");
    private readonly TextBlock _cache = Ui.Caption("");
    private readonly TextBlock _folder = Ui.Caption("");
    private readonly TextBlock _startupStatus = Ui.Caption("");
    private readonly TextBlock _conflicts = Ui.Caption("部分快捷键被占用。可从面板启动工具。");
    private readonly List<Button> _captureActions = [];
    private readonly ToggleSwitch _captureToggle = ModuleSwitch("启用截图与贴图");
    private readonly ToggleSwitch _folderToggle = ModuleSwitch("启用文件夹跳转");
    private readonly ToggleSwitch _startupToggle = ModuleSwitch("开机启动");
    private readonly ToggleSwitch _failureToggle = ModuleSwitch("功能失败时通知");
    private readonly ComboBox _theme = new() { ItemsSource = new[] { "跟随系统", "浅色", "深色" }, MinWidth = 120 };
    private readonly Button _screenPermission;
    private readonly Button _folderPermission;
    private readonly Button _cancelStartup;
    private readonly Border _capturePermission;
    private readonly Border _notice;
    private readonly Button _back;
    private readonly Button _settings;
    private readonly TextBlock _heading;
    private readonly ContentControl _body = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly Dictionary<SettingsPage, ScrollViewer> _pages = [];
    private bool _updatingSettings;
    private bool _initialSizeApplied;
    private bool _showScreenPermission;

    private static string Version => typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "";
    internal SettingsPage CurrentPage { get; private set; }
    internal ScrollViewer CurrentPageView => _pages[CurrentPage];

    public MainWindow(DesktopController controller)
    {
        _controller = controller;
        Ui.Configure(this, "Tbox", 560, 460);
        MinWidth = 360;
        MinHeight = 360;

        _screenPermission = Ui.Button("允许屏幕录制", () => RunCommand(controller.RequestPermission));
        _folderPermission = Ui.Button("允许辅助功能", () => RunCommand(controller.RequestFolderPermission));
        _cancelStartup = Ui.Button("取消开机启动申请", () => controller.Run(() => controller.SetStartupAsync(false)));
        _theme.SelectionChanged += (_, _) =>
        {
            if (!_updatingSettings) controller.Run(() => controller.ChangeThemeAsync(_theme.SelectedIndex));
        };
        _startupToggle.IsCheckedChanged += (_, _) =>
        {
            if (!_updatingSettings) controller.Run(() => controller.SetStartupAsync(_startupToggle.IsChecked == true));
        };
        _failureToggle.IsCheckedChanged += (_, _) =>
        {
            if (!_updatingSettings) controller.Run(() => controller.SetFailureNotificationsAsync(_failureToggle.IsChecked == true));
        };
        _captureToggle.IsCheckedChanged += (_, _) =>
        {
            if (!_updatingSettings) controller.Run(() => controller.SetModulesAsync(_captureToggle.IsChecked == true, null));
        };
        _folderToggle.IsCheckedChanged += (_, _) =>
        {
            if (!_updatingSettings) controller.Run(() => controller.SetModulesAsync(null, _folderToggle.IsChecked == true));
        };

        _back = IconButton(IconKind.Back, "返回主面板", () => Navigate(SettingsPage.Home));
        _settings = IconButton(IconKind.Settings, "设置", () => Navigate(SettingsPage.General));
        _heading = Ui.Text("Tbox", DesignTokens.SectionTitle);
        _heading.FontWeight = FontWeight.SemiBold;
        _heading.VerticalAlignment = VerticalAlignment.Center;
        using var brandAsset = AssetLoader.Open(new Uri("avares://Tbox.Desktop/Assets/StoreLogo.png"));
        var brandBitmap = new Bitmap(brandAsset);
        var brand = new Image
        {
            Source = brandBitmap, Width = 24, Height = 24,
            VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false
        };
        Closed += (_, _) => brandBitmap.Dispose();
        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            MaxWidth = 560,
            Margin = new Thickness(16, 12, 16, 4)
        };
        header.Children.Add(Ui.Row(_back, brand, _heading));
        Grid.SetColumn(_settings, 1);
        header.Children.Add(_settings);

        _notice = Ui.Card(_status);
        _notice.Padding = new Thickness(12, 8);
        _notice.MaxWidth = 560;
        _notice.Margin = new Thickness(16, 0, 16, 12);
        _notice.IsVisible = false;
        _capturePermission = Ui.Card(new StackPanel
        {
            Spacing = 8,
            Children = { Ui.Caption("截图和录屏需要屏幕录制权限。"), _screenPermission }
        });
        _capturePermission.Padding = new Thickness(12);
        _capturePermission.IsVisible = false;
        BuildPages();

        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(_notice, Dock.Bottom);
        root.Children.Add(header);
        root.Children.Add(_notice);
        root.Children.Add(_body);
        Content = root;
        Navigate(SettingsPage.Home);
        Refresh();

        Closing += (_, args) =>
        {
            if (!controller.IsQuitting && args.CloseReason is WindowCloseReason.WindowClosing or WindowCloseReason.Undefined)
            {
                args.Cancel = true;
                Hide();
            }
        };
        Activated += (_, _) =>
        {
            Refresh();
            if (_initialSizeApplied) controller.Run(controller.RefreshStartupAsync);
        };
        Opened += (_, _) =>
        {
            if (_initialSizeApplied) return;
            _initialSizeApplied = true;
            if (Screens.ScreenFromWindow(this) is not { } screen) return;
            var workWidth = screen.WorkingArea.Width / screen.Scaling;
            var workHeight = screen.WorkingArea.Height / screen.Scaling;
            MinWidth = Math.Min(MinWidth, workWidth * .9);
            MinHeight = Math.Min(MinHeight, workHeight * .9);
            Width = Math.Min(Width, workWidth * .9);
            Height = Math.Min(Height, workHeight * .9);
        };
        controller.ThemeLoaded += _ => Refresh();
    }

    internal void Navigate(SettingsPage page)
    {
        CurrentPage = page;
        _body.Content = _pages[page];
        _back.IsVisible = page != SettingsPage.Home;
        _settings.IsVisible = page != SettingsPage.General;
        _heading.Text = page switch
        {
            SettingsPage.General => "设置",
            SettingsPage.Folders => "文件夹跳转",
            SettingsPage.Ocr => "文字识别",
            _ => "Tbox"
        };
        _showScreenPermission = false;
        _capturePermission.IsVisible = false;
        SetStatus("");
    }

    internal void ShowCapturePermission()
    {
        if (CurrentPage != SettingsPage.Home) Navigate(SettingsPage.Home);
        SetStatus("");
        _showScreenPermission = true;
        Refresh();
    }

    private void BuildPages()
    {
        var capture = ActionRow("开始截图", IconKind.Capture, ShortcutLabels.Capture, () => _controller.Run(_controller.CaptureAsync), primary: true);
        var pin = ActionRow("剪贴板贴图", IconKind.Pin, ShortcutLabels.Pin, () => _controller.Run(_controller.PinClipboardAsync));
        var ocr = ActionRow("文字识别", IconKind.Ocr, null, () => Navigate(SettingsPage.Ocr));
        var folders = ActionRow("文件夹跳转", IconKind.Folder, ShortcutLabels.Folder, () => Navigate(SettingsPage.Folders));
        var toggle = Ui.Button("显隐贴图", () => RunCommand(_controller.TogglePins), $"显隐贴图 · {ShortcutLabels.Toggle}");
        var record = Ui.Button("区域录屏", () => _controller.Run(() => _controller.CaptureAsync(selectRecordingRegion: true)));
        var secondary = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { toggle, record } };
        Grid.SetColumn(record, 1);
        _captureActions.AddRange([capture, pin, ocr, toggle, record]);
        AddPage(SettingsPage.Home, capture, _capturePermission, pin, ocr, folders, secondary);

        AddPage(SettingsPage.General,
            Section("常规",
                new SettingsRow("主题", _theme),
                new SettingsRow("登录时启动", _startupToggle), _startupStatus, _cancelStartup,
                new SettingsRow("失败通知", _failureToggle)),
            Section("工具",
                new SettingsRow("截图与贴图", _captureToggle), _conflicts,
                Ui.Button("检查快捷键", () =>
                {
                    Refresh();
                    SetStatus(_controller.HasHotkeyConflict ? "快捷键被占用。请关闭占用它们的应用后重新启用工具。" : "当前未发现快捷键冲突。");
                })),
            Section("截图缓存", _cache, Actions(
                Ui.Button("打开目录", () => RunCommand(_controller.OpenCache)),
                Ui.Button("更换目录…", () => _controller.Run(_controller.ChooseCacheAsync)))),
            Section("关于",
                new SettingsRow($"Tbox {Version}", Ui.Button("检查更新", _controller.ShowUpdates)),
                Ui.Button("帮助与反馈", () => _controller.Run(async () =>
                {
                    if (!await Launcher.LaunchUriAsync(new Uri("https://github.com/xsddpx/Tbox/issues")))
                        SetStatus("无法打开反馈页面。");
                }))));

        AddPage(SettingsPage.Folders,
            Section("当前目录", _folder, _folderPermission),
            Section("在打开或保存面板中跳转",
                new SettingsRow("启用文件夹跳转", _folderToggle),
                Ui.Caption($"在系统文件面板中按 {ShortcutLabels.Folder}，跳到最近使用的目录。")));

        var file = ActionRow("选择图片…", IconKind.Folder, null, () => _controller.Run(_controller.OpenImageForOcrAsync), primary: true);
        var clipboard = ActionRow("识别剪贴板图片", IconKind.Ocr, null, () => _controller.Run(_controller.OcrClipboardAsync));
        _captureActions.AddRange([file, clipboard]);
        AddPage(SettingsPage.Ocr, file, clipboard);
    }

    private void AddPage(SettingsPage key, params Control[] content)
    {
        var panel = new StackPanel { Spacing = 8, MaxWidth = 560, Margin = new Thickness(16, 8, 16, 16) };
        foreach (var child in content) panel.Children.Add(child);
        _pages[key] = new ScrollViewer
        {
            Content = panel,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
    }

    private static Border Section(string title, params Control[] children)
    {
        var heading = Ui.Text(title, DesignTokens.SectionTitle);
        heading.FontWeight = FontWeight.SemiBold;
        var panel = new StackPanel { Spacing = 8, Children = { heading } };
        foreach (var child in children) panel.Children.Add(child);
        var card = Ui.Card(panel);
        card.Padding = new Thickness(16);
        return card;
    }

    private static Button ActionRow(string title, IconKind icon, string? shortcut, Action action, bool primary = false)
    {
        var button = Ui.Button(title, action, shortcut is null ? title : $"{title} · {shortcut}");
        var text = Ui.Text(title, primary ? DesignTokens.SectionTitle : DesignTokens.Body);
        text.VerticalAlignment = VerticalAlignment.Center;
        text.FontWeight = primary ? FontWeight.SemiBold : FontWeight.Normal;
        var content = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 12 };
        content.Children.Add(new LineIcon(icon, primary) { VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(text, 1);
        content.Children.Add(text);
        Control accessory = shortcut is null ? new LineIcon(IconKind.Chevron) : Ui.Caption(shortcut);
        accessory.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(accessory, 2);
        content.Children.Add(accessory);
        button.Content = content;
        button.MinHeight = primary ? 72 : 52;
        button.Padding = new Thickness(16, 8);
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        void Update()
        {
            var colors = DesignTokens.Colors(button.ActualThemeVariant == ThemeVariant.Dark);
            button.Background = Brush.Parse(primary ? colors.AccentSurface : colors.Surface);
            button.BorderBrush = Brush.Parse(colors.Border);
            button.Resources["ButtonBackgroundPointerOver"] = Brush.Parse(colors.Hover);
            button.Resources["ButtonBackgroundPressed"] = Brush.Parse(colors.AccentSurface);
        }
        button.ActualThemeVariantChanged += (_, _) => Update();
        Update();
        return button;
    }

    private static Button IconButton(IconKind icon, string label, Action action)
    {
        var button = Ui.Button(label, action, label);
        button.Content = new LineIcon(icon);
        button.Padding = new Thickness(8);
        return button;
    }

    private static WrapPanel Actions(params Control[] children)
    {
        var panel = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var child in children)
        {
            child.Margin = new Thickness(0, 0, 8, 0);
            panel.Children.Add(child);
        }
        return panel;
    }

    private static ToggleSwitch ModuleSwitch(string label)
    {
        var toggle = new ToggleSwitch { OnContent = null, OffContent = null, MinHeight = 40 };
        AutomationProperties.SetName(toggle, label);
        return toggle;
    }

    private void RunCommand(Action action)
    {
        SetStatus("");
        _controller.Run(() =>
        {
            action();
            return Task.CompletedTask;
        });
    }

    public void SetStatus(string message)
    {
        _status.Text = message;
        _notice.IsVisible = !string.IsNullOrWhiteSpace(message);
    }

    public void Refresh()
    {
        _updatingSettings = true;
        _captureToggle.IsChecked = _controller.Settings.ScreenPasteEnabled;
        _folderToggle.IsChecked = _controller.Settings.FolderJumpEnabled;
        _failureToggle.IsChecked = _controller.Settings.ShowFailureNotifications;
        _startupToggle.IsChecked = _controller.Startup.Enabled;
        _theme.SelectedIndex = _controller.Settings.AppTheme switch { "light" => 1, "dark" => 2, _ => 0 };
        _updatingSettings = false;

        foreach (var action in _captureActions) action.IsEnabled = _controller.Settings.ScreenPasteEnabled;
        _conflicts.IsVisible = _controller.HasHotkeyConflict;
        _startupStatus.Text = _controller.Startup.Message ?? "";
        _startupStatus.IsVisible = _startupStatus.Text.Length > 0;
        _cancelStartup.IsVisible = _controller.Startup.RequiresApproval;
        _folder.Text = !_controller.HasFolderPermission
            ? "启用辅助功能权限后，Tbox 才能读取最近目录并跳转。"
            : string.IsNullOrWhiteSpace(_controller.FolderDirectory) ? "尚未读取目录，将使用用户主目录。" : _controller.FolderDirectory;
        _folderPermission.IsVisible = !_controller.HasFolderPermission;
        if (_controller.Platform.HasScreenPermission || !_controller.Settings.ScreenPasteEnabled)
            _showScreenPermission = false;
        _capturePermission.IsVisible = _showScreenPermission;
        _screenPermission.Content = _controller.ScreenPermissionRequested ? "打开屏幕录制设置" : "允许屏幕录制";
        AutomationProperties.SetName(_screenPermission, (string)_screenPermission.Content);
        _cache.Text = _controller.CacheDirectory;
    }
}

internal sealed class SettingsRow : Grid
{
    public SettingsRow(string title, Control input)
    {
        MinHeight = 44; RowSpacing = 4; ColumnSpacing = 12;
        ColumnDefinitions = new ColumnDefinitions("*,Auto"); RowDefinitions = new RowDefinitions("Auto,Auto");
        var text = Ui.Text(title);
        text.VerticalAlignment = VerticalAlignment.Center;
        Children.Add(text); Children.Add(input); Grid.SetColumn(input, 1);
        SizeChanged += (_, e) =>
        {
            var stacked = e.NewSize.Width < 320;
            ColumnDefinitions[1].Width = stacked ? new GridLength(0) : GridLength.Auto;
            Grid.SetColumn(input, stacked ? 0 : 1); Grid.SetRow(input, stacked ? 1 : 0);
            input.HorizontalAlignment = stacked ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        };
    }
}
