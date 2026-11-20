using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Tbox.Core;

namespace Tbox.Desktop.Views;

/// <summary>
/// Playback track with trim handles at both ends (PixPin-style): the light red band is the exported range,
/// the solid red part is what has played inside it, and an optional set of ticks marks contact-sheet samples.
/// </summary>
internal sealed class RecordingTrimBar : Control
{
    private const double HandleWidth = 10, HandleHeight = 24, HeadSize = 16, RailHeight = 4, Inset = HandleWidth / 2 + 1;
    private static readonly IBrush Rail = Brush.Parse("#E6E4EE"), Band = new SolidColorBrush(Color.Parse("#E5484D"), .18),
        Tick = Brush.Parse("#1A1B22"), Tip = Brush.Parse("#1D1D24"), Muted = Brush.Parse("#5F6270");
    private enum Part { None, Start, End, Head }
    private Part _drag;
    private TimeSpan _duration, _position;
    private RecordingTimeRange _range;
    private IReadOnlyList<TimeSpan> _ticks = [];

    public event Action<TimeSpan>? Seeked;
    public event Action<RecordingTimeRange>? RangeChanged;

    public RecordingTrimBar()
    {
        Height = 40; MinWidth = 100; Focusable = true; Cursor = new Cursor(StandardCursorType.Hand); ClipToBounds = false;
        AutomationProperties.SetName(this, "播放进度与导出范围");
    }

    public TimeSpan Duration
    {
        get => _duration;
        set
        {
            // A full range keeps following the clip when the player reports a more precise length.
            var full = _range.End >= _duration;
            _duration = value < TimeSpan.Zero ? TimeSpan.Zero : value;
            _range = RecordingTimeRange.Normalize(_range.Start, full ? _duration : _range.End, _duration); InvalidateVisual();
        }
    }

    public RecordingTimeRange Range
    {
        get => _range;
        set { _range = RecordingTimeRange.Normalize(value.Start, value.End, _duration); InvalidateVisual(); RangeChanged?.Invoke(_range); }
    }

    public TimeSpan Position { get => _position; set { _position = value; InvalidateVisual(); } }
    public IReadOnlyList<TimeSpan> Ticks { get => _ticks; set { _ticks = value; InvalidateVisual(); } }
    internal bool IsDragging => _drag != Part.None;

    private double Track => Math.Max(1, Bounds.Width - 2 * Inset);
    private double X(TimeSpan time) => Inset + (_duration > TimeSpan.Zero ? Math.Clamp(time / _duration, 0, 1) : 0) * Track;
    private TimeSpan Time(double x) => _duration * Math.Clamp((x - Inset) / Track, 0, 1);
    private double Mid => Bounds.Height - HandleHeight / 2 - 2;

    public override void Render(DrawingContext context)
    {
        var mid = Mid;
        context.FillRectangle(Rail, new Rect(Inset, mid - RailHeight / 2, Track, RailHeight), 2);
        double start = X(_range.Start), end = X(_range.End), head = X(_position);
        context.FillRectangle(Band, new Rect(start, mid - 4, end - start, 8));
        var red = Ui.RecordRed;
        context.DrawRectangle(null, new Pen(red, 1.5), new Rect(start, mid - 4, end - start, 8));
        if (head > start) context.FillRectangle(red, new Rect(start, mid - RailHeight / 2, Math.Min(head, end) - start, RailHeight));
        for (var i = 0; i < _ticks.Count; i++)
        {
            var x = X(_ticks[i]);
            context.FillRectangle(Tick, new Rect(x - 1, mid - 5, 2, 10), 1);
            Label(context, (i + 1).ToString("00"), x, mid - HandleHeight / 2 - 14, Muted, 10, null);
        }
        foreach (var x in new[] { start, end })
        {
            context.DrawRectangle(red, null, new RoundedRect(new Rect(x - HandleWidth / 2, mid - HandleHeight / 2, HandleWidth, HandleHeight), 3),
                new BoxShadows(new BoxShadow { OffsetY = 1, Blur = 3, Color = Color.FromArgb(64, 16, 18, 32) }));
            context.FillRectangle(new SolidColorBrush(Colors.White, .8), new Rect(x - 1, mid - 5, 2, 10), 1);
        }
        context.DrawRectangle(Brushes.White, new Pen(new SolidColorBrush(Color.FromArgb(38, 16, 18, 32)), 1),
            new RoundedRect(new Rect(head - HeadSize / 2, mid - HeadSize / 2, HeadSize, HeadSize), HeadSize / 2),
            new BoxShadows(new BoxShadow { OffsetY = 2, Blur = 6, Color = Color.FromArgb(51, 16, 18, 32) }));
        if (_drag is Part.Start or Part.End)
        {
            var at = _drag == Part.Start ? _range.Start : _range.End;
            Label(context, FormatPrecise(at), X(at), mid - HandleHeight / 2 - 22, Brushes.White, 11, Tip);
        }
    }

    private static void Label(DrawingContext context, string text, double centre, double top, IBrush foreground, double size, IBrush? background)
    {
        var layout = new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold), size, foreground);
        var x = centre - layout.Width / 2;
        if (background is not null) context.FillRectangle(background, new Rect(x - 6, top, layout.Width + 12, 18), 4);
        context.DrawText(layout, new Point(x, top + (background is null ? 0 : (18 - layout.Height) / 2)));
    }

    internal static string FormatPrecise(TimeSpan time) => $"{(int)time.TotalMinutes:00}:{time.Seconds:00}.{time.Milliseconds / 100}";

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || _duration <= TimeSpan.Zero) return;
        var x = e.GetPosition(this).X;
        double start = X(_range.Start), end = X(_range.End);
        // Prefer the nearer handle when both overlap; clicks elsewhere seek inside the range.
        _drag = Math.Abs(x - start) <= HandleWidth && Math.Abs(x - start) <= Math.Abs(x - end) ? Part.Start
            : Math.Abs(x - end) <= HandleWidth ? Part.End : Part.Head;
        e.Pointer.Capture(this); Move(x); e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_drag != Part.None) { Move(e.GetPosition(this).X); e.Handled = true; }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag == Part.None) return;
        var trimmed = _drag != Part.Head; _drag = Part.None; e.Pointer.Capture(null); InvalidateVisual();
        if (trimmed) RangeChanged?.Invoke(_range);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e) { base.OnPointerCaptureLost(e); _drag = Part.None; InvalidateVisual(); }

    private void Move(double x)
    {
        var time = Time(x);
        switch (_drag)
        {
            case Part.Start: _range = RecordingTimeRange.Normalize(time, _range.End, _duration); Seek(_range.Start); break;
            case Part.End: _range = RecordingTimeRange.Normalize(_range.Start, time, _duration); Seek(_range.End); break;
            case Part.Head: Seek(time < _range.Start ? _range.Start : time > _range.End ? _range.End : time); break;
        }
        InvalidateVisual();
        if (_drag is Part.Start or Part.End) RangeChanged?.Invoke(_range);
    }

    private void Seek(TimeSpan time) { _position = time; Seeked?.Invoke(time); }
}
