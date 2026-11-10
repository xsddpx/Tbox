using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Tbox.Core.ModelServices;
using Tbox.Desktop.ModelServices;
using Tbox.Desktop.Platform;
using Tbox.Desktop.Views;

namespace Tbox.Desktop.Translation;

// Installed-app acceptance uses the actual shared ModelService with a process-local HTTP fixture, never a paid endpoint.
internal static class TranslationSmokeTest
{
    public static async Task<object> RunAsync(string directory)
    {
        var handler = new Fixture();
        using var service = new ModelService(Path.Combine(directory, "translation-models.json"), ModelCredentialStore.Create(), handler);
        var connection = new ModelConnection(Guid.NewGuid(), "离线验证模型", "https://api.example.invalid/v1", ["fixture-text"]) { RequiresKey = false };
        await service.SaveConnectionAsync(connection, null, false, true, "fixture-text");
        var window = new Window(); Ui.Configure(window, "翻译 · 安装版离线验证", 800, 600);
        var page = new TranslationPage(service, () => Task.FromResult(service.Configuration), () => { });
        window.Content = new ScrollViewer { Content = page, Margin = new Thickness(24) };
        try
        {
            await page.OpenAsync();
            if (handler.Calls != 0) throw new InvalidOperationException("打开翻译页触发了请求。");
            page.Source.Text = "Good tools should fit into your workflow, not interrupt it.\n\nConfigure your model once, then use it for translation and more.";
            window.Show(); window.UpdateLayout(); await Task.Delay(100);
            await page.TranslateOrStopAsync(); window.UpdateLayout();
            if (page.Session.State != TranslationState.Completed || page.Session.Text != Fixture.Result || handler.Calls != 1)
                throw new InvalidOperationException("翻译未通过公共能力完成。");
            using var preview = new RenderTargetBitmap(new PixelSize((int)window.ClientSize.Width, (int)window.ClientSize.Height), new Vector(96, 96));
            preview.Render(window); preview.Save(Path.Combine(directory, "translation.png"));
            return new { sharedModelService = true, sourcePreserved = true, translationCompleted = true, externalApiCalls = 0 };
        }
        finally { page.Dispose(); window.Close(); await service.DeleteConnectionAsync(connection.Id); }
    }
    private sealed class Fixture : HttpMessageHandler
    {
        public const string Result = "好的工具应该融入你的工作流程，而不是打断它。\n\n只需配置一次模型，即可用于翻译等更多场景。";
        public int Calls { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var messages = body.RootElement.GetProperty("messages");
            if (messages.GetArrayLength() != 2 || !messages[0].GetProperty("content").GetString()!.Contains("Simplified Chinese"))
                throw new InvalidOperationException("翻译指令未经过公共能力。");
            var first = JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content = Result } } } });
            var stream = "data: " + first + "\n\ndata: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";
            return new(System.Net.HttpStatusCode.OK) { Content = new StringContent(stream, Encoding.UTF8, "text/event-stream") };
        }
    }
}
