using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tbox.Core;
using Tbox.Desktop.Platform;
using Tbox.Desktop.Views;
using Xunit;

namespace Tbox.Desktop.Tests;

public sealed class TrayPanelTests
{
    [AvaloniaFact]
    public void PanelOffersEveryToolInOnePlaceInBothThemes()
    {
        if (!Application.Current!.Styles.OfType<FluentTheme>().Any()) Application.Current.Styles.Add(DesignTokens.CreateTheme());
        using var scope = new Scope(new FakePlatform { HasScreenPermission = true });
        var panel = scope.Panel;
        foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            panel.RequestedThemeVariant = theme;
            scope.Controller.ShowPanel(); panel.UpdateLayout(); Dispatcher.UIThread.RunJobs(); panel.UpdateLayout();
            foreach (var tool in new[] { "截图", "贴图", "录屏", "长截图", "识别图片文字", "显隐全部贴图", "文件夹跳转", "设置", "退出 Tbox" })
                Assert.True(Find(panel, tool).IsEffectivelyEnabled, tool);
            Assert.Equal(TrayPanel.PanelWidth, panel.Bounds.Width, 1);
            Assert.All(panel.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible),
                b => Assert.True(b.TranslatePoint(new Point(b.Bounds.Width, 0), panel)!.Value.X <= panel.Bounds.Width + 1, AutomationProperties.GetName(b)));
            if (Environment.GetEnvironmentVariable("TBOX_UI_PREVIEWS") is { Length: > 0 } directory)
            {
                Directory.CreateDirectory(directory);
                using var preview = new RenderTargetBitmap(new PixelSize(TrayPanel.PanelWidth, (int)panel.Bounds.Height), new Vector(96, 96));
                preview.Render(panel); preview.Save(Path.Combine(directory, $"tray-panel-{theme.Key}.png"));
            }
            panel.Hide();
        }
    }

    [AvaloniaFact]
    public async Task DisabledCaptureModuleLeavesSettingsAndFolderJumpAvailable()
    {
        using var scope = new Scope(new FakePlatform { HasScreenPermission = true });
        await scope.Controller.SetModulesAsync(false, null);
        scope.Controller.ShowPanel(); scope.Panel.UpdateLayout();
        foreach (var tool in new[] { "截图", "贴图", "录屏", "长截图", "识别图片文字" })
            Assert.False(Find(scope.Panel, tool).IsEffectivelyEnabled, tool);
        Assert.True(Find(scope.Panel, "设置").IsEffectivelyEnabled);
        Assert.True(Find(scope.Panel, "文件夹跳转").IsEffectivelyEnabled);
        Assert.Contains(scope.Panel.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "截图与贴图已在设置中关闭");
    }

    [AvaloniaFact]
    public void ToolHidesThePanelBeforeTheCaptureStartsAndSettingsOpenFromTheGear()
    {
        var platform = new FakePlatform { HasScreenPermission = true };
        using var scope = new Scope(platform);
        scope.Controller.ShowPanel();
        Click(scope.Panel, "设置");
        Assert.False(scope.Panel.IsVisible); Assert.True(scope.Controller.Main.IsVisible);
        scope.Controller.Main.Hide();

        scope.Controller.ShowPanel();
        Click(scope.Panel, "截图");
        Assert.False(scope.Panel.IsVisible);
    }

    [AvaloniaFact]
    public void FocusLossAndEscapeCloseThePanel()
    {
        using var scope = new Scope(new FakePlatform());
        scope.Controller.ShowPanel();
        scope.Panel.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.False(scope.Panel.IsVisible);
        scope.Controller.ShowPanel();
        // Headless windows never lose activation to each other, so raise the same handler directly.
        scope.Panel.OnFocusLost();
        Assert.True(scope.Panel.IsVisible); // A stale deactivation right after opening is ignored.
        Thread.Sleep(300);
        scope.Panel.OnFocusLost();
        Assert.False(scope.Panel.IsVisible);
    }

    [AvaloniaFact]
    public async Task RecentCapturesShowNewestFirstAndPinFromTheCache()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tbox-panel-" + Guid.NewGuid().ToString("N"));
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
            scope.Controller.ShowPanel(); scope.Panel.UpdateLayout();
            Assert.Equal(Enumerable.Range(2, 4).Reverse().Select(i => paths[i]), scope.Panel.RecentPaths);
            Assert.Equal(TrayPanel.RecentCount, scope.Panel.GetVisualDescendants().OfType<Image>().Count(i => i.IsEffectivelyVisible && i.Width is double.NaN));

            await scope.Controller.PinFileAsync(paths[5]);
            Assert.Equal(1, scope.Controller.PinCount);
            Assert.Single(scope.Lifetime.Windows.OfType<PinnedWindow>(), pin => pin.Picture.CachePath == paths[5]);
            // Pinning a cached capture must not create a second copy in the cache.
            Assert.Equal(6, Directory.GetFiles(directory, "*.png").Length);
            await Assert.ThrowsAsync<InvalidOperationException>(() => scope.Controller.PinFileAsync(Path.Combine(Path.GetTempPath(), "elsewhere.png")));
            foreach (var pin in scope.Lifetime.Windows.OfType<PinnedWindow>().ToArray()) pin.Close();
        }
        finally { Directory.Delete(directory, true); }
    }

    [AvaloniaFact]
    public void EmptyCacheExplainsWhereCapturesWillAppear()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tbox-panel-empty-" + Guid.NewGuid().ToString("N"));
        using var scope = new Scope(new FakePlatform(), directory);
        scope.Controller.ShowPanel(); scope.Panel.UpdateLayout();
        Assert.Empty(scope.Panel.RecentPaths);
        Assert.Contains(scope.Panel.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == "截图后会显示在这里。");
    }

    [Theory]
    [InlineData(1500, 1040, 1148, 668)]   // Windows taskbar at the bottom: opens upward, kept inside the right edge.
    [InlineData(1000, 12, 820, 24)]       // macOS menu bar at the top: opens downward under the icon.
    [InlineData(40, 1040, 12, 668)]       // Icon near the left edge stays fully visible.
    public void PanelOpensBesideTheIconAndStaysOnScreen(double x, double y, double left, double top)
    {
        var place = PanelPlacementPolicy.Place(360, 360, x, y, 0, 0, 1520, 1040, 12);
        Assert.Equal(left, place.Left);
        Assert.Equal(top, place.Top);
    }

    [Theory]
    [InlineData("2026-11-08 12:08", "12:08")]
    [InlineData("2026-11-07 23:59", "昨天")]
    [InlineData("2026-10-30 09:00", "10/30")]
    public void RecentLabelsUseTimeTodayAndDatesBefore(string captured, string expected) =>
        Assert.Equal(expected, RecentCaptureLabel.Format(DateTime.Parse(captured), new DateTime(2026, 11, 8, 12, 14, 0)));

    private static Button Find(Window window, string label)
    {
        window.UpdateLayout();
        return Assert.Single(window.GetVisualDescendants().OfType<Button>(), b =>
            b.IsEffectivelyVisible && AutomationProperties.GetName(b)?.Split(" · ")[0] == label);
    }

    private static void Click(Window window, string label) => Find(window, label).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private sealed class Scope : IDisposable
    {
        public ClassicDesktopStyleApplicationLifetime Lifetime { get; } = new() { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        public DesktopController Controller { get; }
        public TrayPanel Panel { get; }
        private readonly string? _settings;
        public Scope(FakePlatform platform, string? directory = null)
        {
            // Headless tests do not run AppBuilder's internal lifetime setup.
            typeof(ClassicDesktopStyleApplicationLifetime).GetInterfaces().Single(i => i.Name == "ISetupApplicationLifetime")
                .GetMethod("BeforeAppInit")!.Invoke(Lifetime, null);
            _settings = Path.Combine(Path.GetTempPath(), "tbox-panel-settings-" + Guid.NewGuid().ToString("N"));
            Controller = new DesktopController(platform, Lifetime, Path.Combine(_settings, "settings.json"));
            Controller.UseCacheDirectoryForTests(directory ?? Path.Combine(_settings, "cache"));
            Controller.Main = new MainWindow(Controller);
            Panel = Controller.Panel = new TrayPanel(Controller);
        }
        public void Dispose()
        {
            Controller.Dispose(); Panel.Close(); Controller.Main.Close(); Lifetime.Dispose();
            if (_settings is not null && Directory.Exists(_settings)) Directory.Delete(_settings, true);
        }
    }
}
