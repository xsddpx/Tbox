using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Tbox.Core;
using Tbox.Desktop.Models;

namespace Tbox.Desktop.Views;

internal sealed class AnnotationTextEditor
{
    internal const string OverflowMessage = "文字放不进图像，请减少文字或调小字号。";
    private readonly Canvas _host;
    private readonly AnnotationEditor _editor;
    private readonly Func<AnnotationPoint, Point> _map;
    private readonly Func<Vector> _scale;
    private readonly bool _commitOnEscape;
    private readonly Func<Rect> _contentBounds;
    private TextBox? _input;
    private AnnotationPoint _origin;
    public bool IsEditing => _input is not null;
    public event Action? EscapeCommitted;

    public AnnotationTextEditor(Canvas host, AnnotationEditor editor, Func<AnnotationPoint, Point> map, Func<Vector> scale,
        bool commitOnEscape = false, Func<Rect>? contentBounds = null)
    {
        _host = host; _editor = editor; _map = map; _scale = scale;
        _commitOnEscape = commitOnEscape;
        _contentBounds = contentBounds ?? (() => new Rect(_host.Bounds.Size));
        editor.TextRequested += Begin;
        editor.Changed += UpdatePlacement;
        host.SizeChanged += (_, _) => UpdatePlacement();
    }

    private void Begin(AnnotationPoint origin)
    {
        if (!Commit()) return;
        _origin = origin;
        _input = new TextBox
        {
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontFamily = Typeface.Default.FontFamily,
            Padding = new Thickness(0), BorderThickness = new Thickness(0), MinWidth = 0, MinHeight = 0, ClipToBounds = true,
            Background = Brush.Parse("#DD202020")
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_input, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(_input, ScrollBarVisibility.Hidden);
        _input.TextChanged += (_, _) =>
        {
            if (_input is not { } input) return;
            ToolTip.SetIsOpen(input, false);
            UpdatePlacement();
        };
        _input.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0)
            { Commit(); e.Handled = true; }
            else if (e.Key == Key.Escape)
            {
                if (!_commitOnEscape) Cancel();
                else if (Commit()) EscapeCommitted?.Invoke();
                e.Handled = true;
            }
        };
        _host.Children.Add(_input);
        UpdatePlacement();
        _input.Focus();
    }

    private (Rect Bounds, bool Fits, Vector Scale) Placement(string text)
    {
        var area = _contentBounds();
        var scale = _scale();
        if (area.Width <= 0 || area.Height <= 0 || scale.X <= 0 || scale.Y <= 0) return (default, false, scale);

        var anchor = _map(_origin);
        var maxWidth = Math.Min(320, area.Width);
        var width = Math.Clamp(area.Right - anchor.X, Math.Min(100, maxWidth), maxWidth);
        var measured = MeasureText(text, width / scale.X);
        // Wider text can fit on a short image if there is room to move its starting point left.
        if (measured.Height * scale.Y > area.Height && width < maxWidth)
        {
            width = maxWidth;
            measured = MeasureText(text, width / scale.X);
        }
        var height = Math.Min(area.Height, Math.Max(36, Math.Ceiling(measured.Height * scale.Y)));
        var left = Math.Clamp(anchor.X, area.Left, Math.Max(area.Left, area.Right - width));
        var top = Math.Clamp(anchor.Y, area.Top, Math.Max(area.Top, area.Bottom - height));
        var fits = measured.Height * scale.Y <= area.Height + .01 && measured.Width * scale.X <= width + .01;
        return (new Rect(left, top, width, height), fits, scale);
    }

    private Size MeasureText(string text, double width)
    {
        using var layout = AnnotationRenderer.LayoutText(text.Length == 0 ? " " : text, _editor.FontSize, width, Brushes.White);
        return new Size(layout.Width, layout.Height);
    }

    private void UpdatePlacement()
    {
        if (_input is not { } input) return;
        var (bounds, _, scale) = Placement(input.Text ?? "");
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        input.FontSize = _editor.FontSize * scale.Y;
        input.Foreground = new SolidColorBrush(Color.FromUInt32(_editor.Color));
        input.Width = bounds.Width;
        input.Height = bounds.Height;
        Canvas.SetLeft(input, bounds.X);
        Canvas.SetTop(input, bounds.Y);
    }

    public bool Commit()
    {
        if (_input is not { } input) return true;
        var text = input.Text ?? "";
        if (!string.IsNullOrWhiteSpace(text))
        {
            var (bounds, fits, scale) = Placement(text);
            if (!fits)
            {
                ToolTip.SetTip(input, OverflowMessage);
                ToolTip.SetIsOpen(input, true);
                input.Focus();
                return false;
            }
            var offset = bounds.Position - _map(_origin);
            _editor.CommitText(text, new AnnotationPoint(_origin.X + offset.X / scale.X, _origin.Y + offset.Y / scale.Y),
                bounds.Width / scale.X, bounds.Height / scale.Y);
        }
        Cancel();
        return true;
    }
    public void Cancel()
    {
        if (_input is not { } input) return;
        _input = null;
        ToolTip.SetIsOpen(input, false);
        _host.Children.Remove(input);
    }
}
