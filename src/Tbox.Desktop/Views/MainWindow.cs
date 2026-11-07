using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Tbox.Desktop.Models;

namespace Tbox.Desktop.Views;

public sealed class MainWindow : Window
{
    private readonly DesktopController _controller;
    private readonly TextBlock _status = Ui.Caption("正在启动…");
    private readonly TextBlock _permission = Ui.Caption("");
    private readonly TextBlock _cache = Ui.Caption("");
    private readonly TextBlock _folder = Ui.Caption("");
    private readonly TextBlock _startupStatus = Ui.Caption("");
    private readonly TextBlock _summary = Ui.Caption("");
    private readonly TextBlock _conflicts = Ui.Caption("");
    private readonly List<ToggleSwitch> _captureToggles = [];
    private readonly List<ToggleSwitch> _folderToggles = [];
    private readonly List<Control> _captureActions = [];
    private readonly ToggleSwitch _startupToggle = new() { OnContent = null, OffContent = null };
    private readonly ToggleSwitch _failureToggle = new() { OnContent = null, OffContent = null };
    private readonly ComboBox _theme = new() { ItemsSource = new[] { "跟随系统", "浅色", "深色" }, MinWidth = 120 };
    private readonly Button _folderPermission;
    private readonly Button _screenPermission;
    private readonly Button _cancelStartup;
    private readonly Dictionary<SettingsPage, ScrollViewer> _pages = [];
    private readonly ListBox _navigation;
    private readonly SplitView _split;
    private readonly Button _menu;
    private bool _updatingSettings;
    private bool _initialSizeApplied;
    private static string Version => typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "";
    internal SettingsPage CurrentPage { get; private set; }
    internal bool HasFixedSidebar => _split.DisplayMode == SplitViewDisplayMode.Inline;
    internal ScrollViewer CurrentPageView => _pages[CurrentPage];

    public MainWindow(DesktopController controller)
    {
        _controller = controller;
        Ui.Configure(this, "Tbox", 1120, 800); MinWidth = 520; MinHeight = 480;
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaTitleBarHeightHint = 52;
        ExtendClientAreaChromeHints = Avalonia.Platform.ExtendClientAreaChromeHints.PreferSystemChrome;
        _screenPermission = Ui.Button("允许屏幕录制", () => controller.Run(() => { controller.RequestPermission(); return Task.CompletedTask; }));
        _folderPermission = Ui.Button("允许辅助功能", () => controller.Run(() => { controller.RequestFolderPermission(); return Task.CompletedTask; }));
        _cancelStartup = Ui.Button("取消开机启动申请", () => controller.Run(() => controller.SetStartupAsync(false)));
        _theme.SelectionChanged += (_, _) => { if (!_updatingSettings) controller.Run(() => controller.ChangeThemeAsync(_theme.SelectedIndex)); };
        _startupToggle.IsCheckedChanged += (_, _) => { if (!_updatingSettings) controller.Run(() => controller.SetStartupAsync(_startupToggle.IsChecked == true)); };
        _failureToggle.IsCheckedChanged += (_, _) => { if (!_updatingSettings) controller.Run(() => controller.SetFailureNotificationsAsync(_failureToggle.IsChecked == true)); };
        AutomationProperties.SetName(_startupToggle, "开机启动");
        AutomationProperties.SetName(_failureToggle, "功能失败时通知");
        _navigation = new ListBox { ItemsSource = new[] { "主页", "常规", "文件夹跳转", "截图与贴图" }, SelectedIndex = 0, Background = Brushes.Transparent };
        _navigation.ItemTemplate = new FuncDataTemplate<string>((name, _) =>
        {
            var icon = name switch { "主页" => IconKind.Home, "常规" => IconKind.Settings, "文件夹跳转" => IconKind.Folder, _ => IconKind.Capture };
            var row = Ui.Row(new LineIcon(icon), Ui.Text(name ?? "")); row.Spacing = 12; row.Margin = new Thickness(4, 8); return row;
        });
        _navigation.Margin = new Thickness(12, 16);
        AutomationProperties.SetName(_navigation, "页面导航");
        var footer = new StackPanel { Spacing = 4, Margin = new Thickness(12), Children =
        {
            Ui.Button("欢迎使用 Tbox", () => { Navigate(SettingsPage.Home); SetStatus("从主页快速启动截图、贴图和文件夹跳转。"); }),
            Ui.Button("新增内容", () => SetStatus("共用桌面界面现已支持截图、贴图、离线识别、长截图和区域录屏，持续进行平台验收。")),
            Ui.Button("提供反馈", () => SetStatus("请在已授权访问的 Tbox 项目仓库中提交问题，附上版本与复现步骤。"))
        }};
        foreach (var button in footer.Children.OfType<Button>())
        {
            button.Background = Brushes.Transparent; button.BorderThickness = new Thickness(0);
            button.HorizontalAlignment = HorizontalAlignment.Stretch; button.HorizontalContentAlignment = HorizontalAlignment.Left;
        }
        DockPanel.SetDock(footer, Dock.Bottom);
        var sidebar = new DockPanel { LastChildFill = true, Children =
        {
            footer, new ScrollViewer { Content = _navigation, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }
        }};
        _split = new SplitView { Pane = sidebar, OpenPaneLength = DesignTokens.SidebarWidth, CompactPaneLength = 0, DisplayMode = SplitViewDisplayMode.Inline, IsPaneOpen = true };
        _menu = Ui.Button("☰", () => _split.IsPaneOpen = !_split.IsPaneOpen, "打开或关闭导航");
        _menu.Width = 40; _menu.IsVisible = false;
        var brand = Ui.Text("Tbox", 16); brand.FontWeight = FontWeight.SemiBold;
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"), ColumnSpacing = 12,
            Margin = OperatingSystem.IsMacOS() ? new Thickness(88, 8, 16, 8) : new Thickness(16, 8, 140, 8) };
        header.PointerPressed += (_, e) =>
        {
            if ((ReferenceEquals(e.Source, header) || ReferenceEquals(e.Source, brand)) && e.GetCurrentPoint(header).Properties.IsLeftButtonPressed)
                BeginMoveDrag(e);
        };
        header.Children.Add(_menu); Grid.SetColumn(brand, 1); header.Children.Add(brand);
        var tagline = Ui.Caption("桌面工具"); tagline.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(tagline, 2); header.Children.Add(tagline);
        BuildPages();
        _navigation.SelectionChanged += (_, _) => { if (_navigation.SelectedIndex >= 0) Navigate((SettingsPage)_navigation.SelectedIndex); };
        var status = new Border { Padding = new Thickness(16, 8), Child = _status };
        var root = new Grid { RowDefinitions = new RowDefinitions("52,*,Auto") };
        root.Children.Add(header); Grid.SetRow(_split, 1); root.Children.Add(_split); Grid.SetRow(status, 2); root.Children.Add(status); Content = root;
        SizeChanged += (_, _) => ApplyResponsiveLayout(ClientSize.Width);
        ActualThemeVariantChanged += (_, _) => ApplySidebarTheme(); ApplySidebarTheme();
        Navigate(SettingsPage.Home);
        Closing += (_, args) =>
        {
            if (!controller.IsQuitting && args.CloseReason is WindowCloseReason.WindowClosing or WindowCloseReason.Undefined)
            { args.Cancel = true; Hide(); }
        };
        Activated += (_, _) => { Refresh(); if (_initialSizeApplied) controller.Run(controller.RefreshStartupAsync); };
        Opened += (_, _) =>
        {
            if (_initialSizeApplied) return; _initialSizeApplied = true;
            if (Screens.ScreenFromWindow(this) is { } screen)
            {
                var workWidth = screen.WorkingArea.Width / screen.Scaling;
                var workHeight = screen.WorkingArea.Height / screen.Scaling;
                MinWidth = Math.Min(520, workWidth * .9); MinHeight = Math.Min(480, workHeight * .9);
                Width = Math.Min(1120, workWidth * .9); Height = Math.Min(800, workHeight * .9);
            }
            ApplyResponsiveLayout(ClientSize.Width);
        };
        controller.ThemeLoaded += _ => Refresh();
    }

    private void ApplySidebarTheme() => _split.PaneBackground = Brush.Parse(DesignTokens.Colors(ActualThemeVariant == ThemeVariant.Dark).SecondarySurface);

    internal void ApplyResponsiveLayout(double width)
    {
        var fixedSidebar = width >= 1080;
        var mode = fixedSidebar ? SplitViewDisplayMode.Inline : SplitViewDisplayMode.Overlay;
        if (_split.DisplayMode != mode) { _split.DisplayMode = mode; _split.IsPaneOpen = fixedSidebar; }
        _menu.IsVisible = !fixedSidebar;
        foreach (var page in _pages.Values)
            if (page.Content is Control content) content.Margin = new Thickness(width < 720 ? 16 : width < 1080 ? 24 : 32, 24);
    }

    internal void Navigate(SettingsPage page)
    {
        CurrentPage = page; _split.Content = _pages[page];
        if (_navigation.SelectedIndex != (int)page) _navigation.SelectedIndex = (int)page;
        if (_split.DisplayMode == SplitViewDisplayMode.Overlay) _split.IsPaneOpen = false;
    }

    private void BuildPages()
    {
        var actions = new QuickActionGrid(
            ActionCard("开始截图", "选区、标注与录屏", ShortcutLabels.Capture, IconKind.Capture, () => _controller.Run(_controller.CaptureAsync), true),
            ActionCard("剪贴板贴图", "把图片或文字留在桌面", ShortcutLabels.Pin, IconKind.Pin, () => _controller.Run(_controller.PinClipboardAsync), true),
            ActionCard("文件夹跳转", "快速回到最近目录", "Control + G", IconKind.Folder, () => Navigate(SettingsPage.Folders), false));
        AddPage(SettingsPage.Home, "主页", "随时捕捉，高效处理。", _summary,
            Block("快速开始", actions),
            Section("工具", Setting("截图与贴图", "截图、标注、贴图、识别、长截图与录屏。", ModuleToggle(true)),
                Setting("文件夹跳转", "在系统文件面板中快速定位最近目录。", ModuleToggle(false))),
            Section("快捷键", ShortcutRows(), _conflicts));
        AddPage(SettingsPage.General, "常规", "管理应用外观与行为",
            Section("版本和更新", Setting("当前版本", $"Tbox {Version} · 跨平台预览版", Ui.Button("检查版本", () => SetStatus($"当前版本 {Version}。授权用户可在私有仓库 Releases 查看可用安装包。")))),
            Section("外观和行为", Setting("应用主题", "应用于设置、文字识别和图片结果窗口。", _theme),
                Setting("登录时启动", "登录电脑后启动 Tbox。", _startupToggle), _startupStatus, _cancelStartup,
                Setting("失败通知", "关闭通知后，页面内仍保留操作状态。", _failureToggle)),
            Section("快捷键", ShortcutRows(), Ui.Button("检查快捷键", () => { Refresh(); SetStatus(_controller.HasHotkeyConflict ? "快捷键注册失败，请关闭占用快捷键的应用后重新启用模块。" : "当前快捷键注册正常。"); })));
        AddPage(SettingsPage.Folders, "文件夹跳转", "在系统标准打开、保存或文件夹选择面板中，跳转到最近使用的目录。",
            Section("启用", Setting("文件夹跳转", "按 Control + G 跳转到最近使用的文件管理器目录。", ModuleToggle(false))),
            Section("最近目录", _folder, _folderPermission,
                Ui.Text("无法确认当前文件面板时，不会向其他窗口输入路径；没有可用目录时使用用户主目录。", 12)));
        AddPage(SettingsPage.Capture, "截图与贴图", "截图、标注和识别，保留你需要的内容。",
            Section("启用", Setting("截图与贴图", "关闭时结束所属浮层并释放快捷键。", ModuleToggle(true))),
            Section("截图与贴图", Actions(CaptureButton("开始截图", () => _controller.Run(_controller.CaptureAsync), ShortcutLabels.Capture),
                CaptureButton("剪贴板贴图", () => _controller.Run(_controller.PinClipboardAsync), ShortcutLabels.Pin),
                CaptureButton("显示 / 隐藏贴图", _controller.TogglePins, ShortcutLabels.Toggle)),
                Ui.Text("选择区域后可复制、保存、贴图、识别文字，或切换到长截图与录屏。", 12), _permission, _screenPermission),
            Section("文字识别", Ui.Text("中英文离线识别，结果可编辑；不保存文字历史。", 12),
                Actions(CaptureButton("选择图片…", () => _controller.Run(_controller.OpenImageForOcrAsync)), CaptureButton("识别剪贴板图片", () => _controller.Run(_controller.OcrClipboardAsync)))),
            Section("截图缓存", _cache, Ui.Text("按 30 天、200 个文件与 512 MB 上限清理；显示中的贴图受保护。更改目录不会移动已有文件。", 12),
                Actions(Ui.Button("打开缓存", () => _controller.Run(() => { _controller.OpenCache(); return Task.CompletedTask; })), Ui.Button("更换缓存…", () => _controller.Run(_controller.ChooseCacheAsync)))));
    }

    private void AddPage(SettingsPage key, string title, string description, params Control[] content)
    {
        var heading = Ui.Text(title, 28); heading.FontWeight = FontWeight.SemiBold;
        var panel = new StackPanel { Spacing = 16, MaxWidth = 1120, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(32, 24) };
        panel.Children.Add(heading); panel.Children.Add(Ui.Caption(description));
        foreach (var child in content) panel.Children.Add(child);
        _pages[key] = new ScrollViewer { Content = panel, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    }
    private static Border Section(string title, params Control[] children)
    {
        var heading = Ui.Text(title, 16); heading.FontWeight = FontWeight.SemiBold;
        var panel = new StackPanel { Spacing = 12, Children = { heading } };
        foreach (var child in children) panel.Children.Add(child);
        return Ui.Card(panel);
    }
    private static StackPanel Block(string title, Control child)
    {
        var heading = Ui.Text(title, DesignTokens.SectionTitle); heading.FontWeight = FontWeight.SemiBold;
        return new StackPanel { Spacing = 12, Children = { heading, child } };
    }
    private Button ActionCard(string title, string description, string shortcut, IconKind icon, Action action, bool capture)
    {
        var heading = Ui.Text(title, DesignTokens.SectionTitle); heading.FontWeight = FontWeight.SemiBold;
        var badge = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(10),
            Child = new LineIcon(icon, true) { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        var keys = Ui.Caption(shortcut); keys.HorizontalAlignment = HorizontalAlignment.Right; keys.VerticalAlignment = VerticalAlignment.Center;
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        top.Children.Add(badge); Grid.SetColumn(keys, 1); top.Children.Add(keys);
        var button = Ui.Button(title, action, title + " · " + shortcut);
        button.Content = new StackPanel { Spacing = 12, Children = { top, heading, Ui.Caption(description) } };
        button.Padding = new Thickness(20); button.CornerRadius = new CornerRadius(DesignTokens.CardRadius);
        button.HorizontalAlignment = HorizontalAlignment.Stretch; button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        void Update()
        {
            var colors = DesignTokens.Colors(button.ActualThemeVariant == ThemeVariant.Dark);
            button.Background = Brush.Parse(colors.Surface); button.BorderBrush = Brush.Parse(colors.Border);
            badge.Background = Brush.Parse(colors.AccentSurface);
            button.Resources["ButtonBackgroundPointerOver"] = Brush.Parse(colors.Hover);
            button.Resources["ButtonBackgroundPressed"] = Brush.Parse(colors.AccentSurface);
        }
        button.ActualThemeVariantChanged += (_, _) => Update(); Update();
        if (capture) _captureActions.Add(button);
        return button;
    }
    private static Control Setting(string title, string description, Control input) => new SettingsRow(title, description, input);
    private static WrapPanel Actions(params Control[] children)
    {
        var panel = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var child in children) { child.Margin = new Thickness(0, 0, 8, 8); panel.Children.Add(child); }
        return panel;
    }
    private Button CaptureButton(string title, Action action, string? shortcut = null)
    {
        var button = Ui.Button(title, action, shortcut is null ? title : $"{title} · {shortcut}");
        _captureActions.Add(button); return button;
    }
    private ToggleSwitch ModuleToggle(bool capture)
    {
        var toggle = new ToggleSwitch { OnContent = null, OffContent = null, MinHeight = 40 };
        AutomationProperties.SetName(toggle, capture ? "启用截图与贴图" : "启用文件夹跳转");
        (capture ? _captureToggles : _folderToggles).Add(toggle);
        toggle.IsCheckedChanged += (_, _) =>
        {
            if (_updatingSettings) return;
            var enabled = toggle.IsChecked == true;
            _controller.Run(() => _controller.SetModulesAsync(capture ? enabled : null, capture ? null : enabled));
        };
        return toggle;
    }
    private static StackPanel ShortcutRows() => new() { Spacing = 8, Children =
    {
        Ui.Text($"开始截图    {ShortcutLabels.Capture}"), Ui.Text($"剪贴板贴图    {ShortcutLabels.Pin}"),
        Ui.Text($"贴图显隐    {ShortcutLabels.Toggle}"), Ui.Text("文件夹跳转    Control + G")
    }};

    public void SetStatus(string message) => _status.Text = message;
    public void Refresh()
    {
        _updatingSettings = true;
        foreach (var toggle in _captureToggles) toggle.IsChecked = _controller.Settings.ScreenPasteEnabled;
        foreach (var toggle in _folderToggles) toggle.IsChecked = _controller.Settings.FolderJumpEnabled;
        _failureToggle.IsChecked = _controller.Settings.ShowFailureNotifications;
        _startupToggle.IsChecked = _controller.Startup.Enabled;
        _theme.SelectedIndex = _controller.Settings.AppTheme switch { "light" => 1, "dark" => 2, _ => 0 };
        _updatingSettings = false;
        foreach (var action in _captureActions) action.IsEnabled = _controller.Settings.ScreenPasteEnabled;
        var count = (_controller.Settings.ScreenPasteEnabled ? 1 : 0) + (_controller.Settings.FolderJumpEnabled ? 1 : 0);
        _summary.Text = $"{count} 个工具已启用    ·    Tbox {Version}";
        _conflicts.Text = _controller.HasHotkeyConflict ? "部分快捷键注册失败，请关闭占用它们的应用后重试。" : "当前未发现快捷键注册冲突。";
        _startupStatus.Text = _controller.Startup.Message ?? ""; _startupStatus.IsVisible = _startupStatus.Text.Length > 0;
        _cancelStartup.IsVisible = _controller.Startup.RequiresApproval;
        _folder.Text = _controller.HasFolderPermission ? "最近目录：" + _controller.FolderDirectory : "需要辅助功能权限，才能读取 Finder 目录并跳转系统文件面板。";
        _folderPermission.IsVisible = OperatingSystem.IsMacOS() && !_controller.HasFolderPermission;
        var allowed = _controller.Platform.HasScreenPermission;
        _permission.Text = !OperatingSystem.IsMacOS() ? "截图服务已就绪" : allowed ? "屏幕录制权限已允许" : "截图需要屏幕录制权限；贴图和已有图片识别可直接使用。";
        _screenPermission.IsVisible = OperatingSystem.IsMacOS() && !allowed;
        _screenPermission.Content = _controller.ScreenPermissionRequested ? "打开屏幕录制设置" : "允许屏幕录制";
        _cache.Text = _controller.CacheDirectory;
    }
}

internal sealed class QuickActionGrid : Grid
{
    public QuickActionGrid(params Control[] actions)
    {
        ColumnSpacing = RowSpacing = 12;
        ColumnDefinitions = new ColumnDefinitions("*,*,*"); RowDefinitions = new RowDefinitions("Auto,Auto,Auto");
        for (var i = 0; i < actions.Length; i++) { Children.Add(actions[i]); SetColumn(actions[i], i); }
        SizeChanged += (_, e) =>
        {
            var stacked = e.NewSize.Width < 720;
            ColumnDefinitions[1].Width = ColumnDefinitions[2].Width = stacked ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            for (var i = 0; i < actions.Length; i++) { SetColumn(actions[i], stacked ? 0 : i); SetRow(actions[i], stacked ? i : 0); }
        };
    }
}

internal sealed class SettingsRow : Grid
{
    internal bool IsStacked { get; private set; }
    public SettingsRow(string title, string description, Control input)
    {
        MinHeight = 64; RowSpacing = 12; ColumnSpacing = 16;
        ColumnDefinitions = new ColumnDefinitions("*,Auto"); RowDefinitions = new RowDefinitions("Auto,Auto");
        var text = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center, Children = { Ui.Text(title), Ui.Caption(description) } };
        Children.Add(text); Children.Add(input); Grid.SetColumn(input, 1);
        SizeChanged += (_, e) =>
        {
            IsStacked = e.NewSize.Width < 600;
            ColumnDefinitions[1].Width = IsStacked ? new GridLength(0) : GridLength.Auto;
            Grid.SetColumn(input, IsStacked ? 0 : 1); Grid.SetRow(input, IsStacked ? 1 : 0);
            input.HorizontalAlignment = IsStacked ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        };
    }
}
