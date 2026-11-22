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
using Tbox.Desktop.Platform;
using Tbox.Desktop.Views;
using Xunit;

namespace Tbox.Desktop.Tests;

// The single main window (spec ③): sidebar pages, capture tools, recent captures and status dots.
public sealed class MainWindowTests
{
    [AvaloniaFact]
    public async Task UpgradeRestoresOnlyOpenPinsWithEditableAnnotationsAndTheirWindowState()
    {
        var expected = new DesktopPlacement(100, 120, 160, 80);
        var platform = new FakePlatform { WindowPlacementOverride = expected };
        platform.WindowPlaced = (placement, _) => platform.WindowPlacementOverride = placement;
        (bool Hidden, List<PinSessionImage> Pins) checkpoint;
        PixelRegion region;
        using (var previous = new Scope(platform))
        {
            await previous.InitializeAsync();
            using var original = Picture.FromPixels(new byte[400 * 200 * 4], 400, 200);
            await previous.Controller.PinPictureAsync(original);
            await previous.Controller.PinPictureAsync(original);
            var open = previous.Lifetime.Windows.OfType<PinnedWindow>().ToArray();
            open[1].Close();
            var pin = open[0];
            pin.UpdateLayout();
            pin.Crop(new Point(10, 10), new Point(110, 60));
            region = pin.Region;
            Assert.NotEqual(400, region.Width);
            pin.Editor.Select(AnnotationTool.Number);
            pin.Editor.Begin(new AnnotationPoint(25, 25), false, false);
            platform.WindowPlacementOverride = expected;
            previous.Controller.TogglePins();
            await previous.Controller.SavePinsForUpgradeAsync();
            Assert.Empty(previous.PinSessions.ReadForUpgrade(typeof(DesktopController).Assembly.GetName().Version!.ToString()).Pins);
            checkpoint = previous.PinSessions.ReadForUpgrade("next version");
        }
        Assert.True(checkpoint.Hidden);
        var saved = Assert.Single(checkpoint.Pins);
        Assert.Equal(expected, saved.State.Bounds);
        Assert.Equal(region, saved.State.Region);
        var command = Assert.Single(saved.State.Annotations.ToSnapshot().Commands);
        Assert.IsType<NumberAnnotationCommand>(command);
        using var next = new Scope(platform);
        next.PinSessions.Save("older version", checkpoint.Hidden, checkpoint.Pins);
        await next.InitializeAsync();
        Assert.True(next.Controller.PinsHidden);
        next.Controller.TogglePins();
        var restored = Assert.Single(next.Lifetime.Windows.OfType<PinnedWindow>());
        Assert.True(restored.IsVisible);
        Assert.Equal(region, restored.Region);
        Assert.Equal(expected, platform.WindowPlacementOverride);
        Assert.Equal(command.Id, Assert.Single(restored.Editor.Document.Commands).Id);
        restored.Editor.Document.Undo();
        Assert.Empty(restored.Editor.Document.Commands);
        Assert.Equal(saved.State.Zoom, restored.SnapshotForUpgrade().State.Zoom, 8);
        Assert.Empty(next.PinSessions.ReadForUpgrade("next version").Pins);
    }

    [AvaloniaFact]
    public async Task SessionWriteFailureKeepsPinsOpenAndSkipsInstallerHandoff()
    {
        using var scope = new Scope(new FakePlatform());
        await scope.InitializeAsync();
        using var original = Picture.FromPixels([1, 2, 3, 255], 1, 1);
        await scope.Controller.PinPictureAsync(original);
        var archive = Path.Combine(Path.GetDirectoryName(scope.Controller.CacheDirectory)!, "update-pins.zip");
        Directory.CreateDirectory(archive);
        var launched = false;
        var failure = await Record.ExceptionAsync(() => scope.Controller.InstallUpdateAsync(() => launched = true));
        Assert.True(failure is IOException or UnauthorizedAccessException, failure?.Message);
        Assert.False(launched);
        Assert.False(scope.Controller.IsQuitting);
        Assert.Equal(1, scope.Controller.PinCount);
        Directory.Delete(archive);
        await scope.Controller.SavePinsForUpgradeAsync();
        Assert.Single(scope.PinSessions.ReadForUpgrade("next version").Pins);
    }

    [AvaloniaFact]
    public async Task LongCaptureReusesItsBoundedPreviewWhenTheViewportMovesInsideTheStitch()
    {
        const int width = 360;
        const int height = 120;
        var document = new byte[width * (height + 40) * 4];
        new Random(408).NextBytes(document);
        for (var i = 3; i < document.Length; i += 4) document[i] = 255;
        byte[] PngAt(int top)
        {
            using var frame = Picture.FromPixels(document.AsSpan(top * width * 4, width * height * 4).ToArray(), width, height);
            return frame.Png;
        }
        var initial = PngAt(0);
        var appended = PngAt(40);
        var inside = PngAt(20);
        var frames = new Queue<byte[]>([initial, appended, appended]);
        var move = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var platform = new FakePlatform
        {
            CaptureRegion = async token =>
            {
                if (frames.TryDequeue(out var next)) return next;
                await move.Task.WaitAsync(token);
                return inside;
            }
        };
        using var scope = new Scope(platform);
        var session = new LongCaptureSession(scope.Controller, new DesktopPlacement(100, 100, width, height), () => { });
        session.Start();
        try
        {
            var window = Assert.Single(scope.Lifetime.Windows, w => w.Title == "Tbox 长截图实时预览");
            var preview = Assert.Single(window.GetVisualDescendants().OfType<LongPreview>());
            await WaitForViewportAsync(preview, 40);
            var bitmap = Assert.IsType<Picture>(preview.Picture);
            Assert.Equal(new PixelSize(width, height + 40), preview.SourceSize);
            Assert.Equal(new PixelSize(180, 80), bitmap.Bitmap.PixelSize);
            move.SetResult();
            await WaitForViewportAsync(preview, 20);
            Assert.Same(bitmap, preview.Picture);
            Assert.Equal(new PixelSize(width, height + 40), preview.SourceSize);

            var toolbar = Assert.Single(scope.Lifetime.Windows, w => w.Title == "Tbox 长截图工具条");
            var openPreview = Assert.Single(toolbar.GetVisualDescendants().OfType<Button>(),
                b => AutomationProperties.GetName(b) == "全屏预览");
            openPreview.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var timeout = Environment.TickCount64 + 3000;
            while (!scope.Lifetime.Windows.OfType<ImageResultWindow>().Any() && Environment.TickCount64 < timeout)
                await Task.Delay(10);
            var result = Assert.Single(scope.Lifetime.Windows.OfType<ImageResultWindow>());
            try
            {
                Assert.False(result.Current.HasEncodedPng);
                Assert.Equal(document, result.Current.GetPixels());
                using var exported = new Picture(result.Current.Png);
                Assert.Equal(document, exported.GetPixels());
            }
            finally { result.Close(); }
        }
        finally { session.Stop(); }
    }

    private static async Task WaitForViewportAsync(LongPreview preview, int expected)
    {
        var deadline = Environment.TickCount64 + 3000;
        while (preview.ViewportTop != expected && Environment.TickCount64 < deadline) await Task.Delay(20);
        Assert.Equal(expected, preview.ViewportTop);
    }

    [AvaloniaFact]
    public async Task FolderBarCoalescesMovesAndRestoresItsSearchUntilTheTargetCloses()
    {
        var platform = new FakePlatform();
        using var scope = new Scope(platform);
        using var navigation = new ObservedDialog();
        using var session = new FolderJumpBarSession(scope.Controller, navigation, CancellationToken.None);
        var placements = new List<(DesktopPlacement Placement, bool MoveOnly)>();
        platform.WindowPlaced = (placement, moveOnly) => placements.Add((placement, moveOnly));
        session.Sync();
        await session.CheckAsync();
        var bar = Assert.Single(scope.Lifetime.Windows.OfType<FolderJumpBarWindow>());
        Assert.True(bar.IsVisible);
        Assert.False(bar.IsActive);
        Assert.Single(placements);
        var search = Assert.Single(bar.GetVisualDescendants().OfType<TextBox>());
        search.Text = "复现目录";

        var notify = Assert.IsType<Action<FolderDialogChange>>(navigation.Notify);
        var original = navigation.Target!;
        for (var x = 220; x <= 260; x += 10)
        {
            navigation.Target = original with { Bounds = original.Bounds with { Left = x } };
            notify(FolderDialogChange.Position);
        }
        Assert.Empty(placements.Skip(1));
        await navigation.FirstGeometryRead.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, navigation.GeometryReads);
        Assert.True(placements[^1].MoveOnly);
        Assert.Equal(386, placements[^1].Placement.Left);

        navigation.Target = original with { Bounds = new(0, 40, 840, 730), WorkArea = new(0, 0, 1200, 800) };
        notify(FolderDialogChange.Settled);
        Dispatcher.UIThread.RunJobs();
        Assert.False(bar.IsVisible);

        navigation.Target = original with { Scale = 1.25 };
        notify(FolderDialogChange.Settled);
        Dispatcher.UIThread.RunJobs();
        Assert.True(bar.IsVisible);
        Assert.False(placements[^1].MoveOnly);
        Assert.Equal("复现目录", search.Text);

        navigation.Target = null;
        notify(FolderDialogChange.Position);
        notify(FolderDialogChange.Closed);
        Dispatcher.UIThread.RunJobs();
        Assert.Null(navigation.Notify);
        Assert.DoesNotContain(bar, scope.Lifetime.Windows);
        var reads = navigation.GeometryReads;
        notify(FolderDialogChange.Position);
        await Task.Delay(40);
        Assert.Equal(reads, navigation.GeometryReads);
    }

    private sealed class ObservedDialog : IFolderNavigation
    {
        public FolderDialogTarget? Target { get; set; } = new(1, new(200, 80, 840, 500), new(0, 0, 1600, 1000), 1);
        public Action<FolderDialogChange>? Notify { get; private set; }
        public int GeometryReads { get; private set; }
        public TaskCompletionSource FirstGeometryRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool HasPermission => true;
        public string CurrentDirectory => "";
        public bool RequestPermission() => true;
        public void Start() { }
        public void Stop() { }
        public Task<FolderDialogTarget?> FindDialogAsync(long? retainedTarget, nint activeBar, CancellationToken token) =>
            Task.FromResult(Target);
        public IDisposable WatchDialog(long target, Action<FolderDialogChange> changed)
        {
            Notify = changed;
            return this;
        }
        public FolderDialogTarget? ReadTrackedDialog(long target, nint activeBar)
        {
            GeometryReads++;
            FirstGeometryRead.TrySetResult();
            return Target;
        }
        public Task<IReadOnlyList<string>> GetOpenFoldersAsync(CancellationToken token) =>
            Task.FromResult<IReadOnlyList<string>>([]);
        public Task<IReadOnlyList<string>> SearchFoldersAsync(string query, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<string>>([]);
        public Task<bool> RestoreDialogAsync(long target, nint activeBar, CancellationToken token) => Task.FromResult(false);
        public Task<FolderNavigationResult> JumpAsync(Shortcut shortcut, CancellationToken token) => throw new NotSupportedException();
        public Task<FolderNavigationResult> JumpToAsync(long target, nint activeBar, string path, Shortcut? shortcut,
            CancellationToken token) => throw new NotSupportedException();
        public void Dispose() => Notify = null;
    }

    [AvaloniaFact]
    public void CapturePageOffersEveryToolAndTheSidebarSwitchesPages()
    {
        if (!Application.Current!.Styles.OfType<FluentTheme>().Any()) Application.Current.Styles.Add(DesignTokens.CreateTheme());
        using var scope = new Scope(new FakePlatform { HasScreenPermission = true });
        var main = scope.Controller.Main;
        scope.Controller.ShowMain(); Layout(main);
        Assert.Equal(MainPage.Capture, main.CurrentPage);
        Assert.Equal(MainWindow.MinimumWidth, main.MinWidth); Assert.Equal(MainWindow.MinimumHeight, main.MinHeight);
        main.Navigate(MainPage.Settings);
        Layout(main);
        var headings = main.CurrentPageView.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).ToArray();
        Assert.Equal(18, Assert.Single(headings, t => t.Text == "设置").FontSize);
        foreach (var group in new[] { "应用行为", "模型服务" })
            Assert.Equal(DesignTokens.Caption, Assert.Single(headings, t => t.Text == group).FontSize);
        var card = Assert.Single(main.CurrentPageView.GetVisualDescendants().OfType<UpdateCard>());
        var behavior = Assert.Single(headings, t => t.Text == "应用行为");
        Assert.True(card.TranslatePoint(new Point(0, card.Bounds.Height), main)!.Value.Y <= behavior.TranslatePoint(default, main)!.Value.Y);
        var version = Assert.Single(main.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == $"v{MainWindow.Version}");
        var brand = Assert.Single(main.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Tbox");
        Assert.Same(brand.Parent, version.Parent);
        Assert.True(version.TranslatePoint(default, main)!.Value.Y >= brand.TranslatePoint(new Point(0, brand.Bounds.Height), main)!.Value.Y);
        main.Navigate(MainPage.Capture);
        Layout(main);
        var actions = new[] { "贴出剪贴板", "长截图", "录屏", "识别文字" }.Select(label => Find(main, label)).ToArray();
        Assert.All(actions, button => Assert.Same(actions[0].Parent, button.Parent));
        var settings = Find(main, "设置", sidebar: true);
        Assert.True(settings.TranslatePoint(default, main)!.Value.Y > main.Bounds.Height * .7);
        ExpandGroup(main, "截图设置");
        foreach (var tool in new[] { "截图", "贴出剪贴板", "长截图", "录屏", "识别文字", "更改保存位置" })
            Assert.True(Find(main, tool).IsEffectivelyEnabled, tool);
        foreach (var (label, page) in new[] { ("文件夹跳转", MainPage.Folders), ("翻译", MainPage.Translation), ("保持唤醒", MainPage.KeepAwake), ("设置", MainPage.Settings), ("截图", MainPage.Capture) })
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
        var paths = Enumerable.Range(1, 4).Select(i => Path.Combine(Path.GetPathRoot(AppContext.BaseDirectory)!, "示例", $"目录 {i}")).ToArray();
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
        var restores = 0;
        using var frame = DesktopController.RenderText("Screen capture");
        var size = frame.Bitmap.PixelSize;
        var platform = new FakePlatform
        {
            HasScreenPermission = true,
            CaptureForegroundRestore = () => restores++,
            Displays = [new(1, 0, 0, size.Width, size.Height, frame.Png)]
        };
        using var scope = new Scope(platform);
        await scope.InitializeAsync();
        var main = scope.Controller.Main;
        var page = useShortcut ? MainPage.Settings : MainPage.Capture;
        if (mainVisible) { scope.Controller.ShowMain(page); Layout(main); }

        if (useShortcut) platform.HotkeyHandler!(HotkeyAction.Capture);
        else Click(main, "截图");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(mainVisible, main.IsVisible);

        for (var attempt = 0; attempt < 100 && !scope.Lifetime.Windows.OfType<CaptureWindow>().Any(); attempt++) await Task.Delay(10);
        Assert.Equal(1, platform.CaptureRequests);
        var overlay = Assert.Single(scope.Lifetime.Windows.OfType<CaptureWindow>());
        Assert.Equal(mainVisible, main.IsVisible);
        // A reopen request arrives while the overlay is showing, then the capture ends before it is dispatched.
        App.RestoreOnReopen(scope.Controller, new ActivatedEventArgs(ActivationKind.Reopen));
        overlay.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(scope.Lifetime.Windows.OfType<CaptureWindow>());
        Assert.Equal(1, restores);
        Assert.Equal(mainVisible, main.IsVisible);
        if (mainVisible) Assert.Equal(page, main.CurrentPage);
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancelingRegionSelectionReturnsInputToTheWindowUncoveredByTheDashboard(bool recording)
    {
        var restored = 0;
        using var frame = DesktopController.RenderText("Capture fixture");
        var size = frame.Bitmap.PixelSize;
        var platform = new FakePlatform { HasScreenPermission = true, UncoveredWindowRestore = () => restored++,
            Displays = [new(1, 0, 0, size.Width, size.Height, frame.Png)] };
        using var scope = new Scope(platform);
        scope.Controller.ShowMain();
        await (recording ? scope.Controller.RecordRegionAsync() : scope.Controller.LongCaptureAsync());
        var overlay = Assert.Single(scope.Lifetime.Windows.OfType<CaptureWindow>());
        Assert.False(scope.Controller.Main.IsVisible);
        overlay.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, restored);
        Assert.False(scope.Controller.Main.IsVisible);
    }

    [AvaloniaFact]
    public async Task HotkeyStartsBeforeAlreadyQueuedNormalPriorityWork()
    {
        var order = new List<string>();
        var platform = new FakePlatform { HasScreenPermission = true, CaptureStarting = () => order.Add("capture") };
        using var scope = new Scope(platform);
        await scope.InitializeAsync();
        Dispatcher.UIThread.Post(() => order.Add("normal"), DispatcherPriority.Normal);
        platform.HotkeyHandler!(HotkeyAction.Capture);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(new[] { "capture", "normal" }, order);
    }

    [AvaloniaFact]
    public void ReopeningAHiddenWindowReturnsToTheCapturePage()
    {
        using var scope = new Scope(new FakePlatform());
        var main = scope.Controller.Main;
        scope.Controller.ShowMain(); main.Navigate(MainPage.Settings);
        scope.Controller.ShowMain();
        Assert.Equal(MainPage.Settings, main.CurrentPage); // Already open: stays where the user is.
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
    public void RecentCapturesReuseOnlyUnchangedThumbnailsAndRecoverFromUnreadableFiles()
    {
        var directory = Directory.CreateTempSubdirectory("tbox-recent-");
        try
        {
            using var scope = new Scope(new FakePlatform(), directory.FullName);
            var main = scope.Controller.Main;
            var now = DateTime.UtcNow.AddHours(-1);
            var paths = Enumerable.Range(0, 5).Select(i => Path.Combine(directory.FullName, $"capture-{i}.png")).ToArray();
            void Write(int index, int minute, byte red)
            {
                using var picture = Picture.FromPixels([0, 0, red, 255], 1, 1);
                File.WriteAllBytes(paths[index], picture.Png);
                File.SetLastWriteTimeUtc(paths[index], now.AddMinutes(minute));
            }
            Bitmap[] Refresh()
            {
                main.Refresh();
                Layout(main);
                return main.CurrentPageView.GetVisualDescendants().OfType<Image>()
                    .Where(image => image.IsEffectivelyVisible && image.Width is double.NaN)
                    .Select(image => Assert.IsType<Bitmap>(image.Source)).ToArray();
            }

            for (var i = 0; i < 4; i++) Write(i, i, (byte)i);
            scope.Controller.ShowMain();
            var initial = Refresh();
            Assert.Equal(4, initial.Length);
            Write(4, 4, 4);
            var shifted = Refresh();
            Assert.Equal(paths.Skip(1).Reverse(), main.RecentPaths);
            Assert.Equal(initial.Take(3), shifted.Skip(1));

            Write(4, 5, 99);
            var replaced = Refresh();
            Assert.NotSame(shifted[0], replaced[0]);
            Assert.Equal(shifted.Skip(1), replaced.Skip(1));
            using (var replacement = Picture.FromBitmap(replaced[0]))
                Assert.Equal((byte)99, replacement.GetPixels()[2]);

            File.WriteAllBytes(paths[4], [1, 2, 3]);
            Assert.Equal(replaced.Skip(1), Refresh());
            Write(4, 6, 42);
            Assert.Equal(4, Refresh().Length);
            File.Delete(paths[4]);
            var afterDelete = Refresh();
            Assert.Equal(paths.Take(4).Reverse(), main.RecentPaths);
            Assert.Equal(initial.Take(3), afterDelete.Take(3));
            Assert.NotSame(initial[3], afterDelete[3]);
        }
        finally { directory.Delete(true); }
    }

    [AvaloniaFact]
    public async Task ClipboardPinAppearsBeforeCacheEncodingAndGetsItsProtectedCachePathAfterwards()
    {
        using var scope = new Scope(new FakePlatform { HasScreenPermission = true });
        using var picture = Picture.FromPixels([10, 20, 30, 255], 1, 1);
        var operation = scope.Controller.PinPictureAsync(picture);
        var pin = Assert.Single(scope.Lifetime.Windows.OfType<PinnedWindow>());
        Assert.True(pin.IsVisible);
        Assert.False(pin.Picture.HasEncodedPng);
        await operation;
        Assert.NotNull(pin.Picture.CachePath);
        Assert.Equal(picture.CachePath, pin.Picture.CachePath);
        Assert.True(File.Exists(pin.Picture.CachePath));
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
    public async Task ScreenshotShortcutIsOptInAndReleasesWhenDisabledOrReassigned()
    {
        var platform = new FakePlatform { SupportsSystemScreenshotShortcutTakeover = true };
        using var scope = new Scope(platform);
        var chord = ShortcutPolicy.Default(HotkeyAction.Toggle, mac: true);
        await scope.InitializeAsync(new HotkeyBindings(Toggle: chord));
        var controller = scope.Controller;
        controller.ShowMain();
        Layout(controller.Main);
        ExpandGroup(controller.Main, "快捷键");
        var option = Assert.Single(controller.Main.GetVisualDescendants().OfType<ToggleSwitch>(),
            t => Name(t) == "接管 macOS 全屏截图快捷键");
        Assert.False(option.IsChecked);
        Assert.Empty(platform.SystemScreenshotShortcutAttempts);
        Assert.False(platform.Hotkeys.ContainsKey(HotkeyAction.Toggle));

        await controller.SetSystemScreenshotShortcutTakeoverAsync(true);
        Assert.False(option.IsChecked);
        Assert.Equal(new[] { true }, platform.SystemScreenshotShortcutAttempts);
        Assert.False((await scope.ReadSettingsAsync()).TakeOverSystemScreenshotShortcut);

        platform.CanStartSystemScreenshotShortcutTakeover = true;
        await controller.SetSystemScreenshotShortcutTakeoverAsync(true);
        Assert.True(option.IsChecked);
        Assert.True((await scope.ReadSettingsAsync()).TakeOverSystemScreenshotShortcut);
        Assert.NotNull(platform.SystemScreenshotShortcutHandler);
        await controller.PinPictureAsync(DesktopController.RenderText("Takeover fixture"));
        platform.SystemScreenshotShortcutHandler!();
        Dispatcher.UIThread.RunJobs();
        Assert.True(controller.PinsHidden);

        Assert.True(controller.TryPauseHotkeys());
        Assert.Null(platform.SystemScreenshotShortcutHandler);
        controller.ResumeHotkeys();
        Assert.False(platform.SystemScreenshotShortcutAttempts[^1]);
        Assert.NotNull(platform.SystemScreenshotShortcutHandler);

        Assert.Null(await controller.SetShortcutAsync(HotkeyAction.Toggle, new Shortcut(false, false, false, false, "F9")));
        Assert.Null(platform.SystemScreenshotShortcutHandler);
        Assert.Equal("F9", platform.Hotkeys[HotkeyAction.Toggle].Key);
        Assert.Null(await controller.SetShortcutAsync(HotkeyAction.Toggle, chord));
        Assert.NotNull(platform.SystemScreenshotShortcutHandler);

        var previousHandler = platform.SystemScreenshotShortcutHandler!;
        var werePinsHidden = controller.PinsHidden;
        await controller.SetSystemScreenshotShortcutTakeoverAsync(false);
        previousHandler();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(werePinsHidden, controller.PinsHidden);
        Assert.Null(platform.SystemScreenshotShortcutHandler);
        Assert.False((await scope.ReadSettingsAsync()).TakeOverSystemScreenshotShortcut);
        Assert.False(platform.Hotkeys.ContainsKey(HotkeyAction.Toggle));
    }

    [AvaloniaFact]
    public async Task SavedScreenshotTakeoverStartsWithoutPromptAndStopsWithTheModule()
    {
        var platform = new FakePlatform
        {
            SupportsSystemScreenshotShortcutTakeover = true,
            CanStartSystemScreenshotShortcutTakeover = true
        };
        using var scope = new Scope(platform);
        await scope.InitializeAsync(new HotkeyBindings(Toggle: ShortcutPolicy.Default(HotkeyAction.Toggle, mac: true)),
            screenshotTakeover: true);
        Assert.Equal(new[] { false }, platform.SystemScreenshotShortcutAttempts);
        Assert.NotNull(platform.SystemScreenshotShortcutHandler);
        await scope.Controller.SetModulesAsync(false, null);
        Assert.Null(platform.SystemScreenshotShortcutHandler);
        await scope.Controller.SetModulesAsync(true, null);
        Assert.NotNull(platform.SystemScreenshotShortcutHandler);
        scope.Controller.Dispose();
        Assert.Null(platform.SystemScreenshotShortcutHandler);
    }

    [AvaloniaFact]
    public async Task SavedShortcutTakesPrecedenceWhenAnUpgradeAddsTheSameDefault()
    {
        var platform = new FakePlatform();
        using var scope = new Scope(platform);
        var mac = OperatingSystem.IsMacOS();
        var previousPin = ShortcutPolicy.Default(HotkeyAction.Capture, mac);
        await scope.InitializeAsync(new HotkeyBindings(Pin: previousPin));

        Assert.Equal(previousPin, platform.Hotkeys[HotkeyAction.Pin]);
        Assert.True(scope.Controller.HasHotkeyConflictFor(HotkeyAction.Capture));
        Assert.False(platform.Hotkeys.ContainsKey(HotkeyAction.Capture));
        Assert.Equal(ShortcutPolicy.Default(HotkeyAction.Toggle, mac), platform.Hotkeys[HotkeyAction.Toggle]);

        Assert.Null(await scope.Controller.SetShortcutAsync(HotkeyAction.Pin, null));
        Assert.Equal(previousPin, platform.Hotkeys[HotkeyAction.Capture]);
        Assert.False(scope.Controller.HasHotkeyConflictFor(HotkeyAction.Capture));
    }

    [AvaloniaFact]
    public async Task RecordingAShortcutPausesTboxHotkeysSavesItAndUpdatesLabels()
    {
        var platform = new FakePlatform { HasScreenPermission = true };
        using var scope = new Scope(platform);
        var controller = scope.Controller; var main = controller.Main;
        await scope.InitializeAsync();
        controller.ShowMain(); Layout(main);
        ExpandGroup(main, "快捷键");
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
        await Press(row, Key.D2, KeyModifiers.Shift | (mac ? KeyModifiers.Meta : KeyModifiers.Control));
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
        ExpandGroup(main, "快捷键");
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

    [AvaloniaFact]
    public async Task WindowTopmostRegistersOnlyWhenEnabledAndCleansUpOnDisableOrExit()
    {
        var platform = new FakePlatform { HasScreenPermission = true };
        using var scope = new Scope(platform);
        var controller = scope.Controller;
        var main = controller.Main;
        await scope.InitializeAsync();
        controller.ShowMain(MainPage.WindowTopmost);
        Assert.DoesNotContain(HotkeyAction.WindowTopmost, platform.Hotkeys.Keys);
        Assert.True(platform.Topmost.BorderVisible);

        await controller.SetWindowTopmostEnabledAsync(true);
        Assert.Equal(ShortcutPolicy.Default(HotkeyAction.WindowTopmost, OperatingSystem.IsMacOS()), platform.Hotkeys[HotkeyAction.WindowTopmost]);
        platform.HotkeyHandler!(HotkeyAction.WindowTopmost);
        Dispatcher.UIThread.RunJobs();
        Assert.Single(platform.Topmost.Windows);
        platform.Topmost.Foreground = new(2, "工作草稿");
        platform.HotkeyHandler(HotkeyAction.WindowTopmost);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, platform.Topmost.Windows.Count);
        Layout(main);
        TaskToolbarTests.Preview(main, "main-window-topmost");

        var remove = Assert.Single(main.CurrentPageView.GetVisualDescendants().OfType<Button>(), button => Name(button) == "解除参考笔记置顶");
        remove.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(2, Assert.Single(platform.Topmost.Windows).Id);
        await controller.SetWindowTopmostBorderAsync(false);
        Assert.False(platform.Topmost.BorderVisible);
        Assert.False((await scope.ReadSettingsAsync()).WindowTopmostBorder);
        await controller.SetWindowTopmostEnabledAsync(false);
        Assert.Empty(platform.Topmost.Windows);
        Assert.DoesNotContain(HotkeyAction.WindowTopmost, platform.Hotkeys.Keys);
        platform.HotkeyHandler(HotkeyAction.WindowTopmost);
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(platform.Topmost.Windows);
        await controller.SetWindowTopmostEnabledAsync(true);
        platform.HotkeyHandler(HotkeyAction.WindowTopmost);
        Dispatcher.UIThread.RunJobs();
        Assert.Single(platform.Topmost.Windows);
        controller.Dispose();
        Assert.Empty(platform.Topmost.Windows);
    }

    [AvaloniaFact]
    public async Task WindowTopmostConflictDoesNotReplaceAnotherShortcutAndAuthorizationIsExplicit()
    {
        var platform = new FakePlatform { Topmost = { NeedsAccessibilityPermission = true, NeedsScreenPermission = true } };
        var original = ShortcutPolicy.Default(HotkeyAction.WindowTopmost, OperatingSystem.IsMacOS());
        platform.TakenShortcuts.Add(original);
        using var scope = new Scope(platform);
        var controller = scope.Controller;
        await scope.InitializeAsync();
        await controller.SetWindowTopmostEnabledAsync(true);
        Assert.True(controller.HasHotkeyConflictFor(HotkeyAction.WindowTopmost));
        Assert.False(controller.HasConflictIn(capture: true));
        Assert.Equal(0, platform.PermissionRequests);
        Assert.Equal(0, platform.Topmost.AccessibilityRequests);

        var replacement = new Shortcut(true, true, false, false, "Y");
        Assert.Null(await controller.SetShortcutAsync(HotkeyAction.WindowTopmost, replacement));
        Assert.NotNull(await controller.SetShortcutAsync(HotkeyAction.WindowTopmost, original));
        Assert.Equal(replacement, platform.Hotkeys[HotkeyAction.WindowTopmost]);
        Assert.Equal(replacement, (await scope.ReadSettingsAsync()).Hotkeys!.WindowTopmost);
        controller.RequestTopmostScreenPermission();
        controller.RequestTopmostAccessibilityPermission();
        Assert.Equal(1, platform.PermissionRequests);
        Assert.Equal(1, platform.Topmost.AccessibilityRequests);
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

    private static void ExpandGroup(MainWindow window, string title)
    {
        var expander = Assert.Single(window.CurrentPageView.GetVisualDescendants().OfType<Expander>(), e => Name(e) == title);
        expander.IsExpanded = true;
        Layout(window);
    }

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
        internal PinSessionStore PinSessions => new(_settings);
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
        public async Task InitializeAsync(HotkeyBindings? hotkeys = null, bool screenshotTakeover = false)
        {
            await new JsonSettingsStore(Path.Combine(_settings, "settings.json")).SaveAsync(new AppSettings
            {
                ScreenCaptureCacheDirectory = Path.Combine(_settings, "cache"),
                Hotkeys = hotkeys,
                TakeOverSystemScreenshotShortcut = screenshotTakeover
            });
            await Controller.InitializeAsync([]);
        }
        public Task<AppSettings> ReadSettingsAsync() =>
            new JsonSettingsStore(Path.Combine(_settings, "settings.json")).LoadAsync();
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
