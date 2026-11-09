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

// Every window shares the tray panel's design language: one header, one control style and line icons.
public sealed class UnifiedDesignTests
{
    [AvaloniaFact]
    public void ResultWindowsShareTheBrandHeaderAndThemedControlsInBothThemes()
    {
        if (!Application.Current!.Styles.OfType<FluentTheme>().Any()) Application.Current.Styles.Add(DesignTokens.CreateTheme());
        using var controller = new DesktopController(new FakePlatform(), new ClassicDesktopStyleApplicationLifetime());
        controller.Main = new MainWindow(controller);
        using var picture = DesktopController.RenderText("Tbox");
        var windows = new (string Name, Window Window)[]
        {
            ("ocr", new OcrWindow(controller)),
            ("preview", new ImageResultWindow(controller, new Picture(picture.Png), false)),
            ("confirm", new ConfirmWindow("录屏尚未保存，是否放弃？", "放弃录屏"))
        };
        try
        {
            foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            foreach (var (name, window) in windows)
            {
                window.RequestedThemeVariant = theme; window.Show(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                var colors = DesignTokens.Colors(theme == ThemeVariant.Dark);
                Assert.Equal(Color.Parse(colors.Background), ((ISolidColorBrush)window.Background!).Color);
                if (name != "confirm")
                    Assert.Contains(window.GetVisualDescendants().OfType<Image>(), i => i.Width == 26 && !i.IsHitTestVisible);
                var buttons = window.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible && b.GetType() == typeof(Button)).ToArray();
                Assert.NotEmpty(buttons);
                Assert.All(buttons, b => Assert.Equal(DesignTokens.ControlRadius, b.CornerRadius.TopLeft));
                // A window offers at most one filled amethyst action, so the main step is always obvious.
                Assert.True(buttons.Count(b => b.Classes.Contains("accent")) <= 1, name);
                Assert.All(window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text)),
                    t => Assert.DoesNotMatch(@"^[□○╱⌁↗①✎▰▦◇●−+↶↷↕⌖↓⧉×✓⛶▶Ⅱ■]$", t.Text!));
                Preview(window, $"{name}-{theme.Key}");
            }
        }
        finally { foreach (var (_, window) in windows) window.Close(); controller.Main.Close(); }
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
