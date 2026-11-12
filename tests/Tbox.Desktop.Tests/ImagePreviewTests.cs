using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tbox.Desktop.Models;
using Tbox.Desktop.Views;
using Xunit;

namespace Tbox.Desktop.Tests;

// Image preview (spec ④): a cached capture can be stepped through in place with the arrows, the wheel and , / .
// "上一张" is the older capture (position + 1); "下一张" is the newer one (position - 1).
public sealed class ImagePreviewTests
{
    [AvaloniaFact]
    public async Task PreviewStepsThroughOlderAndNewerCachesWithKeysButtonsAndTheWheel()
    {
        using var cache = new Cache();
        using var scope = new Scope(cache.Directory);
        var paths = cache.Files(5);
        await scope.Controller.OpenCachedAsync(paths[4]); // Newest.
        var window = Assert.Single(scope.Lifetime.Windows.OfType<ImageResultWindow>());
        window.Show(); Layout(window);

        Assert.Equal("1 / 5", Position(window));
        Assert.True(Older(window).IsEffectivelyEnabled);   // Older captures exist.
        Assert.False(Newer(window).IsEffectivelyEnabled);  // Nothing is newer than the newest capture.

        // , and ← go to the older capture; . and → go back towards the newest.
        await Press(window, Key.OemComma);
        Assert.Equal("2 / 5", Position(window));
        await Press(window, Key.OemComma);
        Assert.Equal("3 / 5", Position(window));
        await Press(window, Key.OemPeriod);
        Assert.Equal("2 / 5", Position(window));
        await Press(window, Key.Left);
        Assert.Equal("3 / 5", Position(window));
        await Press(window, Key.Right);
        Assert.Equal("2 / 5", Position(window));

        // The wheel works without a keyboard: up is older, down is newer.
        Wheel(window, -1);
        await Settle(window);
        Assert.Equal("3 / 5", Position(window));
        Wheel(window, 1);
        await Settle(window);
        Assert.Equal("2 / 5", Position(window));

        // The visible buttons step too, and the window is reused rather than reopened.
        Click(Older(window)); await Settle(window);
        Assert.Equal("3 / 5", Position(window));
        Click(Newer(window)); await Settle(window);
        Assert.Equal("2 / 5", Position(window));
        Assert.Single(scope.Lifetime.Windows.OfType<ImageResultWindow>());

        // Home / End jump to the ends of the history.
        await Press(window, Key.End);
        Assert.Equal("1 / 5", Position(window));
        Assert.False(Newer(window).IsEffectivelyEnabled);
        await Press(window, Key.Home);
        Assert.Equal("5 / 5", Position(window));
        Assert.False(Older(window).IsEffectivelyEnabled);
        Assert.True(Newer(window).IsEffectivelyEnabled);
        window.Close();
    }

    [AvaloniaFact]
    public async Task PreviewCyclesFromBothEndsAndDisablesNavigationForASingleCapture()
    {
        using var cache = new Cache();
        using var scope = new Scope(cache.Directory);
        var paths = cache.Files(3);
        await scope.Controller.OpenCachedAsync(paths[2]);
        var window = Assert.Single(scope.Lifetime.Windows.OfType<ImageResultWindow>());
        window.Show(); Layout(window);

        // Wrapping keeps repeated presses moving instead of stopping on the oldest capture.
        await Press(window, Key.Home);
        Assert.Equal("3 / 3", Position(window));
        await Press(window, Key.OemComma);
        Assert.Equal("1 / 3", Position(window));
        await Press(window, Key.OemPeriod);
        Assert.Equal("3 / 3", Position(window));
        await Press(window, Key.Right);
        Assert.Equal("2 / 3", Position(window));
        window.Close();

        using var single = new Cache();
        using var solo = new Scope(single.Directory);
        var only = single.Files(1);
        await solo.Controller.OpenCachedAsync(only[0]);
        var one = Assert.Single(solo.Lifetime.Windows.OfType<ImageResultWindow>());
        one.Show(); Layout(one);
        Assert.False(Older(one).IsEffectivelyEnabled);
        Assert.False(Newer(one).IsEffectivelyEnabled);
        // A single capture has nowhere to go, so the counter stays hidden.
        Assert.DoesNotContain(one.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == "1 / 1");
        one.Close();
    }

    [AvaloniaFact]
    public async Task ACaptureThatWasCleanedUpKeepsTheCurrentImageAndSaysSo()
    {
        using var cache = new Cache();
        using var scope = new Scope(cache.Directory);
        var paths = cache.Files(2);
        await scope.Controller.OpenCachedAsync(paths[1]);
        var window = Assert.Single(scope.Lifetime.Windows.OfType<ImageResultWindow>());
        window.Show(); Layout(window);
        File.Delete(paths[0]); // Cache cleanup can remove an older capture between presses.
        await Press(window, Key.OemComma);
        Assert.Equal("1 / 2", Position(window));
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == "这张截图已被清理或移动。");
        window.Close();
    }

    // The preview owns one picture at a time; stepping must not leak or dispose the one on screen.
    [AvaloniaFact]
    public async Task SteppingReplacesTheShownPicture()
    {
        using var cache = new Cache();
        using var scope = new Scope(cache.Directory);
        var paths = cache.Files(2);
        await scope.Controller.OpenCachedAsync(paths[1]);
        var window = Assert.Single(scope.Lifetime.Windows.OfType<ImageResultWindow>());
        window.Show(); Layout(window);
        var image = Assert.Single(window.GetVisualDescendants().OfType<Image>(), i => i.Source is not null && i.Bounds.Width > 100);
        var first = image.Source;
        await Press(window, Key.OemComma);
        Assert.Equal("2 / 2", Position(window));
        Assert.NotSame(first, image.Source);
        Preview(window, "image-preview-navigation");
        window.Close();
    }

    private static async Task Press(Window window, Key key)
    {
        window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key });
        await Settle(window);
    }

    // A wheel notch of dy: negative scrolls up, which steps to the older capture.
    private static void Wheel(Window window, double dy) =>
        window.RaiseEvent(new PointerWheelEventArgs(null, null!, window, default, 0, default, KeyModifiers.None, new Vector(0, dy)));

    // Loads happen off the UI thread, so pump until the preview reports a stable position.
    private static async Task Settle(Window window)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            Dispatcher.UIThread.RunJobs();
            if (!window.GetVisualDescendants().OfType<TextBlock>().Any(t => t.IsEffectivelyVisible && t.Text == "正在加载…")) break;
            await Task.Delay(10);
        }
        Layout(window);
    }

    private static void Layout(Window window) { window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }

    private static string Position(Window window) =>
        Assert.Single(window.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && (t.Text?.Contains(" / ") ?? false)).Text!;

    private static Button Older(Window window) => Assert.Single(window.GetVisualDescendants().OfType<Button>(),
        b => b.IsEffectivelyVisible && AutomationProperties.GetName(b)?.StartsWith("上一张") == true);

    private static Button Newer(Window window) => Assert.Single(window.GetVisualDescendants().OfType<Button>(),
        b => b.IsEffectivelyVisible && AutomationProperties.GetName(b)?.StartsWith("下一张") == true);

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static void Preview(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("TBOX_UI_PREVIEWS") is not { Length: > 0 } directory) return;
        Directory.CreateDirectory(directory);
        var size = window.Bounds.Size;
        using var preview = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(Math.Max(1, (int)size.Width), Math.Max(1, (int)size.Height)), new Vector(96, 96));
        preview.Render(window); preview.Save(Path.Combine(directory, name + ".png"));
    }

    private sealed class Cache : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "tbox-preview-" + Guid.NewGuid().ToString("N"));
        public Cache() => System.IO.Directory.CreateDirectory(Directory);

        // Writes count captures, oldest first; the last one is the newest and therefore position 1.
        public string[] Files(int count)
        {
            var paths = new List<string>();
            for (var index = 0; index < count; index++)
            {
                var path = Path.Combine(Directory, $"2026-11-08_12-00-0{index}.png");
                using (var picture = DesktopController.RenderText($"capture {index}")) File.WriteAllBytes(path, picture.Png);
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(index - 10));
                paths.Add(path);
            }
            return paths.ToArray();
        }

        public void Dispose() { if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, true); }
    }

    private sealed class Scope : IDisposable
    {
        public ClassicDesktopStyleApplicationLifetime Lifetime { get; } = new() { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        public DesktopController Controller { get; }
        private readonly string _settings;
        public Scope(string directory)
        {
            typeof(ClassicDesktopStyleApplicationLifetime).GetInterfaces().Single(i => i.Name == "ISetupApplicationLifetime")
                .GetMethod("BeforeAppInit")!.Invoke(Lifetime, null);
            _settings = Path.Combine(Path.GetTempPath(), "tbox-preview-settings-" + Guid.NewGuid().ToString("N"));
            Controller = new DesktopController(new FakePlatform(), Lifetime, Path.Combine(_settings, "settings.json"));
            Controller.UseCacheDirectoryForTests(directory);
            Controller.Main = new MainWindow(Controller);
        }
        public void Dispose()
        {
            Controller.Dispose();
            foreach (var window in Lifetime.Windows.ToArray()) window.Close();
            Controller.Main.Close();
            Lifetime.Dispose();
            if (System.IO.Directory.Exists(_settings)) System.IO.Directory.Delete(_settings, true);
        }
    }
}
