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
    private static DesktopController CreateController(FakePlatform platform)
    {
        var controller = new DesktopController(platform, new ClassicDesktopStyleApplicationLifetime());
        controller.Main = new MainWindow(controller);
        controller.Panel = new TrayPanel(controller);
        return controller;
    }

    [AvaloniaFact]
    public async Task RepeatedCaptureAndActivationWithoutPermissionNeverRequestOrCapture()
    {
        var platform = new FakePlatform();
        using var controller = CreateController(platform);
        var panel = controller.Panel!;
        try
        {
            // Opening the panel alone must not show an unrequested permission prompt.
            controller.ShowPanel();
            Assert.False(HasPermissionButton());
            panel.Hide();
            await controller.CaptureAsync(CaptureAction.Record);
            Assert.True(panel.IsVisible);
            Assert.True(HasPermissionButton());
            for (var attempt = 0; attempt < 5; attempt++)
            {
                controller.ShowPanel();
                await controller.CaptureAsync();
                await controller.LongCaptureAsync();
            }
            Assert.Equal(0, platform.PermissionRequests);
            Assert.Equal(0, platform.PermissionSettingsOpens);
            Assert.Equal(0, platform.CaptureRequests);

            platform.HasScreenPermission = true;
            panel.Refresh();
            Assert.False(HasPermissionButton());
        }
        finally { panel.Close(); controller.Main.Close(); }

        bool HasPermissionButton()
        {
            panel.UpdateLayout();
            return panel.GetVisualDescendants().OfType<Button>()
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
