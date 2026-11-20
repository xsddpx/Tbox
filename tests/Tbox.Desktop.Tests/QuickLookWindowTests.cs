using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tbox.Core;
using Tbox.Desktop.Platform;
using Tbox.Desktop.Views;
using Xunit;

namespace Tbox.Desktop.Tests;

public sealed class QuickLookWindowTests
{
    [AvaloniaFact]
    public async Task ActivationOpensTheSelectionWalksTheFolderAndClosesOnTheSecondPress()
    {
        using var scope = await Scope.CreateAsync();
        var (a, b, c) = (scope.File("a.txt"), scope.File("b.txt"), scope.File("c.txt"));
        scope.Source.Selection = new QuickLookSelection([b], false);
        scope.Source.Folder = [a, b, c];
        Assert.True(scope.Source.SpaceToActivate);

        await scope.ActivateAsync();
        var window = Assert.IsType<QuickLookWindow>(scope.Controller.QuickLookWindow);
        Assert.True(window.IsVisible);
        Assert.Equal((b, 3), (window.CurrentPath, window.ItemCount));
        Assert.Equal("b.txt - 速览", window.Title);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "(2/3)");
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "用 记事本 打开");
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "文件类型: 文本文档");

        window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
        await scope.SettleAsync();
        Assert.Equal(c, window.CurrentPath);
        await Task.Delay(QuickLookPolicy.NavigationThrottle);
        window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
        await scope.SettleAsync();
        Assert.Equal(a, window.CurrentPath); // Wraps around like Peek.

        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await scope.SettleAsync();
        Assert.Equal([a], scope.Source.Opened);

        // Pressing the key again with the same file selected closes the window.
        scope.Source.Selection = new QuickLookSelection([a], false);
        await scope.ActivateAsync();
        Assert.False(window.IsVisible);
    }

    [AvaloniaFact]
    public async Task MultipleSelectionWalksOnlyTheSelectedItemsAndEscCloses()
    {
        using var scope = await Scope.CreateAsync();
        var (a, c) = (scope.File("a.txt"), scope.File("c.txt"));
        scope.Source.Selection = new QuickLookSelection([a, c], true);
        scope.Source.Folder = [a, scope.File("b.txt"), c];
        await scope.ActivateAsync();
        var window = scope.Controller.QuickLookWindow!;
        Assert.Equal(2, window.ItemCount);
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        await scope.SettleAsync();
        Assert.False(window.IsVisible);
    }

    [AvaloniaFact]
    public async Task NothingSelectedShowsNoWindow()
    {
        using var scope = await Scope.CreateAsync();
        scope.Source.Selection = null;
        await scope.ActivateAsync();
        Assert.False(scope.Controller.QuickLookWindow?.IsVisible ?? false);
    }

    [AvaloniaFact]
    public async Task DeleteWithoutConfirmationMovesToTheNextItem()
    {
        using var scope = await Scope.CreateAsync();
        await scope.Controller.SetQuickLookAsync(scope.Controller.Settings.QuickLook with { ConfirmFileDelete = false });
        var (a, b) = (scope.File("a.txt"), scope.File("b.txt"));
        scope.Source.Selection = new QuickLookSelection([a, b], true);
        await scope.ActivateAsync();
        var window = scope.Controller.QuickLookWindow!;
        window.KeyReleaseQwerty(PhysicalKey.Delete, RawInputModifiers.None);
        await scope.SettleAsync();
        Assert.Equal([a], scope.Source.Deleted);
        Assert.Equal((b, 1), (window.CurrentPath, window.ItemCount));
        window.KeyReleaseQwerty(PhysicalKey.Delete, RawInputModifiers.None);
        await scope.SettleAsync();
        Assert.Null(window.CurrentPath);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "没有其他要预览的文件。");
    }

    [AvaloniaFact]
    public async Task SettingsSwitchTheActivationAndTurnTheHookOff()
    {
        using var scope = await Scope.CreateAsync();
        await scope.Controller.SetQuickLookAsync(scope.Controller.Settings.QuickLook with { SpaceToActivate = false });
        Assert.False(scope.Source.SpaceToActivate);
        Assert.Equal("Ctrl+Space", scope.Source.Shortcut!.Display(false));
        await scope.Controller.SetQuickLookAsync(scope.Controller.Settings.QuickLook with { Enabled = false });
        Assert.Null(scope.Source.Activated);
        Assert.False((await scope.ReadSettingsAsync()).QuickLook.Enabled);
    }

    private sealed class Scope : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "tbox-quicklook-" + Guid.NewGuid().ToString("N"));
        private readonly FakePlatform _platform = new();
        public ClassicDesktopStyleApplicationLifetime Lifetime { get; } = new() { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        public DesktopController Controller { get; }
        public FakeQuickLook Source => _platform.QuickLook;

        private Scope()
        {
            typeof(ClassicDesktopStyleApplicationLifetime).GetInterfaces().Single(i => i.Name == "ISetupApplicationLifetime")
                .GetMethod("BeforeAppInit")!.Invoke(Lifetime, null);
            Directory.CreateDirectory(_directory);
            Controller = new DesktopController(_platform, Lifetime, Path.Combine(_directory, "settings.json"));
            Controller.UseCacheDirectoryForTests(Path.Combine(_directory, "cache"));
            Controller.Main = new MainWindow(Controller);
        }

        public static async Task<Scope> CreateAsync()
        {
            var scope = new Scope();
            await scope.Controller.InitializeAsync([]);
            return scope;
        }

        public string File(string name)
        {
            var path = Path.Combine(_directory, name);
            System.IO.File.WriteAllText(path, name);
            return path;
        }

        public async Task ActivateAsync()
        {
            Source.Activated!(1);
            await SettleAsync();
        }

        public async Task SettleAsync()
        {
            for (var i = 0; i < 10; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
            Dispatcher.UIThread.RunJobs();
        }

        public Task<AppSettings> ReadSettingsAsync() => new JsonSettingsStore(Path.Combine(_directory, "settings.json")).LoadAsync();

        public void Dispose()
        {
            Controller.Dispose();
            foreach (var window in Lifetime.Windows.ToArray()) window.Close();
            Controller.Main.Close();
            Lifetime.Dispose();
            try { Directory.Delete(_directory, true); } catch (IOException) { }
        }
    }
}
