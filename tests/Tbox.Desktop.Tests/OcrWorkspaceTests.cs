using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.Text.Json;
using Tbox.Core.ModelServices;
using Tbox.Desktop.ModelServices;
using Tbox.Desktop.Platform;
using Tbox.Desktop.Views;
using Xunit;

namespace Tbox.Desktop.Tests;

public sealed class OcrWorkspaceTests
{
    [AvaloniaFact]
    public void MacPixelInputPreservesOpaqueBgraAndFallsBackForEncodedAndTransparentPictures()
    {
        byte[] colors = [9, 19, 200, 255, 180, 7, 31, 255];
        using var opaque = Models.Picture.FromPixels(colors, 2, 1);
        Assert.Equal(colors, MacPlatformServices.OpaqueOcrPixels(opaque));
        Assert.False(opaque.HasEncodedPng);

        using var translucent = Models.Picture.FromPixels([22, 31, 48, 255, 60, 70, 90, 254], 2, 1);
        Assert.Null(MacPlatformServices.OpaqueOcrPixels(translucent));
        var imported = opaque.Png;
        using var encoded = new Models.Picture(imported);
        Assert.Null(MacPlatformServices.OpaqueOcrPixels(encoded));
        Assert.Same(imported, encoded.Png);
    }

    [AvaloniaFact]
    public void ControllerHandsOverOwnedImagesAndProtectsBorrowedOnesWhenReplacingRecognition()
    {
        var seen = new List<(Models.Picture Picture, CancellationToken Token)>();
        var pending = new TaskCompletionSource<RecognitionResult>();
        var platform = new FakePlatform
        {
            OcrPicture = (image, token) => { seen.Add((image, token)); return pending.Task; }
        };
        var controller = new DesktopController(platform, new ClassicDesktopStyleApplicationLifetime());
        var owned = Models.Picture.FromPixels([11, 2, 24, 255], 1, 1);
        using var borrowed = Models.Picture.FromPixels([19, 42, 27, 255], 1, 1);
        controller.ShowOcr(owned, false, takeOwnership: true);
        Assert.Same(owned, Assert.Single(seen).Picture);
        Assert.False(owned.HasEncodedPng);
        controller.ShowOcr(borrowed, false);
        Assert.Equal(2, seen.Count);
        Assert.NotSame(borrowed, seen[1].Picture);
        Assert.True(seen[0].Token.IsCancellationRequested);
        controller.Dispose();
        Assert.True(seen[1].Token.IsCancellationRequested);
        pending.SetResult(new RecognitionResult("过时结果", []));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(new byte[] { 19, 42, 27, 255 }, borrowed.GetPixels());
    }

    [AvaloniaFact]
    public void RepeatOfflineRecognitionKeepsTheOriginalBitmapAlive()
    {
        using var scope = new AiScope((_, _) => throw new InvalidOperationException());
        var picture = Models.Picture.FromPixels([3, 17, 45, 255], 1, 1);
        scope.Show(picture);
        var preview = Assert.Single(scope.Window.GetVisualDescendants().OfType<Image>(),
            image => AutomationProperties.GetName(image) == "识别原图");
        Assert.Same(picture.Bitmap, preview.Source);
        scope.Editor.Text = "edited";
        Click(scope.Window, "离线识别");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("离线结果", scope.Editor.Text);
        Assert.Same(picture.Bitmap, preview.Source);
        Assert.Equal(new byte[] { 3, 17, 45, 255 }, picture.GetPixels());
    }

    [AvaloniaFact]
    public async Task ImageIsSentOnlyAfterChoosingAModelAndConfirming()
    {
        var calls = 0;
        string? body = null;
        Uri? endpoint = null;
        using var scope = new AiScope(async (request, ct) =>
        {
            calls++;
            endpoint = request.RequestUri;
            body = await request.Content!.ReadAsStringAsync(ct);
            return AiResponse("图片原文\n第二行");
        });
        await scope.SaveModelAsync();
        var picture = DesktopController.RenderText("图片原文");
        var png = picture.Png.ToArray();
        scope.Show(picture);
        Assert.Equal("离线结果", scope.Editor.Text);
        Assert.Equal(0, calls);

        var choose = scope.Window.RecognizeWithAiAsync();
        PickModel(scope.Window, out var dialog);
        Assert.Equal(0, calls);
        Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.Contains("https://vision.example.com/v1/chat/completions") == true);
        TaskToolbarTests.Preview(dialog, "ocr-ai-confirm");
        Click(dialog, "取消");
        await choose;
        Assert.Equal("离线结果", scope.Editor.Text);
        Assert.Equal(0, calls);

        choose = scope.Window.RecognizeWithAiAsync();
        PickModel(scope.Window, out dialog);
        Click(dialog, "发送并识别");
        await choose;
        Assert.Equal(1, calls);
        Assert.Equal("图片原文\n第二行", scope.Editor.Text);
        Assert.Equal("https://vision.example.com/v1/chat/completions", endpoint?.AbsoluteUri);
        using var request = JsonDocument.Parse(Assert.IsType<string>(body));
        Assert.Equal("vision-fixture", request.RootElement.GetProperty("model").GetString());
        var parts = request.RootElement.GetProperty("messages")[0].GetProperty("content");
        Assert.Equal(2, parts.GetArrayLength());
        Assert.Equal("data:image/png;base64," + Convert.ToBase64String(png), parts[1].GetProperty("image_url").GetProperty("url").GetString());
        Assert.DoesNotContain("离线结果", body);
        TaskToolbarTests.Preview(scope.Window, "ocr-ai-result");
    }

    [AvaloniaFact]
    public async Task NoConfiguredModelLeavesTheOfflineResultAvailable()
    {
        var calls = 0;
        using var scope = new AiScope((_, _) => { calls++; return Task.FromResult(AiResponse("不应发送")); });
        scope.Show(DesktopController.RenderText("当前原图"));
        await scope.Window.RecognizeWithAiAsync();
        Assert.Equal(0, calls);
        Assert.Empty(scope.Window.OwnedWindows);
        Assert.Equal("离线结果", scope.Editor.Text);
        Assert.True(FindButton(scope.Window, "复制全部").IsEnabled);
        Assert.Contains(scope.Window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.Contains("模型服务") == true);
    }

    [AvaloniaFact]
    public async Task StoppingAiKeepsEditedTextEvenIfTheServiceReturnsLate()
    {
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var scope = new AiScope((_, ct) => { started.TrySetResult(ct); return response.Task; });
        await scope.SaveModelAsync();
        scope.Show(DesktopController.RenderText("当前原图"));
        scope.Editor.Text = "用户编辑";
        var request = scope.Window.RecognizeWithAiAsync();
        PickModel(scope.Window, out var dialog);
        Click(dialog, "发送并识别");
        var token = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await scope.Window.RecognizeWithAiAsync();
        Assert.True(token.IsCancellationRequested);
        response.SetResult(AiResponse("迟到文字"));
        await request;
        Assert.Equal("用户编辑", scope.Editor.Text);
        Assert.True(FindButton(scope.Window, "AI 识别").IsEnabled);
    }

    private static void PickModel(OcrWindow owner, out OcrModelWindow dialog)
    {
        dialog = Assert.IsType<OcrModelWindow>(Assert.Single(owner.OwnedWindows));
        Assert.False(FindButton(dialog, "发送并识别").IsEnabled);
        Assert.Single(dialog.GetVisualDescendants().OfType<ComboBox>()).SelectedIndex = 0;
        dialog.UpdateLayout();
    }

    private static Button FindButton(Window window, string label) =>
        Assert.Single(window.GetVisualDescendants().OfType<Button>(), b => Equals(b.Content, label));

    private static void Click(Window window, string label) => FindButton(window, label).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static HttpResponseMessage AiResponse(string text) => ModelServiceTests.Response(JsonSerializer.Serialize(new
    {
        choices = new[] { new { message = new { content = text }, finish_reason = "stop" } }
    }), "application/json");

    private sealed class AiScope : IDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("tbox-ai-ocr-");
        private readonly ModelService _service;
        private readonly DesktopController _controller;
        public OcrWindow Window { get; }
        public TextBox Editor => Assert.Single(Window.GetVisualDescendants().OfType<TextBox>());

        public AiScope(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            if (!Application.Current!.Styles.OfType<Avalonia.Themes.Fluent.FluentTheme>().Any())
                Application.Current.Styles.Add(DesignTokens.CreateTheme());
            _service = new ModelService(Path.Combine(_directory.FullName, "models.json"), handler: new ModelServiceTests.Handler(handler));
            var platform = new FakePlatform { OcrResult = new RecognitionResult("离线结果", []) };
            _controller = new DesktopController(platform, new ClassicDesktopStyleApplicationLifetime());
            Window = new OcrWindow(_controller, _service);
        }

        public Task SaveModelAsync() => _service.SaveConnectionAsync(
            new ModelConnection(Guid.NewGuid(), "模拟连接", "https://vision.example.com/v1", ["vision-fixture"]), "fixture-key");

        public void Show(Models.Picture picture)
        {
            Window.Show();
            Window.Recognize(picture);
            Dispatcher.UIThread.RunJobs();
            Window.UpdateLayout();
        }

        public void Dispose()
        {
            Window.Close();
            _controller.Dispose();
            _service.Dispose();
            _directory.Delete(recursive: true);
        }
    }

    [AvaloniaFact]
    public void OriginalReflowsWithoutReplacingEditedTextOrSource()
    {
        const string recognized = "Tbox 文字识别\n这段文字可以编辑和复制。\n12345";
        var platform = new FakePlatform { OcrResult = new RecognitionResult(recognized, []) };
        using var controller = new DesktopController(platform, new ClassicDesktopStyleApplicationLifetime());
        var window = new OcrWindow(controller);
        window.Show();
        window.Recognize(DesktopController.RenderText(recognized));
        try
        {
            Resize(800);
            var editor = Assert.Single(window.GetVisualDescendants().OfType<TextBox>());
            var source = Assert.Single(window.GetVisualDescendants().OfType<Image>(), i => AutomationProperties.GetName(i) == "识别原图");
            Assert.Equal(recognized, editor.Text);
            Assert.True(source.IsEffectivelyVisible);
            Assert.True(Position(editor).X > Position(source).X + source.Bounds.Width);
            var bitmap = source.Source;
            SavePreview("ocr-wide");

            editor.Text = "已经编辑的文字\n第二行";
            Resize(480);
            Assert.False(source.IsEffectivelyVisible);
            SavePreview("ocr-narrow");
            Click("显示原图");
            Assert.True(source.IsEffectivelyVisible);
            Assert.True(Position(editor).Y >= Position(source).Y + source.Bounds.Height);
            var expandedHeight = editor.Bounds.Height;
            Click("收起原图");
            Assert.False(source.IsEffectivelyVisible);
            Assert.True(editor.Bounds.Height > expandedHeight);
            Resize(800);
            Assert.False(source.IsEffectivelyVisible);
            Click("显示原图");
            Assert.Equal("已经编辑的文字\n第二行", editor.Text);
            Assert.Same(bitmap, source.Source);
        }
        finally { window.Close(); }

        Point Position(Control control) => control.TranslatePoint(default, window)!.Value;
        void Resize(int width)
        {
            window.Width = width;
            Layout();
        }
        void Click(string label)
        {
            var button = Assert.Single(window.GetVisualDescendants().OfType<Button>(), b => Equals(b.Content, label));
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Layout();
        }
        void Layout()
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }
        void SavePreview(string name)
        {
            if (Environment.GetEnvironmentVariable("TBOX_UI_PREVIEWS") is not { Length: > 0 } directory) return;
            Directory.CreateDirectory(directory);
            using var preview = new RenderTargetBitmap(new PixelSize((int)window.Width, (int)window.Height), new Vector(96, 96));
            preview.Render(window);
            preview.Save(Path.Combine(directory, name + ".png"));
        }
    }

    [AvaloniaFact]
    public void EmptyResultExplainsTheNextStepAndDisablesCopying()
    {
        var platform = new FakePlatform { OcrResult = new RecognitionResult("", []) };
        using var controller = new DesktopController(platform, new ClassicDesktopStyleApplicationLifetime());
        var window = new OcrWindow(controller) { Width = 600 };
        window.Show();
        window.Recognize(DesktopController.RenderText("blurred"));
        try
        {
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == "未识别到文字");
            Assert.False(Assert.Single(window.GetVisualDescendants().OfType<Button>(), b => Equals(b.Content, "复制全部")).IsEnabled);
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == "0 行");
        }
        finally { window.Close(); }
    }
}
