using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
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
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MosaicPointerPreviewMatchesTheCommittedStrokeAndUndoInEachWindow(bool onPin)
    {
        using var scope = await CaptureScope.StartAsync(null);
        var capture = scope.Window;
        PinnedWindow? pin = null;
        try
        {
            Window window = capture;
            Control surface = capture.Surface;
            var editor = capture.Editor;
            var start = new Point(320, 195);
            var end = new Point(390, 228);
            if (onPin)
            {
                pin = new PinnedWindow(scope.Controller, scope.Frame.Clone());
                pin.Show(); pin.Activate(); Layout(pin);
                pin.MouseDown(new Point(12, 12), MouseButton.Left, RawInputModifiers.None);
                pin.MouseUp(new Point(12, 12), MouseButton.Left, RawInputModifiers.None);
                pin.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, null);
                Assert.True(pin.CanAnnotate);
                window = pin;
                surface = pin.GetVisualDescendants().OfType<PinSurface>().Single();
                editor = pin.Editor;
                start = new Point(160, 97); end = new Point(195, 114);
            }
            editor.Select(AnnotationTool.Mosaic);
            byte[] SurfacePixels()
            {
                using var target = new RenderTargetBitmap(new PixelSize((int)surface.Bounds.Width, (int)surface.Bounds.Height));
                using (var context = target.CreateDrawingContext()) surface.Render(context);
                using var copy = Picture.FromBitmap(target);
                return copy.GetPixels();
            }
            var before = SurfacePixels();
            window.MouseDown(start, MouseButton.Left, RawInputModifiers.None);
            window.MouseMove(end, RawInputModifiers.LeftMouseButton);
            Assert.True(editor.IsDrawing);
            var live = SurfacePixels();
            Assert.False(before.AsSpan().SequenceEqual(live));
            window.MouseUp(end, MouseButton.Left, RawInputModifiers.None);
            Assert.IsType<MosaicAnnotationCommand>(Assert.Single(editor.Document.Commands));
            Assert.True(live.AsSpan().SequenceEqual(SurfacePixels()));
            editor.Undo();
            Assert.True(before.AsSpan().SequenceEqual(SurfacePixels()));
            editor.Redo();
            Assert.True(live.AsSpan().SequenceEqual(SurfacePixels()));
        }
        finally { pin?.Close(); }
    }

    [AvaloniaFact]
    public async Task ScreenshotCopyButtonHasTheSameIdleBackgroundAsSave()
    {
        using var scope = await CaptureScope.StartAsync(null);
        var buttons = scope.Window.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible).ToArray();
        var copy = Assert.Single(buttons, b => AutomationProperties.GetName(b)?.StartsWith("复制 · ", StringComparison.Ordinal) == true);
        var save = Assert.Single(buttons, b => AutomationProperties.GetName(b)?.StartsWith("保存 · ", StringComparison.Ordinal) == true);
        Assert.Equal(Colors.Transparent, Assert.IsAssignableFrom<ISolidColorBrush>(copy.Background).Color);
        Assert.Equal(save.Background, copy.Background);
        Preview(scope.Window, "capture-copy-idle");
    }

    [AvaloniaFact]
    public async Task CaptureToolbarRightAlignsWithASelectionAwayFromDisplayEdges()
    {
        using var scope = await CaptureScope.StartAsync(null);
        var toolbar = Assert.Single(scope.Window.GetVisualDescendants().OfType<AnnotationToolbar>());
        Assert.InRange(toolbar.Bounds.Right, 759, 761);
        Assert.InRange(toolbar.Bounds.Top, 431, 433);
        Preview(scope.Window, "capture-toolbar-right-aligned");
    }

    [AvaloniaTheory]
    [InlineData(CaptureAction.Pin, false)]
    [InlineData(CaptureAction.Long, false)]
    [InlineData(CaptureAction.Record, false)]
    [InlineData(CaptureAction.Pin, true)]
    [InlineData(CaptureAction.Long, true)]
    [InlineData(CaptureAction.Record, true)]
    public async Task FloatingToolbarsAlignToTheRightAndFlipAboveAtTheBottom(CaptureAction mode, bool nearBottom)
    {
        if (!Application.Current!.Styles.OfType<FluentTheme>().Any()) Application.Current.Styles.Add(DesignTokens.CreateTheme());
        using var lifetime = new ClassicDesktopStyleApplicationLifetime { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        typeof(ClassicDesktopStyleApplicationLifetime).GetInterfaces().Single(i => i.Name == "ISetupApplicationLifetime")
            .GetMethod("BeforeAppInit")!.Invoke(lifetime, null);
        var area = new DesktopPlacement(480, nearBottom ? 900 : 300, 440, 86);
        using var fixture = DesktopController.RenderText("Toolbar alignment");
        var placements = new List<DesktopPlacement>();
        var platform = new FakePlatform { WindowPlacementOverride = area, CaptureRegion = _ => Task.FromResult(fixture.Png) };
        platform.WindowPlaced = (placement, _) => placements.Add(placement);
        using var controller = new DesktopController(platform, lifetime);
        controller.Main = new MainWindow(controller);
        PinnedWindow? pin = null;
        LongCaptureSession? longCapture = null;
        RecordingControlWindow? recording = null;
        try
        {
            Window toolbar;
            switch (mode)
            {
                case CaptureAction.Pin:
                    pin = new PinnedWindow(controller, fixture.Clone());
                    pin.Show(); pin.Activate(); Layout(pin);
                    pin.MouseDown(new Point(12, 12), MouseButton.Left, RawInputModifiers.None);
                    pin.MouseUp(new Point(12, 12), MouseButton.Left, RawInputModifiers.None);
                    pin.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, null);
                    toolbar = Assert.Single(lifetime.Windows, window => window.Title == "Tbox 贴图工具条");
                    break;
                case CaptureAction.Long:
                    longCapture = new LongCaptureSession(controller, area, () => { });
                    longCapture.Start();
                    toolbar = Assert.Single(lifetime.Windows, window => window.Title == "Tbox 长截图工具条");
                    break;
                default:
                    recording = new RecordingControlWindow(controller, new RecordingArea(area, (int)area.Width, (int)area.Height));
                    recording.Show();
                    toolbar = recording;
                    break;
            }
            Layout(toolbar);
            var placed = Assert.Single(placements.Distinct(), placement =>
                placement.Width == Math.Ceiling(toolbar.Width) && placement.Height == Math.Ceiling(toolbar.Height));
            Assert.Equal(area.Left + area.Width, placed.Left + placed.Width);
            Assert.Equal(nearBottom ? area.Top - 12 - placed.Height : area.Top + area.Height + 12, placed.Top);
            if (!nearBottom || mode == CaptureAction.Record) PreviewToolbarAlignment(mode, toolbar, area, placed, nearBottom);
        }
        finally
        {
            if (recording is not null) await recording.CancelAsync();
            longCapture?.Stop();
            pin?.Close();
            controller.Main.Close();
        }
    }

    private static void PreviewToolbarAlignment(CaptureAction mode, Window toolbar, DesktopPlacement area, DesktopPlacement placed, bool above)
    {
        if (Environment.GetEnvironmentVariable("TBOX_UI_PREVIEWS") is not { Length: > 0 } directory) return;
        Directory.CreateDirectory(directory);
        var toolbarSize = new PixelSize((int)placed.Width, (int)placed.Height);
        using var rendered = new RenderTargetBitmap(toolbarSize, new Vector(96, 96));
        rendered.Render(toolbar);
        using var preview = new RenderTargetBitmap(new PixelSize(660, 182), new Vector(96, 96));
        var name = mode switch { CaptureAction.Pin => "贴图", CaptureAction.Long => "长截图", _ => above ? "录屏 · 下方空间不足" : "录屏" };
        var origin = new Vector(628 - area.Left - area.Width, (above ? 78 : 34) - area.Top);
        var imageBounds = new Rect(area.Left + origin.X, area.Top + origin.Y, area.Width, area.Height);
        var toolbarBounds = new Rect(placed.Left + origin.X, placed.Top + origin.Y, placed.Width, placed.Height);
        using (var context = preview.CreateDrawingContext())
        {
            context.FillRectangle(Brush.Parse("#F4F3F8"), new Rect(0, 0, 660, 182));
            var label = new FormattedText(name, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(FontFamily.Default, weight: FontWeight.SemiBold), 15, Brush.Parse("#262431"));
            context.DrawText(label, new Point(24, 18));
            var border = mode switch { CaptureAction.Long => Ui.LongYellow, CaptureAction.Record => Ui.RecordRed, _ => Brush.Parse("#6D4AE8") };
            context.DrawRectangle(Brushes.White, new Pen(border, 1), imageBounds, 4);
            context.FillRectangle(Brush.Parse("#E8E6EE"), new Rect(imageBounds.X + 16, imageBounds.Y + 16, 180, 8), 2);
            context.FillRectangle(Brush.Parse("#E8E6EE"), new Rect(imageBounds.X + 16, imageBounds.Y + 34, 260, 8), 2);
            context.DrawLine(new Pen(Brush.Parse("#A29AB5"), 1, DashStyle.Dash),
                new Point(imageBounds.Right, Math.Min(imageBounds.Top, toolbarBounds.Top) - 8),
                new Point(imageBounds.Right, Math.Max(imageBounds.Bottom, toolbarBounds.Bottom)));
            context.DrawImage(rendered, new Rect(0, 0, toolbarSize.Width, toolbarSize.Height), toolbarBounds);
        }
        var suffix = above ? "-above" : "";
        preview.Save(Path.Combine(directory, $"toolbar-alignment-{mode.ToString().ToLowerInvariant()}{suffix}.png"));
    }

    [AvaloniaFact]
    public async Task ATextNoteAtThePinEdgeFitsInBothTheEditorAndTheSavedImage()
    {
        using var scope = await CaptureScope.StartAsync(null);
        var pin = new PinnedWindow(scope.Controller, scope.Frame.Crop(new PixelRegion(90, 85, 420, 220)));
        pin.Show();
        Layout(pin);
        try
        {
            var surface = pin.GetVisualDescendants().OfType<PinSurface>().Single();
            pin.Editor.Select(AnnotationTool.Text);
            pin.Editor.FontSize = 24;
            pin.Editor.Color = 0xFFE5484D;
            pin.Editor.Begin(pin.ToPixel(new Point(surface.Bounds.Width - 9, surface.Bounds.Height - 8)), false);
            var input = pin.GetVisualDescendants().OfType<TextBox>().Single();
            input.Text = "Review note\nCheck the source figures.";
            Layout(pin);
            Assert.True(new Rect(surface.Bounds.Size).Contains(input.Bounds));
            Preview(pin, "pin-text-edge-editing");

            pin.FinishEditing();
            Layout(pin);
            var text = Assert.IsType<TextAnnotationCommand>(Assert.Single(pin.Editor.Document.Commands));
            Assert.Equal(input.Text, text.Text);
            Assert.InRange(text.Bounds.Right, 0, pin.Region.Width + .01);
            Assert.InRange(text.Bounds.Bottom, 0, pin.Region.Height + .01);
            if (Environment.GetEnvironmentVariable("TBOX_UI_PREVIEWS") is { Length: > 0 } directory)
            {
                using var rendered = AnnotationRenderer.Render(pin.Picture, pin.Region, pin.Editor.Document.Commands);
                await File.WriteAllBytesAsync(Path.Combine(directory, "pin-text-edge-saved.png"), rendered.Png);
            }
        }
        finally { pin.Close(); }
    }

    [AvaloniaFact]
    public async Task CancelledSaveLeavesPreviousScreenshotAndCacheIntact()
    {
        using var scope = await CaptureScope.StartAsync(null);
        var controller = scope.Controller;
        var cache = Path.Combine(Path.GetTempPath(), "tbox-capture-history-" + Guid.NewGuid().ToString("N"));
        controller.UseCacheDirectoryForTests(cache);
        var display = scope.Window.Frame;
        var previousRegion = new DesktopPlacement(10, 20, 30, 40);
        var previousFrame = new CaptureFrame([display], display.Id, new(10, 20, 30, 40), new AnnotationDocument().Snapshot());
        try
        {
            await controller.CompleteCaptureAsync(scope.Frame.Clone(), previousRegion, CaptureAction.Pin, frame: previousFrame);
            var cached = Assert.Single(Directory.GetFiles(cache, "*.png"));

            var cancelledRegion = new DesktopPlacement(40, 50, 60, 70);
            var cancelledFrame = previousFrame with { Selection = new(40, 50, 60, 70) };
            // The headless save picker returns no file, just as a dismissed native picker does.
            await controller.CompleteCaptureAsync(scope.Frame.Clone(), cancelledRegion, CaptureAction.Save, frame: cancelledFrame);

            Assert.Equal(cached, Assert.Single(Directory.GetFiles(cache, "*.png")));
            Assert.Same(previousFrame, Assert.Single(controller.CaptureFrames.CompatibleWith([display])).Frame);
            Assert.Equal(previousRegion, controller.CaptureHistory.LastRegion);
        }
        finally
        {
            if (Directory.Exists(cache)) Directory.Delete(cache, true);
        }
    }

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
        await controller.CaptureFrames.AddAsync(new CaptureFrame([oldDisplay], 1, oldSelection, oldEditor.Document.Snapshot()));
        var session = new CaptureSession(controller, [currentDisplay], () => { });
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

    [AvaloniaFact]
    public async Task CaptureHistorySavedBeforeARestartIsLoadedFromDiskWhenSteppedTo()
    {
        using var scope = await CaptureScope.StartAsync(null);
        scope.Window.Close();
        var controller = scope.Controller;
        var folder = Path.Combine(Path.GetTempPath(), "tbox-saved-history-" + Guid.NewGuid().ToString("N"));
        try
        {
            var savedDisplay = new CapturedDisplay(1, 0, 0, 1200, 720, scope.Frame.Png);
            var document = new AnnotationDocument();
            document.Commit(new RectangleAnnotationCommand(new(10, 10), new(50, 50), AnnotationStyle.Default));
            var savedSelection = new CaptureSelectionBounds(100, 80, 300, 200);
            await controller.CaptureFrames.OpenAsync(folder);
            await controller.CaptureFrames.AddAsync(new CaptureFrame([savedDisplay], 1, savedSelection, document.Snapshot()));
            // Reopening the folder drops the in-memory pixels, as a restart does.
            await controller.CaptureFrames.OpenAsync(Path.Combine(folder, "other"));
            await controller.CaptureFrames.OpenAsync(folder);
            Assert.Null(Assert.Single(controller.CaptureFrames.CompatibleWith([savedDisplay])).Frame);

            var currentDisplay = new CapturedDisplay(1, 0, 0, 1200, 720, scope.Frame.Png);
            var session = new CaptureSession(controller, [currentDisplay], () => { });
            session.Show();
            var window = Assert.Single(session.Windows);
            Layout(window);
            window.KeyPress(Key.OemComma, RawInputModifiers.None, PhysicalKey.Comma, null);
            for (var i = 0; i < 200 && ReferenceEquals(window.Frame, currentDisplay); i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }

            Assert.NotSame(currentDisplay, window.Frame);
            Assert.Equal(scope.Frame.Png, window.Frame.Png);
            Assert.Equal(savedSelection, session.State.Selection);
            Assert.Single(window.Editor.Document.Commands);
            window.KeyPress(Key.OemPeriod, RawInputModifiers.None, PhysicalKey.Period, null);
            Assert.Same(currentDisplay, window.Frame);
            session.Cancel();
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
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
        Assert.Equal(Color.Parse(DesignTokens.TaskSelected), ((ISolidColorBrush)rectangle.Background!).Color);
        Assert.Equal(Color.Parse(DesignTokens.TaskBackground), ((ISolidColorBrush)((LineIcon)rectangle.Content!).Stroke!).Color);
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
        Assert.Equal(new[] { "颜色 E5484D", "颜色 F5B400", "颜色 1F9D61", "颜色 3D7BFF", "颜色 6D4AE8" }, swatches.Select(b => AutomationProperties.GetName(b)!));
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
    public async Task PinMiddleClickRestoresCroppedPixelsAndShowsATemporaryBadge()
    {
        using var lifetime = new ClassicDesktopStyleApplicationLifetime { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        typeof(ClassicDesktopStyleApplicationLifetime).GetInterfaces().Single(i => i.Name == "ISetupApplicationLifetime")
            .GetMethod("BeforeAppInit")!.Invoke(lifetime, null);
        var platform = new FakePlatform();
        using var controller = new DesktopController(platform, lifetime);
        var pin = new PinnedWindow(controller, Picture.FromPixels(new byte[400 * 240 * 4], 400, 240));
        pin.Show();
        Layout(pin);
        try
        {
            platform.WindowPlacementOverride = new(100, 200, 200, 120);
            platform.WindowPlaced = (placement, _) => platform.WindowPlacementOverride = placement;
            pin.Crop(new Point(20, 20), new Point(120, 90));
            var region = pin.Region;
            Assert.NotEqual(400, region.Width);

            var point = new Point(25, 25);
            pin.MouseDown(point, MouseButton.Middle, RawInputModifiers.None);
            pin.MouseUp(point, MouseButton.Middle, RawInputModifiers.None);
            Assert.Equal(region, pin.Region);
            Assert.Equal(region.Width, platform.WindowPlacementOverride.Width);
            Assert.Equal(region.Height, platform.WindowPlacementOverride.Height);
            var label = Assert.Single(pin.GetVisualDescendants().OfType<TextBlock>(), item => item.Text == "100%");
            Assert.True(label.IsEffectivelyVisible);
            Assert.False(Assert.IsType<Border>(label.Parent).IsHitTestVisible);

            await Task.Delay(TimeSpan.FromMilliseconds(1100));
            Assert.False(label.IsEffectivelyVisible);
        }
        finally { pin.Close(); }
    }

    [Fact]
    public void PinZoomKeepsAnchorAndFitsOriginalPixelsInTheCurrentWorkArea()
    {
        var bounds = new DesktopPlacement(-800, 50, 200, 100);
        var work = new DesktopPlacement(-900, 0, 900, 700);
        var zoomed = PinnedWindow.ZoomAround(bounds, new Point(-750, 125), 400, 200);
        Assert.Equal(-750, zoomed.Left + 0.25 * zoomed.Width);
        Assert.Equal(125, zoomed.Top + 0.75 * zoomed.Height);
        var retina = PinnedWindow.OriginalSize(bounds, work, new PixelRegion(25, 10, 600, 200), 2);
        Assert.Equal(new DesktopPlacement(-850, 50, 300, 100), retina);
        var tall = PinnedWindow.OriginalSize(bounds, work, new PixelRegion(0, 0, 1000, 50_000), 1);
        Assert.Equal(14, tall.Width, precision: 8);
        Assert.Equal(700, tall.Height, precision: 8);
        Assert.Equal(0, tall.Top, precision: 8);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PinZoomAccumulatesFractionalInputAndReversesWithoutDrift(bool pinch)
    {
        using var lifetime = new ClassicDesktopStyleApplicationLifetime { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        typeof(ClassicDesktopStyleApplicationLifetime).GetInterfaces().Single(i => i.Name == "ISetupApplicationLifetime")
            .GetMethod("BeforeAppInit")!.Invoke(lifetime, null);
        var original = new DesktopPlacement(-180, 120, 53, 257);
        var platform = new FakePlatform { WindowPlacementOverride = original };
        using var controller = new DesktopController(platform, lifetime);
        var pin = new PinnedWindow(controller, Picture.FromPixels(new byte[53 * 257 * 4], 53, 257));
        pin.Show();
        Layout(pin);
        try
        {
            var surface = pin.GetVisualDescendants().OfType<PinSurface>().Single();
            var pointer = new Pointer(1, PointerType.Mouse, true);
            var anchor = new Point(original.Left + 27, original.Top + 49);
            void Zoom(double steps)
            {
                var current = platform.WindowPlacementOverride!;
                var point = new Point(anchor.X - current.Left, anchor.Y - current.Top);
                var properties = new PointerPointProperties();
                if (pinch)
                    surface.RaiseEvent(new PointerDeltaEventArgs(Gestures.PointerTouchPadGestureMagnifyEvent, surface,
                        pointer, pin, point, 0, properties, KeyModifiers.None, new Vector(0, Math.Pow(1.05, steps) - 1)));
                else
                    surface.RaiseEvent(new PointerWheelEventArgs(surface, pointer, pin, point, 0,
                        properties, KeyModifiers.None, new Vector(0, steps)));
            }
            var calls = 0;
            platform.WindowPlaced = (placement, _) =>
            {
                calls++;
                platform.WindowPlacementOverride = placement;
            };
            Zoom(-0.5);
            await Task.Delay(50);
            Assert.Equal(0, calls);

            for (var i = 0; i < 8; i++)
            {
                var resized = new TaskCompletionSource<DesktopPlacement>(TaskCreationOptions.RunContinuationsAsynchronously);
                platform.WindowPlaced = (placement, _) =>
                {
                    platform.WindowPlacementOverride = placement;
                    resized.TrySetResult(placement);
                };
                Zoom(i == 0 ? -0.5 : i % 2 == 0 ? -1 : 1);
                var bounds = await resized.Task.WaitAsync(TimeSpan.FromSeconds(2));
                if (i % 2 == 0)
                    Assert.Equal(new DesktopPlacement(-179, 122, 50, 244), bounds);
                else
                    Assert.Equal(original, bounds);
            }
        }
        finally { pin.Close(); }
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
            var stepButton = StepButton(toolbar.PropertiesSurface, "增大线宽");
            stepButton.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
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

        Assert.Empty(surface.GetVisualDescendants().OfType<Slider>());
        var increase = StepButton(surface, "增大" + name);
        var decrease = StepButton(surface, "减小" + name);
        for (var attempt = 0; attempt < 120 && scope.Window.Editor.CurrentSize != value; attempt++)
            Click(scope.Window.Editor.CurrentSize < value ? increase : decrease);
        Layout(scope.Window);
        Assert.Equal(value, scope.Window.Editor.CurrentSize);
        Assert.Contains(toolbar.PropertiesSurface.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == expected);
        var colors = toolbar.PropertiesSurface.GetVisualDescendants().OfType<Button>().Count(b => AutomationProperties.GetName(b)?.StartsWith("颜色 ") == true);
        Assert.Equal(tool is AnnotationTool.Mosaic or AnnotationTool.Eraser ? 0 : 5, colors);
    }

    [AvaloniaFact]
    public async Task EllipseWidthButtonsUpdateTheDrawingAndStopAtBothLimits()
    {
        using var scope = await CaptureScope.StartAsync(null);
        var window = scope.Window;
        var toolbar = Assert.Single(window.GetVisualDescendants().OfType<AnnotationToolbar>());
        window.Editor.Select(AnnotationTool.Ellipse);
        Layout(window);
        var minus = StepButton(toolbar.PropertiesSurface, "减小线宽");
        var plus = StepButton(toolbar.PropertiesSurface, "增大线宽");
        Click(plus);
        Assert.Equal(4, window.Editor.CurrentSize);
        while (minus.IsEnabled) Click(minus);
        Assert.Equal(1, window.Editor.CurrentSize);
        while (plus.IsEnabled) Click(plus);
        Assert.Equal(32, window.Editor.CurrentSize);
        Assert.True(toolbar.PropertiesPopup.IsOpen);
    }

    private static Button StepButton(Control surface, string label) => Assert.Single(
        surface.GetVisualDescendants().OfType<Button>(), button => AutomationProperties.GetName(button) == label);

    [AvaloniaFact]
    public async Task EscapeClosesPropertiesBeforeClearingToolOrEndingCapture()
    {
        using var scope = await CaptureScope.StartAsync(null);
        var window = scope.Window;
        var toolbar = Assert.Single(window.GetVisualDescendants().OfType<AnnotationToolbar>());
        window.Editor.Select(AnnotationTool.Text); Layout(window);
        var sizeButton = StepButton(toolbar.PropertiesSurface, "增大字号");
        sizeButton.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        Assert.True(window.IsVisible);
        Assert.True(toolbar.PropertiesPopup.IsOpen);
        sizeButton.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
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
        var recorder = new FakeRecorder(); var platform = new FakePlatform { Recorder = recorder };
        using var controller = new DesktopController(platform, new ClassicDesktopStyleApplicationLifetime());
        controller.Main = new MainWindow(controller);
        var control = new RecordingControlWindow(controller, new RecordingArea(new DesktopPlacement(100, 100, 640, 360), 640, 360));
        control.Show(); Layout(control);
        try
        {
            var bar = (Border)control.Content!;
            Assert.Equal(["系统声音", "录制鼠标", "|", "开始录制", "取消"], Names(bar));
            var boxes = bar.GetVisualDescendants().OfType<CheckBox>().ToArray();
            Assert.Equal([false, true], boxes.Select(b => b.IsChecked == true));
            var start = bar.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b)?.StartsWith("开始录制") == true);
            Assert.Equal(Color.Parse(DesignTokens.Recording), ((ISolidColorBrush)start.Resources["ButtonBackground"]!).Color);
            Preview(control, "task-record-ready");
            boxes[1].IsChecked = false;

            control.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Dispatcher.UIThread.RunJobs(); Layout(control);
            Assert.True(recorder.Started);
            Assert.False(platform.LastRecorderCursor);
            Assert.Equal(["00:00", "|", "暂停", "停止并预览", "|", "取消"], Names(bar));
            Assert.True(control.Outline.Dashed);
            Preview(control, "task-record-active");
        }
        finally { _ = control.CancelAsync(); Dispatcher.UIThread.RunJobs(); controller.Main.Close(); }
    }

    [AvaloniaFact]
    public void RecordingToolbarStaysOnTheSelectionsDisplay()
    {
        if (!Application.Current!.Styles.OfType<FluentTheme>().Any()) Application.Current.Styles.Add(DesignTokens.CreateTheme());
        // Display 2 sits to the right of the primary display; the bar must follow the selection there.
        var second = new DesktopPlacement(1600, 0, 1920, 1040);
        var platform = new FakePlatform { Recorder = new FakeRecorder(), SecondaryWorkArea = second };
        var placements = new List<DesktopPlacement>();
        platform.WindowPlaced = (placement, _) => placements.Add(placement);
        using var controller = new DesktopController(platform, new ClassicDesktopStyleApplicationLifetime());
        controller.Main = new MainWindow(controller);
        var area = new DesktopPlacement(2000, 200, 800, 450);
        var control = new RecordingControlWindow(controller, new RecordingArea(area, 800, 450));
        control.Show(); Layout(control);
        try
        {
            var bar = placements.Last();
            Assert.True(bar.Left >= second.Left && bar.Left + bar.Width <= second.Left + second.Width, $"控制条不在选区所在显示器：{bar}");
            Assert.Equal(area.Top + area.Height + 12, bar.Top);
        }
        finally { _ = control.CancelAsync(); Dispatcher.UIThread.RunJobs(); controller.Main.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(CaptureAction.Long, false, false)]
    [InlineData(CaptureAction.Long, false, true)]
    [InlineData(CaptureAction.Long, true, false)]
    [InlineData(CaptureAction.Long, true, true)]
    [InlineData(CaptureAction.Record, false, false)]
    [InlineData(CaptureAction.Record, false, true)]
    [InlineData(CaptureAction.Record, true, false)]
    [InlineData(CaptureAction.Record, true, true)]
    public async Task LongCaptureAndRecordingKeepTheDashboardHiddenFromHandoffThroughDismissal(CaptureAction mode, bool fromScreenshot, bool mainVisible)
    {
        using var scope = await CaptureScope.StartAsync(fromScreenshot ? null : mode, mainVisible: mainVisible);
        void CheckVisibility(bool expected = false)
        {
            App.RestoreOnReopen(scope.Controller, new ActivatedEventArgs(ActivationKind.Reopen));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(expected, scope.Controller.Main.IsVisible);
        }

        CheckVisibility(fromScreenshot && mainVisible);
        var longCapture = mode == CaptureAction.Long;
        if (fromScreenshot)
        {
            var toolbarMenu = Assert.Single(scope.Window.GetVisualDescendants().OfType<AnnotationToolbar>());
            Click(toolbarMenu.MainRow.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == "更多操作"));
            Layout(scope.Window);
            var entry = longCapture ? "长截图" : "录屏";
            Click(toolbarMenu.MorePopup.Child!.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == entry));
        }
        else scope.Window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);

        CheckVisibility();
        var toolbar = await scope.WaitForWindowAsync(longCapture ? "Tbox 长截图工具条" : "Tbox 录屏控制");
        Assert.NotNull(toolbar);
        CheckVisibility();
        toolbar.Close();
        Assert.False(scope.Controller.Main.IsVisible);
    }

    [AvaloniaFact]
    public async Task CopyingACaptureRestoresThePreviousAppAfterWritingTheClipboard()
    {
        var restored = new TaskCompletionSource();
        using var scope = await CaptureScope.StartAsync(null, restoreForeground: () => restored.TrySetResult());
        var cache = Directory.CreateTempSubdirectory("tbox-capture-focus-");
        try
        {
            scope.Controller.UseCacheDirectoryForTests(cache.FullName);
            scope.Window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            await restored.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(scope.Controller.Main.IsVisible);
            Assert.False(scope.Window.IsVisible);
            using var copied = await scope.Controller.Main.Clipboard!.TryGetBitmapAsync();
            Assert.NotNull(copied);
            for (var attempt = 0; attempt < 100 && !cache.GetFiles("*.png").Any(); attempt++) await Task.Delay(10);
            Assert.Single(cache.GetFiles("*.png"));
        }
        finally { cache.Delete(recursive: true); }
    }

    [AvaloniaFact]
    public async Task ContactSheetPlayheadSnapsToSamplesAndShowsTheirFrames()
    {
        if (!Application.Current!.Styles.OfType<FluentTheme>().Any()) Application.Current.Styles.Add(DesignTokens.CreateTheme());
        // The real fixture and bundled FFmpeg decode the six sample thumbnails. macOS test builds do not bundle FFmpeg
        // (the media smoke test covers decoding there), so the card then shows its placeholder.
        // Output depth differs per platform (bin/x64/Release/Windows target framework vs bin/Release on Mac), so walk up to the repository.
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "assets", "media-test", "fixture.mp4"))) root = root.Parent;
        Assert.NotNull(root);
        var fixture = Path.Combine(root.FullName, "assets", "media-test", "fixture.mp4");
        var decodes = File.Exists(RecordingExporter.FfmpegPath);
        Assert.True(decodes || !OperatingSystem.IsWindows(), "Windows 测试输出应带 FFmpeg。");
        using var controller = new DesktopController(new FakePlatform(), new ClassicDesktopStyleApplicationLifetime());
        controller.Main = new MainWindow(controller);
        var workspace = new RecordingWorkspace(Path.Combine(Path.GetTempPath(), "tbox-sheet-" + Guid.NewGuid().ToString("N")));
        File.Copy(fixture, workspace.MasterFilePath);
        var review = new RecordingReviewWindow(controller, workspace, new RecordingArea(new DesktopPlacement(0, 0, 320, 240), 320, 240),
            new RecordingOutput(workspace.MasterFilePath, false, 2), TimeSpan.FromSeconds(2));
        review.Show(); Layout(review);
        try
        {
            Click(review.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == "六宫格")); Layout(review);
            var track = review.Track; var ticks = track.Ticks;
            Assert.Equal(6, ticks.Count);
            for (var wait = 0; decodes && wait < 200 && review.LoadedThumbnails < 6; wait++) { await Task.Delay(50); Dispatcher.UIThread.RunJobs(); }
            Assert.Equal(decodes ? 6 : 0, review.LoadedThumbnails);

            Assert.Equal(ticks, track.FrameTimes);
            foreach (var index in Enumerable.Range(0, 6))
            {
                var center = track.TranslatePoint(new Point(6 + (track.Bounds.Width - 12) * (index + .5) / 6, 18), review)!.Value;
                review.MouseDown(center, MouseButton.Left, RawInputModifiers.None);
                review.MouseUp(center, MouseButton.Left, RawInputModifiers.None);
                Assert.Equal(ticks[index], track.Position);
            }
            // Dragging the playhead 5 px past node 04 lands exactly on its sample time.
            Point At(int tick, double offset) => track.TranslatePoint(new Point(6 + (track.Bounds.Width - 12) * (ticks[tick] / track.Duration) + offset, track.Bounds.Height - 14), review)!.Value;
            review.MouseDown(At(1, 30), MouseButton.Left, RawInputModifiers.None);
            review.MouseMove(At(3, 5), RawInputModifiers.LeftMouseButton); Layout(review);
            Assert.Equal(3, track.HotTick);
            Assert.Equal(ticks[3], track.Position);
            // The card above node 04 shows that frame and its label, centred over the tick.
            Assert.True(review.ThumbnailPopup.IsOpen);
            var card = (Border)review.ThumbnailPopup.Child!;
            Assert.Equal("04 · " + RecordingTrimBar.FormatPrecise(ticks[3]), card.GetVisualDescendants().OfType<TextBlock>().Single().Text);
            Assert.Equal(decodes, card.GetVisualDescendants().OfType<Image>().Single().Source is not null);
            var tick = track.TranslatePoint(track.TickAnchor(3).Center, review)!.Value;
            var centre = card.TranslatePoint(new Point(card.Bounds.Width / 2, card.Bounds.Height), review)!.Value;
            Assert.InRange(centre.X - tick.X, -2, 2); Assert.True(centre.Y <= track.TranslatePoint(default, review)!.Value.Y);
            Preview(review, "recording-review-contact-sheet-snap");
            // Further away the playhead moves freely again.
            review.MouseMove(At(3, 20), RawInputModifiers.LeftMouseButton);
            Assert.Equal(-1, track.HotTick); Assert.False(review.ThumbnailPopup.IsOpen);
            Assert.NotEqual(ticks[3], track.Position);
            review.MouseUp(At(3, 20), MouseButton.Left, RawInputModifiers.None);

            // Hovering a node shows its thumbnail without moving the playhead.
            var position = track.Position;
            review.MouseMove(At(5, -3), RawInputModifiers.None); Layout(review);
            Assert.Equal(5, track.HotTick); Assert.Equal(position, track.Position); Assert.True(review.ThumbnailPopup.IsOpen);
            Click(review.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == "MP4")); Layout(review);
            Assert.Empty(track.Ticks); Assert.Equal(-1, track.HotTick); Assert.False(review.ThumbnailPopup.IsOpen);
        }
        finally { await review.RequestCloseAsync(discardUnsaved: true); controller.Main.Close(); }
    }

    [AvaloniaFact]
    public async Task RecordingReviewChoosesFormatRateSizeAndRangeWhenSaving()
    {
        if (!Application.Current!.Styles.OfType<FluentTheme>().Any()) Application.Current.Styles.Add(DesignTokens.CreateTheme());
        var temp = Path.Combine(Path.GetTempPath(), "tbox-review-" + Guid.NewGuid().ToString("N"));
        using var controller = new DesktopController(new FakePlatform(), new ClassicDesktopStyleApplicationLifetime());
        controller.Main = new MainWindow(controller);
        var workspace = new RecordingWorkspace(temp);
        File.WriteAllBytes(workspace.MasterFilePath, [1, 2, 3]);
        var area = new RecordingArea(new DesktopPlacement(0, 0, 2560, 1440), 2560, 1440);
        var review = new RecordingReviewWindow(controller, workspace, area, new RecordingOutput(workspace.MasterFilePath, false, 42), TimeSpan.FromSeconds(42));
        review.Show(); Layout(review);
        Button Chip(string name) => review.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == name);
        bool Shows(string text) => review.GetVisualDescendants().OfType<TextBlock>().Any(t => t.IsEffectivelyVisible && t.Text == text);
        try
        {
            Assert.True(Shows("2560 × 1440 · 00:42.0 · 30 FPS · 静音"));
            Assert.True(Shows("导出全部 · 00:00.0 – 00:42.0"));
            Assert.Equal(["MP4", "GIF", "WebP", "六宫格"], RecordingReviewWindow.Formats.Select(f => AutomationProperties.GetName(Chip(f switch
            {
                RecordingExportFormat.Mp4 => "MP4", RecordingExportFormat.Gif => "GIF", RecordingExportFormat.WebP => "WebP", _ => "六宫格"
            }))));
            // Opening defaults: full range, master rate, full size; an unchanged MP4 copies the master.
            var options = review.Options();
            Assert.Equal(new RecordingExportOptions(RecordingExportFormat.Mp4, 30, new(2560, 1440), new(TimeSpan.Zero, TimeSpan.FromSeconds(42))), options);
            Assert.True(options.CopiesMaster(new(2560, 1440), TimeSpan.FromSeconds(42)));
            Assert.Equal(["100%", "75%", "50%", "25%", "自定义"], (string[])review.Size.ItemsSource!);
            Assert.True(Shows("输出 2560 × 1440"));
            Preview(review, "recording-review-mp4");

            // Trimming moves into the exported options and shows the reset link.
            review.Track.Range = new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(29.8)); Layout(review);
            Assert.True(Shows("截取 00:05.0 – 00:29.8 · 共 00:24.8"));
            Assert.True(Chip("恢复全部").IsEffectivelyVisible);

            // GIF: 10 FPS and the largest preset within 1280 px; a custom width keeps the aspect ratio and never enlarges.
            Click(Chip("GIF")); Layout(review);
            Assert.Equal(new RecordingExportOptions(RecordingExportFormat.Gif, 10, new(1280, 720), new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(29.8))), review.Options());
            review.Size.SelectedIndex = 4; review.CustomWidth.Text = "640"; Layout(review);
            Assert.True(review.CustomWidth.IsEffectivelyVisible);
            Assert.True(Shows("× 360"));
            Assert.Equal(new VideoSize(640, 360), review.Options().Size);
            review.CustomWidth.Text = "99999";
            Assert.Equal(new VideoSize(2560, 1440), review.Options().Size);
            review.CustomWidth.Text = "640"; review.FrameRate.SelectedIndex = 0;
            Assert.Equal(1, review.Options().FramesPerSecond);
            Preview(review, "recording-review-gif-trimmed");

            Click(Chip("WebP")); Layout(review);
            Assert.Equal((25, new VideoSize(2560, 1440)), (review.Options().FramesPerSecond, review.Options().Size));
            Assert.False(review.CustomWidth.IsEffectivelyVisible);

            // 六宫格: no frame rate, per-cell long edge defaults to 320, six sample ticks inside the range.
            Click(Chip("六宫格")); Layout(review);
            Assert.False(review.FrameRate.IsEffectivelyVisible);
            Assert.Equal(new RecordingExportOptions(RecordingExportFormat.ContactSheet, 0, new(320, 180), new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(29.8))), review.Options());
            Assert.Equal(["原尺寸", "每格 ≤ 640 px", "每格 ≤ 480 px", "每格 ≤ 320 px", "每格 ≤ 200 px", "自定义"],
                (string[])review.Size.ItemsSource!);
            Assert.Equal(6, review.Track.Ticks.Count);
            Assert.Equal(TimeSpan.FromSeconds(5), review.Track.Ticks[0]);
            Assert.Equal(ContactSheetPolicy.SampleTimes(review.Options().Range, RecordingFrameRatePolicy.RecordingFrameRate), review.Track.FrameTimes);
            Assert.True(Shows("输出 1008 × 452"));
            review.Size.SelectedIndex = 0; Layout(review);
            Assert.True(Shows("输出 7728 × 2972"));
            review.Size.SelectedIndex = 3; Layout(review);
            var panel = review.Size.TranslatePoint(default, review)!.Value;
            var timeline = review.Track.TranslatePoint(default, review)!.Value;
            Assert.True(panel.X > timeline.X + review.Track.Bounds.Width);
            review.Width = 540; Layout(review);
            Assert.True(review.Size.TranslatePoint(default, review)!.Value.Y > review.Track.TranslatePoint(default, review)!.Value.Y);
            review.Width = 940; Layout(review);
            Preview(review, "recording-review-contact-sheet");

            Click(Chip("恢复全部")); Layout(review);
            Assert.True(review.Options().Range.IsFull(TimeSpan.FromSeconds(42)));
            Assert.False(Chip("恢复全部").IsEffectivelyVisible);
        }
        finally { await review.RequestCloseAsync(discardUnsaved: true); controller.Main.Close(); }
        Assert.False(Directory.Exists(workspace.SessionDirectory));
    }

    [AvaloniaFact]
    public async Task ScrubbingAStoppedReviewKeepsTheSelectedTimeAndShowsTheDecodedFrame()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "assets", "media-test", "fixture.mp4"))) root = root.Parent;
        Assert.NotNull(root);
        using var controller = new DesktopController(new FakePlatform(), new ClassicDesktopStyleApplicationLifetime());
        controller.Main = new MainWindow(controller);
        var workspace = new RecordingWorkspace(Path.Combine(Path.GetTempPath(), "tbox-scrub-" + Guid.NewGuid().ToString("N")));
        File.Copy(Path.Combine(root.FullName, "assets", "media-test", "fixture.mp4"), workspace.MasterFilePath);
        var area = new RecordingArea(new DesktopPlacement(0, 0, 320, 240), 320, 240);
        var review = new RecordingReviewWindow(controller, workspace, area, new RecordingOutput(workspace.MasterFilePath, false, 2), TimeSpan.FromSeconds(2));
        review.Show(); Layout(review);
        try
        {
            var track = review.Track;
            Point At(double fraction, double y) => track.TranslatePoint(new Point(6 + (track.Bounds.Width - 12) * fraction, y), review)!.Value;
            // The lower part of the white playhead is draggable even when it shares the start handle's position.
            review.MouseDown(At(0, track.Bounds.Height - 7), MouseButton.Left, RawInputModifiers.None);
            review.MouseMove(At(0.63, 8), RawInputModifiers.LeftMouseButton);
            review.MouseUp(At(0.63, 8), MouseButton.Left, RawInputModifiers.None);
            Assert.True(track.Range.IsFull(track.Duration));
            var selected = track.Position;
            Assert.InRange(selected.TotalMilliseconds, 1250, 1270);

            var image = review.GetVisualDescendants().OfType<Image>().Single(i => AutomationProperties.GetName(i) == "录屏定位画面");
            var decodes = File.Exists(RecordingExporter.FfmpegPath);
            for (var wait = 0; decodes && image.Source is null && wait < 100; wait++) await Task.Delay(20);
            Assert.Equal(decodes, image.Source is not null);
            await Task.Delay(150);
            Assert.Equal(selected, track.Position);
            Assert.Contains(review.GetVisualDescendants().OfType<TextBlock>(), block => block.Text == "00:01.2 / 00:02.0");

            // Press the red start handle above the white playhead and release at a different time.
            review.MouseDown(At(0, track.Bounds.Height - 18), MouseButton.Left, RawInputModifiers.None);
            review.MouseUp(At(0.27, track.Bounds.Height - 18), MouseButton.Left, RawInputModifiers.None);
            Assert.InRange(review.Options().Range.Start.TotalMilliseconds, 530, 550);
            Assert.Equal(review.Options().Range.Start, track.Position);
            Assert.Contains(review.GetVisualDescendants().OfType<TextBlock>(), block => block.Text == "00:00.5 / 00:02.0");
        }
        finally { await review.RequestCloseAsync(discardUnsaved: true); controller.Main.Close(); }
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

        public async Task<Window?> WaitForWindowAsync(string title)
        {
            for (var attempt = 0; attempt < 100; attempt++)
            {
                if (_lifetime.Windows.FirstOrDefault(window => window.Title == title) is { } window) return window;
                await Task.Delay(10);
            }
            return null;
        }

        public static async Task<CaptureScope> StartAsync(CaptureAction? preset, double topSafeInset = 0, bool mainVisible = false,
            Action? restoreForeground = null)
        {
            if (!Application.Current!.Styles.OfType<FluentTheme>().Any()) Application.Current.Styles.Add(DesignTokens.CreateTheme());
            var frame = Desktop(1200, 720);
            var lifetime = new ClassicDesktopStyleApplicationLifetime { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            typeof(ClassicDesktopStyleApplicationLifetime).GetInterfaces().Single(i => i.Name == "ISetupApplicationLifetime")
                .GetMethod("BeforeAppInit")!.Invoke(lifetime, null);
            var platform = new FakePlatform
            {
                HasScreenPermission = true,
                CaptureForegroundRestore = restoreForeground,
                Displays = [new CapturedDisplay(1, 0, 0, 1200, 720, frame.Png)],
                CaptureRegion = async token => { await Task.Delay(Timeout.Infinite, token); return frame.Png; },
                Recorder = new FakeRecorder(),
                DisplayTopSafeInset = topSafeInset
            };
            var controller = new DesktopController(platform, lifetime);
            controller.Main = new MainWindow(controller);
            if (mainVisible) controller.Main.Show();
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
