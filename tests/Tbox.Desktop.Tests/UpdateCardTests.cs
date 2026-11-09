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

// The About page's update banner: manual public check without credentials; only open results block a restart.
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
    public void OpenResultsPreventRestartWhileTheMainWindowDoesNot()
    {
        using var lifetime = new ClassicDesktopStyleApplicationLifetime { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        // Headless tests do not run AppBuilder's internal lifetime setup.
        typeof(ClassicDesktopStyleApplicationLifetime).GetInterfaces().Single(i => i.Name == "ISetupApplicationLifetime")
            .GetMethod("BeforeAppInit")!.Invoke(lifetime, null);
        using var controller = new DesktopController(new FakePlatform(), lifetime);
        controller.Main = new MainWindow(controller); controller.Main.Show();
        controller.Main.Navigate(Models.MainPage.About); Assert.False(controller.HasOpenWorkForUpdate);
        var result = new Window(); result.Show(); Assert.True(controller.HasOpenWorkForUpdate);
        result.Close(); Assert.False(controller.HasOpenWorkForUpdate);
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
