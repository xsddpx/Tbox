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
                if (page is not (MainPage.Settings or MainPage.VideoConverter))
                {
                    var banner = Assert.Single(window.CurrentPageView.GetVisualDescendants().OfType<ToolBanner>());
                    Assert.True(banner.IsEffectivelyVisible);
                    Assert.Contains(banner.GetVisualDescendants(), visual => visual is LineIcon);
                    Assert.False(string.IsNullOrWhiteSpace(banner.Title.Text));
                    Assert.Equal(DesignTokens.PageTitle, banner.Title.FontSize);
                }
                if (page == MainPage.VideoConverter)
                {
                    var actions = window.CurrentPageView.GetVisualDescendants().OfType<Button>().Select(AutomationProperties.GetName);
                    Assert.Equal(1, actions.Count(name => name == "选择文件…"));
                    Assert.DoesNotContain("导入", actions);
                    Assert.DoesNotContain("重新录屏", actions);
                    AssertConverterFits(window, "保存视频…");
                }
                // Model-service fields are intentional inputs, not the removed application-search box.
                if (page is not (MainPage.Settings or MainPage.Translation))
                    Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBox>(), t => t.IsEffectivelyVisible);
                else if (page == MainPage.Settings)
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

    [AvaloniaFact]
    public async Task LoadedConverterFillsTheWindowWithoutHidingTimelineOrExport()
    {
        if (!Application.Current!.Styles.OfType<FluentTheme>().Any()) Application.Current.Styles.Add(DesignTokens.CreateTheme());
        using var controller = new DesktopController(new FakePlatform(), new ClassicDesktopStyleApplicationLifetime());
        var window = controller.Main = new MainWindow(controller);
        var workspace = new RecordingWorkspace();
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "assets", "media-test", "fixture.mp4"))) repository = repository.Parent;
        Assert.NotNull(repository);
        var fixture = Path.Combine(repository.FullName, "assets", "media-test", "fixture.mp4");
        var duration = TimeSpan.FromSeconds(2);
        window.Show();
        window.Navigate(MainPage.VideoConverter);
        window.Converter.SetSource(workspace, new(fixture, "2026-11-21_12-19-10.mp4", new(320, 240), duration, 10, true, false));
        try
        {
            foreach (var (width, height) in new[] { (680, 520), (800, 600), (1015, 532), (1280, 760) })
            {
                window.Width = width; window.Height = height; window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                var buttons = window.CurrentPageView.GetVisualDescendants().OfType<Button>().Where(button => button.IsEffectivelyVisible).ToArray();
                Assert.DoesNotContain(buttons, button => AutomationProperties.GetName(button) is "导入" or "选择文件…");
                Assert.Contains(buttons, button => AutomationProperties.GetName(button) == "移除素材");
                AssertConverterFits(window, "保存视频…");
                var editor = window.Converter.Editor!;
                var track = editor.Track;
                var trackEnd = track.TranslatePoint(new Point(track.Bounds.Width, track.Bounds.Height), window)!.Value;
                Assert.InRange(trackEnd.Y, 0, window.ClientSize.Height - 18);
                Assert.InRange(trackEnd.X, 0, window.ClientSize.Width);
            }
            var format = window.CurrentPageView.GetVisualDescendants().OfType<Button>().Single(button => AutomationProperties.GetName(button) == "WebP");
            format.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(15, window.Converter.Editor!.Options().FramesPerSecond);
            format = window.CurrentPageView.GetVisualDescendants().OfType<Button>().Single(button => AutomationProperties.GetName(button) == "宫格图");
            format.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.Width = MainWindow.MinimumWidth; window.Height = MainWindow.MinimumHeight; window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            AssertConverterFits(window, "保存宫格图…");
            for (var wait = 0; wait < 50 && File.Exists(RecordingExporter.FfmpegPath) && window.Converter.Editor!.LoadedThumbnails < 6; wait++)
                await Task.Delay(20);
            TaskToolbarTests.Preview(window, "video-converter-minimum-loaded");
            var remove = window.CurrentPageView.GetVisualDescendants().OfType<Button>().Single(button => AutomationProperties.GetName(button) == "移除素材");
            remove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            for (var wait = 0; wait < 50 && window.Converter.Editor is not null; wait++) await Task.Delay(20);
            Assert.Null(window.Converter.Editor);
            Assert.True(File.Exists(fixture));
            Assert.Contains(window.CurrentPageView.GetVisualDescendants().OfType<Button>(), button =>
                button.IsEffectivelyVisible && AutomationProperties.GetName(button) == "选择文件…");
        }
        finally
        {
            await window.Converter.RequestCloseAsync(window, true, () => { });
            window.Close();
        }
    }

    private static void AssertConverterFits(MainWindow window, string saveLabel)
    {
        var controls = window.CurrentPageView.GetVisualDescendants().OfType<Button>().Where(button => button.IsEffectivelyVisible).ToArray();
        var segments = controls.Where(button => AutomationProperties.GetName(button) is "MP4" or "GIF" or "WebP" or "宫格图").ToArray();
        Assert.Equal(4, segments.Length);
        Assert.All(segments, segment =>
        {
            Assert.True(segment.Bounds.Width > 30);
            Assert.InRange(Math.Abs(segment.Bounds.Width - segments[0].Bounds.Width), 0, 1);
        });
        var save = controls.Single(button => AutomationProperties.GetName(button) == saveLabel);
        var saveBottom = save.TranslatePoint(new Point(0, save.Bounds.Height), window)!.Value.Y;
        Assert.InRange(window.ClientSize.Height - saveBottom, 19, 37);
    }

    // The model page opens on its connection list, which always offers "添加连接" regardless of this machine's saved models.
    private static void AssertModelPageOffersAKeyField(Window window)
    {
        Assert.Contains(window.GetVisualDescendants().OfType<Button>(),
            b => b.IsEffectivelyVisible && b.Content as string == "添加连接");
    }

    // The saved key is loaded masked into the same input; a connection without a key shows an empty input.
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ModelPageLoadsTheSavedKeyMasked(bool hasKey)
    {
        if (!Application.Current!.Styles.OfType<FluentTheme>().Any()) Application.Current.Styles.Add(DesignTokens.CreateTheme());
        var directory = Path.Combine(Path.GetTempPath(), "tbox-model-key-" + Guid.NewGuid().ToString("N"));
        try
        {
            var configurationPath = Path.Combine(directory, "models.json");
            var id = Guid.NewGuid();
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(configurationPath, $$$"""{"Connections":[{"Id":"{{{id}}}","Name":"DeepSeek","BaseUrl":"https://api.deepseek.com","Models":["deepseek-flash"]{{{(hasKey ? ",\"ApiKey\":\"settings-test-key\"" : "")}}}}]}""");
            using var service = new Tbox.Desktop.ModelServices.ModelService(configurationPath);
            var page = new ModelServicesPage(service, () => throw new InvalidOperationException());
            var window = new Window { Content = page, Width = 900, Height = 600 };
            window.Show();
            await page.OpenAsync();
            await page.BeginEditAsync(id);
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();

            Assert.Equal(hasKey ? "settings-test-key" : "", page.Secret.Text);
            Assert.False(page.IsKeyRevealed);
            Assert.True(page.Secret.IsEffectivelyVisible);
            window.Close();
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
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
