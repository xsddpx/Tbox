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
        // .bin with binary content shows the information page, which carries the type line checked below.
        var (a, b, c) = (scope.Bytes("a.bin", [0, 1]), scope.Bytes("b.bin", [0, 1]), scope.Bytes("c.bin", [0, 1]));
        scope.Source.Selection = new QuickLookSelection([b], false);
        scope.Source.Folder = [a, b, c];
        Assert.True(scope.Source.SpaceToActivate);

        await scope.ActivateAsync();
        var window = Assert.IsType<QuickLookWindow>(scope.Controller.QuickLookWindow);
        Assert.True(window.IsVisible);
        Assert.Equal((b, 3), (window.CurrentPath, window.ItemCount));
        Assert.Equal("b.bin - 速览", window.Title);
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
    public async Task AMinimizedPreviewOpensAgainRestored()
    {
        using var scope = await Scope.CreateAsync();
        var a = scope.File("a.txt");
        scope.Source.Selection = new QuickLookSelection([a], false);
        await scope.ActivateAsync();
        var window = scope.Controller.QuickLookWindow!;
        // Minimized from the taskbar or by Win+D: the next press must open it, not close it.
        window.WindowState = WindowState.Minimized;
        await scope.ActivateAsync();
        Assert.True(window.IsVisible);
        Assert.Equal(WindowState.Normal, window.WindowState);
        // Closed while minimized, it still opens restored.
        window.WindowState = WindowState.Minimized;
        window.Dismiss();
        await scope.ActivateAsync();
        Assert.True(window.IsVisible);
        Assert.Equal(WindowState.Normal, window.WindowState);
    }

    [AvaloniaFact]
    public async Task ArrowsNeverMoveFocusToAButtonAndSpaceReleaseClicksNothing()
    {
        using var scope = await Scope.CreateAsync();
        var (a, b) = (scope.File("a.txt"), scope.File("b.txt"));
        scope.Source.Selection = new QuickLookSelection([a, b], true);
        await scope.ActivateAsync();
        var window = scope.Controller.QuickLookWindow!;
        foreach (var key in new[] { Key.Right, Key.Down, Key.Left, Key.Up, Key.Tab })
        {
            await Task.Delay(QuickLookPolicy.NavigationThrottle);
            window.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, null);
            await scope.SettleAsync();
            Assert.IsNotType<Button>(window.FocusManager?.GetFocusedElement());
        }
        // Even with a title-bar button focused, the Space released after opening must not click it.
        var pin = window.GetVisualDescendants().OfType<Button>().First(button => ToolTip.GetTip(button) as string == "将窗口固定为当前大小");
        pin.Focusable = true; pin.Focus();
        window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
        window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
        await scope.SettleAsync();
        Assert.False(window.Pinned);
        Assert.True(window.IsVisible);
    }

    [AvaloniaFact]
    public async Task TextMarkdownAndSvgOpenInTheirOwnPreviews()
    {
        using var scope = await Scope.CreateAsync();
        const string Code = "class A\n{\n    int x;\n}\n";
        var code = scope.File("Program.cs", Code);
        var markdown = scope.File("README.md", "# 标题\n\n正文 **加粗** 和 `code`\n\n- [x] 完成\n- 第二项\n\n| 列 | 值 |\n| --- | --- |\n| a | 1 |\n");
        var svg = scope.File("logo.svg", "<svg xmlns='http://www.w3.org/2000/svg' width='120' height='60'><rect width='120' height='60' fill='red'/></svg>");
        var unknown = scope.File("notes", "plain words without an extension");
        var binary = scope.Bytes("blob", [0x4D, 0x5A, 0x90, 0x00, 0x03]);
        scope.Source.Selection = new QuickLookSelection([code, markdown, svg, unknown, binary], true);
        await scope.ActivateAsync();
        var window = scope.Controller.QuickLookWindow!;
        IEnumerable<string?> Texts() => window.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text);

        var editor = Assert.Single(window.GetVisualDescendants().OfType<AvaloniaEdit.TextEditor>());
        Assert.Equal(Code, editor.Text);
        Assert.True(editor.IsReadOnly);
        Assert.Contains("UTF-8", Texts());
        Assert.Contains("5 行", Texts());

        await Next();
        Assert.Contains(window.GetVisualDescendants().OfType<SelectableTextBlock>(), text => text.Inlines?.Text == "标题");
        Assert.Contains(window.GetVisualDescendants().OfType<CheckBox>(), box => box.IsChecked == true);
        Assert.Contains(window.GetVisualDescendants().OfType<SelectableTextBlock>(), text => text.Inlines?.Text == "值");

        await Next();
        var image = window.GetVisualDescendants().OfType<Image>().Single(image => image.Source is Avalonia.Media.Imaging.WriteableBitmap);
        Assert.Equal(new PixelSize(120, 60), ((Avalonia.Media.Imaging.Bitmap)image.Source!).PixelSize);

        // A file without an extension is sniffed: text opens in the editor, binary shows the information page.
        await Next();
        Assert.Equal("plain words without an extension", Assert.Single(window.GetVisualDescendants().OfType<AvaloniaEdit.TextEditor>()).Text);
        await Next();
        Assert.Empty(window.GetVisualDescendants().OfType<AvaloniaEdit.TextEditor>());
        Assert.Contains("文件类型: 文本文档", Texts());

        async Task Next()
        {
            await Task.Delay(QuickLookPolicy.NavigationThrottle);
            window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
            await scope.SettleAsync();
        }
    }

    [AvaloniaFact]
    public async Task TextFollowsTheSizeLimitAndWrapSetting()
    {
        using var scope = await Scope.CreateAsync();
        await scope.Controller.SetQuickLookAsync(scope.Controller.Settings.QuickLook with { MaxTextFileSizeKb = 1, WrapText = true, TextFontSize = 20 });
        var big = scope.File("big.log", new string('x', 3000));
        scope.Source.Selection = new QuickLookSelection([big], false);
        await scope.ActivateAsync();
        var window = scope.Controller.QuickLookWindow!;
        var editor = Assert.Single(window.GetVisualDescendants().OfType<AvaloniaEdit.TextEditor>());
        Assert.Equal(1024, editor.Text.Length);
        Assert.True(editor.WordWrap);
        Assert.Equal(20, editor.FontSize);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "文件超过 1 KB，只显示开头部分");
        var saved = (await scope.ReadSettingsAsync()).QuickLook;
        Assert.Equal((1, true, 20), (saved.MaxTextFileSizeKb, saved.WrapText, saved.TextFontSize));
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

        public string File(string name, string? text = null)
        {
            var path = Path.Combine(_directory, name);
            System.IO.File.WriteAllText(path, text ?? name);
            return path;
        }

        public string Bytes(string name, byte[] bytes)
        {
            var path = Path.Combine(_directory, name);
            System.IO.File.WriteAllBytes(path, bytes);
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
