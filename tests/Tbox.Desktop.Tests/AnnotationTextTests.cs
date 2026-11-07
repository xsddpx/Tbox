using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Tbox.Core;
using Tbox.Desktop.Models;
using Tbox.Desktop.Views;
using Xunit;

namespace Tbox.Desktop.Tests;

public sealed class AnnotationTextTests
{
    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void EscapeCommitsPinTextAndFinishesOnceButCancelsCaptureText(bool pinMode)
    {
        var canvas = new Canvas { Width = 400, Height = 300 };
        var window = new Window { Content = canvas, Width = 400, Height = 300 };
        window.Show();
        try
        {
            var editor = new AnnotationEditor(); editor.Select(AnnotationTool.Text);
            var input = new AnnotationTextEditor(canvas, editor, p => new Point(p.X, p.Y), () => new Vector(1, 1), pinMode);
            var finished = 0;
            input.EscapeCommitted += () => { finished++; editor.Select(null); };
            editor.Begin(new AnnotationPoint(20, 20), false);
            var text = Assert.IsType<TextBox>(Assert.Single(canvas.Children));
            text.Text = "保留文字";
            canvas.UpdateLayout();
            text.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
            Assert.False(input.IsEditing); Assert.Empty(canvas.Children);
            Assert.Equal(pinMode ? 1 : 0, finished);
            if (pinMode)
            {
                Assert.IsType<TextAnnotationCommand>(Assert.Single(editor.Document.Commands));
                Assert.Null(editor.Tool);
            }
            else Assert.Empty(editor.Document.Commands);
        }
        finally { window.Close(); }
    }
}
