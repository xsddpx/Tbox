using System.Text.Json;
using Avalonia.Headless.XUnit;
using Tbox.Desktop.Platform;
using Xunit;

namespace Tbox.Desktop.Tests;

public sealed class PermissionDiagnosticsTests
{
    [Fact]
    public async Task MissingPermissionNeverPromptsOrAttemptsCapture()
    {
        var platform = new FakePlatform();
        using var result = JsonDocument.Parse(await PermissionDiagnostics.RunAsync(platform));
        Assert.False(result.RootElement.GetProperty("screenPermission").GetBoolean());
        Assert.False(result.RootElement.GetProperty("captureVerified").GetBoolean());
        Assert.Equal(0, platform.PermissionRequests); Assert.Equal(0, platform.CaptureRequests);
    }

    [AvaloniaFact]
    public async Task ExistingPermissionIsVerifiedInsideAppWithoutReturningPixels()
    {
        using var picture = DesktopController.RenderText("仅用于验证的测试图");
        var platform = new FakePlatform { HasScreenPermission = true, Displays = [new CapturedDisplay(1, 0, 0, 640, 200, picture.Png)] };
        var json = await PermissionDiagnostics.RunAsync(platform);
        using var result = JsonDocument.Parse(json);
        Assert.True(result.RootElement.GetProperty("captureVerified").GetBoolean());
        Assert.Equal(1280, result.RootElement.GetProperty("displays")[0].GetProperty("width").GetInt32());
        Assert.Equal(0, platform.PermissionRequests); Assert.Equal(1, platform.CaptureRequests);
        Assert.DoesNotContain(Convert.ToBase64String(picture.Png), json);
        Assert.False(result.RootElement.TryGetProperty("recognizedText", out _));
    }
}
