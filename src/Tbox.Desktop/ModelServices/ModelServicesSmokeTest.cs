using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Tbox.Core.ModelServices;
using Tbox.Desktop.Platform;
using Tbox.Desktop.Views;

namespace Tbox.Desktop.ModelServices;

// Explicit --verify-model-services only: uses a unique disposable credential and fake HTTP, never user connections.
internal static class ModelServicesSmokeTest
{
    public static async Task<object> RunAsync(string directory)
    {
        var credentials = ModelCredentialStore.Create();
        var id = Guid.NewGuid();
        var calls = 0;
        using var service = new ModelService(Path.Combine(directory, "model-smoke.json"), credentials, new FakeHandler(() => calls++));
        var window = new Window();
        Ui.Configure(window, "模型服务验证", 800, 600);
        var page = new ModelServicesPage(service, () => window);
        window.Content = new ScrollViewer { Content = page, Margin = new Thickness(24), HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
        var connection = new ModelConnection(id, "离线验证连接", "https://api.example.invalid/v1", ["fixture-model"]);
        try
        {
            await service.SaveConnectionAsync(connection, "Tbox-disposable-smoke-value", false, true, "fixture-model");
            var saved = service.Configuration.Connections.Single();
            if (!await service.HasCredentialAsync(saved)) throw new InvalidOperationException("凭据保存后不存在。");
            if ((await File.ReadAllTextAsync(Path.Combine(directory, "model-smoke.json"))).Contains("Tbox-disposable-smoke-value")) throw new InvalidOperationException("普通配置包含测试密钥。");
            await page.OpenAsync();
            if (page.IsEditingKey || !string.IsNullOrEmpty(page.Secret.Text)) throw new InvalidOperationException("密钥被回填到输入框。");
            window.Show(); window.UpdateLayout();
            await Task.Delay(150);
            await page.TestAsync();
            if (page.Status != "连接正常" || calls != 1) throw new InvalidOperationException("公共调用验证失败。");
            window.UpdateLayout();
            using (var preview = new RenderTargetBitmap(new PixelSize((int)window.ClientSize.Width, (int)window.ClientSize.Height), new Vector(96, 96)))
            { preview.Render(window); preview.Save(Path.Combine(directory, "model-services.png")); }
            await service.SaveConnectionAsync(saved, "Tbox-replacement-smoke-value", false, true, "fixture-model");
            var replacement = service.Configuration.Connections.Single();
            if (await credentials.ReadAsync(replacement.CredentialId!.Value) != "Tbox-replacement-smoke-value") throw new InvalidOperationException("系统凭据替换失败。");
            await service.DeleteCredentialAsync(id);
            if (await credentials.ContainsAsync(replacement.CredentialId.Value)) throw new InvalidOperationException("系统凭据未删除。");
            return new { credentialStore = true, opaqueSavedState = true, textGeneration = true, replacement = true, deletion = true, externalApiCalls = 0 };
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
