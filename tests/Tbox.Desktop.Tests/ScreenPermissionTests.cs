using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless.XUnit;
using Tbox.Desktop.Views;
using Xunit;

namespace Tbox.Desktop.Tests;

public sealed class ScreenPermissionTests
{
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
        for (var attempt = 0; attempt < 5; attempt++)
        {
            controller.ShowMain();
            await controller.CaptureAsync();
        }
        Assert.Equal(0, platform.PermissionRequests);
        Assert.Equal(0, platform.PermissionSettingsOpens);
        Assert.Equal(0, platform.CaptureRequests);
        controller.Main.Hide();
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
