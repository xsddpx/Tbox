using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Tbox.Core.ModelServices;
using Tbox.Desktop.Views;

namespace Tbox.Desktop.ModelServices;

// Explicit --verify-model-services only: uses a disposable configuration file and fake HTTP, never user connections.
internal static class ModelServicesSmokeTest
{
    public static async Task<object> RunAsync(string directory)
    {
        var id = Guid.NewGuid();
        var calls = 0;
        var path = Path.Combine(directory, "model-smoke.json");
        using var service = new ModelService(path, null, new FakeHandler(() => calls++));
        var window = new Window();
        Ui.Configure(window, "模型服务验证", 800, 600);
        var page = new ModelServicesPage(service, () => window);
        window.Content = new ScrollViewer { Content = page, Margin = new Thickness(24), HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
        var connection = new ModelConnection(id, "离线验证连接", "https://api.example.invalid/v1", ["fixture-model"]);
        try
        {
            await service.SaveConnectionAsync(connection, "Tbox-disposable-smoke-value", true, "fixture-model");
            if (!(await File.ReadAllTextAsync(path)).Contains("Tbox-disposable-smoke-value")) throw new InvalidOperationException("密钥未写入配置文件。");
            await page.OpenAsync();
            await page.BeginEditAsync(id);
            if (page.Secret.Text != "Tbox-disposable-smoke-value" || page.IsKeyRevealed) throw new InvalidOperationException("已保存密钥未以遮挡形式回填。");
            window.Show(); window.UpdateLayout();
            await Task.Delay(150);
            await page.TestAsync();
            if (page.Status != "连接正常" || calls != 1) throw new InvalidOperationException("公共调用验证失败。");
            window.UpdateLayout();
            using (var preview = new RenderTargetBitmap(new PixelSize((int)window.ClientSize.Width, (int)window.ClientSize.Height), new Vector(96, 96)))
            { preview.Render(window); preview.Save(Path.Combine(directory, "model-services.png")); }
            await service.SaveConnectionAsync(connection, "Tbox-replacement-smoke-value", true, "fixture-model");
            if (service.KeyOf(id) != "Tbox-replacement-smoke-value") throw new InvalidOperationException("密钥修改失败。");
            return new { plaintextKey = true, maskedEcho = true, textGeneration = true, replacement = true, externalApiCalls = 0 };
        }
        finally
        {
            page.Dispose(); window.Close();
            await service.DeleteConnectionAsync(id);
        }
    }
    private sealed class FakeHandler(Action called) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            called();
            if (request.Headers.Authorization?.Parameter != "Tbox-disposable-smoke-value") throw new InvalidOperationException("凭据没有用于请求授权。");
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            { Content = new StringContent("data: {\"choices\":[{\"delta\":{\"content\":\"OK\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n", System.Text.Encoding.UTF8, "text/event-stream") });
        }
    }
}
