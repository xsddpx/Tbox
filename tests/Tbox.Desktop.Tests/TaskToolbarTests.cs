using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
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
    public async Task CaptureHistoryKeysRestoreFrameSelectionAndEditableUndoRedo()
    {
        using var scope = await CaptureScope.StartAsync(null);
        scope.Window.Close();
        var controller = scope.Controller;
        var oldDisplay = new CapturedDisplay(1, 0, 0, 1200, 720, scope.Frame.Png);
        var currentDisplay = new CapturedDisplay(1, 0, 0, 1200, 720, scope.Frame.Png);
        var oldEditor = new AnnotationEditor();
        oldEditor.Document.Commit(new RectangleAnnotationCommand(new(10, 10), new(50, 50), AnnotationStyle.Default));
        oldEditor.Document.Commit(new RectangleAnnotationCommand(new(60, 60), new(90, 90), AnnotationStyle.Default));
        oldEditor.Undo();
        var oldSelection = new CaptureSelectionBounds(100, 80, 300, 200);
        controller.CaptureFrames.Add(new CaptureFrame([oldDisplay], 1, oldSelection, oldEditor.Document.Snapshot()));
        var session = new CaptureSession(controller, [currentDisplay], _ => { });
        session.Show();
        var window = Assert.Single(session.Windows);
        Layout(window);
        var currentSelection = new CaptureSelectionBounds(200, 140, 400, 260);
        session.Select(1, currentSelection);
        window.Editor.Document.Commit(new RectangleAnnotationCommand(new(20, 20), new(30, 30), AnnotationStyle.Default));

        window.KeyPress(Key.None, RawInputModifiers.None, PhysicalKey.Comma, null);
        Assert.Same(oldDisplay, window.Frame);
        Assert.Equal(oldSelection, session.State.Selection);
        Assert.Single(window.Editor.Document.Commands);
        window.Editor.Redo();
        Assert.Equal(2, window.Editor.Document.Commands.Count);
        window.Editor.Undo();
        var editedSelection = new CaptureSelectionBounds(110, 90, 250, 180);
        session.Select(1, editedSelection);
        window.KeyPress(Key.OemPeriod, RawInputModifiers.None, PhysicalKey.Period, null);
        Assert.Same(currentDisplay, window.Frame);
        Assert.Equal(currentSelection, session.State.Selection);
        Assert.Single(window.Editor.Document.Commands);
        window.KeyPress(Key.OemComma, RawInputModifiers.None, PhysicalKey.Comma, null);
        Assert.Equal(editedSelection, session.State.Selection);
        Assert.Single(window.Editor.Document.Commands);
        Assert.True(window.Editor.Document.CanRedo);
        window.Editor.Select(AnnotationTool.Text);
        window.Editor.Begin(new AnnotationPoint(150, 120), false);
        Layout(window);
        var text = Assert.Single(window.GetVisualDescendants().OfType<TextBox>());
        window.KeyPress(Key.OemPeriod, RawInputModifiers.None, PhysicalKey.Period, null);
        window.KeyTextInput(",.");
        Assert.Equal(",.", text.Text);
        Assert.Same(oldDisplay, window.Frame);
        session.Cancel();
    }

    [AvaloniaTheory]
    [InlineData(null, 0, "拖动框选，单击选窗口")]
    [InlineData(null, 37, "拖动框选，单击选窗口")]
    [InlineData(CaptureAction.Long, 37, "拖动框选长截图区域")]
    [InlineData(CaptureAction.Record, 37, "拖动框选录屏区域")]
    public async Task CaptureHintStaysBelowTheDisplayTopInset(CaptureAction? preset, int inset, string label)
    {
        using var scope = await CaptureScope.StartAsync(preset, inset);
        var window = scope.Window;
        var text = Assert.Single(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == label);
        var hint = Assert.IsType<Border>(text.FindAncestorOfType<Border>());
        var position = hint.TranslatePoint(default, window)!.Value;
        Assert.Equal(inset + 18, position.Y);
        Assert.InRange(Math.Abs(window.Bounds.Width / 2 - position.X - hint.Bounds.Width / 2), 0, 0.5);
        if (preset is null && inset > 0) Preview(window, "task-capture-safe-area");
    }

    [AvaloniaFact]
    public async Task CaptureToolbarKeepsTenToolsAndOutputsInOneRowAndModesInMore()
    {
        using var scope = await CaptureScope.StartAsync(null);
        var window = scope.Window;
        var toolbar = Assert.Single(window.GetVisualDescendants().OfType<AnnotationToolbar>());
        Assert.Equal(["矩形", "椭圆", "箭头", "直线", "画笔", "高亮", "马赛克", "文字", "序号", "橡皮擦", "|", "撤销", "|", "贴图", "识别文字", "保存", "复制", "更多操作", "|", "取消"], Names(toolbar.MainRow));
        Assert.InRange(toolbar.Bounds.Height, 32, 48);
        Assert.True(toolbar.Bounds.Right <= window.ClientSize.Width);
        // Every tooltip names the action first; shortcuts follow " · ".
        Assert.All(toolbar.MainRow.GetVisualDescendants().OfType<Button>(), b => Assert.Equal(AutomationProperties.GetName(b), ToolTip.GetTip(b)));
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == "上次区域");
        Preview(window, "task-capture");

        // The selected tool is filled amethyst with a white icon; clicking it again clears the selection.
        var rectangle = toolbar.MainRow.GetVisualDescendants().OfType<Button>().First();
        Click(rectangle);
        Assert.Equal(Color.Parse(DesignTokens.Accent), ((ISolidColorBrush)rectangle.Background!).Color);
        Assert.Equal(Colors.White, ((ISolidColorBrush)((LineIcon)rectangle.Content!).Stroke!).Color);
        Click(rectangle);
        Assert.Null(window.Editor.Tool);
        Assert.Equal(Color.Parse(DesignTokens.TaskText), ((ISolidColorBrush)((LineIcon)rectangle.Content!).Stroke!).Color);
        Click(toolbar.MainRow.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == "更多操作"));
        Assert.True(toolbar.MorePopup.IsOpen);
        Assert.Equal(["长截图", "录屏", "重做"], Names(toolbar.MorePopup.Child!));
        Assert.True(toolbar.DismissPopover());
    }

    [AvaloniaFact]
    public async Task CaptureTooltipsWaitOnEachButtonAndAppearInsideTheCaptureOverlay()
    {
        using var scope = await CaptureScope.StartAsync(null);
        var window = scope.Window;
        var toolbar = Assert.Single(window.GetVisualDescendants().OfType<AnnotationToolbar>());
        var buttons = toolbar.MainRow.GetVisualDescendants().OfType<Button>().ToArray();
        var popup = Assert.Single(window.GetVisualDescendants().OfType<Popup>(), p => p.Placement == PlacementMode.Bottom);
        Assert.All(buttons, button =>
        {
            Assert.Equal(1000, ToolTip.GetShowDelay(button));
            Assert.Equal(-1, ToolTip.GetBetweenShowDelay(button));
        });

        foreach (var name in new[] { "贴图", "保存" })
        {
            var button = Assert.Single(buttons, b => AutomationProperties.GetName(b)?.StartsWith(name + " · ") == true);
            var center = new Point(button.Bounds.Width / 2, button.Bounds.Height / 2);
            window.MouseMove(button.TranslatePoint(center, window)!.Value, RawInputModifiers.None);
            Assert.False(popup.IsOpen);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            while (!popup.IsOpen) await Task.Delay(25, timeout.Token);
            Layout(window);
            Assert.True(popup.IsUsingOverlayLayer);
            Assert.Equal(button, popup.PlacementTarget);
            var text = Assert.IsType<TextBlock>(Assert.IsType<Border>(popup.Child).Child);
            Assert.Equal(ToolTip.GetTip(button), text.Text);
        }

        window.MouseMove(new Point(50, 600), RawInputModifiers.None);
        Assert.False(popup.IsOpen);
    }

    [AvaloniaFact]
    public async Task PropertiesUseOneHorizontalStripWithThePaletteAndCurrentSize()
    {
        using var scope = await CaptureScope.StartAsync(null);
        var toolbar = Assert.Single(scope.Window.GetVisualDescendants().OfType<AnnotationToolbar>());
        Click(toolbar.MainRow.GetVisualDescendants().OfType<Button>().First());
        Layout(scope.Window);
        Assert.True(toolbar.PropertiesPopup.IsOpen);
        var surface = toolbar.PropertiesSurface;
        Assert.Equal(Ui.ToolbarBackground, surface.Background);
        Assert.Equal(Orientation.Horizontal, Assert.IsType<StackPanel>(surface.Child).Orientation);
        var swatches = surface.GetVisualDescendants().OfType<Button>().Where(b => AutomationProperties.GetName(b)?.StartsWith("颜色 ") == true).ToArray();
        Assert.Equal(new[] { "颜色 E5484D", "颜色 F5B400", "颜色 1F9D61", "颜色 3D7BFF", "颜色 6D4AE8", "颜色 1A1B22" }, swatches.Select(b => AutomationProperties.GetName(b)!));
        Assert.Equal(0xFFE5484D, scope.Window.Editor.Color);
        var blue = Assert.IsType<ToggleButton>(swatches[3]);
        var ring = Assert.IsType<Border>(blue.Content);
        Assert.InRange(ring.Bounds.Width, 1, 18);
        // The surrounding hit area still selects blue outside the small colored dot.
        var hit = blue.TranslatePoint(new Point(2, blue.Bounds.Height / 2), scope.Window)!.Value;
        scope.Window.MouseDown(hit, MouseButton.Left, RawInputModifiers.None);
        scope.Window.MouseUp(hit, MouseButton.Left, RawInputModifiers.None);
        Assert.Equal(0xFF3D7BFF, scope.Window.Editor.Color);
        Assert.True(blue.IsChecked);
        Assert.False(Assert.IsType<ToggleButton>(swatches[0]).IsChecked);
        Assert.True(toolbar.PropertiesPopup.IsOpen);
        Assert.Contains(surface.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "3 px");
        Preview(scope.Window, "task-capture-properties-below");
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
            var toolbar = Assert.IsType<AnnotationToolbar>(window.Content);
            Assert.Equal(["矩形", "椭圆", "箭头", "直线", "画笔", "高亮", "马赛克", "文字", "序号", "橡皮擦", "|", "撤销", "|", "识别文字", "保存", "复制图片", "更多操作", "|", "完成"], Names(toolbar.MainRow));
            Assert.InRange(toolbar.Bounds.Height, 32, 48);
            Assert.True(toolbar.Bounds.Right <= window.ClientSize.Width);
            Click(toolbar.MainRow.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == "更多操作"));
            Assert.Equal(["重做"], Names(toolbar.MorePopup.Child!));
            toolbar.DismissPopover();
            var ellipse = toolbar.MainRow.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == "椭圆");
            Click(ellipse);
            Assert.Equal(AnnotationTool.Ellipse, pin.Editor.Tool);
            var propertySlider = Assert.Single(toolbar.PropertiesSurface.GetVisualDescendants().OfType<Slider>());
            propertySlider.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            Assert.True(pin.CanAnnotate);
            Assert.True(toolbar.PropertiesPopup.IsOpen);
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

    [AvaloniaTheory]
    [InlineData(640, 420, false)]
    [InlineData(800, 420, false)]
    [InlineData(1200, 420, false)]
    [InlineData(640, 620, true)]
    [InlineData(800, 620, true)]
    [InlineData(1200, 620, true)]
    public async Task PropertiesPreferBelowAndFlipAboveAtTheBottomWithoutClipping(int width, int bottom, bool above)
    {
        using var scope = await CaptureScope.StartAsync(null);
        var window = scope.Window;
        window.Width = width; Layout(window);
        window.MouseDown(new Point(20, 100), MouseButton.Left, RawInputModifiers.None);
        window.MouseMove(new Point(120, bottom), RawInputModifiers.LeftMouseButton);
        window.MouseUp(new Point(120, bottom), MouseButton.Left, RawInputModifiers.None); Layout(window);
        var toolbar = Assert.Single(window.GetVisualDescendants().OfType<AnnotationToolbar>());
        Click(toolbar.MainRow.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == "文字 · T"));
        Layout(window);
        var surface = toolbar.PropertiesSurface;
        Assert.True(toolbar.PropertiesPopup.IsOpen);
        var barPoint = toolbar.TranslatePoint(default, window)!.Value;
        var propertyPoint = surface.TranslatePoint(default, window)!.Value;
        Assert.InRange(propertyPoint.X, 0, width - surface.Bounds.Width + 1);
        Assert.InRange(propertyPoint.Y, 0, window.ClientSize.Height - surface.Bounds.Height + 1);
        var gap = above ? barPoint.Y - propertyPoint.Y - surface.Bounds.Height : propertyPoint.Y - barPoint.Y - toolbar.Bounds.Height;
        Assert.True(Math.Abs(gap - 6) <= 1, $"bar={barPoint} {toolbar.Bounds.Size}, properties={propertyPoint} {surface.Bounds.Size}, above={above}, gap={gap}, anchor={toolbar.PropertiesPopup.PlacementRect}");
        Assert.InRange(surface.Bounds.Height, 32, 48);
        Preview(window, $"task-properties-{width}-{(above ? "above" : "below")}");
    }

    [AvaloniaTheory]
    [InlineData(AnnotationTool.Rectangle, "线宽", 5, "5 px")]
    [InlineData(AnnotationTool.Ellipse, "线宽", 5, "5 px")]
    [InlineData(AnnotationTool.Text, "字号", 32, "32 px")]
    [InlineData(AnnotationTool.Number, "序号大小", 36, "36 px")]
    [InlineData(AnnotationTool.Highlighter, "线宽", 5, "30 px")]
    [InlineData(AnnotationTool.Mosaic, "颗粒大小", 5, "10 px")]
    [InlineData(AnnotationTool.Eraser, "擦除范围", 5, "15 px")]
    public async Task PropertyMeaningAndValueFollowTheSelectedTool(AnnotationTool tool, string name, double value, string expected)
    {
        using var scope = await CaptureScope.StartAsync(null);
        var toolbar = Assert.Single(scope.Window.GetVisualDescendants().OfType<AnnotationToolbar>());
        scope.Window.Editor.Select(tool); Layout(scope.Window);
        var surface = toolbar.PropertiesSurface;
        var row = Assert.IsType<StackPanel>(surface.Child);
        var title = Assert.IsType<TextBlock>(row.Children[0]);
        var divider = Assert.IsType<Border>(row.Children[1]);
        var titleCenter = title.TranslatePoint(new Point(title.Bounds.Width / 2, title.Bounds.Height / 2), surface)!.Value;
        var dividerLeft = divider.TranslatePoint(default, surface)!.Value.X;
        Assert.Equal(TextAlignment.Center, title.TextAlignment);
        Assert.InRange(Math.Abs(titleCenter.X - dividerLeft / 2), 0, 1);
        Assert.InRange(Math.Abs(titleCenter.Y - surface.Bounds.Height / 2), 0, 1);

        var slider = Assert.Single(surface.GetVisualDescendants().OfType<Slider>());
        Assert.Equal(name, AutomationProperties.GetName(slider));
        slider.Value = value; Layout(scope.Window);
        Assert.Equal(value, scope.Window.Editor.CurrentSize);
        Assert.Contains(toolbar.PropertiesSurface.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == expected);
        var colors = toolbar.PropertiesSurface.GetVisualDescendants().OfType<Button>().Count(b => AutomationProperties.GetName(b)?.StartsWith("颜色 ") == true);
        Assert.Equal(tool is AnnotationTool.Mosaic or AnnotationTool.Eraser ? 0 : 6, colors);
    }

    [AvaloniaFact]
    public async Task EllipseWidthSliderStaysCenteredAndCanBeDragged()
    {
        using var scope = await CaptureScope.StartAsync(null);
        var window = scope.Window;
        var toolbar = Assert.Single(window.GetVisualDescendants().OfType<AnnotationToolbar>());
        window.Editor.Select(AnnotationTool.Ellipse);
        Layout(window);
        var surface = toolbar.PropertiesSurface;
        var slider = Assert.Single(surface.GetVisualDescendants().OfType<Slider>());
        var track = Assert.Single(slider.GetVisualDescendants().OfType<Track>());
        var thumb = Assert.Single(track.GetVisualDescendants().OfType<Thumb>());
        var label = Assert.Single(surface.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "线宽");
        var thumbOrigin = thumb.TranslatePoint(default, slider)!.Value;
        Assert.True(thumbOrigin.Y >= 0 && thumbOrigin.Y + thumb.Bounds.Height <= slider.Bounds.Height);
        var thumbCenter = thumb.TranslatePoint(new Point(thumb.Bounds.Width / 2, thumb.Bounds.Height / 2), surface)!.Value;
        var labelCenter = label.TranslatePoint(new Point(0, label.Bounds.Height / 2), surface)!.Value;
        Assert.InRange(Math.Abs(thumbCenter.Y - labelCenter.Y), 0, 1);
        var segment = Assert.Single(track.IncreaseButton!.GetVisualDescendants().OfType<Border>(), border => border.Name == "TrackBackground");
        var trackCenter = segment.TranslatePoint(new Point(0, segment.Bounds.Height / 2), surface)!.Value;
        Assert.InRange(Math.Abs(trackCenter.Y - thumbCenter.Y), 0, 1);

        var start = surface.TranslatePoint(thumbCenter, window)!.Value;
        window.MouseDown(start, MouseButton.Left, RawInputModifiers.None);
        var end = start + new Vector(28, 0);
        window.MouseMove(end, RawInputModifiers.LeftMouseButton);
        window.MouseUp(end, MouseButton.Left, RawInputModifiers.None);
        Assert.True(window.Editor.StrokeWidth > 3);
        Assert.Contains(surface.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == $"{window.Editor.StrokeWidth:0} px");
        Assert.True(toolbar.PropertiesPopup.IsOpen);
    }

    [AvaloniaFact]
    public async Task EscapeClosesPropertiesBeforeClearingToolOrEndingCapture()
    {
        using var scope = await CaptureScope.StartAsync(null);
        var window = scope.Window;
        var toolbar = Assert.Single(window.GetVisualDescendants().OfType<AnnotationToolbar>());
        window.Editor.Select(AnnotationTool.Text); Layout(window);
        var slider = Assert.Single(toolbar.PropertiesSurface.GetVisualDescendants().OfType<Slider>());
        slider.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        Assert.True(window.IsVisible);
        Assert.True(toolbar.PropertiesPopup.IsOpen);
        slider.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
        Assert.False(toolbar.PropertiesPopup.IsOpen);
        Assert.Equal(AnnotationTool.Text, window.Editor.Tool);
        Assert.True(window.IsVisible);
        window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
        Assert.Null(window.Editor.Tool);
        Assert.True(window.IsVisible);
        window.Editor.Select(AnnotationTool.Pen); Layout(window);
        window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Space });
        Assert.False(toolbar.PropertiesPopup.IsOpen);
        Assert.False(toolbar.IsVisible);
    }

    [AvaloniaFact]
    public async Task DismissingPropertiesWithTheCanvasStillDrawsTheFirstStroke()
    {
        using var scope = await CaptureScope.StartAsync(null);
        var window = scope.Window;
        var toolbar = Assert.Single(window.GetVisualDescendants().OfType<AnnotationToolbar>());
        window.Editor.Select(AnnotationTool.Pen); Layout(window);
        Assert.True(toolbar.PropertiesPopup.IsOpen);
        window.MouseDown(new Point(300, 200), MouseButton.Left, RawInputModifiers.None);
        window.MouseMove(new Point(350, 240), RawInputModifiers.LeftMouseButton);
        window.MouseUp(new Point(350, 240), MouseButton.Left, RawInputModifiers.None);
        Assert.False(toolbar.PropertiesPopup.IsOpen);
        Assert.Equal(AnnotationTool.Pen, window.Editor.Tool);
        Assert.IsType<FreePathAnnotationCommand>(Assert.Single(window.Editor.Document.Commands));
    }

    [AvaloniaFact]
    public async Task RedoInMoreRestoresAnnotationsAndModeSwitchStillConfirmsDiscard()
    {
        using var scope = await CaptureScope.StartAsync(null);
        var window = scope.Window;
        var toolbar = Assert.Single(window.GetVisualDescendants().OfType<AnnotationToolbar>());
        window.Editor.Select(AnnotationTool.Rectangle); toolbar.ClosePopovers();
        window.Editor.Begin(new AnnotationPoint(250, 180), false); window.Editor.End(new AnnotationPoint(320, 220), false);
        window.Editor.Undo();
        var more = toolbar.MainRow.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == "更多操作");
        Click(more); Layout(window);
        var redo = toolbar.MorePopup.Child!.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b)!.StartsWith("重做"));
        Assert.True(redo.IsEnabled); Click(redo);
        Assert.Single(window.Editor.Document.Commands);
        Assert.False(toolbar.MorePopup.IsOpen);
        Click(more); Layout(window);
        Click(toolbar.MorePopup.Child!.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == "长截图"));
        Assert.True(window.IsVisible);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == "长截图使用原始画面，是否丢弃本次标注？");
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
        public DesktopController Controller => _controller;
        public Picture Frame => _frame;
        private CaptureScope(ClassicDesktopStyleApplicationLifetime lifetime, DesktopController controller, Picture frame, CaptureWindow window)
        { _lifetime = lifetime; _controller = controller; _frame = frame; Window = window; }

        public static async Task<CaptureScope> StartAsync(CaptureAction? preset, double topSafeInset = 0)
        {
            if (!Application.Current!.Styles.OfType<FluentTheme>().Any()) Application.Current.Styles.Add(DesignTokens.CreateTheme());
            var frame = Desktop(1200, 720);
            var lifetime = new ClassicDesktopStyleApplicationLifetime { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            typeof(ClassicDesktopStyleApplicationLifetime).GetInterfaces().Single(i => i.Name == "ISetupApplicationLifetime")
                .GetMethod("BeforeAppInit")!.Invoke(lifetime, null);
            var platform = new FakePlatform
            {
                HasScreenPermission = true,
                Displays = [new CapturedDisplay(1, 0, 0, 1200, 720, frame.Png)],
                DisplayTopSafeInset = topSafeInset
            };
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
