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
using Tbox.Core.ModelServices;
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
        // The window never gets narrower than its 680 × 520 minimum.
        foreach (var width in new[] { 1280, 800, MainWindow.MinimumWidth })
        {
            window.Width = width; window.Height = MainWindow.MinimumHeight;
            foreach (var page in Enum.GetValues<MainPage>())
            {
                window.Navigate(page); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                Assert.Empty(window.GetVisualDescendants().OfType<SplitView>());
                // Model-service fields are intentional inputs, not the removed application-search box.
                if (page is not (MainPage.Models or MainPage.Translation))
                    Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBox>(), t => t.IsEffectivelyVisible);
                else if (page == MainPage.Models)
                    AssertModelPageOffersAKeyField(window);
                else
                    Assert.Contains(window.GetVisualDescendants().OfType<TextBox>(), t => t.IsEffectivelyVisible && AutomationProperties.GetName(t) == "翻译原文");
                Assert.True(window.CurrentPageView.Bounds.Width > width - 208 - 10 - 10);
                foreach (var control in window.CurrentPageView.GetVisualDescendants().OfType<Control>()
                    .Where(c => c.IsEffectivelyVisible && c.Bounds.Width > 0 && c is Button or ToggleSwitch or ComboBox or TextBox))
                {
                    var point = control.TranslatePoint(default, window)!.Value;
                    Assert.True(point.X >= -1 && point.X + control.Bounds.Width <= width + 1,
                        $"{page} width {width}: {control.GetType().Name} extends from {point.X} by {control.Bounds.Width}");
                }
                if (Environment.GetEnvironmentVariable("TBOX_UI_PREVIEWS") is { Length: > 0 } directory)
                {
                    Directory.CreateDirectory(directory);
                    using var preview = new RenderTargetBitmap(new PixelSize(width, MainWindow.MinimumHeight), new Vector(96, 96));
                    preview.Render(window); preview.Save(Path.Combine(directory, $"main-{page}-{width}.png"));
                }
            }
        }
        controller.Dispose(); window.Close();
    }

    // The model page always offers a way to set the API key, but it has two legitimate shapes.
    // A connection with no saved credential shows the "新的 API 密钥" input; one that already has a
    // credential collapses that input and shows the "替换" link instead. Which one appears depends on
    // the saved settings, so the assertion must accept either rather than assume a fresh install.
    private static void AssertModelPageOffersAKeyField(Window window)
    {
        var keyInput = window.GetVisualDescendants().OfType<TextBox>()
            .Any(t => t.IsEffectivelyVisible && AutomationProperties.GetName(t) == "新的 API 密钥");
        var replaceLink = window.GetVisualDescendants().OfType<Button>()
            .Any(b => b.IsEffectivelyVisible && AutomationProperties.GetName(b) == "替换");
        Assert.True(keyInput || replaceLink,
            $"模型服务页应提供密钥输入框或“替换”入口（当前 savedCredential={!keyInput}）");
    }

    // Pins both shapes of the key field so the layout test above does not depend on this machine's saved models.
    [AvaloniaTheory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task ModelPageShowsTheKeyInputOnlyUntilACredentialIsSaved(bool hasCredential, bool expectsInput)
    {
        if (!Application.Current!.Styles.OfType<FluentTheme>().Any()) Application.Current.Styles.Add(DesignTokens.CreateTheme());
        var directory = Path.Combine(Path.GetTempPath(), "tbox-model-key-" + Guid.NewGuid().ToString("N"));
        try
        {
            var configurationPath = Path.Combine(directory, "models.json");
            var connection = new ModelConnection(Guid.NewGuid(), "DeepSeek", "https://api.deepseek.com", ["deepseek-chat"])
            {
                CredentialId = hasCredential ? Guid.NewGuid() : null
            };
            await new Tbox.Desktop.ModelServices.ModelConfigurationStore(configurationPath)
                .SaveAsync(new ModelConfiguration { Connections = [connection] }, default);
            var service = new Tbox.Desktop.ModelServices.ModelService(configurationPath, new FakeCredentials());
            var page = new ModelServicesPage(service, () => throw new InvalidOperationException());
            var window = new Window { Content = page, Width = 900, Height = 600 };
            window.Show();
            await page.OpenAsync();
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();

            // IsEditingKey drives whether the input or the "替换" link is offered; it is the authoritative state.
            Assert.Equal(expectsInput, page.IsEditingKey);
            var replace = page.GetVisualDescendants().OfType<Button>()
                .Any(b => b.IsEffectivelyVisible && AutomationProperties.GetName(b) == "替换");
            Assert.Equal(!expectsInput, replace);            window.Close();
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    // Settings-only credential store: the page never needs real secrets to decide what to show.
    private sealed class FakeCredentials : Tbox.Desktop.Platform.IModelCredentialStore
    {
        public Task<bool> ContainsAsync(Guid connectionId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<string?> ReadAsync(Guid connectionId, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task WriteAsync(Guid connectionId, string secret, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteAsync(Guid connectionId, CancellationToken cancellationToken = default) => Task.CompletedTask;
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
                controller.SetFailureNotificationsAsync(false));
            var saved = await new JsonSettingsStore(path).LoadAsync();
            Assert.False(saved.ScreenPasteEnabled); Assert.False(saved.FolderJumpEnabled);
            Assert.False(saved.ShowFailureNotifications);
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

    [AvaloniaFact]
    public async Task PassiveStartupRefreshFailurePreservesStateAndDoesNotOpenNotification()
    {
        var registration = new FakeStartup { State = new(true) };
        using var lifetime = new ClassicDesktopStyleApplicationLifetime();
        using var controller = new DesktopController(new FakePlatform { StartupRegistration = registration }, lifetime);
        controller.Main = new MainWindow(controller);
        try
        {
            await controller.RefreshStartupAsync();
            registration.ReadError = new System.Runtime.InteropServices.COMException("", unchecked((int)0x8001010D));
            await controller.RefreshStartupAsync();
            Assert.True(controller.Startup.Enabled);
            Assert.False(FailureNotificationWindow.IsShowing);
        }
        finally { controller.Main.Close(); }
    }

    [AvaloniaFact]
    public async Task FolderJumpReportsItsOwnFailureWhileTheMainWindowIsHidden()
    {
        const string reason = "无法控制当前文件对话框。";
        using var lifetime = new ClassicDesktopStyleApplicationLifetime { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        typeof(ClassicDesktopStyleApplicationLifetime).GetInterfaces().Single(i => i.Name == "ISetupApplicationLifetime")
            .GetMethod("BeforeAppInit")!.Invoke(lifetime, null);
        using var controller = new DesktopController(new FakePlatform { FolderJumpResult = new(false, reason) }, lifetime);
        controller.Main = new MainWindow(controller);
        try
        {
            await controller.JumpFolderAsync();
            var notice = Assert.Single(lifetime.Windows.OfType<FailureNotificationWindow>());
            Assert.Contains(notice.GetVisualDescendants().OfType<TextBlock>(), label => label.Text == reason);
            Assert.False(controller.Main.IsVisible);
        }
        finally { FailureNotificationWindow.Dismiss(); controller.Main.Close(); }
    }

    private sealed class FakeStartup : IStartupRegistration
    {
        public StartupState State = new(false);
        public bool ThrowOnSet;
        public Exception? ReadError;
        public Task<StartupState> ReadAsync() => ReadError is { } error ? Task.FromException<StartupState>(error) : Task.FromResult(State);
        public Task<StartupState> SetEnabledAsync(bool enabled)
        {
            if (ThrowOnSet) throw new IOException("system rejected change");
            State = enabled ? new(false, true, "Needs approval") : new(false);
            return Task.FromResult(State);
        }
    }
}
