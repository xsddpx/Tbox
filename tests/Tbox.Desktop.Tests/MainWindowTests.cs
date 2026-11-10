using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
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
        var switches = main.CurrentPageView.GetVisualDescendants().OfType<ToggleSwitch>().Where(t => t.IsEffectivelyVisible);
        Assert.Equal(2, switches.Count());
        Assert.All(switches, toggle => Assert.True(toggle.IsEffectivelyEnabled));
    }

    [AvaloniaFact]
    public void ClosingTheMainWindowHidesItAndKeepsTboxRunning()
    {
        using var scope = new Scope(new FakePlatform());
        var main = scope.Controller.Main;
        scope.Controller.ShowMain();
        main.Close();
        Assert.False(main.IsVisible); // Closing hides; Tbox keeps running in the tray.
        scope.Controller.ShowMain();
        Assert.True(main.IsVisible);
    }

    [AvaloniaFact]
    public void FolderJumpBarShowsTheOfficialIconAndOnlyThreeRecommendations()
    {
        using var scope = new Scope(new FakePlatform());
        using var navigation = scope.Controller.Platform.CreateFolderNavigation();
        var bar = new FolderJumpBarWindow(scope.Controller, navigation, (_, _) => Task.CompletedTask,
            () => Task.FromResult(true), () => { }) { Width = 500, Height = 220 };
        var paths = Enumerable.Range(1, 4).Select(i => Path.Combine(Path.GetTempPath(), $"tbox-open-{i}")).ToArray();
        try
        {
            bar.SetOpenFolders(paths);
            bar.Show();
            Layout(bar);
            var icon = Assert.Single(bar.GetVisualDescendants().OfType<Image>(), image => AutomationProperties.GetName(image) == "Tbox");
            using var official = Ui.Brand();
            using var expected = new MemoryStream();
            using var actual = new MemoryStream();
            official.Save(expected);
            Assert.IsType<Bitmap>(icon.Source).Save(actual);
            Assert.Equal(expected.ToArray(), actual.ToArray());
            foreach (var path in paths.Take(3))
                Assert.Contains(bar.GetVisualDescendants().OfType<Button>(), button => AutomationProperties.GetName(button) == path);
            Assert.DoesNotContain(bar.GetVisualDescendants().OfType<Button>(), button => AutomationProperties.GetName(button) == paths[3]);
        }
        finally { bar.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task NormalCaptureKeepsTheMainWindowInItsCurrentState(bool useShortcut, bool mainVisible)
    {
        using var frame = DesktopController.RenderText("Screen capture");
        var size = frame.Bitmap.PixelSize;
        var platform = new FakePlatform
        {
            HasScreenPermission = true,
            Displays = [new(1, 0, 0, size.Width, size.Height, frame.Png)]
        };
        using var scope = new Scope(platform);
        await scope.InitializeAsync();
        var main = scope.Controller.Main;
        var page = useShortcut ? MainPage.About : MainPage.Capture;
        if (mainVisible) { scope.Controller.ShowMain(page); Layout(main); }

        if (useShortcut) platform.HotkeyHandler!(HotkeyAction.Capture);
        else Click(main, "截图");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(mainVisible, main.IsVisible);

        for (var attempt = 0; attempt < 100 && platform.CaptureRequests == 0; attempt++) await Task.Delay(10);
        Assert.Equal(1, platform.CaptureRequests);
        var overlay = Assert.Single(scope.Lifetime.Windows.OfType<CaptureWindow>());
        Assert.Equal(mainVisible, main.IsVisible);
        overlay.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
        Assert.Empty(scope.Lifetime.Windows.OfType<CaptureWindow>());
        Assert.Equal(mainVisible, main.IsVisible);
        if (mainVisible) Assert.Equal(page, main.CurrentPage);
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

    [AvaloniaFact]
    public async Task RecordingAShortcutPausesTboxHotkeysSavesItAndUpdatesLabels()
    {
        var platform = new FakePlatform { HasScreenPermission = true };
        using var scope = new Scope(platform);
        var controller = scope.Controller; var main = controller.Main;
        await scope.InitializeAsync();
        controller.ShowMain(); Layout(main);
        var row = Assert.Single(main.CurrentPageView.GetVisualDescendants().OfType<ShortcutRow>(), r => Name(r.Recorder) == "修改截图快捷键");
        var mac = OperatingSystem.IsMacOS();
        var captureDefault = ShortcutPolicy.Default(HotkeyAction.Capture, mac);
        Assert.Equal(captureDefault.Caps(mac), Caps(row));
        var changed = 0; controller.ShortcutsChanged += () => changed++;

        // All of Tbox's combinations reach the recorder during editing.
        row.Recorder.Start();
        Assert.Empty(platform.Hotkeys);
        platform.HotkeyHandler!(HotkeyAction.Capture);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, platform.CaptureRequests);
        Assert.Equal("按 Esc 取消", row.Description);
        Layout(main); TaskToolbarTests.Preview(main, "main-shortcut-recording");
        row.Recorder.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
        Assert.False(row.Recorder.IsRecording);
        Assert.Equal(4, platform.Hotkeys.Count);
        Assert.Equal(captureDefault, platform.Hotkeys[HotkeyAction.Capture]);

        // A combination already used by another Tbox shortcut is refused and the old one stays.
        await Press(row, mac ? Key.D2 : Key.F7, mac ? KeyModifiers.Control | KeyModifiers.Shift : KeyModifiers.None);
        Assert.Equal("已被“贴出剪贴板”使用。", row.Description);
        Assert.Equal(captureDefault.Caps(mac), Caps(row));

        // One held by another application is refused too.
        var taken = new Shortcut(false, true, false, false, "S");
        platform.TakenShortcuts.Add(taken);
        await Press(row, Key.S, KeyModifiers.Alt);
        Assert.Equal($"{taken.Display(mac)} 已被其他应用占用，请换一个组合。", row.Description);
        Assert.Equal(captureDefault, platform.Hotkeys[HotkeyAction.Capture]);

        await Press(row, Key.NumPad5, KeyModifiers.Control);
        Assert.Contains("不支持这个按键", row.Description);
        Assert.Equal(captureDefault, platform.Hotkeys[HotkeyAction.Capture]);

        await Press(row, Key.A, KeyModifiers.Control | KeyModifiers.Shift);
        var chosen = new Shortcut(true, false, true, false, "A");
        Assert.Equal(chosen, platform.Hotkeys[HotkeyAction.Capture]);
        Assert.Equal(chosen, controller.Settings.Hotkeys!.Capture);
        Assert.Equal(chosen.Caps(mac), Caps(row));
        Assert.Equal($"截图 · {chosen.Display(mac)}", Name(Find(main, "截图")));
        Assert.Equal(1, changed);

        // "恢复默认" appears only for a changed shortcut and clears the saved override.
        var reset = Assert.Single(row.GetVisualDescendants().OfType<Button>(), b => Name(b) == "恢复截图默认快捷键");
        Assert.True(reset.IsVisible);
        await row.Recorder.ResetAsync(); Layout(main);
        Assert.Null(controller.Settings.Hotkeys);
        Assert.Equal(captureDefault, platform.Hotkeys[HotkeyAction.Capture]);
        Assert.False(reset.IsVisible);
    }

    [AvaloniaFact]
    public async Task StartingWithSavedShortcutsRefreshesTheMenuAndButtonLabels()
    {
        var platform = new FakePlatform { HasScreenPermission = true };
        using var scope = new Scope(platform);
        var controller = scope.Controller;
        var saved = new Shortcut(true, true, false, false, "K");
        string? menuLabel = null;
        controller.ShortcutsChanged += () => menuLabel = ShortcutLabels.Capture;

        await scope.InitializeAsync(new HotkeyBindings(Capture: saved));
        controller.ShowMain(); Layout(controller.Main);

        var display = saved.Display(OperatingSystem.IsMacOS());
        Assert.Equal(saved, platform.Hotkeys[HotkeyAction.Capture]);
        Assert.Equal(display, menuLabel);
        Assert.Equal($"截图 · {display}", Name(Find(controller.Main, "截图")));
    }

    [AvaloniaFact]
    public async Task AShortcutHeldElsewhereIsReportedOnItsOwnRowAndTheOthersStillWork()
    {
        var platform = new FakePlatform { HasScreenPermission = true };
        platform.TakenShortcuts.Add(ShortcutPolicy.Default(HotkeyAction.Pin, OperatingSystem.IsMacOS()));
        using var scope = new Scope(platform);
        var controller = scope.Controller; var main = controller.Main;
        await scope.InitializeAsync();
        controller.ShowMain(); Layout(main);
        Assert.True(platform.Hotkeys.ContainsKey(HotkeyAction.Capture)); Assert.True(platform.Hotkeys.ContainsKey(HotkeyAction.Folder));
        var rows = main.CurrentPageView.GetVisualDescendants().OfType<ShortcutRow>().ToDictionary(r => Name(r.Recorder)!);
        Assert.Equal("被其他应用占用，请换一个组合", rows["修改贴出剪贴板快捷键"].Description);
        Assert.Equal("框选区域后标注、贴图或识别文字", rows["修改截图快捷键"].Description);
        Assert.Equal(Color.Parse(DesignTokens.Recording), Dot(main, "截图"));
        Assert.Null(Dot(main, "文件夹跳转"));
        Layout(main); TaskToolbarTests.Preview(main, "main-shortcut-conflict");

        // The module switch is no longer blocked by the conflict.
        await controller.SetModulesAsync(false, null);
        Assert.False(controller.Settings.ScreenPasteEnabled);
        Assert.Equal([HotkeyAction.Folder], platform.Hotkeys.Keys);
        Assert.Null(Dot(main, "截图"));
    }

    private static string? Name(Control control) => AutomationProperties.GetName(control);

    private static string[] Caps(ShortcutRow row) =>
        row.Recorder.GetLogicalDescendants().OfType<TextBlock>().Where(t => t.FindLogicalAncestorOfType<Border>() is { Height: 20 }).Select(t => t.Text!).ToArray();

    // Records one combination and waits for the save, which finishes off the UI thread.
    private static async Task Press(ShortcutRow row, Key key, KeyModifiers modifiers = KeyModifiers.None)
    {
        row.Recorder.Start();
        row.Recorder.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers });
        for (var wait = 0; row.Recorder.IsRecording && wait < 200; wait++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
        Assert.False(row.Recorder.IsRecording);
    }

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
        public async Task InitializeAsync(HotkeyBindings? hotkeys = null)
        {
            await new JsonSettingsStore(Path.Combine(_settings, "settings.json")).SaveAsync(new AppSettings
            {
                ScreenCaptureCacheDirectory = Path.Combine(_settings, "cache"),
                Hotkeys = hotkeys
            });
            await Controller.InitializeAsync([]);
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
