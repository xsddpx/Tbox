using System.Net;
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

public sealed class UpdateWindowTests
{
    [AvaloniaFact]
    public void PublicCheckIsManualAndDoesNotAskForCredentials()
    {
        using var controller = new DesktopController(new FakePlatform(), new ClassicDesktopStyleApplicationLifetime());
        var handler = new EmptyReleases();
        var window = new UpdateWindow(controller, new GitHubUpdateClient(handler)); window.Show(); window.UpdateLayout();
        Assert.Equal(0, handler.Requests);
        Assert.Empty(window.GetVisualDescendants().OfType<TextBox>());
        var button = window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "检查更新"));
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, handler.Requests);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "当前没有可用的新正式版本。");
        window.Close();
    }

    [AvaloniaFact]
    public void OpenResultsPreventRestartWhileMainAndUpdateWindowDoNot()
    {
        using var lifetime = new ClassicDesktopStyleApplicationLifetime { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        // Headless tests do not run AppBuilder's internal lifetime setup.
        typeof(ClassicDesktopStyleApplicationLifetime).GetInterfaces().Single(i => i.Name == "ISetupApplicationLifetime")
            .GetMethod("BeforeAppInit")!.Invoke(lifetime, null);
        using var controller = new DesktopController(new FakePlatform(), lifetime);
        controller.Main = new MainWindow(controller); controller.Main.Show();
        controller.ShowUpdates(); Assert.False(controller.HasOpenWorkForUpdate);
        var result = new Window(); result.Show(); Assert.True(controller.HasOpenWorkForUpdate);
        result.Close(); Assert.False(controller.HasOpenWorkForUpdate);
        foreach (var window in controller.Main.OwnedWindows.ToArray()) window.Close();
        controller.Dispose(); controller.Main.Close();
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
