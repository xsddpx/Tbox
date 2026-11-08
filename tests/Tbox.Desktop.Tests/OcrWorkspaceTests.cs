using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tbox.Desktop.Platform;
using Tbox.Desktop.Views;
using Xunit;

namespace Tbox.Desktop.Tests;

public sealed class OcrWorkspaceTests
{
    [AvaloniaFact]
    public void OriginalReflowsWithoutReplacingEditedTextOrSource()
    {
        const string recognized = "Tbox 文字识别\n这段文字可以编辑和复制。\n12345";
        var platform = new FakePlatform { OcrResult = new RecognitionResult(recognized, []) };
        using var controller = new DesktopController(platform, new ClassicDesktopStyleApplicationLifetime());
        var window = new OcrWindow(controller);
        window.Show();
        window.Recognize(DesktopController.RenderText(recognized));
        try
        {
            Resize(800);
            var editor = Assert.Single(window.GetVisualDescendants().OfType<TextBox>());
            var source = Assert.Single(window.GetVisualDescendants().OfType<Image>(), i => AutomationProperties.GetName(i) == "识别原图");
            Assert.Equal(recognized, editor.Text);
            Assert.True(source.IsEffectivelyVisible);
            Assert.True(Position(editor).X > Position(source).X + source.Bounds.Width);
            var bitmap = source.Source;
            SavePreview("ocr-wide");

            editor.Text = "已经编辑的文字\n第二行";
            Resize(480);
            Assert.False(source.IsEffectivelyVisible);
            SavePreview("ocr-narrow");
            Click("显示原图");
            Assert.True(source.IsEffectivelyVisible);
            Assert.True(Position(editor).Y >= Position(source).Y + source.Bounds.Height);
            var expandedHeight = editor.Bounds.Height;
            Click("收起原图");
            Assert.False(source.IsEffectivelyVisible);
            Assert.True(editor.Bounds.Height > expandedHeight);
            Resize(800);
            Assert.False(source.IsEffectivelyVisible);
            Click("显示原图");
            Assert.Equal("已经编辑的文字\n第二行", editor.Text);
            Assert.Same(bitmap, source.Source);
        }
        finally { window.Close(); }

        Point Position(Control control) => control.TranslatePoint(default, window)!.Value;
        void Resize(int width)
        {
            window.Width = width;
            Layout();
        }
        void Click(string label)
        {
            var button = Assert.Single(window.GetVisualDescendants().OfType<Button>(), b => Equals(b.Content, label));
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Layout();
        }
        void Layout()
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }
        void SavePreview(string name)
        {
            if (Environment.GetEnvironmentVariable("TBOX_UI_PREVIEWS") is not { Length: > 0 } directory) return;
            Directory.CreateDirectory(directory);
            using var preview = new RenderTargetBitmap(new PixelSize((int)window.Width, (int)window.Height), new Vector(96, 96));
            preview.Render(window);
            preview.Save(Path.Combine(directory, name + ".png"));
        }
    }
}
