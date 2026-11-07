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
using Xunit;

namespace Tbox.Desktop.Tests;

public sealed class OcrWindowTests
{
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
        public OcrWindow Open(bool topmost = false)
        {
            using var picture = DesktopController.RenderText("测试原图");
            Controller.ShowOcr(picture, topmost);
            var result = Assert.Single(Lifetime.Windows.OfType<OcrWindow>());
            Layout(result); return result;
        }
        public void Dispose() { Controller.Dispose(); Controller.Main.Close(); Lifetime.Dispose(); }
    }
}
