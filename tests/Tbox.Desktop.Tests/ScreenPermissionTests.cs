using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Tbox.Desktop.Views;
using Xunit;

namespace Tbox.Desktop.Tests;

public sealed class ScreenPermissionTests
{
    [AvaloniaFact]
    public async Task RepeatedCaptureWhileAcquiringDoesNotStartAnotherSession()
    {
        var response = new TaskCompletionSource<IReadOnlyList<Platform.CapturedDisplay>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var platform = new FakePlatform { HasScreenPermission = true, PendingCapture = response.Task };
        using var controller = CreateController(platform);
        var main = controller.Main;
        try
        {
            var first = controller.CaptureAsync();
            Assert.Equal(1, platform.CaptureRequests);
            await controller.CaptureAsync();
            Assert.Equal(1, platform.CaptureRequests);
            response.SetResult([]);
            await first;
        }
        finally { response.TrySetResult([]); controller.Dispose(); main.Close(); }
    }

    [AvaloniaFact]
    public async Task NormalCaptureStartsAcquisitionImmediatelyWithoutAnArtificialDelay()
    {
        var platform = new FakePlatform { HasScreenPermission = true };
        using var controller = CreateController(platform);
        var main = controller.Main;
        try
        {
            var capture = controller.CaptureAsync();
            // A deterministic check of the first synchronous portion, not a wall-clock timing assertion.
            Assert.Equal(1, platform.CaptureRequests);
            await capture;
            Assert.False(main.IsVisible);
        }
        finally { controller.Dispose(); main.Close(); }
    }

    private static DesktopController CreateController(FakePlatform platform)
    {
        var controller = new DesktopController(platform, new ClassicDesktopStyleApplicationLifetime());
        controller.Main = new MainWindow(controller);
        return controller;
    }

    [AvaloniaFact]
    public async Task RepeatedCaptureAndActivationWithoutPermissionNeverRequestOrCapture()
    {
        var platform = new FakePlatform();
        using var controller = CreateController(platform);
        var main = controller.Main;
        try
        {
            // Opening the main window alone must not show an unrequested permission prompt.
            controller.ShowMain();
            Assert.False(HasPermissionButton());
            main.Hide();
            await controller.CaptureAsync(CaptureAction.Record);
            Assert.True(main.IsVisible);
            Assert.True(HasPermissionButton());
            for (var attempt = 0; attempt < 5; attempt++)
            {
                controller.ShowMain();
                await controller.CaptureAsync();
                await controller.LongCaptureAsync();
            }
            Assert.Equal(0, platform.PermissionRequests);
            Assert.Equal(0, platform.PermissionSettingsOpens);
            Assert.Equal(0, platform.CaptureRequests);

            platform.HasScreenPermission = true;
            main.Refresh();
            Assert.False(HasPermissionButton());
        }
        finally { controller.Dispose(); main.Close(); }

        bool HasPermissionButton()
        {
            main.UpdateLayout();
            return main.GetVisualDescendants().OfType<Button>()
                .Any(button => button.IsEffectivelyVisible && AutomationProperties.GetName(button) == "允许屏幕录制");
        }
    }

    [AvaloniaFact]
    public void ExplicitRequestPromptsOnceThenOpensSettings()
    {
        var platform = new FakePlatform();
        using var controller = CreateController(platform);
        controller.RequestPermission();
        controller.RequestPermission();
        controller.RequestPermission();
        Assert.Equal(1, platform.PermissionRequests);
        Assert.Equal(2, platform.PermissionSettingsOpens);
        Assert.Equal(0, platform.CaptureRequests);
    }

    [AvaloniaFact]
    public void GrantedPermissionDoesNotRequestAgain()
    {
        var platform = new FakePlatform { HasScreenPermission = true };
        using var controller = CreateController(platform);
        controller.RequestPermission();
        Assert.Equal(0, platform.PermissionRequests);
        Assert.Equal(0, platform.PermissionSettingsOpens);
    }
}
