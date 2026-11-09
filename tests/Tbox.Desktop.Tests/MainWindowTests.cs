using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tbox.Core;
using Tbox.Desktop.Models;
using Tbox.Desktop.Views;
using Xunit;

namespace Tbox.Desktop.Tests;

// The single main window (spec ③): sidebar pages, capture tools, recent captures and status dots.
public sealed class MainWindowTests
{
    [AvaloniaFact]
    public void CapturePageOffersEveryToolAndTheSidebarSwitchesPages()
    {
        if (!Application.Current!.Styles.OfType<FluentTheme>().Any()) Application.Current.Styles.Add(DesignTokens.CreateTheme());
        using var scope = new Scope(new FakePlatform { HasScreenPermission = true });
        var main = scope.Controller.Main;
        scope.Controller.ShowMain(); Layout(main);
        Assert.Equal(MainPage.Capture, main.CurrentPage);
        Assert.Equal(MainWindow.MinimumWidth, main.MinWidth); Assert.Equal(MainWindow.MinimumHeight, main.MinHeight);
        foreach (var tool in new[] { "截图", "贴出剪贴板", "长截图", "录屏", "识别文字", "更改保存位置" })
            Assert.True(Find(main, tool).IsEffectivelyEnabled, tool);
        foreach (var (label, page) in new[] { ("文件夹跳转", MainPage.Folders), ("通用", MainPage.General), ("关于", MainPage.About), ("截图", MainPage.Capture) })
        {
            Click(main, label, sidebar: true);
            Assert.Equal(page, main.CurrentPage);
        }
        // The theme switch is gone; Tbox UI 3.0 is light only.
        Assert.DoesNotContain(main.GetVisualDescendants().OfType<ComboBox>(), c => c.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task DisabledCaptureModuleExplainsItAndLeavesFolderJumpAvailable()
    {
        using var scope = new Scope(new FakePlatform { HasScreenPermission = true });
        var main = scope.Controller.Main;
        await scope.Controller.SetModulesAsync(false, null);
        scope.Controller.ShowMain(); Layout(main);
        foreach (var tool in new[] { "截图", "贴出剪贴板", "长截图", "录屏", "识别文字" })
            Assert.False(Find(main, tool).IsEffectivelyEnabled, tool);
        Assert.Contains(main.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == "截图与贴图已关闭，快捷键已释放。打开右上角开关即可恢复。");
        Click(main, "文件夹跳转", sidebar: true); Layout(main);
        Assert.True(Assert.Single(main.CurrentPageView.GetVisualDescendants().OfType<ToggleSwitch>(), t => t.IsEffectivelyVisible).IsEffectivelyEnabled);
    }

    [AvaloniaFact]
    public void ToolsHideTheMainWindowBeforeTheCaptureStartsAndClosingOnlyHides()
    {
        using var scope = new Scope(new FakePlatform { HasScreenPermission = true });
        var main = scope.Controller.Main;
        scope.Controller.ShowMain(); Layout(main);
        Click(main, "截图");
        Assert.False(main.IsVisible);

        scope.Controller.ShowMain();
        main.Close();
        Assert.False(main.IsVisible); // Closing hides; Tbox keeps running in the tray.
        scope.Controller.ShowMain();
        Assert.True(main.IsVisible);
    }

    [AvaloniaFact]
    public void ReopeningAHiddenWindowReturnsToTheCapturePage()
    {
        using var scope = new Scope(new FakePlatform());
        var main = scope.Controller.Main;
        scope.Controller.ShowMain(); main.Navigate(MainPage.About);
        scope.Controller.ShowMain();
        Assert.Equal(MainPage.About, main.CurrentPage); // Already open: stays where the user is.
        main.Hide(); scope.Controller.ShowMain();
        Assert.Equal(MainPage.Capture, main.CurrentPage);
    }

    [AvaloniaFact]
    public async Task MissingPermissionMakesTheWarningBarTheOnlyPrimaryButtonAndMarksTheSidebar()
    {
        using var scope = new Scope(new FakePlatform());
        var main = scope.Controller.Main;
        scope.Controller.ShowMain(); Layout(main);
        Assert.False(HasButton(main, "允许屏幕录制")); // Opening the window never prompts unrequested.
        Assert.Null(Dot(main, "截图"));
        main.Hide();
        await scope.Controller.CaptureAsync(); Layout(main);
        Assert.True(main.IsVisible);
        Assert.True(HasButton(main, "允许屏幕录制"));
        var primaries = main.CurrentPageView.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible && b.Classes.Contains("accent")).ToArray();
        Assert.Equal("允许屏幕录制", AutomationProperties.GetName(Assert.Single(primaries)));
        Assert.Equal(Color.Parse(DesignTokens.AttentionDot), Dot(main, "截图"));
    }

    [AvaloniaFact]
    public async Task RecentCapturesShowNewestFirstPinFromTheCacheAndOpenThePreview()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tbox-main-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var scope = new Scope(new FakePlatform { HasScreenPermission = true }, directory);
            var paths = new List<string>();
            for (var index = 0; index < 6; index++)
            {
                var path = Path.Combine(directory, $"2026-11-08_12-00-0{index}.png");
                using (var picture = DesktopController.RenderText($"capture {index}")) await File.WriteAllBytesAsync(path, picture.Png);
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(index - 10));
                paths.Add(path);
            }
            var main = scope.Controller.Main;
            scope.Controller.ShowMain(); Layout(main);
            Assert.Equal(Enumerable.Range(2, 4).Reverse().Select(i => paths[i]), main.RecentPaths);
            var thumbnails = main.CurrentPageView.GetVisualDescendants().OfType<Image>().Where(i => i.IsEffectivelyVisible && i.Width is double.NaN).ToArray();
            Assert.Equal(MainWindow.RecentCount, thumbnails.Length);
            Assert.All(thumbnails, image => Assert.True(Math.Abs(image.Bounds.Width / image.Bounds.Height - 16.0 / 9) < .1, $"{image.Bounds}"));

            await scope.Controller.PinFileAsync(paths[5]);
            Assert.Equal(1, scope.Controller.PinCount);
            Assert.Single(scope.Lifetime.Windows.OfType<PinnedWindow>(), pin => pin.Picture.CachePath == paths[5]);
            // Pinning a cached capture must not create a second copy in the cache.
            Assert.Equal(6, Directory.GetFiles(directory, "*.png").Length);
            await Assert.ThrowsAsync<InvalidOperationException>(() => scope.Controller.PinFileAsync(Path.Combine(Path.GetTempPath(), "elsewhere.png")));
            foreach (var pin in scope.Lifetime.Windows.OfType<PinnedWindow>().ToArray()) pin.Close();

            // A single click on a thumbnail opens the image preview.
            await scope.Controller.OpenCachedAsync(paths[5]);
            Assert.Single(scope.Lifetime.Windows.OfType<ImageResultWindow>()).Close();
        }
        finally { Directory.Delete(directory, true); }
    }

    [AvaloniaFact]
    public void EmptyCacheExplainsWhereCapturesWillAppear()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tbox-main-empty-" + Guid.NewGuid().ToString("N"));
        using var scope = new Scope(new FakePlatform(), directory);
        var main = scope.Controller.Main;
        scope.Controller.ShowMain(); Layout(main);
        Assert.Empty(main.RecentPaths);
        Assert.Contains(main.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == "截图后会显示在这里。");
    }

    [Theory]
    [InlineData("Ctrl+G", new[] { "Ctrl", "G" })]
    [InlineData("⌃G", new[] { "⌃", "G" })]
    public void ShortcutsUseOneKeyCapPerKey(string label, string[] caps) => Assert.Equal(caps, MainWindow.KeyCaps(label));

    [Theory]
    [InlineData("2026-11-08 12:08", "12:08")]
    [InlineData("2026-11-07 23:59", "昨天")]
    [InlineData("2026-10-30 09:00", "10/30")]
    public void RecentLabelsUseTimeTodayAndDatesBefore(string captured, string expected) =>
        Assert.Equal(expected, RecentCaptureLabel.Format(DateTime.Parse(captured), new DateTime(2026, 11, 8, 12, 14, 0)));

    private static void Layout(Window window) { window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }

    private static Button Find(MainWindow window, string label, bool sidebar = false)
    {
        window.UpdateLayout();
        var page = window.CurrentPageView;
        var buttons = sidebar
            ? window.GetVisualDescendants().OfType<Button>().Where(b => !b.GetVisualAncestors().Contains(page))
            : page.GetVisualDescendants().OfType<Button>();
        return Assert.Single(buttons, b => b.IsEffectivelyVisible && AutomationProperties.GetName(b)?.Split(" · ")[0] == label);
    }

    private static void Click(MainWindow window, string label, bool sidebar = false) =>
        Find(window, label, sidebar).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static bool HasButton(Window window, string name) =>
        window.GetVisualDescendants().OfType<Button>().Any(b => b.IsEffectivelyVisible && AutomationProperties.GetName(b) == name);

    // Colour of the sidebar status dot beside a page, or null when it is hidden.
    private static Color? Dot(MainWindow window, string page)
    {
        var dot = Find(window, page, sidebar: true).GetVisualDescendants().OfType<Border>().Single(b => b.Width == 7);
        return dot.IsVisible ? ((ISolidColorBrush)dot.Background!).Color : null;
    }

    private sealed class Scope : IDisposable
    {
        public ClassicDesktopStyleApplicationLifetime Lifetime { get; } = new() { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        public DesktopController Controller { get; }
        private readonly string _settings;
        public Scope(FakePlatform platform, string? directory = null)
        {
            // Headless tests do not run AppBuilder's internal lifetime setup.
            typeof(ClassicDesktopStyleApplicationLifetime).GetInterfaces().Single(i => i.Name == "ISetupApplicationLifetime")
                .GetMethod("BeforeAppInit")!.Invoke(Lifetime, null);
            _settings = Path.Combine(Path.GetTempPath(), "tbox-main-settings-" + Guid.NewGuid().ToString("N"));
            Controller = new DesktopController(platform, Lifetime, Path.Combine(_settings, "settings.json"));
            Controller.UseCacheDirectoryForTests(directory ?? Path.Combine(_settings, "cache"));
            Controller.Main = new MainWindow(Controller);
        }
        public void Dispose()
        {
            Controller.Dispose();
            foreach (var window in Lifetime.Windows.ToArray()) window.Close();
            Controller.Main.Close();
            Lifetime.Dispose();
            if (Directory.Exists(_settings)) Directory.Delete(_settings, true);
        }
    }
}
