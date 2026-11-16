using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tbox.Core.ModelServices;

namespace Tbox.Desktop.Views;

internal sealed class ModelOrderList : StackPanel
{
    private const double RowHeight = 38;
    private static readonly IBrush Divider = Brush.Parse(DesignTokens.Hairline);
    private static readonly IBrush Accent = Brush.Parse(DesignTokens.Accent);
    private readonly StackPanel _rows = new();
    private readonly TextBlock _hint = Ui.Description("上下拖动调整顺序");
    private readonly TextBlock _error = Ui.Description("");
    private readonly Func<ModelReference, ModelReference, bool, Task<ModelConfiguration>> _move;
    private readonly DispatcherTimer _scrollTimer = new() { Interval = TimeSpan.FromMilliseconds(32) };
    private Drag? _drag;
    private Row? _target;
    private bool _after, _saving;

    public ModelOrderList(Func<ModelReference, ModelReference, bool, Task<ModelConfiguration>> move)
    {
        _move = move;
        _hint.FontSize = DesignTokens.Caption;
        _error.IsVisible = false;
        AutomationProperties.SetLiveSetting(_error, AutomationLiveSetting.Polite);
        Children.Add(Ui.GroupHeader("模型", _hint));
        Children.Add(_rows);
        Children.Add(_error);
        _rows.PointerMoved += HandleMove;
        _rows.PointerReleased += HandleRelease;
        _rows.PointerCaptureLost += (_, _) => EndDrag();
        _scrollTimer.Tick += (_, _) => ScrollWhileDragging();
    }

    internal void Show(ModelConfiguration configuration)
    {
        EndDrag();
        _rows.Children.Clear();
        var connections = configuration.Connections.ToDictionary(connection => connection.Id);
        foreach (var reference in ModelPolicy.OrderedModels(configuration))
        {
            var row = new Row(reference, connections[reference.ConnectionId].Name);
            row.Handle.PointerPressed += (_, e) => BeginDrag(row, e);
            row.Handle.KeyDown += (_, e) => HandleKey(row, e);
            _rows.Children.Add(row);
        }
        IsVisible = _rows.Children.Count > 0;
        _hint.IsVisible = _rows.Children.Count > 1;
    }

    private void BeginDrag(Row row, PointerPressedEventArgs e)
    {
        if (_saving || _drag is not null || _rows.Children.Count < 2 || !e.GetCurrentPoint(_rows).Properties.IsLeftButtonPressed) return;
        var viewer = this.FindAncestorOfType<ScrollViewer>();
        var viewport = viewer?.GetVisualDescendants().OfType<ScrollContentPresenter>().FirstOrDefault();
        var point = e.GetPosition((Visual?)viewport ?? _rows);
        _drag = new(e.Pointer, row, point, viewer, viewport);
        e.Pointer.Capture(_rows);
        e.Handled = true;
    }

    private void HandleMove(object? sender, PointerEventArgs e)
    {
        if (_drag is not { } drag || drag.Pointer != e.Pointer) return;
        drag.Current = e.GetPosition((Visual?)drag.Viewport ?? _rows);
        if (!drag.Started && Math.Abs(drag.Current.Y - drag.Origin.Y) < 4) return;
        drag.Started = true;
        if (drag.Viewport is not null) _scrollTimer.Start();
        UpdateTarget(e.GetPosition(_rows));
        e.Handled = true;
    }

    private void UpdateTarget(Point point)
    {
        ClearTarget();
        if (point.X < 0 || point.X >= _rows.Bounds.Width) return;
        var index = Math.Clamp((int)Math.Floor(point.Y / RowHeight), 0, _rows.Children.Count - 1);
        var row = (Row)_rows.Children[index];
        if (row == _drag?.Source) return;
        _target = row;
        _after = point.Y - row.Bounds.Y >= RowHeight / 2;
        row.BorderBrush = Accent;
        row.BorderThickness = _after ? new Thickness(0, 0, 0, 2) : new Thickness(0, 2, 0, 0);
    }

    private void ScrollWhileDragging()
    {
        if (_drag is not { Started: true, Viewer: { } viewer, Viewport: { } viewport } drag) return;
        const double edge = 26;
        var y = drag.Current.Y;
        var step = y < edge ? -12 : y > viewport.Bounds.Height - edge ? 12 : 0;
        if (step == 0 || drag.Current.X < 0 || drag.Current.X >= viewport.Bounds.Width) return;
        viewer.Offset += new Vector(0, step);
        if (viewport.TranslatePoint(drag.Current, _rows) is { } translated) UpdateTarget(translated);
    }

    private void HandleRelease(object? sender, PointerReleasedEventArgs e)
    {
        if (_drag is not { } drag || drag.Pointer != e.Pointer) return;
        if (drag.Started) UpdateTarget(e.GetPosition(_rows));
        var target = _target;
        var after = _after;
        EndDrag();
        e.Handled = true;
        if (target is not null) Move(drag.Source, target, after);
    }

    private void EndDrag()
    {
        _scrollTimer.Stop();
        ClearTarget();
        var pointer = _drag?.Pointer;
        _drag = null;
        if (pointer?.Captured == _rows) pointer.Capture(null);
    }

    private void ClearTarget()
    {
        if (_target is null) return;
        _target.BorderBrush = Divider;
        _target.BorderThickness = new Thickness(0, 1, 0, 0);
        _target = null;
    }

    private void HandleKey(Row row, KeyEventArgs e)
    {
        if (_saving || _drag is not null || e.Key is not (Key.Up or Key.Down)) return;
        var after = e.Key == Key.Down;
        var next = _rows.Children.IndexOf(row) + (after ? 1 : -1);
        e.Handled = true;
        if (next >= 0 && next < _rows.Children.Count) Move(row, (Row)_rows.Children[next], after);
    }

    private async void Move(Row row, Row target, bool after)
    {
        _saving = true;
        _error.IsVisible = false;
        try
        {
            var configuration = await _move(row.Reference, target.Reference, after);
            var restoreFocus = row.Handle.IsFocused;
            Show(configuration);
            if (restoreFocus) _rows.Children.OfType<Row>().FirstOrDefault(item => item.Reference == row.Reference)?.Handle.Focus();
        }
        catch (ModelServiceException error) { ShowError(error.Message); }
        catch { ShowError("模型顺序未保存，请重试。"); }
        finally { _saving = false; }
    }

    private void ShowError(string message)
    {
        _error.Text = message;
        _error.IsVisible = true;
    }

    private sealed record Drag(IPointer Pointer, Row Source, Point Origin, ScrollViewer? Viewer, ScrollContentPresenter? Viewport)
    {
        public Point Current { get; set; } = Origin;
        public bool Started { get; set; }
    }

    private sealed class Row : Border
    {
        public ModelReference Reference { get; }
        public Control Handle { get; }

        public Row(ModelReference reference, string connection)
        {
            Reference = reference;
            Height = RowHeight;
            BorderBrush = Divider;
            BorderThickness = new Thickness(0, 1, 0, 0);
            Handle = new Border
            {
                Width = 28, Height = 30, Background = Brushes.Transparent, Focusable = true,
                Cursor = new Cursor(StandardCursorType.SizeNorthSouth),
                Child = new LineIcon(IconKind.Grip)
                {
                    Width = 16, Height = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                }
            };
            AutomationProperties.SetName(Handle, $"调整 {reference.ModelId}（{connection}）的顺序");
            ToolTip.SetTip(Handle, "上下拖动，或聚焦后按 ↑ / ↓");
            var label = Ui.Text(reference.ModelId, DesignTokens.Body);
            label.VerticalAlignment = VerticalAlignment.Center;
            label.TextTrimming = TextTrimming.CharacterEllipsis;
            ToolTip.SetTip(label, reference.ModelId);
            var provider = Ui.Description(connection);
            provider.FontSize = DesignTokens.Caption;
            provider.VerticalAlignment = VerticalAlignment.Center;
            provider.MaxWidth = 140;
            provider.TextTrimming = TextTrimming.CharacterEllipsis;
            ToolTip.SetTip(provider, connection);
            Child = new Grid
            {
                ColumnDefinitions = new("Auto,*,Auto"), ColumnSpacing = 8,
                Children = { Handle, Ui.Column(label, 1), Ui.Column(provider, 2) }
            };
        }
    }
}
