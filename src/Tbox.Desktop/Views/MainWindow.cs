using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tbox.Core;
using Tbox.Desktop.Models;

namespace Tbox.Desktop.Views;

// The one Tbox window (spec ③): a sidebar with 截图 / 文件夹跳转 / 通用 / 关于 and a white content card.
// It replaces the tray panel, the settings window and the update window. Closing only hides it.
public sealed class MainWindow : Window
{
    public const int DefaultWidth = 800, DefaultHeight = 600, MinimumWidth = 680, MinimumHeight = 520, RecentCount = 4;
    private static readonly IBrush NavText = Brush.Parse("#3B3D49"), NavIcon = Brush.Parse("#6B6E7B"), Strong = Brush.Parse("#1A1B22");
    private readonly DesktopController _controller;
    private readonly Dictionary<MainPage, NavItem> _nav = [];
    private readonly Dictionary<MainPage, ScrollViewer> _pages = [];
    private readonly ContentControl _body = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    private readonly TextBlock _statusText = Ui.Description("");
    private readonly Border _status;
    private readonly List<Bitmap> _thumbnails = [];
    private readonly Bitmap _brand;
    private bool _updating;
    private bool _initialSizeApplied;

    // 截图
    private readonly ToggleSwitch _captureToggle = Switch("启用截图与贴图");
    private readonly Border _capturePermission;
    private readonly Button _capturePermissionButton;
    private readonly Border _captureOff = Ui.Note("截图与贴图已关闭，快捷键已释放。打开右上角开关即可恢复。");
    private readonly Control _captureTools;
    private readonly Button _captureButton;
    private readonly Dictionary<HotkeyAction, ShortcutRow> _shortcuts = [];
    private readonly SettingsLine _cache;
    private readonly Grid _recent = new() { ColumnDefinitions = new ColumnDefinitions("*,*,*,*"), ColumnSpacing = 10, Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBlock _recentEmpty = Ui.Description("截图后会显示在这里。");
    // 文件夹跳转
    private readonly ToggleSwitch _folderToggle = Switch("启用文件夹跳转");
    private readonly ToggleSwitch _folderBarToggle = Switch("显示文件对话框跳转栏");
    private readonly Border _folderPermission;
    private readonly Border _folderOff = Ui.Note("文件夹跳转已关闭，快捷键已释放。打开右上角开关即可恢复。");
    private readonly Control _folderHero;
    private readonly SettingsLine _folderTarget;
    // 通用
    private readonly ToggleSwitch _startupToggle = Switch("开机时启动");
    private readonly ToggleSwitch _failureToggle = Switch("失败时提示");
    private readonly Button _cancelStartup;
    private readonly SettingsLine _startup;
    // 关于
    private readonly UpdateCard _update;
    private readonly ModelServicesPage _models;

    internal static string Version => typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "";
    internal MainPage CurrentPage { get; private set; }
    internal ScrollViewer CurrentPageView => _pages[CurrentPage];
    internal IReadOnlyList<string> RecentPaths { get; private set; } = [];

    public MainWindow(DesktopController controller)
    {
        _controller = controller;
        Ui.Configure(this, "Tbox", DefaultWidth, DefaultHeight);
        MinWidth = MinimumWidth; MinHeight = MinimumHeight;
        // The sidebar runs to the top edge and the system caption buttons sit over the window background, as in the spec.
        ExtendClientAreaToDecorationsHint = true; ExtendClientAreaTitleBarHeightHint = 34;
        using (var asset = AssetLoader.Open(new Uri("avares://Tbox.Desktop/Assets/StoreLogo.png"))) _brand = new Bitmap(asset);

        _capturePermissionButton = Ui.Primary("去授权", controller.RequestPermission, "允许屏幕录制");
        _capturePermission = Ui.WarningBar(IconKind.Capture, "需要屏幕录制权限",
            OperatingSystem.IsMacOS() ? "在“系统设置 › 隐私与安全性”中允许 Tbox 后即可截图" : "在系统设置中允许 Tbox 截取屏幕后即可截图", _capturePermissionButton);
        _captureButton = Ui.Primary("截图", () => controller.Run(controller.CaptureAsync), $"截图 · {ShortcutLabels.Capture}");
        _captureButton.MinHeight = 36; _captureButton.Padding = new Thickness(14, 0, 10, 0);
        foreach (var action in ShortcutPolicy.Actions) _shortcuts[action] = new ShortcutRow(controller, action);
        controller.ShortcutsChanged += RefreshShortcutLabels;
        _cache = new SettingsLine("保存位置", "", Ui.Button("更改…", () => controller.Run(controller.ChooseCacheAsync), "更改保存位置"));
        _captureTools = CaptureTools();
        var folderButton = Ui.Primary("去授权", controller.RequestFolderPermission, "允许辅助功能");
        _folderPermission = Ui.WarningBar(IconKind.Warn, "需要辅助功能权限", "允许后 Tbox 才能读取访达当前目录并跳转", folderButton);
        _folderHero = FolderHero();
        _folderTarget = new SettingsLine("当前会跳到", "");
        _cancelStartup = Ui.Button("取消申请", () => controller.Run(() => controller.SetStartupAsync(false)), "取消开机启动申请");
        _startup = new SettingsLine("开机时启动", "启动后只在托盘运行，不打开窗口", Ui.Row(_cancelStartup, _startupToggle));
        _update = new UpdateCard(controller);
        _models = new ModelServicesPage(controller.Models, () => this);
        _update.AvailabilityChanged += RefreshDots;
        Wire();
        BuildPages();
        RefreshShortcutLabels();

        var statusIcon = new LineIcon(IconKind.Info, brush: Brush.Parse(DesignTokens.Hint)) { Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 0, 0) };
        _status = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 10), Margin = new Thickness(26, 0, 26, 16),
            Background = Brush.Parse("#F7F6FB"), IsVisible = false,
            Child = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 8, Children = { statusIcon, Ui.Column(_statusText, 1) } } };
        DockPanel.SetDock(_status, Dock.Bottom);
        var card = new Border
        {
            Background = Brushes.White, CornerRadius = new CornerRadius(DesignTokens.PanelRadius), Margin = new Thickness(0, 34, 10, 10), ClipToBounds = true,
            BoxShadow = BoxShadows.Parse("0 0 0 1 #0D101220, 0 10 30 0 #0F101220"),
            Child = new DockPanel { Children = { _status, _body } }
        };
        var root = new Grid { ColumnDefinitions = new ColumnDefinitions($"{208},*"), Children = { Sidebar(), Ui.Column(card, 1) } };
        Content = root;
        Navigate(MainPage.Capture);
        Refresh();

        AddHandler(PointerPressedEvent, (_, args) =>
        {
            var source = args.Source as Visual;
            foreach (var row in _shortcuts.Values)
                if (source != row.Recorder && source?.GetVisualAncestors().Contains(row.Recorder) != true)
                    row.Recorder.Stop();
        }, RoutingStrategies.Tunnel);
        Deactivated += (_, _) => StopRecordingShortcut();
        Closing += (_, args) =>
        {
            StopRecordingShortcut();
            if (!controller.IsQuitting && args.CloseReason is WindowCloseReason.WindowClosing or WindowCloseReason.Undefined)
            {
                args.Cancel = true;
                Hide();
            }
        };
        Closed += (_, _) => { _models.Dispose(); _update.Release(); ReleaseThumbnails(); _brand.Dispose(); };
        PropertyChanged += (_, args) => { if (args.Property == IsVisibleProperty && !IsVisible) _models.Leave(); };
        Activated += (_, _) =>
        {
            Refresh();
            // A native modal dialog may activate its owner during a synchronous input call.
            if (_initialSizeApplied) Dispatcher.UIThread.Post(() =>
            {
                if (IsActive && !controller.IsQuitting) controller.Run(controller.RefreshStartupAsync);
            }, DispatcherPriority.Background);
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
    }

    internal void Navigate(MainPage page)
    {
        StopRecordingShortcut();
        if (CurrentPage == MainPage.Models && page != MainPage.Models) _models.Leave();
        CurrentPage = page;
        _body.Content = _pages[page];
        foreach (var (key, item) in _nav) item.Selected = key == page;
        SetStatus("");
        if (page == MainPage.Models) _controller.Run(_models.OpenAsync);
    }

    private void StopRecordingShortcut()
    {
        foreach (var row in _shortcuts.Values) row.Recorder.Stop();
    }

    public void SetStatus(string message)
    {
        _statusText.Text = message;
        _status.IsVisible = !string.IsNullOrWhiteSpace(message);
    }

    public void Refresh()
    {
        var settings = _controller.Settings;
        var capture = settings.ScreenPasteEnabled;
        var folders = settings.FolderJumpEnabled;
        _updating = true;
        _captureToggle.IsChecked = capture; _folderToggle.IsChecked = folders;
        _folderBarToggle.IsChecked = settings.ShowFolderJumpBar;
        _folderBarToggle.IsEnabled = folders;
        _startupToggle.IsChecked = _controller.Startup.Enabled; _failureToggle.IsChecked = settings.ShowFailureNotifications;
        _updating = false;

        // 截图: the permission bar owns the page's single primary button while it is shown.
        var needsPermission = capture && _controller.CapturePermissionNeeded;
        _capturePermission.IsVisible = needsPermission;
        _capturePermissionButton.Content = _controller.ScreenPermissionRequested ? "打开设置" : "去授权";
        AutomationProperties.SetName(_capturePermissionButton, _controller.ScreenPermissionRequested ? "打开屏幕录制设置" : "允许屏幕录制");
        SetCaptureButton(primary: !needsPermission);
        _captureOff.IsVisible = !capture;
        _captureTools.IsEnabled = capture; _captureTools.Opacity = capture ? 1 : .45;
        foreach (var row in _shortcuts.Values) row.Refresh();
        _cache.SetDescription(_controller.CacheDirectory);

        // 文件夹跳转
        var folderPermission = _controller.HasFolderPermission;
        _folderPermission.IsVisible = folders && !folderPermission;
        _folderOff.IsVisible = !folders;
        _folderHero.Opacity = folders ? 1 : .45;
        _folderTarget.SetDescription(!folderPermission ? "授权后显示"
            : string.IsNullOrWhiteSpace(_controller.FolderDirectory) ? "尚未读取目录，将使用用户主目录" : _controller.FolderDirectory);
        _folderTarget.Trailing = !folders ? Ui.Status("已关闭", DesignTokens.Hint)
            : folderPermission ? Ui.Status("可用", DesignTokens.Ready) : Ui.Status("未授权", DesignTokens.Error);

        // 通用
        _startup.SetDescription(_controller.Startup.Message ?? "启动后只在托盘运行，不打开窗口");
        _cancelStartup.IsVisible = _controller.Startup.RequiresApproval;

        RefreshDots();
        RefreshRecent();
    }

    // Red = hotkey conflict, yellow = permission needed or an update is available.
    private void RefreshDots()
    {
        var capture = _controller.Settings.ScreenPasteEnabled;
        _nav[MainPage.Capture].Dot = capture && _controller.HasConflictIn(capture: true) ? DesignTokens.Recording
            : capture && _controller.CapturePermissionNeeded ? DesignTokens.AttentionDot : null;
        var folders = _controller.Settings.FolderJumpEnabled;
        _nav[MainPage.Folders].Dot = folders && _controller.HasConflictIn(capture: false) ? DesignTokens.Recording
            : folders && !_controller.HasFolderPermission ? DesignTokens.AttentionDot : null;
        _nav[MainPage.About].Dot = _update.UpdateAvailable ? DesignTokens.AttentionDot : null;
    }

    private void Wire()
    {
        void On(ToggleSwitch toggle, Func<bool, Task> change) =>
            toggle.IsCheckedChanged += (_, _) => { if (!_updating) _controller.Run(() => change(toggle.IsChecked == true)); };
        On(_captureToggle, enabled => _controller.SetModulesAsync(enabled, null));
        On(_folderToggle, enabled => _controller.SetModulesAsync(null, enabled));
        On(_folderBarToggle, _controller.SetFolderJumpBarAsync);
        On(_startupToggle, _controller.SetStartupAsync);
        On(_failureToggle, _controller.SetFailureNotificationsAsync);
    }

    private Control Sidebar()
    {
        var name = new TextBlock { Text = "Tbox", FontSize = 15, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(8, 0, 0, 18), IsHitTestVisible = false,
            Children = { new Image { Source = _brand, Width = 28, Height = 28, IsHitTestVisible = false }, name } };
        var items = new StackPanel { Spacing = 2 };
        void Group(string title) => items.Children.Add(new TextBlock { Text = title, FontSize = 11.5, FontWeight = FontWeight.SemiBold,
            Foreground = Brush.Parse(DesignTokens.Hint), Margin = new Thickness(10, 12, 0, 6), IsHitTestVisible = false });
        void Item(MainPage page, IconKind icon, string title) { var item = new NavItem(icon, title, () => Navigate(page)); _nav[page] = item; items.Children.Add(item); }
        Group("工具"); Item(MainPage.Capture, IconKind.Capture, "截图"); Item(MainPage.Folders, IconKind.Jump, "文件夹跳转");
        Group("设置"); Item(MainPage.General, IconKind.Sliders, "通用"); Item(MainPage.Models, IconKind.Model, "模型服务"); Item(MainPage.About, IconKind.Update, "关于");
        var version = new TextBlock { Text = $"Tbox {Version}", FontSize = 12, Foreground = Brush.Parse(DesignTokens.Hint), Margin = new Thickness(10, 0, 0, 0) };
        DockPanel.SetDock(brand, Dock.Top); DockPanel.SetDock(version, Dock.Bottom);
        // macOS draws the traffic lights over the top-left corner, so the brand moves below them.
        return new DockPanel { Margin = new Thickness(12, OperatingSystem.IsMacOS() ? 40 : 14, 12, 14), Children = { brand, version, items } };
    }

    private void BuildPages()
    {
        var openCache = Ui.Link(OperatingSystem.IsMacOS() ? "在访达中显示" : "打开文件夹", () => RunCommand(_controller.OpenCache));
        _recentEmpty.Margin = new Thickness(0, 6, 0, 0);
        AddPage(MainPage.Capture, PageHeader(IconKind.Capture, "截图", "截图后可标注、贴图、识别文字", _captureToggle),
            Spaced(_capturePermission, 14), Spaced(_captureOff, 16), _captureTools,
            Ui.GroupHeader("快捷键"), Ui.Rows(_shortcuts[HotkeyAction.Capture], _shortcuts[HotkeyAction.Pin], _shortcuts[HotkeyAction.Toggle]),
            Ui.GroupHeader("设置"), _cache,
            Ui.GroupHeader("最近截图", openCache), _recent, _recentEmpty);

        AddPage(MainPage.Folders, PageHeader(IconKind.Jump, "文件夹跳转", "打开或保存文件时，一键跳到正在用的文件夹", _folderToggle),
            Spaced(_folderPermission, 14), Spaced(_folderOff, 16), _folderHero,
            Ui.GroupHeader("状态"), Ui.Rows(_shortcuts[HotkeyAction.Folder], _folderTarget,
                new SettingsLine("显示跳转栏", "在系统文件对话框旁显示已打开的文件夹、搜索、收藏和最近跳转", _folderBarToggle)));

        AddPage(MainPage.General, PageHeader(IconKind.Sliders, "通用", "启动与提示", null),
            Spaced(Ui.GroupHeader("启动"), 8), _startup,
            Ui.GroupHeader("提示"), new SettingsLine("失败时提示", "截图或识别失败时，在屏幕角落说明原因", _failureToggle),
            Spaced(Ui.Note("关闭窗口后 Tbox 继续在托盘运行，快捷键照常可用。右键托盘图标可退出。"), 18));

        AddPage(MainPage.Models, PageHeader(IconKind.Model, "模型服务", "统一管理连接与模型，供未来工具复用", null), _models);

        var feedback = new SurfaceButton("反馈问题", () => _controller.Run(async () =>
        {
            if (!await Launcher.LaunchUriAsync(new Uri("https://github.com/xsddpx/Tbox/issues"))) SetStatus("无法打开反馈页面。");
        }));
        feedback.Content = new SettingsLine("反馈问题", "在 GitHub 提交问题或建议", new LineIcon(IconKind.Chevron, brush: Brush.Parse(DesignTokens.Hint)) { Width = 16, Height = 16 });
        Hover(feedback, "#F7F6FB", 8);
        AddPage(MainPage.About, PageHeader(IconKind.Update, "关于", "版本、更新与反馈", null),
            Spaced(_update, 16), Ui.GroupHeader("帮助"), feedback);
    }

    private void AddPage(MainPage key, params Control[] content)
    {
        var panel = new StackPanel { Margin = new Thickness(26, 20, 26, 20) };
        foreach (var child in content) panel.Children.Add(child);
        _pages[key] = new ScrollViewer
        {
            Content = panel, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
    }

    private static T Spaced<T>(T control, double top) where T : Control { control.Margin = new Thickness(0, top, 0, 0); return control; }

    private static Grid PageHeader(IconKind icon, string title, string description, ToggleSwitch? toggle)
    {
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children =
        {
            new TextBlock { Text = title, FontSize = DesignTokens.PageTitle, LineHeight = 26, FontWeight = FontWeight.SemiBold, Foreground = Strong },
            Ui.Description(description)
        } };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 14,
            Children = { Ui.IconChip(icon, ToolTone.Violet, 40, 20), Ui.Column(titles, 1) } };
        if (toggle is not null) { toggle.VerticalAlignment = VerticalAlignment.Center; header.Children.Add(Ui.Column(toggle, 2)); }
        return header;
    }

    private Control CaptureTools()
    {
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children =
        {
            new TextBlock { Text = "开始截图", FontSize = DesignTokens.SectionTitle, FontWeight = FontWeight.SemiBold, LineHeight = 22, Foreground = Strong },
            Spaced(Ui.Description("单击窗口或拖动框选，按 R 回到上次区域"), 2),
            Spaced(_captureButton, 14)
        } };
        _captureButton.HorizontalAlignment = HorizontalAlignment.Left;
        var hero = Ui.Hero(new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12, Children =
        {
            text, Ui.Column(CaptureIllustration(), 1)
        } });
        hero.Padding = new Thickness(20, 16, 16, 16);

        var ocrMenu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        ocrMenu.Items.Add(MenuItem("识别剪贴板图片", () => _controller.Run(_controller.OcrClipboardAsync)));
        ocrMenu.Items.Add(MenuItem("选择图片…", () => _controller.Run(_controller.OpenImageForOcrAsync)));
        SurfaceButton? ocr = null;
        var cards = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*,*"), ColumnSpacing = 10, Margin = new Thickness(0, 12, 0, 0), Children =
        {
            (_pinFeature = Feature("贴出剪贴板", IconKind.Pin, ToolTone.Amber, ShortcutLabels.Pin, () => _controller.Run(_controller.PinClipboardAsync))),
            Ui.Column(Feature("长截图", IconKind.Long, ToolTone.Blue, null, () => _controller.Run(_controller.LongCaptureAsync)), 1),
            Ui.Column(Feature("录屏", IconKind.Record, ToolTone.Red, null, () => _controller.Run(_controller.RecordRegionAsync)), 2),
            Ui.Column(ocr = Feature("识别文字", IconKind.Ocr, ToolTone.Green, null, () => ocrMenu.ShowAt(ocr!)), 3)
        } };
        return new StackPanel { Margin = new Thickness(0, 16, 0, 0), Children = { hero, cards } };
    }

    private void SetCaptureButton(bool primary)
    {
        if (primary) _captureButton.Classes.Add("accent"); else _captureButton.Classes.Remove("accent");
        _captureButton.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children =
        {
            new TextBlock { Text = "截图", VerticalAlignment = VerticalAlignment.Center }, Ui.Key(ShortcutLabels.Capture, onAccent: primary)
        } };
    }

    private static void SetFeatureShortcut(SurfaceButton button, string title, string shortcut)
    {
        var tip = $"{title} · {shortcut}";
        AutomationProperties.SetName(button, tip); ToolTip.SetTip(button, tip);
        if (button.Content is Border { Child: Grid layout } && layout.Children.OfType<Border>().FirstOrDefault(b => b.Child is TextBlock) is { Child: TextBlock key })
            key.Text = shortcut;
    }

    private static SurfaceButton Feature(string title, IconKind icon, ToolTone tone, string? shortcut, Action action)
    {
        var button = new SurfaceButton(shortcut is null ? title : $"{title} · {shortcut}", action) { Height = 62, CornerRadius = new CornerRadius(12) };
        var layout = new Grid { Margin = new Thickness(12, 10), RowDefinitions = new RowDefinitions("Auto,*,Auto"), Children =
        {
            Ui.IconChip(icon, tone),
            new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeight.Medium, VerticalAlignment = VerticalAlignment.Bottom,
                TextTrimming = TextTrimming.CharacterEllipsis, [Grid.RowProperty] = 2 }
        } };
        ((Control)layout.Children[0]).HorizontalAlignment = HorizontalAlignment.Left;
        if (shortcut is not null) { var key = Ui.Key(shortcut); key.HorizontalAlignment = HorizontalAlignment.Right; key.VerticalAlignment = VerticalAlignment.Top; key.Margin = new Thickness(0, 2, 0, 0); layout.Children.Add(key); }
        button.Content = new Border { CornerRadius = new CornerRadius(12), BorderBrush = Brush.Parse("#EEEDF3"), BorderThickness = new Thickness(1), Child = layout };
        Hover(button, "#F7F6FB", 12);
        return button;
    }

    private Control FolderHero()
    {
        var mac = OperatingSystem.IsMacOS();
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children =
        {
            _folderTitle,
            Spaced(Ui.Description($"在任意软件的打开 / 保存对话框里，直接跳到{(mac ? "访达" : "资源管理器")}里最近打开的文件夹。"), 2)
        } };
        var hero = Ui.Hero(new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12, Children = { text, Ui.Column(FolderIllustration(), 1) } });
        hero.Padding = new Thickness(20, 16, 16, 16); hero.Margin = new Thickness(0, 16, 0, 0);
        return hero;
    }

    // Two small decorative scenes for the hero banners, drawn so they stay sharp at any scale.
    private static Border Scene(Control content) => new()
    {
        Width = 150, Height = 80, CornerRadius = new CornerRadius(8), ClipToBounds = true, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center,
        BoxShadow = BoxShadows.Parse("0 6 18 0 #24503CA0"), Child = content,
        Background = new LinearGradientBrush { StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(.4, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.Parse("#EBEDF6"), 0), new GradientStop(Color.Parse("#DCE1EF"), 1) } }
    };
    private static Rectangle Box(double x, double y, double w, double h, string fill, double radius = 2) =>
        new() { Width = w, Height = h, Fill = Brush.Parse(fill), RadiusX = radius, RadiusY = radius, [Canvas.LeftProperty] = x, [Canvas.TopProperty] = y };

    private static Control CaptureIllustration()
    {
        var canvas = new Canvas { Children =
        {
            Box(13, 10, 124, 80, "#FFFFFF", 6), Box(13, 10, 124, 10, "#F3F4F8", 0), Box(22, 26, 50, 6, "#E6E8EF"), Box(22, 37, 76, 4, "#EEF0F5"),
            Box(22, 50, 50, 26, "#EEEAFE", 3), Box(78, 50, 50, 26, "#E6F0FF", 3),
            // Dim everything outside the selection.
            Box(0, 0, 150, 34, "#4D1C1838", 0), Box(0, 66, 150, 14, "#4D1C1838", 0), Box(0, 34, 46, 32, "#4D1C1838", 0), Box(112, 34, 38, 32, "#4D1C1838", 0),
            new Rectangle { Width = 66, Height = 32, Stroke = Brush.Parse(DesignTokens.Accent), StrokeThickness = 1.5, [Canvas.LeftProperty] = 46, [Canvas.TopProperty] = 34 },
            new Border { Background = Brush.Parse(DesignTokens.Accent), CornerRadius = new CornerRadius(4), Padding = new Thickness(5, 0), [Canvas.LeftProperty] = 46, [Canvas.TopProperty] = 17,
                Child = new TextBlock { Text = "430 × 250", FontSize = 9, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White, LineHeight = 15 } }
        } };
        foreach (var (x, y) in new[] { (43.0, 31.0), (109.0, 31.0), (43.0, 63.0), (109.0, 63.0) })
            canvas.Children.Add(new Rectangle { Width = 6, Height = 6, Fill = Brushes.White, Stroke = Brush.Parse(DesignTokens.Accent), StrokeThickness = 1.5,
                [Canvas.LeftProperty] = x, [Canvas.TopProperty] = y });
        return Scene(canvas);
    }

    private Control FolderIllustration()
    {
        var path = OperatingSystem.IsMacOS() ? "~/Projects/Tbox/docs" : @"D:\Projects\Tbox\docs";
        var canvas = new Canvas { Children =
        {
            Box(13, 10, 124, 80, "#FFFFFF", 6),
            new Border { Width = 110, Height = 14, CornerRadius = new CornerRadius(4), Background = Brush.Parse("#F1EDFF"), BorderBrush = Brush.Parse("#CDBFFA"),
                BorderThickness = new Thickness(1), Padding = new Thickness(6, 0), [Canvas.LeftProperty] = 20, [Canvas.TopProperty] = 17,
                Child = new TextBlock { Text = path, FontSize = 8.5, FontWeight = FontWeight.SemiBold, Foreground = Brush.Parse("#5A39D6"), VerticalAlignment = VerticalAlignment.Center } }
        } };
        for (var row = 0; row < 2; row++)
        for (var column = 0; column < 4; column++)
            canvas.Children.Add(Box(22 + column * 28, 38 + row * 20, 18, 14, (row + column) % 3 == 0 ? "#C9D3E6" : "#FFD27A"));
        var caps = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, [Canvas.LeftProperty] = 96, [Canvas.TopProperty] = 58 };
        _folderCaps.Add(caps);
        canvas.Children.Add(caps);
        return Scene(canvas);
    }

    // Shortcut text shown outside the shortcut rows; rebuilt when a shortcut changes.
    private readonly List<StackPanel> _folderCaps = [];
    private readonly TextBlock _folderTitle = new() { FontSize = DesignTokens.SectionTitle, FontWeight = FontWeight.SemiBold, LineHeight = 22, Foreground = Strong };
    private SurfaceButton? _pinFeature;

    private void RefreshShortcutLabels()
    {
        SetCaptureButton(_captureButton.Classes.Contains("accent"));
        ToolTip.SetTip(_captureButton, $"截图 · {ShortcutLabels.Capture}"); AutomationProperties.SetName(_captureButton, $"截图 · {ShortcutLabels.Capture}");
        if (_pinFeature is not null) SetFeatureShortcut(_pinFeature, "贴出剪贴板", ShortcutLabels.Pin);
        var folder = ShortcutLabels.Caps(HotkeyAction.Folder);
        _folderTitle.Text = $"按 {string.Join(" + ", folder)}";
        foreach (var caps in _folderCaps)
        {
            caps.Children.Clear();
            foreach (var key in folder)
                caps.Children.Add(new Border { Height = 17, MinWidth = 17, Padding = new Thickness(4, 0), CornerRadius = new CornerRadius(4), Background = Brushes.White,
                    BoxShadow = BoxShadows.Parse("0 1 0 0 #33000000, 0 0 0 1 #14000000"),
                    Child = new TextBlock { Text = key, FontSize = 9, FontWeight = FontWeight.Bold, Foreground = Strong, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center } });
        }
        foreach (var row in _shortcuts.Values) row.Refresh();
    }

    private void RefreshRecent()
    {
        IReadOnlyList<FileInfo> files;
        try { files = new CaptureCache(_controller.CacheDirectory).GetRecentFiles(RecentCount); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { files = []; }
        var paths = files.Select(file => file.FullName).ToArray();
        if (paths.SequenceEqual(RecentPaths) && _recent.Children.Count == paths.Length) return;
        _recent.Children.Clear(); ReleaseThumbnails();
        RecentPaths = paths;
        for (var index = 0; index < files.Count; index++)
        {
            var file = files[index];
            Bitmap? bitmap = null;
            try { using var stream = File.OpenRead(file.FullName); bitmap = Bitmap.DecodeToWidth(stream, 240); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException) { }
            if (bitmap is null) continue;
            _thumbnails.Add(bitmap);
            _recent.Children.Add(Ui.Column(RecentThumbnail(file, bitmap), index));
        }
        _recent.IsVisible = _recent.Children.Count > 0;
        _recentEmpty.IsVisible = !_recent.IsVisible;
    }

    private Control RecentThumbnail(FileInfo file, Bitmap bitmap)
    {
        var path = file.FullName;
        var label = RecentCaptureLabel.Format(file.LastWriteTime, DateTime.Now);
        var time = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(153, 12, 14, 24)), CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 0), Margin = new Thickness(6), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom,
            Child = new TextBlock { Text = label, FontSize = 10, FontWeight = FontWeight.SemiBold, LineHeight = 16, Foreground = Brushes.White }
        };
        var pin = OverlayButton(IconKind.Pin, $"贴到屏幕 · {label}", () => _controller.Run(() => _controller.PinFileAsync(path)));
        var copy = OverlayButton(IconKind.Copy, $"复制截图 · {label}", () => _controller.Run(() => _controller.CopyFileAsync(path)));
        var actions = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(87, 12, 14, 24)), IsVisible = false,
            Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center, Children = { pin, copy } }
        };
        var frame = new Border
        {
            CornerRadius = new CornerRadius(10), ClipToBounds = true, BorderThickness = new Thickness(1), BorderBrush = Brush.Parse("#0F101220"),
            Child = new Grid { Children = { new Image { Source = bitmap, Stretch = Stretch.UniformToFill }, time, actions } }
        };
        var card = new SurfaceButton($"打开截图 · {label}", () => _controller.Run(() => _controller.OpenCachedAsync(path))) { Content = frame, CornerRadius = new CornerRadius(10) };
        ToolTip.SetTip(card, $"{label} · {file.Name}");
        card.Resources["ButtonBackgroundPointerOver"] = Brushes.Transparent; card.Resources["ButtonBackgroundPressed"] = Brushes.Transparent;
        // Thumbnails keep 16:9 at any card width.
        card.SizeChanged += (_, e) => { var height = Math.Round(e.NewSize.Width * 9 / 16); if (double.IsNaN(card.Height) || Math.Abs(card.Height - height) > .5) card.Height = height; };
        void Show(bool visible) { actions.IsVisible = visible; frame.BorderThickness = new Thickness(visible ? 2 : 1); frame.BorderBrush = visible ? Brush.Parse(DesignTokens.Accent) : Brush.Parse("#0F101220"); }
        card.PointerEntered += (_, _) => Show(true);
        card.PointerExited += (_, _) => Show(false);
        // Keyboard users reach the two actions directly, so focus also reveals them.
        foreach (var button in new[] { card, pin, copy }) { button.GotFocus += (_, _) => Show(true); button.LostFocus += (_, _) => Show(card.IsPointerOver); }
        return card;
    }

    private static Button OverlayButton(IconKind icon, string label, Action action)
    {
        var button = Ui.Button(label, action, label);
        button.Content = new LineIcon(icon, brush: Strong) { Width = 16, Height = 16 };
        button.Padding = new Thickness(6); button.MinHeight = 0; button.Width = button.Height = 28; button.MinWidth = 28;
        button.CornerRadius = new CornerRadius(7); button.BorderThickness = default;
        button.Background = Brushes.White;
        button.Resources["ButtonBackgroundPointerOver"] = Brush.Parse("#F7F6FB");
        button.Resources["ButtonBackgroundPressed"] = Brush.Parse("#EFEEF4");
        return button;
    }

    private void ReleaseThumbnails()
    {
        foreach (var bitmap in _thumbnails) bitmap.Dispose();
        _thumbnails.Clear();
    }

    private static void Hover(Button button, string color, double radius)
    {
        button.CornerRadius = new CornerRadius(radius);
        button.Resources["ButtonBackgroundPointerOver"] = Brush.Parse(color);
        button.Resources["ButtonBackgroundPressed"] = Brush.Parse("#EFEEF4");
        foreach (var state in new[] { "", "PointerOver", "Pressed", "Disabled" }) button.Resources["ButtonForeground" + state] = Strong;
        button.Resources["ButtonBackgroundDisabled"] = Brushes.Transparent;
    }

    private static ToggleSwitch Switch(string label)
    {
        var toggle = new ToggleSwitch { OnContent = null, OffContent = null, MinWidth = 0 };
        AutomationProperties.SetName(toggle, label);
        return toggle;
    }

    private static MenuItem MenuItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    private void RunCommand(Action action)
    {
        SetStatus("");
        _controller.Run(() => { action(); return Task.CompletedTask; });
    }

    // Sidebar entry: 38 px, radius 10. The selected item is a raised white surface with an amethyst icon.
    private sealed class NavItem : Border
    {
        private readonly LineIcon _icon;
        private readonly TextBlock _text;
        private readonly Border _dot = new() { Width = 7, Height = 7, CornerRadius = new CornerRadius(3.5), IsVisible = false, VerticalAlignment = VerticalAlignment.Center };
        private readonly SurfaceButton _button;
        public NavItem(IconKind icon, string title, Action select)
        {
            CornerRadius = new CornerRadius(10);
            _icon = new LineIcon(icon, brush: NavIcon) { Width = 18, Height = 18, VerticalAlignment = VerticalAlignment.Center };
            _text = new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center, Foreground = NavText };
            _button = new SurfaceButton(title, select) { Height = 38, Content = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 12, Margin = new Thickness(12, 0),
                Children = { _icon, Ui.Column(_text, 1), Ui.Column(_dot, 2) }
            } };
            Hover(_button, "#99FFFFFF", 10);
            Child = _button;
        }
        public bool Selected
        {
            set
            {
                Background = value ? Brushes.White : Brushes.Transparent;
                BoxShadow = value ? BoxShadows.Parse("0 1 2 0 #0F101220, 0 6 16 0 #0F101220") : default;
                _icon.Stroke = value ? Brush.Parse(DesignTokens.Accent) : NavIcon;
                _text.FontWeight = value ? FontWeight.SemiBold : FontWeight.Normal;
                _text.Foreground = value ? Strong : NavText;
                _button.Resources["ButtonBackgroundPointerOver"] = value ? Brushes.Transparent : Brush.Parse("#99FFFFFF");
            }
        }
        public string? Dot
        {
            set { _dot.IsVisible = value is not null; if (value is not null) _dot.Background = Brush.Parse(value); }
        }
    }
}

// A 46 px settings row: name, one-line description and a trailing control or status.
internal class SettingsLine : Grid
{
    private readonly TextBlock _description = Ui.Description("");
    private Control? _trailing;
    public SettingsLine(string title, string description, Control? trailing = null)
    {
        MinHeight = 46; ColumnSpacing = 14; ColumnDefinitions = new ColumnDefinitions("*,Auto");
        _description.FontSize = DesignTokens.Caption; _description.LineHeight = 16; _description.TextWrapping = TextWrapping.NoWrap;
        _description.TextTrimming = TextTrimming.CharacterEllipsis;
        Children.Add(new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 6), Children =
        {
            new TextBlock { Text = title, FontSize = DesignTokens.Body, Foreground = Brush.Parse("#1A1B22") }, _description
        } });
        SetDescription(description);
        Trailing = trailing;
    }
    public Control? Trailing
    {
        get => _trailing;
        set
        {
            if (_trailing is not null) Children.Remove(_trailing);
            _trailing = value;
            if (value is null) return;
            value.VerticalAlignment = VerticalAlignment.Center; SetColumn(value, 1); Children.Add(value);
        }
    }
    public string Description => _description.Text ?? "";
    public void SetDescription(string text, bool error = false)
    {
        _description.Text = text; _description.IsVisible = text.Length > 0;
        _description.Foreground = Brush.Parse(error ? DesignTokens.Error : "#5F6270");
        ToolTip.SetTip(_description, text.Length > 0 ? text : null);
    }
}
