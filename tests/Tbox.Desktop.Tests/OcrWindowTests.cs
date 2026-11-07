using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tbox.Desktop.Platform;
using Tbox.Desktop.Views;
using Tbox.Core;
using Tbox.Desktop.Models;
using Xunit;

namespace Tbox.Desktop.Tests;

public sealed class OcrWindowTests
{
    [AvaloniaFact]
    public async Task OriginalWordMetadataStaysInMemoryUntilReplacedOrClosed()
    {
        using var fixture = new Fixture();
        var word = new RecognizedWord("原图", .1, .2, .3, .1);
        var result = new RecognitionResult("原图", [new RecognizedLine("原图", .1, .2, .3, .1) { Words = [word] }])
        { ImageWidth = 1280, ImageHeight = 200 };
        fixture.Platform.Recognizer = (_, _) => Task.FromResult(result);
        var window = fixture.Open(); await Drain();
        window.GetVisualDescendants().OfType<TextBox>().Single().Text = "编辑后的文字";
        var retained = Assert.IsType<RecognitionResult>(window.Recognition);
        Assert.Same(result, retained);
        Assert.Same(word, Assert.Single(Assert.Single(retained.Lines).Words));
        var next = new TaskCompletionSource<RecognitionResult>();
        fixture.Platform.Recognizer = (_, _) => next.Task;
        fixture.Open(); Assert.Null(window.Recognition);
        next.SetResult(result); await Drain(); Assert.Same(result, window.Recognition);
        window.Close(); Assert.Null(window.Recognition);
    }

    [Fact]
    public void NativeAcceptanceRejectsMissingWordsWrongSourceSizeAndInvalidCoordinates()
    {
        var line = new RecognizedLine("12345", .1, .2, .3, .1)
        { Words = [new RecognizedWord("12345", .1, .2, .3, .1)] };
        var result = new RecognitionResult("12345", [line]) { ImageWidth = 1280, ImageHeight = 200 };
        var size = new PixelSize(1280, 200);
        Assert.True(SmokeTest.HasValidOcrMetadata(result, size));
        Assert.False(SmokeTest.HasValidOcrMetadata(result with { ImageWidth = 640 }, size));
        Assert.False(SmokeTest.HasValidOcrMetadata(result with { Lines = [line with { Words = [] }] }, size));
        Assert.False(SmokeTest.HasValidOcrMetadata(result with { Lines = [line with { Words = [line.Words[0] with { Left = double.NaN }] }] }, size));
        Assert.False(SmokeTest.HasValidOcrMetadata(result with { Lines = [line with { Words = [line.Words[0] with { Left = .9 }] }] }, size));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2)]
    public void PlacementKeepsLogicalDefaultSizeOnScaledNegativeCoordinateDisplay(double scale)
    {
        var source = new OcrWindowSource(new PixelRect(-3840, -2160, 3840, 2160), new PixelPoint(-3000, -1500), scale);
        var result = source.Fit(new Size(800, 600), new Size(16, 38));
        Assert.Equal(new Size(800, 600), result.ClientSize);
        var outer = new PixelRect(result.Position, new PixelSize((int)Math.Ceiling(816 * scale), (int)Math.Ceiling(638 * scale)));
        Assert.True(source.WorkArea.Contains(outer));
        Assert.True(result.Position.X < 0); Assert.True(result.Position.Y < 0);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2)]
    public void SmallWorkAreaIncludesTitleBarWhenShrinkingAndClamping(double scale)
    {
        var source = new OcrWindowSource(new PixelRect(-700, 50, 600, 400), new PixelPoint(1000, 1000), scale);
        var result = source.Fit(new Size(800, 600), new Size(16, 38));
        Assert.Equal(600 / scale - 16, result.ClientSize.Width, 6);
        Assert.Equal(400 / scale - 38, result.ClientSize.Height, 6);
        Assert.Equal(source.WorkArea.Position, result.Position);
    }

    [AvaloniaFact]
    public void ReusedResultMovesToNewSourceAndRetainsUserSizeAndTopmostChoice()
    {
        using var fixture = new Fixture();
        var left = new OcrWindowSource(new PixelRect(-1920, 0, 1920, 1040), new PixelPoint(-1800, 80), 1);
        var window = fixture.Open(source: left);
        Assert.True(left.WorkArea.Contains(new PixelRect(window.Position, new PixelSize(800, 600))));
        Assert.False(window.Topmost);
        window.Width = 700; window.Height = 500;
        var above = new OcrWindowSource(new PixelRect(0, -1200, 1920, 1160), new PixelPoint(1850, -100), 1);
        Assert.Same(window, fixture.Open(topmost: true, source: above));
        Assert.Equal(700, window.Width); Assert.Equal(500, window.Height);
        Assert.True(above.WorkArea.Contains(new PixelRect(window.Position, new PixelSize(700, 500))));
        Assert.True(window.Topmost);
        window.WindowState = WindowState.Maximized;
        fixture.Open(source: left);
        Assert.Equal(WindowState.Maximized, window.WindowState);
        Assert.False(window.Topmost);
    }

    [AvaloniaFact]
    public void SourceCoordinatesRemainAvailableAfterTheCaptureWindowCloses()
    {
        var sourceWindow = new Window { Width = 600, Height = 400, WindowStartupLocation = WindowStartupLocation.Manual };
        sourceWindow.Show(); sourceWindow.Position = new PixelPoint(240, 120);
        var selectedCenter = sourceWindow.PointToScreen(new Point(100, 150));
        var source = OcrWindowSource.FromWindow(sourceWindow, selectedCenter);
        sourceWindow.Close();
        Assert.NotNull(source);
        Assert.Equal(selectedCenter, source.Center);
        var placement = source.Fit(new Size(800, 600), default);
        Assert.True(source.WorkArea.Contains(new PixelRect(placement.Position,
            new PixelSize((int)Math.Ceiling(placement.ClientSize.Width * source.DesktopScaling),
                (int)Math.Ceiling(placement.ClientSize.Height * source.DesktopScaling)))));
    }

    [AvaloniaFact]
    public void PinRecognitionUsesOriginalPixelsAndKeepsPinPositionAndAnnotations()
    {
        using var fixture = new Fixture();
        byte[]? recognized = null;
        fixture.Platform.Recognizer = (png, _) => { recognized = png.ToArray(); return Task.FromResult(new RecognitionResult("原图识别", [])); };
        using var picture = DesktopController.RenderText("原图内容");
        var pin = new PinnedWindow(fixture.Controller, new Picture(picture.Png));
        pin.Show(); pin.Position = new PixelPoint(300, 100);
        var position = pin.Position; var size = pin.ClientSize;
        pin.Editor.Select(AnnotationTool.Arrow);
        pin.Editor.Begin(new AnnotationPoint(1, 1), false); pin.Editor.End(new AnnotationPoint(60, 30), false);
        var command = Assert.Single(pin.Editor.Document.Commands);
        try
        {
            pin.Recognize();
            Assert.NotNull(recognized);
            using var received = new Picture(recognized);
            Assert.Equal(picture.GetPixels(), received.GetPixels());
            Assert.Equal(position, pin.Position); Assert.Equal(size, pin.ClientSize); Assert.True(pin.IsVisible);
            Assert.Same(command, Assert.Single(pin.Editor.Document.Commands));
            Assert.Equal(AnnotationTool.Arrow, pin.Editor.Tool);
            Assert.True(Assert.Single(fixture.Lifetime.Windows.OfType<OcrWindow>()).Topmost);
        }
        finally { pin.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(460, 360)]
    [InlineData(800, 600)]
    [InlineData(1200, 900)]
    public void ExpandedOriginalSharesAvailableHeightWithEditableText(int width, int height)
    {
        using var fixture = new Fixture();
        var window = fixture.Open();
        window.Width = width; window.Height = height; Layout(window);
        var image = window.GetVisualDescendants().OfType<Image>().Single();
        var editor = window.GetVisualDescendants().OfType<TextBox>().Single();
        Assert.False(image.IsVisible);
        var collapsedHeight = editor.Bounds.Height;
        var expand = window.GetVisualDescendants().OfType<CheckBox>().Single(c => Equals(c.Content, "显示原图"));
        expand.IsChecked = true; Layout(window);
        var layout = Assert.IsType<Grid>(window.Content);
        var ratio = layout.RowDefinitions[1].ActualHeight / (layout.RowDefinitions[1].ActualHeight + layout.RowDefinitions[2].ActualHeight);
        Assert.InRange(ratio, .34, .36);
        Assert.True(editor.Bounds.Height > 20);
        foreach (var control in window.GetVisualDescendants().OfType<Control>()
            .Where(c => c.IsEffectivelyVisible && c is Button or CheckBox or TextBox))
        {
            var p = control.TranslatePoint(default, window)!.Value;
            Assert.True(p.X >= -1 && p.Y >= -1 && p.X + control.Bounds.Width <= width + 1 && p.Y + control.Bounds.Height <= height + 1,
                $"{control.GetType().Name} exceeds {width}x{height}: {p} {control.Bounds}");
        }
        expand.IsChecked = false; Layout(window);
        Assert.False(image.IsVisible);
        Assert.InRange(editor.Bounds.Height, collapsedHeight - 1, collapsedHeight + 1);
    }

    [AvaloniaFact]
    public void NewRecognitionRestoresMinimizedWindowWithoutCreatingAnother()
    {
        using var fixture = new Fixture();
        var window = fixture.Open();
        window.WindowState = WindowState.Minimized;
        Assert.Same(window, fixture.Open(topmost: true));
        Assert.Equal(WindowState.Normal, window.WindowState);
        Assert.True(window.Topmost); Assert.True(window.IsVisible);
        Assert.Same(window, fixture.Open());
        Assert.False(window.Topmost);
    }

    [AvaloniaFact]
    public async Task NewRequestReplacesEditedTextAndLateOldResultCannotOverwriteIt()
    {
        using var fixture = new Fixture();
        var first = new TaskCompletionSource<RecognitionResult>();
        var second = new TaskCompletionSource<RecognitionResult>();
        var requests = 0; CancellationToken firstToken = default;
        fixture.Platform.Recognizer = (_, token) =>
        {
            if (++requests == 1) { firstToken = token; return first.Task; }
            return second.Task;
        };
        var window = fixture.Open();
        var editor = window.GetVisualDescendants().OfType<TextBox>().Single();
        editor.Text = "用户编辑过的内容";
        Assert.Same(window, fixture.Open());
        Assert.True(firstToken.IsCancellationRequested);
        Assert.Equal("", editor.Text);
        second.SetResult(new RecognitionResult("新的识别结果\n第二行", []));
        await Drain();
        Assert.Equal("新的识别结果\n第二行", editor.Text);
        first.SetResult(new RecognitionResult("过期结果", []));
        await Drain();
        Assert.Equal("新的识别结果\n第二行", editor.Text);
    }

    [AvaloniaFact]
    public async Task ClosingCancelsRecognitionAndLateCompletionDoesNotReviveWindow()
    {
        using var fixture = new Fixture();
        var completion = new TaskCompletionSource<RecognitionResult>();
        CancellationToken token = default;
        fixture.Platform.Recognizer = (_, requested) => { token = requested; return completion.Task; };
        var window = fixture.Open(); window.Close();
        Assert.True(token.IsCancellationRequested);
        completion.SetResult(new RecognitionResult("关闭后完成", [])); await Drain();
        Assert.Empty(fixture.Lifetime.Windows.OfType<OcrWindow>());
        Assert.False(window.IsVisible);
    }

    [AvaloniaFact]
    public async Task CopyAllUsesCurrentEditedText()
    {
        using var fixture = new Fixture();
        var window = fixture.Open(); await Drain();
        var editor = window.GetVisualDescendants().OfType<TextBox>().Single();
        editor.Text = "修改后的整段文字\n含第二行"; editor.SelectionStart = 0; editor.SelectionEnd = 2;
        window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "复制全部"))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Drain();
#pragma warning disable CS0618 // Inspect the headless clipboard through the supported text convenience API.
        Assert.Equal(editor.Text, await window.Clipboard!.GetTextAsync());
#pragma warning restore CS0618
    }

    private static async Task Drain() { await Task.Yield(); Dispatcher.UIThread.RunJobs(); await Task.Yield(); }
    private static void Layout(Window window) { window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }

    private sealed class Fixture : IDisposable
    {
        public FakePlatform Platform { get; } = new() { Recognizer = (_, _) => Task.FromResult(new RecognitionResult("识别结果", [])) };
        public ClassicDesktopStyleApplicationLifetime Lifetime { get; } = new() { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        public DesktopController Controller { get; }
        public Fixture()
        {
            if (!Avalonia.Application.Current!.Styles.OfType<FluentTheme>().Any())
                Avalonia.Application.Current.Styles.Add(DesignTokens.CreateTheme());
            typeof(ClassicDesktopStyleApplicationLifetime).GetInterfaces().Single(i => i.Name == "ISetupApplicationLifetime")
                .GetMethod("BeforeAppInit")!.Invoke(Lifetime, null);
            Controller = new DesktopController(Platform, Lifetime);
            Controller.Main = new MainWindow(Controller);
        }
        public OcrWindow Open(bool topmost = false, OcrWindowSource? source = null)
        {
            using var picture = DesktopController.RenderText("测试原图");
            Controller.ShowOcr(picture, topmost, source);
            var result = Assert.Single(Lifetime.Windows.OfType<OcrWindow>());
            Layout(result); return result;
        }
        public void Dispose() { Controller.Dispose(); Controller.Main.Close(); Lifetime.Dispose(); }
    }
}
