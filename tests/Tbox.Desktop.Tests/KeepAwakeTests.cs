using Avalonia;
using Avalonia.Automation;
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

// AWAKE-001–003: the keep-awake page, its remembered switches and what the system is asked to do.
public sealed class KeepAwakeTests
{
    [AvaloniaFact]
    public async Task SwitchesDriveTheSystemRequestAndAreRememberedAcrossRestarts()
    {
        using var scope = new Scope(new FakePlatform());
        await scope.InitializeAsync(new AppSettings());
        var awake = scope.Platform.KeepAwake;
        Assert.Equal(KeepAwakeRequest.None, awake.Applied);

        await scope.Controller.SetKeepAwakeAsync(true);
        Assert.Equal(new KeepAwakeRequest(true, true, false), awake.Applied);
        await scope.Controller.SetKeepAwakeDisplayAsync(false);
        Assert.Equal(new KeepAwakeRequest(true, false, false), awake.Applied);
        await scope.Controller.SetKeepAwakeLidAsync(true);
        Assert.Equal(new KeepAwakeRequest(true, false, true), awake.Applied);

        var saved = await scope.ReadSettingsAsync();
        Assert.True(saved.KeepAwakeEnabled); Assert.False(saved.KeepAwakeDisplay); Assert.True(saved.KeepAwakeLidClosed);

        // A new run restores the saved state on startup.
        using var next = new Scope(new FakePlatform(), scope.Directory);
        await next.Controller.InitializeAsync([]);
        Assert.Equal(new KeepAwakeRequest(true, false, true), next.Platform.KeepAwake.Applied);
    }

    [AvaloniaFact]
    public async Task TurningTheMainSwitchOffReleasesEverythingButKeepsThePreferences()
    {
        using var scope = new Scope(new FakePlatform());
        await scope.InitializeAsync(new AppSettings { KeepAwakeEnabled = true, KeepAwakeLidClosed = true });
        Assert.Equal(new KeepAwakeRequest(true, true, true), scope.Platform.KeepAwake.Applied);

        await scope.Controller.SetKeepAwakeAsync(false);
        Assert.Equal(KeepAwakeRequest.None, scope.Platform.KeepAwake.Applied);
        var saved = await scope.ReadSettingsAsync();
        Assert.False(saved.KeepAwakeEnabled); Assert.True(saved.KeepAwakeDisplay); Assert.True(saved.KeepAwakeLidClosed);

        // Preferences changed while off do not ask the system for anything.
        await scope.Controller.SetKeepAwakeDisplayAsync(false);
        Assert.Equal(KeepAwakeRequest.None, scope.Platform.KeepAwake.Applied);
    }

    [AvaloniaFact]
    public async Task QuittingReleasesKeepAwakeAndLeavesTheSavedSwitchOn()
    {
        using var scope = new Scope(new FakePlatform());
        await scope.InitializeAsync(new AppSettings { KeepAwakeEnabled = true, KeepAwakeLidClosed = true });
        await scope.Controller.QuitAsync();
        Assert.True(scope.Controller.IsQuitting);
        Assert.Equal(1, scope.Shutdowns);
        Assert.Equal(KeepAwakeRequest.None, scope.Platform.KeepAwake.Applied);
        Assert.True((await scope.ReadSettingsAsync()).KeepAwakeEnabled);
    }

    [AvaloniaFact]
    public async Task StartupWithKeepAwakeOffStillReleasesWhatAnEarlierRunLeftBehind()
    {
        using var scope = new Scope(new FakePlatform());
        await scope.InitializeAsync(new AppSettings());
        Assert.Equal(KeepAwakeRequest.None, Assert.Single(scope.Platform.KeepAwake.Requests));
    }

    [AvaloniaFact]
    public async Task ASystemRefusalSwitchesBackOffAndExplainsWhy()
    {
        var platform = new FakePlatform { KeepAwake = { RefuseSystem = true } };
        using var scope = new Scope(platform);
        await scope.InitializeAsync(new AppSettings());
        scope.Controller.ShowMain(MainPage.KeepAwake); Layout(scope.Main);

        await scope.Controller.SetKeepAwakeAsync(true);
        Layout(scope.Main);
        Assert.False(scope.Controller.KeepAwake.System);
        Assert.False((await scope.ReadSettingsAsync()).KeepAwakeEnabled);
        Assert.False(Switch(scope.Main, "保持唤醒").IsChecked);
        Assert.Contains(scope.Main.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == "系统拒绝保持唤醒。");
    }

    [AvaloniaFact]
    public async Task CancellingTheMacPasswordTurnsOnlyTheLidSwitchBackOff()
    {
        var platform = new FakePlatform { KeepAwake = { LidNeedsAuthorization = true, RefuseLid = true } };
        using var scope = new Scope(platform);
        await scope.InitializeAsync(new AppSettings { KeepAwakeEnabled = true });

        await scope.Controller.SetKeepAwakeLidAsync(true);
        Assert.Equal(new KeepAwakeRequest(true, true, false), platform.KeepAwake.Applied);
        var saved = await scope.ReadSettingsAsync();
        Assert.True(saved.KeepAwakeEnabled); Assert.False(saved.KeepAwakeLidClosed);
    }

    [AvaloniaFact]
    public async Task TheLidSwitchIsNotShownOnWhileTheMacPasswordPromptIsOpen()
    {
        var platform = new FakePlatform { KeepAwake = { LidNeedsAuthorization = true, Authorization = new() } };
        using var scope = new Scope(platform);
        await scope.InitializeAsync(new AppSettings { KeepAwakeEnabled = true });
        scope.Controller.ShowMain(MainPage.KeepAwake); Layout(scope.Main);

        var change = scope.Controller.SetKeepAwakeLidAsync(true);
        // Saving the setting is real file IO, so the prompt opens a moment later.
        await WaitAsync(() => scope.Controller.KeepAwakeAuthorizing);
        Layout(scope.Main);
        var lid = Switch(scope.Main, "合上盖子时保持唤醒");
        Assert.False(lid.IsChecked); Assert.False(lid.IsEffectivelyEnabled);
        Assert.Contains(scope.Main.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == "等待管理员授权合盖唤醒…");

        platform.KeepAwake.Authorization.SetResult();
        await change; Layout(scope.Main);
        Assert.True(lid.IsChecked); Assert.True(lid.IsEffectivelyEnabled);
        Assert.Contains(scope.Main.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == "已开启 · 屏幕常亮 · 合盖不睡眠");
    }

    [AvaloniaFact]
    public async Task DevicesWithoutALidCannotTurnOnTheLidOption()
    {
        var platform = new FakePlatform { KeepAwake = { HasLid = false } };
        using var scope = new Scope(platform);
        await scope.InitializeAsync(new AppSettings { KeepAwakeEnabled = true, KeepAwakeLidClosed = true });
        scope.Controller.ShowMain(MainPage.KeepAwake); Layout(scope.Main);

        Assert.Equal(new KeepAwakeRequest(true, true, false), platform.KeepAwake.Applied);
        var lid = Switch(scope.Main, "合上盖子时保持唤醒");
        Assert.False(lid.IsChecked); Assert.False(lid.IsEffectivelyEnabled);
        Assert.Contains(scope.Main.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == "这台电脑没有可合上的盖子");
    }

    [AvaloniaFact]
    public async Task PageShowsTheStateAndTheSwitchesChangeIt()
    {
        if (!Application.Current!.Styles.OfType<FluentTheme>().Any()) Application.Current.Styles.Add(DesignTokens.CreateTheme());
        using var scope = new Scope(new FakePlatform());
        await scope.InitializeAsync(new AppSettings());
        scope.Controller.ShowMain(MainPage.Capture); Layout(scope.Main);
        var item = Assert.Single(scope.Main.GetVisualDescendants().OfType<Button>(),
            b => b.IsEffectivelyVisible && AutomationProperties.GetName(b) == "保持唤醒" && !b.GetVisualAncestors().Contains(scope.Main.CurrentPageView));
        item.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Layout(scope.Main);
        Assert.Equal(MainPage.KeepAwake, scope.Main.CurrentPage);
        Assert.Contains(Texts(scope.Main), t => t == "未开启");
        Assert.Contains(Texts(scope.Main), t => t == "电脑会按系统设置睡眠");
        // The banner only reports state, so the page has no amethyst primary button.
        Assert.DoesNotContain(scope.Main.CurrentPageView.GetVisualDescendants().OfType<Button>(), b => b.IsEffectivelyVisible && b.Classes.Contains("accent"));

        Switch(scope.Main, "保持唤醒").IsChecked = true;
        await WaitAsync(() => scope.Platform.KeepAwake.Applied.System); Layout(scope.Main);
        Assert.Contains(Texts(scope.Main), t => t == "已开启 · 屏幕常亮");
        Assert.Contains(Texts(scope.Main), t => t == "电脑保持唤醒中");

        Switch(scope.Main, "保持屏幕常亮").IsChecked = false;
        await WaitAsync(() => !scope.Platform.KeepAwake.Applied.Display); Layout(scope.Main);
        Assert.Contains(Texts(scope.Main), t => t == "已开启");
    }

    [Fact]
    public void RequestOnlyAsksForDisplayAndLidWhileTheMainSwitchIsOn()
    {
        Assert.Equal(KeepAwakeRequest.None, KeepAwakeRequest.From(new AppSettings { KeepAwakeDisplay = true, KeepAwakeLidClosed = true }));
        Assert.Equal(new KeepAwakeRequest(true, true, false), KeepAwakeRequest.From(new AppSettings { KeepAwakeEnabled = true }));
        Assert.Equal("未开启", KeepAwakeRequest.None.Summary);
        Assert.Equal("已开启 · 合盖不睡眠", new KeepAwakeRequest(true, false, true).Summary);
    }

    private static IEnumerable<string?> Texts(MainWindow window) =>
        window.CurrentPageView.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text);

    private static ToggleSwitch Switch(MainWindow window, string name) =>
        Assert.Single(window.CurrentPageView.GetVisualDescendants().OfType<ToggleSwitch>(), t => AutomationProperties.GetName(t) == name);

    private static void Layout(Window window) { window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }

    private static async Task WaitAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
        Assert.True(condition());
    }

    private sealed class Scope : IDisposable
    {
        private readonly bool _ownsDirectory;
        public ClassicDesktopStyleApplicationLifetime Lifetime { get; } = new() { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        public FakePlatform Platform { get; }
        public DesktopController Controller { get; }
        public MainWindow Main => Controller.Main;
        public string Directory { get; }
        // Counts quit requests; a real lifetime Shutdown hangs headless test hosts on macOS.
        public int Shutdowns { get; private set; }
        private string SettingsPath => Path.Combine(Directory, "settings.json");

        public Scope(FakePlatform platform, string? directory = null)
        {
            typeof(ClassicDesktopStyleApplicationLifetime).GetInterfaces().Single(i => i.Name == "ISetupApplicationLifetime")
                .GetMethod("BeforeAppInit")!.Invoke(Lifetime, null);
            _ownsDirectory = directory is null;
            Directory = directory ?? Path.Combine(Path.GetTempPath(), "tbox-keep-awake-" + Guid.NewGuid().ToString("N"));
            Platform = platform;
            Controller = new DesktopController(platform, () => Shutdowns++, SettingsPath);
            Controller.UseCacheDirectoryForTests(Path.Combine(Directory, "cache"));
            Controller.Main = new MainWindow(Controller);
        }

        public async Task InitializeAsync(AppSettings settings)
        {
            await new JsonSettingsStore(SettingsPath).SaveAsync(settings with { ScreenCaptureCacheDirectory = Path.Combine(Directory, "cache") });
            await Controller.InitializeAsync([]);
        }

        public Task<AppSettings> ReadSettingsAsync() => new JsonSettingsStore(SettingsPath).LoadAsync();

        public void Dispose()
        {
            Controller.Dispose();
            foreach (var window in Lifetime.Windows.ToArray()) window.Close();
            Controller.Main.Close();
            Lifetime.Dispose();
            if (_ownsDirectory && System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, true);
        }
    }
}

// The Windows lid backup: written before any change and used to undo it after a crash.
public sealed class LidCloseGuardTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "tbox-lid-" + Guid.NewGuid().ToString("N"));
    private string Backup => Path.Combine(_directory, "keep-awake-lid.json");

    [Fact]
    public void HoldSetsDoNothingAndReleaseWritesTheOriginalValuesBack()
    {
        var plan = new FakePlan();
        var guard = new LidCloseGuard(plan, Backup);
        guard.Hold();
        Assert.Equal((0u, 0u), plan.Values[plan.Active]);
        Assert.True(File.Exists(Backup));
        guard.Hold();
        guard.Release();
        Assert.Equal((1u, 2u), plan.Values[plan.Active]);
        Assert.False(File.Exists(Backup));
    }

    [Fact]
    public void AnotherRunAfterACrashRestoresFromTheSavedBackup()
    {
        var plan = new FakePlan();
        new LidCloseGuard(plan, Backup).Hold();
        // The process ended without Release; a new guard finds the backup.
        var next = new LidCloseGuard(plan, Backup);
        Assert.True(next.Held);
        next.Release();
        Assert.Equal((1u, 2u), plan.Values[plan.Active]);
    }

    [Fact]
    public void SwitchingPowerPlansGivesTheOldPlanBackBeforeTakingTheNewOne()
    {
        var plan = new FakePlan();
        var guard = new LidCloseGuard(plan, Backup);
        guard.Hold();
        var second = Guid.NewGuid();
        plan.Values[second] = (3, 3); plan.Active = second;
        guard.Hold();
        Assert.Equal((1u, 2u), plan.Values[plan.First]);
        Assert.Equal((0u, 0u), plan.Values[second]);
        guard.Release();
        Assert.Equal((3u, 3u), plan.Values[second]);
    }

    [Fact]
    public void ReleaseWithoutABackupLeavesTheUsersSettingAlone()
    {
        var plan = new FakePlan();
        new LidCloseGuard(plan, Backup).Release();
        Assert.Equal(0, plan.Writes);
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }

    private sealed class FakePlan : ILidPowerPlan
    {
        public Guid First { get; } = Guid.NewGuid();
        public Guid Active;
        public int Writes;
        public Dictionary<Guid, (uint, uint)> Values { get; } = [];
        public FakePlan() { Active = First; Values[First] = (1, 2); }
        public Guid ActiveScheme() => Active;
        public (uint PluggedIn, uint OnBattery) ReadLidAction(Guid scheme) => Values[scheme];
        public void WriteLidAction(Guid scheme, uint pluggedIn, uint onBattery) { Writes++; Values[scheme] = (pluggedIn, onBattery); }
    }
}
