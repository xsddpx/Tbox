using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Tbox.Core;
using Tbox.Desktop.Models;
using Tbox.Desktop.Platform;

namespace Tbox.Desktop.Views;

// Light tray / menu bar panel: frequent tools and recent captures. It closes when focus leaves.
public sealed class TrayPanel : Window
{
    public const int PanelWidth = 360, RecentCount = 4;
    private readonly DesktopController _controller;
    private readonly TextBlock _ready = Ui.Caption("");
    private readonly TextBlock _hiddenPins = Ui.Caption("");
    private readonly TextBlock _footer = Ui.Caption("");
    private readonly Border _permission;
    private readonly Button _permissionButton;
    private readonly Grid _recent = new() { ColumnDefinitions = new ColumnDefinitions("*,*,*,*"), ColumnSpacing = 8 };
    private readonly TextBlock _recentEmpty = Ui.Caption("截图后会显示在这里。");
    private readonly ToggleSwitch _folderToggle = new() { OnContent = null, OffContent = null, MinWidth = 0 };
    private readonly List<Control> _captureActions = [];
    private readonly List<Bitmap> _thumbnails = [];
    private readonly Bitmap _brand;
    private bool _updating;
    private bool _suppressAutoHide;
    private DateTime _shownAt;

    internal IReadOnlyList<string> RecentPaths { get; private set; } = [];

    public TrayPanel(DesktopController controller)
    {
        _controller = controller;
        Title = "Tbox";
        Width = PanelWidth; SizeToContent = SizeToContent.Height;
        CanResize = false; ShowInTaskbar = false; Topmost = true;
        SystemDecorations = SystemDecorations.None;
        WindowStartupLocation = WindowStartupLocation.Manual;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;
        FontSize = DesignTokens.Body;

        using (var asset = AssetLoader.Open(new Uri("avares://Tbox.Desktop/Assets/StoreLogo.png"))) _brand = new Bitmap(asset);
        var name = Ui.Text("Tbox", 15); name.FontWeight = FontWeight.SemiBold;
        var settings = Ui.IconButton(IconKind.Gear, "设置", () => Run(controller.ShowSettings));
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 10, Margin = new Thickness(4, 0, 0, 8) };
        header.Children.Add(new Image { Source = _brand, Width = 26, Height = 26, VerticalAlignment = VerticalAlignment.Center });
        var title = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { name, _ready } };
        Grid.SetColumn(title, 1); header.Children.Add(title);
        Grid.SetColumn(settings, 2); header.Children.Add(settings);

        _permissionButton = Ui.Primary("去授权", () => Run(controller.RequestPermission));
        var permissionText = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children =
        {
            new TextBlock { Text = "需要屏幕录制权限", FontWeight = FontWeight.SemiBold },
            Ui.Caption("截图、长截图和录屏需要先在系统设置中允许 Tbox。")
        } };
        var permissionLayout = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 10, Children = { permissionText } };
        Grid.SetColumn(_permissionButton, 1); permissionLayout.Children.Add(_permissionButton);
        _permission = Ui.Themed(new Border { Child = permissionLayout, Padding = new Thickness(12, 10),
            CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 0, 0, 8), IsVisible = false },
            (b, c) => b.Background = new SolidColorBrush(Color.Parse(c.Warning), .16));

        var capture = Tile("截图", "区域 · 窗口 · 元素", IconKind.Capture, ShortcutLabels.Capture, controller.CaptureAsync, primary: true);
        var pin = Tile("贴图", null, IconKind.Pin, ShortcutLabels.Pin, controller.PinClipboardAsync);
        var record = Tile("录屏", null, IconKind.Record, null, controller.RecordRegionAsync, iconBrush: Ui.RecordRed);
        var tiles = new Grid { ColumnDefinitions = new ColumnDefinitions("1.25*,*,*"), ColumnSpacing = 8, Children = { capture, pin, record } };
        Grid.SetColumn(pin, 1); Grid.SetColumn(record, 2);

        var longCapture = Item("长截图", IconKind.Long, Ui.Caption("滚动拼接"), () => Run(controller.LongCaptureAsync));
        var ocrMenu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        ocrMenu.Items.Add(MenuItem("选择图片…", () => Run(controller.OpenImageForOcrAsync)));
        ocrMenu.Items.Add(MenuItem("识别剪贴板图片", () => Run(controller.OcrClipboardAsync)));
        ocrMenu.Closed += (_, _) => _suppressAutoHide = false;
        Button? ocr = null;
        ocr = Item("识别图片文字", IconKind.Ocr, Ui.Row(Ui.Caption("文件 · 剪贴板"), new LineIcon(IconKind.Chevron) { Width = 16, Height = 16 }),
            () => { _suppressAutoHide = true; ocrMenu.ShowAt(ocr!); });
        var toggle = Item("显隐全部贴图", IconKind.Eye, Ui.Row(_hiddenPins, Ui.Key(ShortcutLabels.Toggle)), () => Run(controller.TogglePins));
        AutomationProperties.SetName(_folderToggle, "启用文件夹跳转");
        _folderToggle.IsCheckedChanged += (_, _) =>
        {
            if (!_updating) controller.Run(() => controller.SetModulesAsync(null, _folderToggle.IsChecked == true));
        };
        var folders = Item("文件夹跳转", IconKind.Jump, Ui.Row(Ui.Key(ShortcutLabels.Folder), _folderToggle),
            () => _folderToggle.IsChecked = _folderToggle.IsChecked != true);
        _captureActions.AddRange([capture, pin, record, longCapture, ocr, toggle]);
        var list = new StackPanel { Margin = new Thickness(0, 8, 0, 0), Children = { longCapture, ocr, toggle, folders } };

        var openCache = Ui.Link(OperatingSystem.IsMacOS() ? "在访达中显示" : "打开文件夹", () => Run(controller.OpenCache));
        var recentTitle = Ui.SectionLabel("最近截图"); recentTitle.Margin = default; recentTitle.VerticalAlignment = VerticalAlignment.Center;
        var recentHeader = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(6, 12, 0, 6), Children = { recentTitle } };
        Grid.SetColumn(openCache, 1); recentHeader.Children.Add(openCache);
        _recentEmpty.Margin = new Thickness(6, 0, 0, 0);

        var quit = Ui.Link("退出", controller.Quit, "退出 Tbox", accent: false);
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(6, 10, 0, 0), Children = { _footer } };
        _footer.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(quit, 1); footer.Children.Add(quit);
        var divider = Ui.Themed(new Border { Height = 1, Margin = new Thickness(0, 12, 0, 0) }, (b, c) => b.Background = Brush.Parse(c.Border));

        var body = new StackPanel { Children = { header, _permission, tiles, list, recentHeader, _recent, _recentEmpty, divider, footer } };
        Content = Ui.Themed(new Border
        {
            Child = body, Padding = new Thickness(12), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(DesignTokens.PanelRadius)
        }, (b, c) => { b.Background = Brush.Parse(c.Surface); b.BorderBrush = Brush.Parse(c.Border); });

        KeyDown += (_, e) => { if (e.Key == Key.Escape) { Hide(); e.Handled = true; } };
        Deactivated += (_, _) => OnFocusLost();
        Closing += (_, e) => { if (!controller.IsQuitting) { e.Cancel = true; Hide(); } };
        Closed += (_, _) => { ReleaseThumbnails(); _brand.Dispose(); };
    }

    internal void OnFocusLost()
    {
        // The OCR flyout takes focus while it is open; the panel stays until it closes.
        // A deactivation queued by the previous Hide can arrive after a fast reopen and must not close it again.
        if (_suppressAutoHide || _controller.IsQuitting || DateTime.UtcNow - _shownAt < TimeSpan.FromMilliseconds(250)) return;
        Hide();
    }

    // Shows the panel next to the tray or menu bar icon. Without an anchor, the pointer that just clicked it is used.
    public void ShowNearPointer(DesktopPoint? anchor = null)
    {
        Refresh();
        _shownAt = DateTime.UtcNow;
        Show();
        try
        {
            var handle = TryGetPlatformHandle()!.Handle; var platform = _controller.Platform;
            var pointer = anchor ?? platform.GetPointerPosition(); var unit = platform.DesktopUnitsPerDip(handle);
            var work = platform.GetWorkArea(handle);
            if (!ContainsPoint(work, pointer) && Screens.ScreenFromPoint(new PixelPoint((int)pointer.X, (int)pointer.Y)) is { } screen)
                work = new DesktopPlacement(screen.WorkingArea.X, screen.WorkingArea.Y, screen.WorkingArea.Width, screen.WorkingArea.Height);
            // Bounds still hold the pre-layout default right after Show; the measured content is the real panel height.
            var content = (Control)Content!;
            content.Measure(new Size(PanelWidth, double.PositiveInfinity));
            var width = PanelWidth * unit; var height = Math.Ceiling(content.DesiredSize.Height) * unit;
            var place = PanelPlacementPolicy.Place(width, height, pointer.X, pointer.Y, work.Left, work.Top, work.Width, work.Height, 12 * unit);
            platform.PlaceWindow(handle, new DesktopPlacement(place.Left, place.Top, width, height), false);
        }
        catch (Exception error) { Tbox.App.AppLog.Write("Tray panel placement: " + error); }
        Activate();
    }

    private static bool ContainsPoint(DesktopPlacement area, DesktopPoint point) =>
        point.X >= area.Left && point.X < area.Left + area.Width && point.Y >= area.Top && point.Y < area.Top + area.Height;

    public void Refresh()
    {
        _updating = true;
        _folderToggle.IsChecked = _controller.Settings.FolderJumpEnabled;
        _updating = false;
        var capture = _controller.Settings.ScreenPasteEnabled;
        foreach (var action in _captureActions) action.IsEnabled = capture;
        var needsPermission = capture && _controller.CapturePermissionNeeded;
        _permission.IsVisible = needsPermission;
        _permissionButton.Content = _controller.ScreenPermissionRequested ? "打开设置" : "去授权";
        AutomationProperties.SetName(_permissionButton, _controller.ScreenPermissionRequested ? "打开屏幕录制设置" : "允许屏幕录制");
        _ready.Text = !capture ? "截图与贴图已在设置中关闭"
            : _controller.HasHotkeyConflict ? "部分快捷键被占用"
            : needsPermission ? "等待屏幕录制授权" : "快捷键已就绪";
        _hiddenPins.Text = _controller.PinCount == 0 ? "无贴图"
            : _controller.PinsHidden ? $"{_controller.PinCount} 张已隐藏" : $"{_controller.PinCount} 张贴图";
        _footer.Text = $"Tbox {MainWindow.Version}";
        RefreshRecent();
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
            try { using var stream = File.OpenRead(file.FullName); bitmap = Bitmap.DecodeToWidth(stream, 160); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException) { }
            if (bitmap is null) continue;
            _thumbnails.Add(bitmap);
            var thumbnail = RecentThumbnail(file, bitmap);
            Grid.SetColumn(thumbnail, index);
            _recent.Children.Add(thumbnail);
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
            Background = new SolidColorBrush(Color.FromArgb(160, 10, 12, 20)), CornerRadius = new CornerRadius(4),
            Padding = new Thickness(4, 0), Margin = new Thickness(4), HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Child = new TextBlock { Text = label, FontSize = 10, Foreground = Brushes.White }
        };
        var pin = OverlayButton(IconKind.Pin, $"贴到屏幕 · {label}", () => _controller.Run(() => _controller.PinFileAsync(path)));
        var copy = OverlayButton(IconKind.Copy, $"复制截图 · {label}", () => _controller.Run(() => _controller.CopyFileAsync(path)));
        var actions = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(115, 10, 12, 20)), IsVisible = false,
            Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center, Children = { pin, copy } }
        };
        var content = new Grid { Children = { new Image { Source = bitmap, Stretch = Stretch.UniformToFill }, time, actions } };
        var card = Ui.Themed(new Border
        {
            Child = content, Height = 58, CornerRadius = new CornerRadius(8), ClipToBounds = true, BorderThickness = new Thickness(1)
        }, (b, c) => b.BorderBrush = Brush.Parse(c.Border));
        ToolTip.SetTip(card, $"{label} · {file.Name}");
        AutomationProperties.SetName(card, $"最近截图 {label}");
        card.PointerEntered += (_, _) => actions.IsVisible = true;
        card.PointerExited += (_, _) => actions.IsVisible = false;
        // Keyboard users reach the two actions directly, so focus also reveals them.
        pin.GotFocus += (_, _) => actions.IsVisible = true; copy.GotFocus += (_, _) => actions.IsVisible = true;
        return card;
    }

    private void ReleaseThumbnails()
    {
        foreach (var bitmap in _thumbnails) bitmap.Dispose();
        _thumbnails.Clear();
    }

    // Runs a panel action after the panel is hidden so captures never include it.
    private void Run(Action action) { Hide(); action(); }
    private void Run(Func<Task> action) { Hide(); _controller.Run(action); }

    private Button Tile(string title, string? subtitle, IconKind icon, string? shortcut, Func<Task> action, bool primary = false, IBrush? iconBrush = null)
    {
        var button = new Button
        {
            Height = 104, Padding = new Thickness(12), HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch,
            CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1)
        };
        ToolTip.SetTip(button, shortcut is null ? title : $"{title} · {shortcut}");
        AutomationProperties.SetName(button, shortcut is null ? title : $"{title} · {shortcut}");
        button.Click += (_, _) => Run(action);
        var glyph = new LineIcon(icon, brush: iconBrush ?? (primary ? Brushes.White : null));
        var box = new Border { Width = 36, Height = 36, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(primary ? 0 : 1),
            Child = glyph, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        var label = new TextBlock { Text = title, FontWeight = FontWeight.SemiBold };
        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Children = { label } };
        TextBlock? sub = null;
        if (subtitle is not null) { sub = new TextBlock { Text = subtitle, FontSize = DesignTokens.Caption, Opacity = .85 }; labels.Children.Add(sub); }
        var grid = new Grid { Children = { box, labels } };
        if (shortcut is not null)
        {
            var key = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(6, 2), HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top, Child = new TextBlock { Text = shortcut, FontSize = 11 } };
            grid.Children.Add(key);
            Ui.Themed(key, (b, c) =>
            {
                b.Background = primary ? new SolidColorBrush(Colors.White, .2) : Brush.Parse(c.Hover);
                ((TextBlock)b.Child!).Foreground = primary ? Brushes.White : Brush.Parse(c.SecondaryText);
            });
        }
        button.Content = grid;
        Ui.Themed(button, (b, c) =>
        {
            // The dark accent is tuned for text on dark surfaces; a filled tile uses the brand amethyst instead.
            var fill = primary ? DesignTokens.Accent : c.SecondarySurface;
            b.Background = Brush.Parse(fill); b.BorderBrush = primary ? Brushes.Transparent : Brush.Parse(c.Border);
            b.Foreground = primary ? Brushes.White : Brush.Parse(c.Text);
            b.Resources["ButtonBackgroundPointerOver"] = primary ? new SolidColorBrush(Color.Parse(fill), .88) : Brush.Parse(c.Hover);
            b.Resources["ButtonForegroundPointerOver"] = b.Foreground;
            b.Resources["ButtonBackgroundPressed"] = primary ? new SolidColorBrush(Color.Parse(fill), .76) : Brush.Parse(c.Border);
            b.Resources["ButtonForegroundPressed"] = b.Foreground;
            box.Background = primary ? new SolidColorBrush(Colors.White, .18) : Brush.Parse(c.Surface);
            box.BorderBrush = Brush.Parse(c.Border);
            if (primary) label.Foreground = b.Foreground;
        });
        if (primary) glyph.Opacity = 1;
        return button;
    }

    private static Button Item(string title, IconKind icon, Control accessory, Action action)
    {
        var button = new Button
        {
            MinHeight = 40, Padding = new Thickness(10, 0), HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(8), BorderThickness = default, Background = Brushes.Transparent
        };
        AutomationProperties.SetName(button, title);
        button.Click += (_, _) => action();
        var text = new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center };
        var layout = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 12 };
        layout.Children.Add(new LineIcon(icon) { VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(text, 1); layout.Children.Add(text);
        accessory.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(accessory, 2); layout.Children.Add(accessory);
        button.Content = layout;
        Ui.Themed(button, (b, c) =>
        {
            b.Foreground = Brush.Parse(c.Text);
            b.Resources["ButtonBackgroundPointerOver"] = Brush.Parse(c.Hover);
            b.Resources["ButtonForegroundPointerOver"] = b.Foreground;
            b.Resources["ButtonBackgroundPressed"] = Brush.Parse(c.Border);
            b.Resources["ButtonForegroundPressed"] = b.Foreground;
        });
        return button;
    }

    private static Button OverlayButton(IconKind icon, string label, Action action)
    {
        var button = Ui.Button(label, action, label);
        button.Content = new LineIcon(icon, brush: Brush.Parse("#17181F")) { Width = 16, Height = 16 };
        button.Padding = new Thickness(3); button.MinHeight = 0; button.Width = button.Height = 24;
        button.CornerRadius = new CornerRadius(6);
        button.Background = new SolidColorBrush(Colors.White, .92);
        button.Resources["ButtonBackgroundPointerOver"] = Brushes.White;
        return button;
    }

    private static MenuItem MenuItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }
}
