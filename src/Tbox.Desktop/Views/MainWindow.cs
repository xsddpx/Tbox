using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Tbox.Desktop.Models;

namespace Tbox.Desktop.Views;

// The settings window. Everyday tools live in the tray panel; this window only holds preferences.
public sealed class MainWindow : Window
{
    private readonly DesktopController _controller;
    private readonly TextBlock _status = Ui.Caption("");
    private readonly SettingsRow _cacheRow;
    private readonly SettingsRow _folderRow;
    private readonly SettingsRow _startupRow;
    private readonly SettingsRow _captureRow;
    private readonly ToggleSwitch _captureToggle = ModuleSwitch("启用截图与贴图");
    private readonly ToggleSwitch _folderToggle = ModuleSwitch("启用文件夹跳转");
    private readonly ToggleSwitch _startupToggle = ModuleSwitch("开机启动");
    private readonly ToggleSwitch _failureToggle = ModuleSwitch("功能失败时通知");
    private readonly ComboBox _theme = new() { ItemsSource = new[] { "跟随系统", "浅色", "深色" }, MinWidth = 128 };
    private readonly Button _folderPermission;
    private readonly Button _cancelStartup;
    private readonly Border _notice;
    private readonly Dictionary<SettingsPage, ToggleButton> _tabs = [];
    private readonly ContentControl _body = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly Dictionary<SettingsPage, ScrollViewer> _pages = [];
    private bool _updatingSettings;
    private bool _initialSizeApplied;

    internal static string Version => typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "";
    internal SettingsPage CurrentPage { get; private set; }
    internal ScrollViewer CurrentPageView => _pages[CurrentPage];

    public MainWindow(DesktopController controller)
    {
        _controller = controller;
        Ui.Configure(this, "Tbox 设置", 560, 560);
        MinWidth = 360;
        MinHeight = 360;

        _folderPermission = Ui.Primary("允许辅助功能", () => RunCommand(controller.RequestFolderPermission));
        _cancelStartup = Ui.Button("取消申请", () => controller.Run(() => controller.SetStartupAsync(false)), "取消开机启动申请");
        _startupRow = new SettingsRow("登录时启动", Ui.Row(_cancelStartup, _startupToggle), icon: IconKind.Startup);
        _captureRow = new SettingsRow("截图与贴图", _captureToggle, icon: IconKind.Capture);
        _cacheRow = new SettingsRow("截图缓存", null, icon: IconKind.Folder);
        _folderRow = new SettingsRow("当前目录", _folderPermission, icon: IconKind.Folder);
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

        // A segmented control: one tinted track with the selected tab raised as a surface.
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        foreach (var (page, label) in new[] { (SettingsPage.General, "常规"), (SettingsPage.Folders, "文件夹跳转") })
        {
            var tab = new ToggleButton { Content = label, Padding = new Thickness(12, 0), Height = 28, MinHeight = 28,
                CornerRadius = new CornerRadius(6), BorderThickness = default, VerticalContentAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(tab, label);
            tab.Click += (_, _) => Navigate(page);
            Ui.Themed(tab, (t, c) =>
            {
                t.Resources["ToggleButtonBackground"] = Brushes.Transparent;
                t.Resources["ToggleButtonBackgroundPointerOver"] = Brush.Parse(c.Hover);
                t.Resources["ToggleButtonBackgroundPressed"] = Brush.Parse(c.Hover);
                t.Resources["ToggleButtonForeground"] = Brush.Parse(c.SecondaryText);
                t.Resources["ToggleButtonForegroundPointerOver"] = Brush.Parse(c.Text);
                t.Resources["ToggleButtonForegroundPressed"] = Brush.Parse(c.Text);
                foreach (var state in new[] { "", "PointerOver", "Pressed" })
                {
                    t.Resources["ToggleButtonBackgroundChecked" + state] = Brush.Parse(c.Surface);
                    t.Resources["ToggleButtonForegroundChecked" + state] = Brush.Parse(c.Text);
                }
            });
            _tabs[page] = tab; tabs.Children.Add(tab);
        }
        var tabStrip = Ui.Themed(new Border { Child = tabs, Padding = new Thickness(2), CornerRadius = new CornerRadius(DesignTokens.ControlRadius),
            BorderThickness = new Thickness(1) }, (b, c) => { b.Background = Brush.Parse(c.SecondarySurface); b.BorderBrush = Brush.Parse(c.Border); });
        var header = Ui.Header("设置", $"Tbox {Version}", tabStrip, out var brandBitmap);
        header.MaxWidth = 560; header.Margin = new Thickness(20, 14, 20, 6);
        Closed += (_, _) => brandBitmap.Dispose();

        _notice = Ui.Card(_status);
        _notice.Padding = new Thickness(12, 8);
        _notice.MaxWidth = 560;
        _notice.Margin = new Thickness(20, 0, 20, 14);
        _notice.IsVisible = false;
        BuildPages();

        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(_notice, Dock.Bottom);
        root.Children.Add(header);
        root.Children.Add(_notice);
        root.Children.Add(_body);
        Content = root;
        Navigate(SettingsPage.General);
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
        foreach (var (key, tab) in _tabs) tab.IsChecked = key == page;
        SetStatus("");
    }

    private void BuildPages()
    {
        var shortcuts = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var (label, key) in new[] { ("截图", ShortcutLabels.Capture), ("贴图", ShortcutLabels.Pin), ("显隐贴图", ShortcutLabels.Toggle) })
            shortcuts.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 2, 14, 2),
                Children = { Ui.Caption(label), Ui.Key(key) } });
        var shortcutRow = new SettingsRow("快捷键", Ui.Button("检查", () =>
        {
            Refresh();
            SetStatus(_controller.HasHotkeyConflict ? "快捷键被占用。请关闭占用它们的应用后重新启用工具。" : "当前未发现快捷键冲突。");
        }, "检查快捷键"), icon: IconKind.Keyboard);
        var cacheActions = Ui.Row(Ui.Button("打开", () => RunCommand(_controller.OpenCache), "打开目录"),
            Ui.Button("更换…", () => _controller.Run(_controller.ChooseCacheAsync), "更换目录…"));
        AddPage(SettingsPage.General,
            Ui.SectionLabel("外观与启动"),
            Ui.Group(new SettingsRow("主题", _theme, icon: IconKind.Theme), _startupRow,
                new SettingsRow("失败通知", _failureToggle, "功能失败时在屏幕角落提示", IconKind.Bell)),
            Ui.SectionLabel("截图与贴图"),
            Ui.Group(_captureRow, Indented(shortcutRow, shortcuts)),
            Ui.SectionLabel("截图缓存"),
            Ui.Group(Indented(_cacheRow, cacheActions)),
            Ui.SectionLabel("关于"),
            Ui.Group(new SettingsRow($"Tbox {Version}", Ui.Primary("检查更新", _controller.ShowUpdates), "公开下载，无需登录", IconKind.Update),
                new SettingsRow("帮助与反馈", Ui.Link("打开", () => _controller.Run(async () =>
                {
                    if (!await Launcher.LaunchUriAsync(new Uri("https://github.com/xsddpx/Tbox/issues")))
                        SetStatus("无法打开反馈页面。");
                }), "帮助与反馈"), "在 GitHub 提交问题或建议", IconKind.Help)));

        AddPage(SettingsPage.Folders,
            Ui.SectionLabel("在打开或保存面板中跳转"),
            Ui.Group(new SettingsRow("启用文件夹跳转", _folderToggle, $"在系统文件面板中按 {ShortcutLabels.Folder}，跳到最近使用的目录", IconKind.Jump),
                _folderRow));
    }

    // A row followed by extra content aligned under its title.
    private static StackPanel Indented(SettingsRow row, Control content) =>
        new() { Children = { row, new Border { Child = content, Margin = new Thickness(48, -4, 16, 12) } } };

    private void AddPage(SettingsPage key, params Control[] content)
    {
        var panel = new StackPanel { Spacing = 8, MaxWidth = 560, Margin = new Thickness(20, 6, 20, 20) };
        foreach (var child in content) panel.Children.Add(child);
        _pages[key] = new ScrollViewer
        {
            Content = panel,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
    }

    private static ToggleSwitch ModuleSwitch(string label)
    {
        var toggle = new ToggleSwitch { OnContent = null, OffContent = null, MinWidth = 0 };
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

        _captureRow.Description = _controller.HasHotkeyConflict ? "部分快捷键被占用。可从托盘面板启动工具。" : "截图、贴图、长截图、录屏和识字";
        _startupRow.Description = _controller.Startup.Message;
        _cancelStartup.IsVisible = _controller.Startup.RequiresApproval;
        _folderRow.Description = !_controller.HasFolderPermission
            ? "启用辅助功能权限后，Tbox 才能读取最近目录并跳转。"
            : string.IsNullOrWhiteSpace(_controller.FolderDirectory) ? "尚未读取目录，将使用用户主目录。" : _controller.FolderDirectory;
        _folderPermission.IsVisible = !_controller.HasFolderPermission;
        _cacheRow.Description = _controller.CacheDirectory;
        if (_controller.Panel is { IsVisible: true } panel) panel.Refresh();
    }
}
