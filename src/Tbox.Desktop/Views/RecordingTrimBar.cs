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
/// The playhead snaps lightly to a tick; <see cref="HotTickChanged"/> lets the window show that sample's thumbnail.
/// </summary>
internal sealed class RecordingTrimBar : Control
{
    private const double HandleWidth = 10, HeadSize = 16, Inset = 6, Snap = 8;
    private static readonly IBrush Rail = Brush.Parse("#DCE3EA"), Tip = Brush.Parse("#22272F"), Muted = Brush.Parse("#54606E");
    internal Func<TimeSpan, IImage?>? ThumbnailAt { get; set; }
    private IReadOnlyList<TimeSpan> _frameTimes = [];
    internal IReadOnlyList<TimeSpan> FrameTimes
    {
        get => _frameTimes;
        set { _frameTimes = value; InvalidateVisual(); }
    }

    private enum Part { None, Start, End, Head }
    private Part _drag;
    private (double X, int Index)? _pressedFrame;
    private TimeSpan _duration, _position;
    private RecordingTimeRange _range;
    private IReadOnlyList<TimeSpan> _ticks = [];
    // Tick under the pointer (hover) or that the playhead snapped to (drag); -1 when none.
    private int _hot = -1;

    public event Action<TimeSpan>? Seeked;
    public event Action<RecordingTimeRange>? RangeChanged;

    public RecordingTrimBar()
    {
        Height = 54; MinWidth = 100; Focusable = true; Cursor = new Cursor(StandardCursorType.Hand); ClipToBounds = false;
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
    public IReadOnlyList<TimeSpan> Ticks { get => _ticks; set { _ticks = value; SetHot(-1); InvalidateVisual(); } }
    /// <summary>Raised with the tick under the pointer or snapped playhead, or -1 when there is none.</summary>
    public event Action<int>? HotTickChanged;
    internal bool IsDragging => _drag != Part.None;
    internal int HotTick => _hot;
    // A thin anchor at the tick, spanning the track, for placing the thumbnail above it.
    internal Rect TickAnchor(int index) => new(X(_ticks[index]) - 1, 0, 2, Bounds.Height);

    private double Track => Math.Max(1, Bounds.Width - 2 * Inset);
    private double X(TimeSpan time) => Inset + (_duration > TimeSpan.Zero ? Math.Clamp(time / _duration, 0, 1) : 0) * Track;
    private TimeSpan Time(double x) => _duration * Math.Clamp((x - Inset) / Track, 0, 1);
    private double Mid => Bounds.Height - 14;

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        var rail = new Rect(Inset, 4, Track, 32);
        context.FillRectangle(Rail, rail, 4);
        double start = X(_range.Start), end = X(_range.End), head = X(_position);
        var left = _ticks.Count > 0 ? start : rail.X;
        var width = (_ticks.Count > 0 ? end - start : rail.Width) / Math.Max(1, _frameTimes.Count);
        if (width > 0)
        {
            using var clip = context.PushClip(new RoundedRect(rail, 4));
            for (var i = 0; i < _frameTimes.Count; i++)
            {
                if (ThumbnailAt?.Invoke(_frameTimes[i]) is not { } image) continue;
                var cell = new Rect(left + i * width, rail.Y, width, rail.Height);
                var scale = Math.Max(cell.Width / image.Size.Width, cell.Height / image.Size.Height);
                var source = new Rect(image.Size).CenterRect(new Rect(cell.Size / scale));
                context.DrawImage(image, source, cell);
            }
        }
        var red = Ui.RecordRed;
        context.DrawRectangle(null, new Pen(red, 1), new Rect(start, rail.Y, Math.Max(0, end - start), rail.Height));
        for (var i = 0; i < _ticks.Count; i++)
            Label(context, (i + 1).ToString("00"), X(_ticks[i]), rail.Bottom + 2, i == _hot ? red : Muted, 10, null);
        foreach (var x in new[] { start, end })
        {
            context.FillRectangle(red, new Rect(x - 3, rail.Y, 6, rail.Height), 2);
            context.FillRectangle(Brushes.White, new Rect(x - .65, rail.Center.Y - 4, 1.3, 8), 1);
        }
        context.FillRectangle(Brushes.White, new Rect(head - 2, 0, 4, rail.Bottom + 2), 2);
        context.FillRectangle(Tip, new Rect(head - 1, 0, 2, rail.Bottom + 2), 1);
        context.FillRectangle(Tip, new Rect(head - 3, 0, 6, 5), 2);
        if (_drag is Part.Start or Part.End)
        {
            var at = _drag == Part.Start ? _range.Start : _range.End;
            Label(context, FormatPrecise(at), X(at), 0, Brushes.White, 11, Tip, Bounds.Width);
        }
    }

    private void SetHot(int index)
    {
        if (index == _hot) return;
        _hot = index; InvalidateVisual(); HotTickChanged?.Invoke(index);
    }

    private static void Label(DrawingContext context, string text, double centre, double top, IBrush foreground, double size, IBrush? background,
        double width = double.PositiveInfinity)
    {
        var layout = new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold), size, foreground);
        var x = centre - layout.Width / 2;
        if (background is not null)
        {
            x = Math.Clamp(x, 6, Math.Max(6, width - layout.Width - 6));
            context.FillRectangle(background, new Rect(x - 6, top, layout.Width + 12, 18), 4);
        }
        context.DrawText(layout, new Point(x, top + (background is null ? 0 : (18 - layout.Height) / 2)));
    }

    internal static string FormatPrecise(TimeSpan time, bool showHours = false) => showHours || time.TotalHours >= 1
        ? time.ToString(@"h\:mm\:ss\.f", System.Globalization.CultureInfo.InvariantCulture)
        : time.ToString(@"mm\:ss\.f", System.Globalization.CultureInfo.InvariantCulture);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || _duration <= TimeSpan.Zero) return;
        var point = e.GetPosition(this);
        var x = point.X;
        double start = X(_range.Start), end = X(_range.End);
        // The lower half of the white playhead remains reachable when it shares a red handle's x coordinate.
        var grabPlayhead = point.Y >= Mid + 4 && Math.Abs(x - X(_position)) <= HeadSize / 2;
        _drag = grabPlayhead ? Part.Head
            : Math.Abs(x - start) <= HandleWidth && Math.Abs(x - start) <= Math.Abs(x - end) ? Part.Start
            : Math.Abs(x - end) <= HandleWidth ? Part.End : Part.Head;
        if (_drag != Part.Head) SetHot(-1);
        var frame = FrameAt(point);
        _pressedFrame = _drag == Part.Head && frame >= 0 ? (x, frame) : null;
        e.Pointer.Capture(this); Move(x); e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var x = e.GetPosition(this).X;
        if (_drag != Part.None) { Move(x); e.Handled = true; return; }
        var frame = FrameAt(e.GetPosition(this));
        SetHot(frame >= 0 ? frame : NearTick(x));
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_drag == Part.None) SetHot(-1);
    }

    // Nearest tick within the snap distance, preferring the closer one when two are near.
    private int FrameAt(Point point)
    {
        if (_ticks.Count == 0 || point.Y < 4 || point.Y > 36) return -1;
        double left = X(_range.Start), right = X(_range.End);
        if (point.X < left || point.X > right || right <= left) return -1;
        return Math.Min(_ticks.Count - 1, (int)((point.X - left) / (right - left) * _ticks.Count));
    }

    private int NearTick(double x)
    {
        var best = -1; var distance = Snap;
        for (var i = 0; i < _ticks.Count; i++)
            if (Math.Abs(X(_ticks[i]) - x) is var d && d <= distance) { best = i; distance = d; }
        return best;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag == Part.None) return;
        Move(e.GetPosition(this).X);
        var trimmed = _drag != Part.Head; _drag = Part.None; e.Pointer.Capture(null);
        var frame = FrameAt(e.GetPosition(this));
        SetHot(IsPointerOver ? (frame >= 0 ? frame : NearTick(e.GetPosition(this).X)) : -1); InvalidateVisual();
        if (trimmed) RangeChanged?.Invoke(_range);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e) { base.OnPointerCaptureLost(e); _drag = Part.None; _pressedFrame = null; SetHot(-1); InvalidateVisual(); }

    private void Move(double x)
    {
        var time = Time(x);
        switch (_drag)
        {
            case Part.Start: _range = RecordingTimeRange.Normalize(time, _range.End, _duration); Seek(_range.Start); break;
            case Part.End: _range = RecordingTimeRange.Normalize(_range.Start, time, _duration); Seek(_range.End); break;
            case Part.Head:
                // Contact sheet: the playhead lightly snaps onto a sample so the video shows exactly that frame.
                if (_pressedFrame is { } pressed && Math.Abs(pressed.X - x) > 3) _pressedFrame = null;
                SetHot(_pressedFrame?.Index ?? NearTick(x));
                if (_hot >= 0) time = _ticks[_hot];
                Seek(time < _range.Start ? _range.Start : time > _range.End ? _range.End : time); break;
        }
        InvalidateVisual();
        if (_drag is Part.Start or Part.End) RangeChanged?.Invoke(_range);
    }

    private void Seek(TimeSpan time) { _position = time; Seeked?.Invoke(time); }
}
