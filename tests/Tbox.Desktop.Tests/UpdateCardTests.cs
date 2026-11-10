using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tbox.Desktop.Updates;
using Tbox.Desktop.Views;
using Xunit;

namespace Tbox.Desktop.Tests;

public sealed class UpdateCardTests
{
    [AvaloniaFact]
    public void PublicCheckIsManualAndDoesNotAskForCredentials()
    {
        using var controller = new DesktopController(new FakePlatform(), new ClassicDesktopStyleApplicationLifetime());
        controller.Main = new MainWindow(controller);
        var handler = new EmptyReleases();
        var card = new UpdateCard(controller, new GitHubUpdateClient(handler));
        var window = new Window { Content = card }; window.Show(); window.UpdateLayout();
        Assert.Equal(0, handler.Requests);
        Assert.Empty(window.GetVisualDescendants().OfType<TextBox>());
        var button = window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "检查更新"));
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, handler.Requests);
        Assert.False(card.UpdateAvailable);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.StartsWith("已是最新版本") == true);
        card.Release(); window.Close(); controller.Main.Close();
    }

    [AvaloniaFact]
    public async Task InstallExplainsTheOpenResultAndCanBeRetriedWithoutDownloadingAgain()
    {
        using var lifetime = new ClassicDesktopStyleApplicationLifetime { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        // Headless tests do not run AppBuilder's internal lifetime setup.
        typeof(ClassicDesktopStyleApplicationLifetime).GetInterfaces().Single(i => i.Name == "ISetupApplicationLifetime")
            .GetMethod("BeforeAppInit")!.Invoke(lifetime, null);
        using var controller = new DesktopController(new FakePlatform(), lifetime);
        controller.Main = new MainWindow(controller);
        var handler = new AvailableRelease();
        var card = new UpdateCard(controller, new GitHubUpdateClient(handler));
        controller.Main.Content = card;
        var result = new ImageResultWindow(controller, Models.Picture.FromPixels([0, 0, 0, 255], 1, 1), false);
        try
        {
            controller.Main.Show();
            var installLabel = OperatingSystem.IsMacOS() ? "安装并重启" : "打开安装程序";
            Button ButtonFor(string label) => card.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, label));

            Click(ButtonFor("检查更新"));
            Click(ButtonFor("下载并安装"));
            var install = ButtonFor(installLabel);
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!install.IsVisible) await Task.Delay(10, wait.Token);

            result.Show();
            Click(install);
            var blocked = Assert.Single(lifetime.Windows.OfType<ConfirmWindow>());
            Assert.Contains(blocked.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "暂时无法安装更新");
            Assert.Contains(blocked.GetVisualDescendants().OfType<TextBlock>(), text => text.Text!.Contains("请先关闭图片预览窗口"));
            blocked.UpdateLayout();
            TaskToolbarTests.Preview(blocked, "update-blocker");
            Assert.True(result.IsVisible);
            Click(Assert.Single(blocked.GetVisualDescendants().OfType<Button>(), b => Equals(b.Content, "知道了")));

            result.Close();
            Assert.Null(controller.GetUpdateBlockReason());
            Assert.True(install.IsVisible);
            Click(install);
            var confirmation = Assert.Single(lifetime.Windows.OfType<ConfirmWindow>());
            Assert.Contains(confirmation.GetVisualDescendants().OfType<Button>(), b => Equals(b.Content, installLabel));
            Assert.Equal(1, handler.Downloads);
        }
        finally
        {
            card.Release();
            controller.Dispose();
            foreach (var window in lifetime.Windows.ToArray()) window.Close();
        }
    }

    private static void Click(Button button)
    {
        Assert.True(button.IsVisible);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    private sealed class AvailableRelease : HttpMessageHandler
    {
        private static readonly byte[] Package = [1, 2, 3];
        public int Downloads;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/releases", StringComparison.Ordinal))
            {
                var release = new
                {
                    tag_name = "v99.0.0", draft = false, prerelease = false,
                    assets = new[] { new
                    {
                        id = 1, name = OperatingSystem.IsMacOS() ? "Tbox_99.0.0_osx-arm64.zip" : "Tbox_99.0.0.0_win-x64.msix",
                        size = Package.Length, digest = "sha256:" + Convert.ToHexString(SHA256.HashData(Package)), state = "uploaded"
                    } }
                };
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new[] { release }))
                });
            }

            Downloads++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Package) });
        }
    }

    private sealed class EmptyReleases : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++; Assert.Null(request.Headers.Authorization);
            Assert.Equal("/repos/xsddpx/Tbox/releases", request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") });
        }
    }
}
