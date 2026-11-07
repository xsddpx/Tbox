using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless.XUnit;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tbox.Core;
using Tbox.Desktop.Models;
using Tbox.Desktop.Platform;
using Tbox.Desktop.Views;
using Xunit;

namespace Tbox.Desktop.Tests;

public sealed class SettingsTests
{
    [Theory]
    [InlineData("  OCR  ", 3)]
    [InlineData("截图缓存", 3)]
    [InlineData("finder 目录", 2)]
    [InlineData("登录启动", 1)]
    [InlineData("失败通知", 1)]
    [InlineData("", 0)]
    public void SearchFindsExistingPages(string query, int expected) => Assert.Equal((SettingsPage)expected, SettingsNavigation.Search(query));

    [AvaloniaFact]
    public void AllPagesFitWideMediumAndNarrowWindows()
    {
        if (!Application.Current!.Styles.OfType<FluentTheme>().Any()) Application.Current.Styles.Add(new FluentTheme());
        using var controller = new DesktopController(new FakePlatform(), new ClassicDesktopStyleApplicationLifetime());
        var window = controller.Main = new MainWindow(controller);
        window.Show();
        foreach (var width in new[] { 1280, 900, 520, 1280, 520 })
        {
            window.Width = width; window.Height = 700; window.ApplyResponsiveLayout(width);
            foreach (var page in Enum.GetValues<SettingsPage>())
            {
                window.Navigate(page); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                Assert.Equal(width >= 1080, window.HasFixedSidebar);
                Assert.True(window.CurrentPageView.Bounds.Width > width - (width >= 1080 ? 250 : 10));
                foreach (var control in window.CurrentPageView.GetVisualDescendants().OfType<Control>()
                    .Where(c => c.IsEffectivelyVisible && c.Bounds.Width > 0 && c is Button or CheckBox or ComboBox))
                {
                    var point = control.TranslatePoint(default, window)!.Value;
                    Assert.True(point.X >= -1 && point.X + control.Bounds.Width <= width + 1,
                        $"{page} width {width}: {control.GetType().Name} extends from {point.X} by {control.Bounds.Width}");
                }
            }
        }
        controller.Dispose(); window.Close();
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
