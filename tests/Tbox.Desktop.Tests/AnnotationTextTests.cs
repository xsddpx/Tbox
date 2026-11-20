using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
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
    public void TextNearTheLowerRightStaysWithinTheImageAndCommitsAtTheVisiblePosition(bool croppedPin)
    {
        var area = croppedPin ? new Rect(0, 0, 78, 60) : new Rect(160, 95, 126, 96);
        var scale = croppedPin ? .5 : 1;
        var imageOffset = croppedPin ? new Point(20, 40) : default;
        var canvas = new Canvas { Width = 400, Height = 300 };
        var window = new Window { Content = canvas, Width = 400, Height = 300 };
        window.Show();
        try
        {
            var editor = new AnnotationEditor { FontSize = 14 };
            editor.Select(AnnotationTool.Text);
            var editing = new AnnotationTextEditor(canvas, editor,
                p => new Point((p.X - imageOffset.X) * scale, (p.Y - imageOffset.Y) * scale), () => new Vector(scale, scale),
                croppedPin, () => area);
            editor.Begin(new AnnotationPoint((area.Right - 9) / scale + imageOffset.X, (area.Bottom - 8) / scale + imageOffset.Y), false);
            var input = Assert.IsType<TextBox>(Assert.Single(canvas.Children));
            input.Text = "边缘备注\n第二行";
            Dispatcher.UIThread.RunJobs();
            canvas.UpdateLayout();
            Assert.True(input.Bounds.Left < area.Right - 9);
            Assert.True(input.Bounds.Top < area.Bottom - 8);
            Assert.True(area.Contains(input.Bounds), $"{input.Bounds} exceeds {area}");
            var expected = input.Bounds;
            Assert.True(editing.Commit());
            var command = Assert.IsType<TextAnnotationCommand>(Assert.Single(editor.Document.Commands));
            Assert.Equal(input.Text, command.Text);
            Assert.Equal(expected.X / scale + imageOffset.X, command.Bounds.Left, 2);
            Assert.Equal(expected.Y / scale + imageOffset.Y, command.Bounds.Top, 2);
            Assert.Equal(expected.Width / scale, command.Bounds.Width, 2);
            Assert.Equal(expected.Height / scale, command.Bounds.Height, 2);
            using var layout = AnnotationRenderer.LayoutText(command.Text, command.Style.FontSizePixels, command.Bounds.Width, Avalonia.Media.Brushes.Black);
            Assert.InRange(layout.Height, 1, command.Bounds.Height);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void TextThatCannotFitStaysEditableUntilShortened()
    {
        var canvas = new Canvas { Width = 100, Height = 48 };
        var window = new Window { Content = canvas, Width = 100, Height = 48 };
        window.Show();
        try
        {
            var editor = new AnnotationEditor { FontSize = 14 };
            editor.Select(AnnotationTool.Text);
            var editing = new AnnotationTextEditor(canvas, editor, p => new Point(p.X, p.Y), () => new Vector(1, 1), true);
            editor.Begin(new AnnotationPoint(95, 42), false);
            var input = Assert.IsType<TextBox>(Assert.Single(canvas.Children));
            var draft = string.Join("\n", Enumerable.Repeat("Long note", 12));
            input.Text = draft;
            Assert.False(editing.Commit());
            Assert.Same(input, Assert.Single(canvas.Children));
            Assert.Equal(draft, input.Text);
            Assert.Empty(editor.Document.Commands);
            input.Text = "Short note";
            Assert.True(editing.Commit());
            Assert.Equal("Short note", Assert.IsType<TextAnnotationCommand>(Assert.Single(editor.Document.Commands)).Text);
        }
        finally { window.Close(); }
    }

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
