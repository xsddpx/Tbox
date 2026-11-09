using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tbox.Core;
using Tbox.Desktop.Models;
using Tbox.Desktop.Views;
using Xunit;

namespace Tbox.Desktop.Tests;

// Result windows follow the main window's structure (spec ④); task toolbars share one dark surface (spec ⑤).
public sealed class UnifiedDesignTests
{
    [AvaloniaFact]
    public void ResultWindowsShareTheLightFrameAndPutThePrimaryActionLast()
    {
        if (!Application.Current!.Styles.OfType<FluentTheme>().Any()) Application.Current.Styles.Add(DesignTokens.CreateTheme());
        using var controller = new DesktopController(new FakePlatform(), new ClassicDesktopStyleApplicationLifetime());
        controller.Main = new MainWindow(controller);
        using var picture = DesktopController.RenderText("Tbox");
        var windows = new (string Name, Window Window, string? Primary)[]
        {
            ("ocr", new OcrWindow(controller), "复制全部"),
            ("preview", new ImageResultWindow(controller, new Picture(picture.Png), false), "复制")
        };
        try
        {
            foreach (var (name, window, primary) in windows)
            {
                window.RequestedThemeVariant = ThemeVariant.Light; window.Show(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                Assert.Equal(Color.Parse("#F4F3F8"), ((ISolidColorBrush)window.Background!).Color);
                // 16 px app mark in the title strip, 40 px tool icon in the header.
                Assert.Contains(window.GetVisualDescendants().OfType<Image>(), i => i.Width == 16);
                Assert.Contains(window.GetVisualDescendants().OfType<Border>(), b => b.Width == 40 && b.Child is LineIcon);
                var buttons = window.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible && b.GetType() == typeof(Button)).ToArray();
                Assert.All(buttons, b => Assert.Equal(DesignTokens.ControlRadius, b.CornerRadius.TopLeft));
                var accent = Assert.Single(buttons, b => b.Classes.Contains("accent"));
                Assert.Equal(primary, AutomationProperties.GetName(accent));
                // The primary button is the rightmost one in the bottom bar.
                var right = accent.TranslatePoint(new Point(accent.Bounds.Width, 0), window)!.Value.X;
                Assert.All(buttons.Where(b => b != accent && b.TranslatePoint(default, window)!.Value.Y >= accent.TranslatePoint(default, window)!.Value.Y - 1),
                    b => Assert.True(b.TranslatePoint(default, window)!.Value.X < right, AutomationProperties.GetName(b)));
                Assert.All(window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text)),
                    t => Assert.DoesNotMatch(@"^[□○╱⌁↗①✎▰▦◇●−+↶↷↕⌖↓⧉×✓⛶▶Ⅱ■]$", t.Text!));
                Preview(window, name);
            }
        }
        finally { foreach (var (_, window, _) in windows) window.Close(); controller.Main.Close(); }
    }

    [AvaloniaFact]
    public void ConfirmDialogAsksAQuestionAndPaintsDestructiveActionsRed()
    {
        if (!Application.Current!.Styles.OfType<FluentTheme>().Any()) Application.Current.Styles.Add(DesignTokens.CreateTheme());
        var dialog = new ConfirmWindow("录屏尚未保存，是否放弃？", "放弃录屏", "放弃后本次录制的内容会被删除，无法恢复。", destructive: true);
        dialog.Show(); dialog.UpdateLayout();
        try
        {
            var buttons = dialog.GetVisualDescendants().OfType<Button>().ToArray();
            Assert.Equal(["返回", "放弃录屏"], buttons.Select(b => AutomationProperties.GetName(b)));
            Assert.DoesNotContain(buttons, b => b.Classes.Contains("accent"));
            Assert.Equal(Color.Parse("#D9363E"), ((ISolidColorBrush)buttons[1].Resources["ButtonBackground"]!).Color);
            Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "放弃后本次录制的内容会被删除，无法恢复。");
            Preview(dialog, "confirm");
        }
        finally { dialog.Close(); }
        var safe = new ConfirmWindow("现在打开安装程序？", "打开安装程序");
        safe.Show();
        try { Assert.Single(safe.GetVisualDescendants().OfType<Button>(), b => b.Classes.Contains("accent")); }
        finally { safe.Close(); }
    }

    [AvaloniaFact]
    public void OnlyAFailureNoticeOffersToOpenTbox()
    {
        var lifetime = new ClassicDesktopStyleApplicationLifetime { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        typeof(ClassicDesktopStyleApplicationLifetime).GetInterfaces().Single(i => i.Name == "ISetupApplicationLifetime")
            .GetMethod("BeforeAppInit")!.Invoke(lifetime, null);
        using var controller = new DesktopController(new FakePlatform(), lifetime);
        controller.Main = new MainWindow(controller);
        bool OffersOpen() => lifetime.Windows.OfType<FailureNotificationWindow>().Single().GetVisualDescendants().OfType<Button>()
            .Any(b => AutomationProperties.GetName(b) == "打开 Tbox");
        try
        {
            FailureNotificationWindow.ShowFailure(controller, "F6 被其他应用占用，请在主窗口查看原因。");
            Assert.True(FailureNotificationWindow.IsShowing); Assert.True(OffersOpen());
            Preview(lifetime.Windows.OfType<FailureNotificationWindow>().Single(), "notice-failure");
            FailureNotificationWindow.ShowInfo(controller, "已复制截图。");
            Assert.True(FailureNotificationWindow.IsShowing); Assert.False(OffersOpen());
        }
        finally { FailureNotificationWindow.Dismiss(); controller.Main.Close(); lifetime.Dispose(); }
    }

    [AvaloniaFact]
    public void MainWindowFollowsTheLightSpecWithOnePrimaryButtonPerPage()
    {
        if (!Application.Current!.Styles.OfType<FluentTheme>().Any()) Application.Current.Styles.Add(DesignTokens.CreateTheme());
        using var controller = new DesktopController(new FakePlatform { HasScreenPermission = true }, new ClassicDesktopStyleApplicationLifetime());
        var main = controller.Main = new MainWindow(controller);
        main.RequestedThemeVariant = ThemeVariant.Light; main.Show();
        try
        {
            Assert.Equal(Color.Parse("#F4F3F8"), ((ISolidColorBrush)main.Background!).Color);
            foreach (var page in Enum.GetValues<MainPage>())
            {
                main.Navigate(page); main.UpdateLayout(); Dispatcher.UIThread.RunJobs(); main.UpdateLayout();
                var buttons = main.CurrentPageView.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible).ToArray();
                Assert.True(buttons.Count(b => b.Classes.Contains("accent")) <= 1, page.ToString());
                // Page titles are 20 px (spec ①).
                Assert.Contains(main.CurrentPageView.GetVisualDescendants().OfType<TextBlock>(), t => t.FontSize == 20);
                Assert.All(buttons.Where(b => b.GetType() == typeof(Button)), b => Assert.True(b.MinHeight <= 36, AutomationProperties.GetName(b)));
                Preview(main, $"main-{page}");
            }
        }
        finally { controller.Dispose(); main.Close(); }
    }

    [AvaloniaFact]
    public void TaskToolbarsUseLineIconsOnTheSharedDarkSurface()
    {
        var editor = new AnnotationEditor();
        var toolbar = new AnnotationToolbar(editor, () => { });
        toolbar.Actions.Children.Add(AnnotationToolbar.IconButton(IconKind.Copy, "复制图片", () => { }));
        var window = new Window { Content = toolbar, SizeToContent = SizeToContent.WidthAndHeight, RequestedThemeVariant = ThemeVariant.Dark };
        window.Show(); window.UpdateLayout();
        try
        {
            Assert.Equal(Color.Parse(DesignTokens.TaskBackground), ((ISolidColorBrush)toolbar.Background!).Color);
            var buttons = toolbar.GetVisualDescendants().OfType<Button>().ToArray();
            Assert.All(buttons, b => Assert.IsNotType<string>(b.Content));
            Assert.All(buttons, b => Assert.False(string.IsNullOrEmpty(AutomationProperties.GetName(b))));
            var rectangle = Assert.Single(buttons, b => AutomationProperties.GetName(b) == "矩形");
            editor.Select(AnnotationTool.Rectangle);
            Assert.Equal(Color.Parse(DesignTokens.Accent), ((ISolidColorBrush)rectangle.Background!).Color);
            editor.Select(null);
            Assert.Equal(Colors.Transparent, ((ISolidColorBrush)rectangle.Background!).Color);
            Preview(window, "annotation-toolbar");
        }
        finally { window.Close(); }
    }

    [Fact]
    public void FilledAccentKeepsWhiteTextReadable()
    {
        static double Luminance(Color color)
        {
            static double Channel(byte value) { var n = value / 255.0; return n <= .04045 ? n / 12.92 : Math.Pow((n + .055) / 1.055, 2.4); }
            return .2126 * Channel(color.R) + .7152 * Channel(color.G) + .0722 * Channel(color.B);
        }
        Assert.True((1.05) / (Luminance(Color.Parse(DesignTokens.Accent)) + .05) >= 4.5);
        Assert.True((Luminance(Color.Parse(DesignTokens.TaskText)) + .05) / (Luminance(Color.Parse(DesignTokens.TaskBackground)) + .05) >= 7);
    }

    private static void Preview(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("TBOX_UI_PREVIEWS") is not { Length: > 0 } directory) return;
        Directory.CreateDirectory(directory);
        var size = window.Bounds.Size;
        using var preview = new RenderTargetBitmap(new PixelSize(Math.Max(1, (int)size.Width), Math.Max(1, (int)size.Height)), new Vector(96, 96));
        preview.Render(window); preview.Save(Path.Combine(directory, name + ".png"));
    }
}
