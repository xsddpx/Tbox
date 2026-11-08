using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Styling;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Tbox.Core;
using Tbox.Desktop.Models;
using Tbox.Desktop.Platform;
using Tbox.Desktop.Views;
using Xunit;

namespace Tbox.Desktop.Tests;

public sealed class SettingsTests
{
    [AvaloniaFact]
    public void AllPagesFitWideMediumAndNarrowWindows()
    {
        if (!Application.Current!.Styles.OfType<FluentTheme>().Any()) Application.Current.Styles.Add(DesignTokens.CreateTheme());
        using var controller = new DesktopController(new FakePlatform(), new ClassicDesktopStyleApplicationLifetime());
        var window = controller.Main = new MainWindow(controller);
        window.Show();
        foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        foreach (var width in new[] { 1280, 900, 560, 360 })
        {
            window.RequestedThemeVariant = theme;
            window.Width = width; window.Height = 460;
            foreach (var page in Enum.GetValues<SettingsPage>())
            {
                window.Navigate(page); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                Assert.Empty(window.GetVisualDescendants().OfType<SplitView>());
                Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBox>(), t => t.IsEffectivelyVisible);
                Assert.True(window.CurrentPageView.Bounds.Width > width - 10);
                foreach (var control in window.CurrentPageView.GetVisualDescendants().OfType<Control>()
                    .Where(c => c.IsEffectivelyVisible && c.Bounds.Width > 0 && c is Button or ToggleSwitch or ComboBox))
                {
                    var point = control.TranslatePoint(default, window)!.Value;
                    Assert.True(point.X >= -1 && point.X + control.Bounds.Width <= width + 1,
                        $"{page} width {width}: {control.GetType().Name} extends from {point.X} by {control.Bounds.Width}");
                }
                if (Environment.GetEnvironmentVariable("TBOX_UI_PREVIEWS") is { Length: > 0 } directory)
                {
                    Directory.CreateDirectory(directory);
                    using var preview = new RenderTargetBitmap(new PixelSize(width, 460), new Vector(96, 96));
                    preview.Render(window); preview.Save(Path.Combine(directory, $"{page}-{theme.Key}-{width}.png"));
                }
            }
        }
        controller.Dispose(); window.Close();
    }

    [AvaloniaFact]
    public void SettingsTabsSwitchPagesAndHoldNoEverydayTools()
    {
        using var controller = new DesktopController(new FakePlatform(), new ClassicDesktopStyleApplicationLifetime());
        var window = controller.Main = new MainWindow(controller);
        window.Show();
        try
        {
            Assert.Equal(SettingsPage.General, window.CurrentPage);
            Tab("文件夹跳转").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(SettingsPage.Folders, window.CurrentPage);
            Assert.True(Tab("文件夹跳转").IsChecked); Assert.False(Tab("常规").IsChecked);
            Tab("常规").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(SettingsPage.General, window.CurrentPage);
            window.UpdateLayout();
            // Capture, pin, OCR and recording entries now live only in the tray panel.
            foreach (var tool in new[] { "开始截图", "剪贴板贴图", "区域录屏", "选择图片…" })
                Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(), b => AutomationProperties.GetName(b)?.Split(" · ")[0] == tool);
            Assert.Contains(window.GetVisualDescendants().OfType<Button>(), b => b.IsEffectivelyVisible && Equals(b.Content, "检查更新"));
        }
        finally { controller.Dispose(); window.Close(); }

        Avalonia.Controls.Primitives.ToggleButton Tab(string label)
        {
            window.UpdateLayout();
            return Assert.Single(window.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>(), b =>
                b is not ToggleSwitch && AutomationProperties.GetName(b) == label);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SharedPaletteKeepsTextAndActionIconsReadable(bool dark)
    {
        var c = DesignTokens.Colors(dark);
        foreach (var background in new[] { c.Background, c.Surface, c.SecondarySurface })
        {
            Assert.True(Contrast(c.Text, background) >= 4.5);
            Assert.True(Contrast(c.SecondaryText, background) >= 4.5);
        }
        Assert.True(Contrast(c.Accent, c.AccentSurface) >= 3);
        static double Contrast(string foreground, string background)
        {
            static double Luminance(string value)
            {
                var color = Color.Parse(value);
                static double Channel(byte value) { var n = value / 255.0; return n <= .04045 ? n / 12.92 : Math.Pow((n + .055) / 1.055, 2.4); }
                return .2126 * Channel(color.R) + .7152 * Channel(color.G) + .0722 * Channel(color.B);
            }
            var a = Luminance(foreground); var b = Luminance(background);
            return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
        }
    }

    [AvaloniaFact]
    public async Task ConcurrentSettingsEditsPreserveOtherChangedFields()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tbox-settings-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(directory, "settings.json");
            using var controller = new DesktopController(new FakePlatform(), new ClassicDesktopStyleApplicationLifetime(), path);
            controller.Main = new MainWindow(controller);
            await Task.WhenAll(controller.SetModulesAsync(false, null), controller.SetModulesAsync(null, false),
                controller.SetFailureNotificationsAsync(false), controller.ChangeThemeAsync(2));
            var saved = await new JsonSettingsStore(path).LoadAsync();
            Assert.False(saved.ScreenPasteEnabled); Assert.False(saved.FolderJumpEnabled);
            Assert.False(saved.ShowFailureNotifications); Assert.Equal("dark", saved.AppTheme);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [AvaloniaFact]
    public async Task StartupApprovalAndFailureShowActualSystemState()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tbox-startup-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var registration = new FakeStartup();
            var path = Path.Combine(directory, "settings.json");
            using var controller = new DesktopController(new FakePlatform { StartupRegistration = registration }, new ClassicDesktopStyleApplicationLifetime(), path);
            controller.Main = new MainWindow(controller);
            await controller.SetStartupAsync(true);
            Assert.False(controller.Startup.Enabled); Assert.True(controller.Startup.RequiresApproval);
            Assert.False((await new JsonSettingsStore(path).LoadAsync()).StartWithWindows);
            registration.ThrowOnSet = true; registration.State = new StartupState(true);
            await Assert.ThrowsAsync<IOException>(() => controller.SetStartupAsync(false));
            Assert.True(controller.Startup.Enabled);
            registration.ThrowOnSet = false;
            await controller.SetStartupAsync(false);
            Assert.False(controller.Startup.Enabled); Assert.False(controller.Startup.RequiresApproval);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void UnwritableCacheFallsBackWithoutMovingExistingFiles()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tbox-cache-location-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var obstruction = Path.Combine(directory, "file"); File.WriteAllText(obstruction, "retain");
            var fallback = Path.Combine(directory, "fallback");
            var location = CacheLocation.Prepare(Path.Combine(obstruction, "cache"), fallback);
            Assert.True(location.IsFallback); Assert.Equal(fallback, location.Path);
            Assert.Equal("retain", File.ReadAllText(obstruction)); Assert.Empty(Directory.GetFiles(fallback));
            Assert.False(CacheLocation.Prepare(fallback, "unused").IsFallback);
        }
        finally { Directory.Delete(directory, true); }
    }

    [AvaloniaFact]
    public async Task DisablingFailureNotificationsDismissesAndSuppressesPopups()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tbox-notification-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var controller = new DesktopController(new FakePlatform(), new ClassicDesktopStyleApplicationLifetime(), Path.Combine(directory, "settings.json"));
            controller.Main = new MainWindow(controller);
            controller.Run(() => Task.FromException(new IOException("模拟功能失败")));
            Assert.True(FailureNotificationWindow.IsShowing);
            await controller.SetFailureNotificationsAsync(false);
            Assert.False(FailureNotificationWindow.IsShowing);
            controller.Run(() => Task.FromException(new IOException("再次模拟功能失败")));
            Assert.False(FailureNotificationWindow.IsShowing);
        }
        finally { FailureNotificationWindow.Dismiss(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private sealed class FakeStartup : IStartupRegistration
    {
        public StartupState State = new(false);
        public bool ThrowOnSet;
        public Task<StartupState> ReadAsync() => Task.FromResult(State);
        public Task<StartupState> SetEnabledAsync(bool enabled)
        {
            if (ThrowOnSet) throw new IOException("system rejected change");
            State = enabled ? new(false, true, "Needs approval") : new(false);
            return Task.FromResult(State);
        }
    }
}
