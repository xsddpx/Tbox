using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Tbox.Desktop.Platform;
using Tbox.Desktop.Views;
using Xunit;

namespace Tbox.Desktop.Tests;

public sealed class RecordingEntryTests
{
    [AvaloniaFact]
    public async Task RecordingEntryShowsSelectionControlsAndCancelReturnsToLauncher()
    {
        using var frame = DesktopController.RenderText("Recording selection");
        using var lifetime = new ClassicDesktopStyleApplicationLifetime { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        typeof(ClassicDesktopStyleApplicationLifetime).GetInterfaces().Single(i => i.Name == "ISetupApplicationLifetime")
            .GetMethod("BeforeAppInit")!.Invoke(lifetime, null);
        var platform = new FakePlatform
        {
            HasScreenPermission = true,
            Displays = [new CapturedDisplay(1, 0, 0, frame.Bitmap.PixelSize.Width, frame.Bitmap.PixelSize.Height, frame.Png)]
        };
        using var controller = new DesktopController(platform, lifetime);
        var main = controller.Main = new MainWindow(controller);
        main.Show();
        try
        {
            await controller.RecordRegionAsync();
            Assert.False(main.IsVisible);
            var selection = Assert.Single(lifetime.Windows.OfType<CaptureWindow>());
            selection.UpdateLayout();
            selection.MouseDown(new Point(12, 12), MouseButton.Left, RawInputModifiers.None);
            selection.MouseMove(new Point(100, 60), RawInputModifiers.LeftMouseButton);
            selection.MouseUp(new Point(100, 60), MouseButton.Left, RawInputModifiers.None);
            selection.UpdateLayout();
            Assert.Empty(selection.GetVisualDescendants().OfType<AnnotationToolbar>());
            Assert.Contains(selection.GetVisualDescendants().OfType<Button>(), b =>
                b.IsEffectivelyVisible && AutomationProperties.GetName(b) == "确认录屏区域 · Enter");
            selection.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Assert.Empty(lifetime.Windows.OfType<CaptureWindow>());
            Assert.True(main.IsVisible); // Cancelling restores the settings window it was started from.
        }
        finally { controller.Dispose(); main.Close(); }
    }
}
