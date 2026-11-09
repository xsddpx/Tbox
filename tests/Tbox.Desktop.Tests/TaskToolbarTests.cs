using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tbox.Core;
using Tbox.Desktop.Models;
using Tbox.Desktop.Platform;
using Tbox.Desktop.Views;
using Xunit;

namespace Tbox.Desktop.Tests;

// Task toolbars (spec ⑤): one dark style, icon-only groups split by dividers, the mode colour on the selection.
public sealed class TaskToolbarTests
{
    [AvaloniaFact]
    public async Task CaptureToolbarHasToolsOnTheFirstRowAndModesOutputsAndCancelOnTheSecond()
    {
        using var scope = await CaptureScope.StartAsync(null);
        var window = scope.Window;
        var rows = window.GetVisualDescendants().OfType<Border>().Where(b => b.IsEffectivelyVisible && Equals(b.Background, Ui.ToolbarBackground)
            && b.Child is StackPanel { Orientation: Avalonia.Layout.Orientation.Horizontal }).ToArray();
        Assert.Equal(2, rows.Length);
        Assert.Equal(["矩形", "椭圆", "箭头", "直线", "画笔", "高亮", "马赛克", "文字", "序号", "橡皮擦", "|", "颜色", "减小粗细", "增大粗细", "|", "撤销", "重做"], Names(rows[0]));
        Assert.Equal(["长截图", "录屏", "|", "贴图", "识别文字", "保存", "复制", "|", "取消"], Names(rows[1]));
        Assert.True(rows[1].TranslatePoint(default, window)!.Value.Y > rows[0].TranslatePoint(default, window)!.Value.Y);
        // Every tooltip names the action first; shortcuts follow " · ".
        Assert.All(rows.SelectMany(r => r.GetVisualDescendants().OfType<Button>()), b => Assert.Equal(AutomationProperties.GetName(b), ToolTip.GetTip(b)));
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == "上次区域");
        Preview(window, "task-capture");

        // The selected tool is filled amethyst with a white icon; clicking it again clears the selection.
        var rectangle = rows[0].GetVisualDescendants().OfType<Button>().First();
        Click(rectangle);
        Assert.Equal(Color.Parse(DesignTokens.Accent), ((ISolidColorBrush)rectangle.Background!).Color);
        Assert.Equal(Colors.White, ((ISolidColorBrush)((LineIcon)rectangle.Content!).Stroke!).Color);
        Click(rectangle);
        Assert.Null(window.Editor.Tool);
        Assert.Equal(Color.Parse(DesignTokens.TaskText), ((ISolidColorBrush)((LineIcon)rectangle.Content!).Stroke!).Color);
    }

    [AvaloniaFact]
    public async Task ColourPopoverOffersTheSpecPaletteOnTheDarkSurface()
    {
        using var scope = await CaptureScope.StartAsync(null);
        var colour = scope.Window.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == "颜色");
        var flyout = Assert.IsType<Flyout>(colour.Flyout);
        var surface = Assert.IsType<Border>(flyout.Content);
        Assert.Equal(Ui.ToolbarBackground, surface.Background);
        var swatches = ((StackPanel)surface.Child!).Children.OfType<Button>().Select(b => AutomationProperties.GetName(b)).ToArray();
        Assert.Equal(["颜色 E5484D", "颜色 F5B400", "颜色 1F9D61", "颜色 3D7BFF", "颜色 6D4AE8", "颜色 1A1B22", "颜色 FFFFFF"], swatches);
        Assert.Equal(0xFFE5484D, scope.Window.Editor.Color);
    }

    [AvaloniaFact]
    public async Task RecordAreaConfirmsWithIconsOnly()
    {
        using var scope = await CaptureScope.StartAsync(CaptureAction.Record);
        var bar = scope.Window.GetVisualDescendants().OfType<Border>().Single(b => b.IsEffectivelyVisible && Equals(b.Background, Ui.ToolbarBackground));
        Assert.Equal(["确认录屏区域", "|", "取消"], Names(bar));
        Assert.All(bar.GetVisualDescendants().OfType<Button>(), b => Assert.IsType<LineIcon>(b.Content));
    }

    [AvaloniaFact]
    public void PinToolbarRequiresSelectionAndSpaceAndShowsTheFullEditingSet()
    {
        if (!Application.Current!.Styles.OfType<FluentTheme>().Any()) Application.Current.Styles.Add(DesignTokens.CreateTheme());
        using var lifetime = new ClassicDesktopStyleApplicationLifetime { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        typeof(ClassicDesktopStyleApplicationLifetime).GetInterfaces().Single(i => i.Name == "ISetupApplicationLifetime")
            .GetMethod("BeforeAppInit")!.Invoke(lifetime, null);
        using var controller = new DesktopController(new FakePlatform(), lifetime);
        var pin = new PinnedWindow(controller, DesktopController.RenderText("Tbox pin")) { ShowActivated = false };
        pin.Show(); Layout(pin);
        try
        {
            var point = new Point(12, 12);
            pin.MouseMove(point, RawInputModifiers.None);
            Assert.False(pin.IsSelected);
            pin.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Space });
            Assert.False(pin.CanAnnotate);

            pin.Activate();
            pin.MouseDown(point, MouseButton.Left, RawInputModifiers.None);
            pin.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
            Assert.True(pin.IsSelected);
            pin.MouseMove(new Point(18, 12), RawInputModifiers.None);
            Assert.False(pin.CanAnnotate);
            pin.KeyPress(Key.Space, RawInputModifiers.Shift, PhysicalKey.Space, null);
            Assert.False(pin.CanAnnotate);
            pin.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, null);
            Assert.True(pin.CanAnnotate);

            var window = Assert.Single(lifetime.Windows, w => w.Title == "Tbox 贴图工具条");
            Layout(window);
            var rows = Assert.IsType<StackPanel>(window.Content).Children;
            Assert.Equal(["矩形", "椭圆", "箭头", "直线", "画笔", "高亮", "马赛克", "文字", "序号", "橡皮擦", "|", "颜色", "减小粗细", "增大粗细", "|", "撤销", "重做"], Names(rows[0]));
            Assert.Equal(["识别文字", "保存", "复制图片", "|", "完成"], Names(rows[1]));
            Assert.True(rows[1].Bounds.Y > rows[0].Bounds.Y);
            Assert.All(rows, row => Assert.True(row.Bounds.Right <= window.ClientSize.Width));
            var ellipse = rows[0].GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == "椭圆");
            Click(ellipse);
            Assert.Equal(AnnotationTool.Ellipse, pin.Editor.Tool);
            Click(ellipse);
            Preview(window, "task-pin-toolbar");

            pin.MouseMove(new Point(-10, -10), RawInputModifiers.None);
            Assert.True(pin.CanAnnotate);
            pin.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, null);
            Assert.False(pin.CanAnnotate);
            Assert.True(pin.IsVisible);
        }
        finally { pin.Close(); }
    }

    [AvaloniaFact]
    public void RecordingToolbarSwitchesFromPrepareToTimerAndDashesTheOutline()
    {
        if (!Application.Current!.Styles.OfType<FluentTheme>().Any()) Application.Current.Styles.Add(DesignTokens.CreateTheme());
        var recorder = new FakeRecorder();
        using var controller = new DesktopController(new FakePlatform { Recorder = recorder }, new ClassicDesktopStyleApplicationLifetime());
        controller.Main = new MainWindow(controller);
        var control = new RecordingControlWindow(controller, new RecordingArea(new DesktopPlacement(100, 100, 640, 360), 640, 360));
        control.Show(); Layout(control);
        try
        {
            var bar = (Border)control.Content!;
            Assert.Equal(["帧率", "5 帧每秒", "10 帧每秒", "15 帧每秒", "25 帧每秒", "60 帧每秒", "|", "系统声音", "|", "开始录制", "取消"], Names(bar));
            var start = bar.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b)?.StartsWith("开始录制") == true);
            Assert.Equal(Color.Parse(DesignTokens.Recording), ((ISolidColorBrush)start.Resources["ButtonBackground"]!).Color);
            Preview(control, "task-record-ready");

            control.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Dispatcher.UIThread.RunJobs(); Layout(control);
            Assert.True(recorder.Started);
            Assert.Equal(["00:00", "|", "暂停", "停止并预览", "|", "取消"], Names(bar));
            Assert.True(control.Outline.Dashed);
            Preview(control, "task-record-active");
        }
        finally { _ = control.CancelAsync(); Dispatcher.UIThread.RunJobs(); controller.Main.Close(); }
    }

    [Fact]
    public void ModeColoursFollowTheSpec()
    {
        Assert.Equal(Color.Parse("#3D7BFF"), ((ISolidColorBrush)Ui.ModeBrush(null)).Color);
        Assert.Equal(Color.Parse("#E8A13A"), ((ISolidColorBrush)Ui.ModeBrush(CaptureAction.Long)).Color);
        Assert.Equal(Color.Parse("#E5484D"), ((ISolidColorBrush)Ui.ModeBrush(CaptureAction.Record)).Color);
    }

    // Visible items in toolbar order: button names, "|" for dividers, and toolbar text.
    private static string[] Names(Control toolbar) => toolbar.GetVisualDescendants().OfType<Control>().Where(c => c.IsEffectivelyVisible).Select(c => c switch
    {
        CheckBox box => box.Content as string,
        Button b when b.FindAncestorOfType<Button>() is null => AutomationProperties.GetName(b) is { } name ? name.Split(" · ")[0] : "?",
        Border { Width: 1 } => "|",
        TextBlock t when t.FindAncestorOfType<Button>() is null && t.FindAncestorOfType<CheckBox>() is null => t.Text,
        _ => null
    }).Where(n => n is not null).Select(n => n!).ToArray();

    private static void Click(Button button) => button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
    private static void Layout(Window window) { window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }

    internal static void Preview(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("TBOX_UI_PREVIEWS") is not { Length: > 0 } directory) return;
        Directory.CreateDirectory(directory);
        var size = window.Bounds.Size;
        using var preview = new RenderTargetBitmap(new PixelSize(Math.Max(1, (int)size.Width), Math.Max(1, (int)size.Height)), new Vector(96, 96));
        preview.Render(window); preview.Save(Path.Combine(directory, name + ".png"));
    }

    // A capture overlay over a rendered desktop, with a selection already dragged.
    private sealed class CaptureScope : IDisposable
    {
        private readonly ClassicDesktopStyleApplicationLifetime _lifetime;
        private readonly DesktopController _controller;
        private readonly Picture _frame;
        public CaptureWindow Window { get; }
        private CaptureScope(ClassicDesktopStyleApplicationLifetime lifetime, DesktopController controller, Picture frame, CaptureWindow window)
        { _lifetime = lifetime; _controller = controller; _frame = frame; Window = window; }

        public static async Task<CaptureScope> StartAsync(CaptureAction? preset)
        {
            if (!Application.Current!.Styles.OfType<FluentTheme>().Any()) Application.Current.Styles.Add(DesignTokens.CreateTheme());
            var frame = Desktop(1200, 720);
            var lifetime = new ClassicDesktopStyleApplicationLifetime { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            typeof(ClassicDesktopStyleApplicationLifetime).GetInterfaces().Single(i => i.Name == "ISetupApplicationLifetime")
                .GetMethod("BeforeAppInit")!.Invoke(lifetime, null);
            var platform = new FakePlatform { HasScreenPermission = true, Displays = [new CapturedDisplay(1, 0, 0, 1200, 720, frame.Png)] };
            var controller = new DesktopController(platform, lifetime);
            controller.Main = new MainWindow(controller);
            await (preset switch { CaptureAction.Record => controller.RecordRegionAsync(), CaptureAction.Long => controller.LongCaptureAsync(), _ => controller.CaptureAsync() });
            var window = Assert.Single(lifetime.Windows.OfType<CaptureWindow>());
            Layout(window);
            window.MouseDown(new Point(220, 140), MouseButton.Left, RawInputModifiers.None);
            window.MouseMove(new Point(760, 420), RawInputModifiers.LeftMouseButton);
            window.MouseUp(new Point(760, 420), MouseButton.Left, RawInputModifiers.None);
            Layout(window);
            return new CaptureScope(lifetime, controller, frame, window);
        }

        public void Dispose()
        {
            foreach (var window in _lifetime.Windows.ToArray()) window.Close();
            _controller.Dispose(); _frame.Dispose(); _lifetime.Dispose();
        }

        // A light dashboard-like backdrop so the previews read like a real desktop.
        private static Picture Desktop(int width, int height)
        {
            using var target = new RenderTargetBitmap(new PixelSize(width, height));
            using (var context = target.CreateDrawingContext())
            {
                context.FillRectangle(Brush.Parse("#F4F6FA"), new Rect(0, 0, width, height));
                context.FillRectangle(Brushes.White, new Rect(40, 40, width - 80, height - 80));
                for (var i = 0; i < 9; i++) context.FillRectangle(Brush.Parse(i % 3 == 0 ? "#DCE3F5" : "#E4E6ED"), new Rect(80, 90 + i * 56, 300 + i * 53 % 420, 18));
                context.FillRectangle(Brush.Parse("#C2CFEE"), new Rect(820, 90, 300, 220));
            }
            using var stream = new MemoryStream(); target.Save(stream);
            return new Picture(stream.ToArray());
        }
    }
}
