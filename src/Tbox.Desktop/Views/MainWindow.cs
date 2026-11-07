using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Tbox.Desktop.Views;

public sealed class MainWindow : Window
{
    private readonly DesktopController _controller;
    private readonly TextBlock _status = Ui.Text("正在启动…", 12);
    private readonly TextBlock _permission = Ui.Text("", 12);
    private readonly TextBlock _cache = Ui.Text("", 12);

    public MainWindow(DesktopController controller)
    {
        _controller = controller;
        Ui.Configure(this, "Tbox", 760, 660);
        MinWidth = 520; MinHeight = 480;
        var heading = Ui.Text("随手捕捉，留在眼前。", 28);
        heading.FontWeight = FontWeight.SemiBold;
        var theme = new ComboBox { ItemsSource = new[] { "跟随系统", "浅色", "深色" }, SelectedIndex = 0, MinWidth = 120 };
        theme.SelectionChanged += (_, _) => controller.Run(() => controller.ChangeThemeAsync(theme.SelectedIndex));
        var panel = new StackPanel { Spacing = 8, Margin = new Thickness(32), MaxWidth = 900 };
        panel.Children.Add(Ui.Text("TBOX  /  桌面工具", 12));
        panel.Children.Add(heading);
        panel.Children.Add(Ui.Text("区域截图、悬浮贴图与离线文字识别。", 14));
        panel.Children.Add(new Border { Height = 12 });
        var capture = Ui.Button("开始截图", () => controller.Run(controller.CaptureAsync), $"区域截图 · {ShortcutLabels.Capture}");
        capture.Classes.Add("accent"); capture.MinHeight = 40;
        var permissionButton = Ui.Button("允许屏幕录制", controller.RequestPermission);
        permissionButton.IsVisible = OperatingSystem.IsMacOS();
        panel.Children.Add(Ui.Card(new StackPanel { Spacing = 12, Children =
        {
            Ui.Text("截图与贴图", 16),
            Ui.Text("拖动选择区域，复制、保存、固定到桌面或识别文字。", 12),
            Ui.Row(capture, Ui.Button("剪贴板贴图", () => controller.Run(controller.PinClipboardAsync)),
                Ui.Button("显示 / 隐藏贴图", controller.TogglePins)),
            Ui.Text($"{ShortcutLabels.Capture} 截图    {ShortcutLabels.Pin} 贴图    {ShortcutLabels.Toggle} 显隐", 12)
        }}));
        panel.Children.Add(Ui.Card(new StackPanel { Spacing = 12, Children =
        {
            Ui.Text("文字识别", 16),
            Ui.Text("从图片读取中英文，在本机处理。识别结果可编辑，不保存文字历史。", 12),
            Ui.Row(Ui.Button("选择图片…", () => controller.Run(controller.OpenImageForOcrAsync)),
                Ui.Button("识别剪贴板图片", () => controller.Run(controller.OcrClipboardAsync)))
        }}));
        panel.Children.Add(Ui.Card(new StackPanel { Spacing = 12, Children =
        {
            Ui.Text("常规", 16),
            Ui.Row(Ui.Text("外观"), theme),
            _permission,
            Ui.Row(permissionButton,
                Ui.Button("打开缓存", controller.OpenCache),
                Ui.Button("更换缓存…", () => controller.Run(controller.ChooseCacheAsync))),
            _cache
        }}));
        panel.Children.Add(_status);
        panel.Children.Add(Ui.Text($"Tbox {typeof(MainWindow).Assembly.GetName().Version?.ToString(3)} · 跨平台预览版 · 关闭窗口后继续在托盘或菜单栏运行", 12));
        Content = new ScrollViewer { Content = panel, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Closing += (_, args) =>
        {
            // A system-requested restart or Quit must terminate the process, not merely hide it.
            if (!controller.IsQuitting && args.CloseReason is WindowCloseReason.WindowClosing or WindowCloseReason.Undefined)
            { args.Cancel = true; Hide(); }
        };
        Activated += (_, _) => Refresh();
        controller.ThemeLoaded += index => theme.SelectedIndex = index;
    }

    public void SetStatus(string message) => _status.Text = message;
    public void Refresh()
    {
        _permission.Text = !OperatingSystem.IsMacOS() ? "截图服务已就绪" : _controller.Platform.HasScreenPermission ? "屏幕录制权限已允许" : "截图需要屏幕录制权限；贴图和已有图片识别可直接使用。";
        _cache.Text = _controller.CacheDirectory;
    }
}
