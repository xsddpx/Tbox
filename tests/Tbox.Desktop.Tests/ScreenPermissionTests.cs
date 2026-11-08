using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Tbox.Desktop.Models;
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
        controller.ShowMain();
        Assert.False(HasPermissionButton());
        controller.Main.Navigate(SettingsPage.Ocr);
        await controller.CaptureAsync(selectRecordingRegion: true);
        Assert.Equal(SettingsPage.Home, controller.Main.CurrentPage);
        Assert.True(HasPermissionButton());
        controller.Main.Navigate(SettingsPage.Ocr);
        Assert.False(HasPermissionButton());
        for (var attempt = 0; attempt < 5; attempt++)
        {
            controller.ShowMain();
            await controller.CaptureAsync();
        }
        Assert.Equal(0, platform.PermissionRequests);
        Assert.Equal(0, platform.PermissionSettingsOpens);
        Assert.Equal(0, platform.CaptureRequests);
        controller.Main.Hide();

        bool HasPermissionButton()
        {
            controller.Main.UpdateLayout();
            return controller.Main.GetVisualDescendants().OfType<Button>()
                .Any(button => button.IsEffectivelyVisible && Equals(button.Content, "允许屏幕录制"));
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
