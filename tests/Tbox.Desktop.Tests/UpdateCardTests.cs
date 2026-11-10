using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tbox.Desktop.Platform;
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

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OneClickInstallsAndExitsWhetherOrNotWorkIsOpen(bool hasWork)
    {
        using var lifetime = new ClassicDesktopStyleApplicationLifetime { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        // Headless tests do not run AppBuilder's internal lifetime setup.
        typeof(ClassicDesktopStyleApplicationLifetime).GetInterfaces().Single(i => i.Name == "ISetupApplicationLifetime")
            .GetMethod("BeforeAppInit")!.Invoke(lifetime, null);
        var recorder = new FakeRecorder();
        using var controller = new DesktopController(new FakePlatform { Recorder = recorder }, lifetime);
        controller.Main = new MainWindow(controller);
        var temp = Path.Combine(Path.GetTempPath(), "tbox-update-card-" + Guid.NewGuid().ToString("N"));
        controller.UseCacheDirectoryForTests(Path.Combine(temp, "captures"));
        var handler = new AvailableRelease();
        var invalidPackage = new TaskCompletionSource<PreparedUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
        var validated = new TaskCompletionSource<PreparedUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lifetime.Exit += (_, _) => exited.TrySetResult();
        var validationAttempts = 0;
        var installerLaunched = false;
        var card = new UpdateCard(controller, new GitHubUpdateClient(handler), (file, _, token) =>
        {
            Assert.True(File.Exists(file));
            validationAttempts++;
            return (validationAttempts == 1 ? invalidPackage.Task : validated.Task).WaitAsync(token);
        });
        controller.Main.Content = card;
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

            string? savedCapture = null;
            var area = new RecordingArea(new DesktopPlacement(0, 0, 320, 240), 320, 240);
            if (hasWork)
            {
                controller.ShowImageResult(Models.Picture.FromPixels([0, 0, 0, 255], 1, 1), false);
                using var picture = Models.Picture.FromPixels([1, 2, 3, 255], 1, 1);
                await controller.PinPictureAsync(picture);
                savedCapture = picture.CachePath;
                controller.TogglePins();
                Assert.True(controller.PinsHidden);
                await controller.StartRecordingAsync(area);
                var recording = Assert.Single(lifetime.Windows.OfType<RecordingControlWindow>());
                Click(Assert.Single(recording.GetVisualDescendants().OfType<Button>(),
                    b => AutomationProperties.GetName(b)?.StartsWith("开始录制") == true));
                Assert.True(recorder.Started);
                controller.Main.Show();
            }
            Click(install);
            Assert.Equal(1, validationAttempts);
            Assert.Empty(lifetime.Windows.OfType<ConfirmWindow>());
            if (hasWork)
            {
                controller.Main.UpdateLayout();
                TaskToolbarTests.Preview(controller.Main, "force-update");
            }
            invalidPackage.SetException(new InvalidDataException("测试安装包验证失败"));
            while (!install.IsVisible) await Task.Delay(10, wait.Token);
            Assert.Contains(card.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "测试安装包验证失败");
            Assert.Equal(hasWork ? 1 : 0, controller.PinCount);
            Assert.False(recorder.Disposed);

            Click(install);
            Assert.Equal(2, validationAttempts);
            Assert.Empty(lifetime.Windows.OfType<ConfirmWindow>());
            Assert.False(controller.IsQuitting);
            Assert.False(installerLaunched);

            Task<bool>? closingReview = null;
            if (hasWork)
            {
                Assert.True(Assert.Single(lifetime.Windows.OfType<ImageResultWindow>()).IsVisible);
                var workspace = new Models.RecordingWorkspace(temp);
                File.WriteAllBytes(workspace.MasterFilePath, [1, 2, 3]);
                controller.ShowRecordingReview(workspace, area, new RecordingOutput(workspace.MasterFilePath, false, 1), TimeSpan.FromSeconds(1));
                closingReview = Assert.Single(lifetime.Windows.OfType<RecordingReviewWindow>()).RequestCloseAsync();
                Assert.False(closingReview.IsCompleted);
                Assert.True(File.Exists(workspace.MasterFilePath));
            }

            validated.SetResult(new PreparedUpdate(() => installerLaunched = true, () => { }));
            await exited.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(installerLaunched);
            Assert.Equal(0, controller.PinCount);
            Assert.Empty(lifetime.Windows.OfType<ImageResultWindow>());
            if (closingReview is not null)
            {
                Assert.True(await closingReview);
                Assert.True(recorder.Disposed);
                Assert.True(File.Exists(savedCapture));
            }
            Assert.Equal(1, handler.Downloads);
        }
        finally
        {
            card.Release();
            controller.Dispose();
            foreach (var window in lifetime.Windows.ToArray()) window.Close();
            if (Directory.Exists(temp)) Directory.Delete(temp, true);
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
