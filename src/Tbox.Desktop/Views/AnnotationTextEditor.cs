using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Tbox.Core;
using Tbox.Desktop.Models;

namespace Tbox.Desktop.Views;

internal sealed class AnnotationTextEditor
{
    private readonly Canvas _host;
    private readonly AnnotationEditor _editor;
    private readonly Func<AnnotationPoint, Point> _map;
    private readonly Func<Vector> _scale;
    private readonly bool _commitOnEscape;
    private TextBox? _input;
    private AnnotationPoint _origin;
    public bool IsEditing => _input is not null;
    public event Action? EscapeCommitted;

    public AnnotationTextEditor(Canvas host, AnnotationEditor editor, Func<AnnotationPoint, Point> map, Func<Vector> scale, bool commitOnEscape = false)
    {
        _host = host; _editor = editor; _map = map; _scale = scale;
        _commitOnEscape = commitOnEscape;
        editor.TextRequested += Begin;
    }

    private void Begin(AnnotationPoint origin)
    {
        Commit(); _origin = origin;
        var position = _map(origin);
        _input = new TextBox
        {
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Padding = new Thickness(2),
            MinWidth = 100, Width = Math.Max(100, Math.Min(320, _host.Bounds.Width - position.X)),
            MinHeight = 36, MaxHeight = Math.Max(36, _host.Bounds.Height - position.Y),
            FontSize = Math.Max(8, _editor.FontSize * _scale().Y), Foreground = new SolidColorBrush(Color.FromUInt32(_editor.Color)),
            Background = Brush.Parse("#DD202020")
        };
        _input.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0)
            { Commit(); e.Handled = true; }
            else if (e.Key == Key.Escape)
            {
                if (_commitOnEscape) { Commit(); EscapeCommitted?.Invoke(); } else Cancel();
                e.Handled = true;
            }
        };
        Canvas.SetLeft(_input, position.X); Canvas.SetTop(_input, position.Y);
        _host.Children.Add(_input); _input.Focus();
    }

    public void Commit()
    {
        if (_input is not { } input) return;
        _editor.CommitText(input.Text ?? "", _origin, input.Bounds.Width / _scale().X,
            Math.Max(input.Bounds.Height, input.DesiredSize.Height) / _scale().Y);
        Cancel();
    }
    public void Cancel()
    {
        if (_input is not { } input) return;
        _host.Children.Remove(input); _input = null;
    }
}
