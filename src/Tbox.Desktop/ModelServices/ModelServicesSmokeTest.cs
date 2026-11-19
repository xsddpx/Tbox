using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Tbox.Desktop.Views;

namespace Tbox.Desktop.ModelServices;

// Explicit --verify-model-services only: uses a disposable configuration file and fake HTTP, never user connections.
internal static class ModelServicesSmokeTest
{
    public static async Task<object> RunAsync(string directory)
    {
        var handler = new FakeHandler();
        var path = Path.Combine(directory, "model-smoke.json");
        using var service = new ModelService(path, null, handler);
        var window = new Window();
        Ui.Configure(window, "模型服务验证", 800, 600);
        using var page = new ModelServicesPage(service, () => window);
        window.Content = new ScrollViewer { Content = page, Margin = new Thickness(24), HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
        try
        {
            await page.OpenAsync();
            await page.BeginAddAsync();
            page.Secret.Text = "Tbox-disposable-smoke-value";
            window.Show();
            await Task.Delay(150);
            await page.TestAsync();
            if (page.Status != "连接正常" || handler.Calls != 1 || File.Exists(path))
                throw new InvalidOperationException("新增连接在保存前测试失败或写入了配置。");
            Render(window, Path.Combine(directory, "model-services-add-tested.png"));

            var id = page.ConnectionId;
            await page.SaveAsync();
            var saved = await File.ReadAllTextAsync(path);
            if (!saved.Contains("Tbox-disposable-smoke-value") || handler.Calls != 1)
                throw new InvalidOperationException("首次保存验证失败。");
            await page.BeginEditAsync(id);
            if (page.Secret.Text != "Tbox-disposable-smoke-value" || page.IsKeyRevealed)
                throw new InvalidOperationException("已保存密钥未以遮挡形式回填。");
            page.BaseUrl.Text = "https://api.example.invalid/v1";
            page.Secret.Text = "Tbox-replacement-smoke-value";
            await page.TestAsync();
            if (page.Status != "连接正常" || handler.Calls != 2 || saved != await File.ReadAllTextAsync(path))
                throw new InvalidOperationException("编辑连接在保存前测试失败或改写了旧配置。");
            Render(window, Path.Combine(directory, "model-services-edit-tested.png"));

            await page.SaveAsync();
            if (service.KeyOf(id) != "Tbox-replacement-smoke-value" || handler.Calls != 2)
                throw new InvalidOperationException("编辑保存验证失败。");
            async Task<bool> ReopenVerifiedAsync()
            {
                using var reopened = new ModelService(path);
                await reopened.InitializeAsync();
                return reopened.Configuration.Connections.Single().IsVerified;
            }
            if (!await ReopenVerifiedAsync()) throw new InvalidOperationException("重新打开后未保留测试成功状态。");
            page.AddModel("fixture-model");
            await page.SaveAsync();
            if (await ReopenVerifiedAsync() || handler.Calls != 2) throw new InvalidOperationException("更改配置后未清除测试状态。");
            return new { plaintextKey = true, maskedEcho = true, textGeneration = true, replacement = true, draftAddTest = true,
                draftEditTest = true, verifiedAfterReopen = true, invalidatedAfterEdit = true, externalApiCalls = 0 };
        }
        finally
        {
            window.Close();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static void Render(Window window, string path)
    {
        window.UpdateLayout();
        using var preview = new RenderTargetBitmap(new PixelSize((int)window.ClientSize.Width, (int)window.ClientSize.Height), new Vector(96, 96));
        preview.Render(window);
        preview.Save(path);
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var (expectedKey, expectedEndpoint) = Calls++ == 0
                ? ("Tbox-disposable-smoke-value", "https://api.deepseek.com/chat/completions")
                : ("Tbox-replacement-smoke-value", "https://api.example.invalid/v1/chat/completions");
            if (request.Headers.Authorization?.Parameter != expectedKey || request.RequestUri?.AbsoluteUri != expectedEndpoint)
                throw new InvalidOperationException("请求未使用表单中的地址和密钥。");
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            { Content = new StringContent("data: {\"choices\":[{\"delta\":{\"content\":\"OK\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n", System.Text.Encoding.UTF8, "text/event-stream") });
        }
    }
}
