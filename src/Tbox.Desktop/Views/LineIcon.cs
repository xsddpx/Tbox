using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace Tbox.Desktop.Views;

internal enum IconKind
{
    Back, Settings, Folder, Capture, Pin, Ocr, Chevron, Record, Long, Eye, EyeOff, Jump, Gear, Copy,
    Rectangle, Ellipse, Line, Polyline, Arrow, Number, Pen, Highlighter, Mosaic, Text, Eraser,
    Undo, Redo, Save, Close, Check, FullScreen, Play, Pause, Stop, Minus, Plus, More, Update, Help, Keyboard, Theme, Startup, Bell, Info, Warn, Sliders, Image, Speaker, Mute
}

// 24-unit line icons drawn with one stroke weight. Filled glyphs (record dot, play, stop) set Fill.
internal sealed class LineIcon : Control
{
    private readonly Geometry _geometry;
    private readonly bool _accent;
    private readonly bool _filled;
    private IBrush? _brush;
    public LineIcon(IconKind kind, bool accent = false, IBrush? brush = null)
    {
        Width = Height = 20; _accent = accent; _brush = brush;
        _filled = kind is IconKind.Play or IconKind.Stop or IconKind.Pause;
        _geometry = Geometry.Parse(kind switch
        {
            IconKind.Back => "M19 12 H5 M11 6 L5 12 L11 18",
            IconKind.Settings => "M4 7 L20 7 M4 17 L20 17 M8 4 L8 10 M16 14 L16 20",
            IconKind.Folder => "M3 6 L9 6 L11 8 L21 8 L21 20 L3 20 Z M3 11 L21 11",
            IconKind.Capture => "M3 8 L3 3 L8 3 M16 3 L21 3 L21 8 M21 16 L21 21 L16 21 M8 21 L3 21 L3 16 M8 8 L16 8 L16 16 L8 16 Z",
            IconKind.Pin => "M8 3 L16 3 L15 10 L19 14 L13 14 L12 21 L11 14 L5 14 L9 10 Z",
            IconKind.Ocr => "M3 8 V3 H8 M16 3 H21 V8 M21 16 V21 H16 M8 21 H3 V16 M7 9 H17 M7 13 H17 M7 17 H13",
            IconKind.Record => "M12 4 A8 8 0 1 1 11.99 4 Z M12 8.5 A3.5 3.5 0 1 1 11.99 8.5 Z M12 10 A2 2 0 1 1 11.99 10 Z M12 11.2 A0.8 0.8 0 1 1 11.99 11.2 Z",
            IconKind.Long => "M8 3 H16 Q18 3 18 5 V19 Q18 21 16 21 H8 Q6 21 6 19 V5 Q6 3 8 3 Z M12 7.5 V16.5 M9.5 10 L12 7.5 L14.5 10 M9.5 14 L12 16.5 L14.5 14",
            IconKind.Eye => "M2.5 12 Q12 2 21.5 12 Q12 22 2.5 12 Z M12 9.2 A2.8 2.8 0 1 1 11.99 9.2 Z",
            IconKind.EyeOff => "M3 3 L21 21 M10 5.8 Q17 4.8 21.5 12 Q20.6 13.6 19 15.2 M6.4 6.9 Q4 8.6 2.5 12 Q8 21 16.2 17.4 M9.9 9.9 A2.8 2.8 0 0 0 14 14",
            IconKind.Jump => "M3 6 L9 6 L11 8 L21 8 L21 20 L3 20 Z M9 14 H15 M13 12 L15 14 L13 16",
            IconKind.Gear => "M12 9 A3 3 0 1 1 11.99 9 Z M12 2.5 V5 M12 19 V21.5 M2.5 12 H5 M19 12 H21.5 M5.3 5.3 L7.1 7.1 M16.9 16.9 L18.7 18.7 M5.3 18.7 L7.1 16.9 M16.9 7.1 L18.7 5.3",
            IconKind.Copy => "M9 9 H19 V19 H9 Z M15 9 V5 H5 V15 H9",
            IconKind.Rectangle => "M4 6 H20 V18 H4 Z",
            IconKind.Ellipse => "M12 5 A8 7 0 1 1 11.99 5 Z",
            IconKind.Line => "M5 19 L19 5",
            IconKind.Polyline => "M4 18 L9 9 L14 15 L20 6",
            IconKind.Arrow => "M5 19 L19 5 M10 5 H19 V14",
            IconKind.Number => "M12 3.5 A8.5 8.5 0 1 1 11.99 3.5 Z M10 9 L12.5 7.5 V16.5 M10 16.5 H15",
            IconKind.Pen => "M4 20 L5 15 L15.5 4.5 Q17 3 18.5 4.5 L19.5 5.5 Q21 7 19.5 8.5 L9 19 Z M14 6 L18 10",
            IconKind.Highlighter => "M5 20 H19 M8 16 L6 18 M8 16 L16 8 L18.5 10.5 L10.5 18.5 Z M14 6 L16 4 L20 8 L18 10",
            IconKind.Mosaic => "M4 4 H10 V10 H4 Z M14 4 H20 V10 H14 Z M4 14 H10 V20 H4 Z M14 14 H20 V20 H14 Z M10 10 H14 V14 H10 Z",
            IconKind.Text => "M5 6 V4 H19 V6 M12 4 V20 M9 20 H15",
            IconKind.Eraser => "M8 20 H20 M4.5 15.5 L13.5 6.5 Q15 5 16.5 6.5 L19.5 9.5 Q21 11 19.5 12.5 L12 20 H8.5 L4.5 16 Z M9 11 L15.5 17.5",
            IconKind.Undo => "M9 14 L4 9 L9 4 M4 9 H14 A6 6 0 0 1 14 21 H9",
            IconKind.Redo => "M15 14 L20 9 L15 4 M20 9 H10 A6 6 0 0 0 10 21 H15",
            IconKind.Save => "M12 4 V15 M7 10 L12 15 L17 10 M5 20 H19",
            IconKind.Close => "M6 6 L18 18 M18 6 L6 18",
            IconKind.Check => "M5 12.5 L10 17.5 L19 7",
            IconKind.FullScreen => "M4 9 V4 H9 M15 4 H20 V9 M20 15 V20 H15 M9 20 H4 V15",
            IconKind.Play => "M8 5 L19 12 L8 19 Z",
            IconKind.Pause => "M7 5 H10 V19 H7 Z M14 5 H17 V19 H14 Z",
            IconKind.Stop => "M6 6 H18 V18 H6 Z",
            IconKind.Minus => "M6 12 H18",
            IconKind.Plus => "M6 12 H18 M12 6 V18",
            IconKind.More => "M5 12 H5.1 M12 12 H12.1 M19 12 H19.1",
            IconKind.Update => "M20 12 A8 8 0 1 1 17.7 6.3 M20 4 V8 H16",
            IconKind.Help => "M12 3.5 A8.5 8.5 0 1 1 11.99 3.5 Z M9.5 9.5 Q9.5 7 12 7 Q14.5 7 14.5 9.3 Q14.5 11 12 12 V13.5 M12 16.5 V17",
            IconKind.Keyboard => "M3 7 H21 V17 H3 Z M7 10 H7.01 M11 10 H11.01 M15 10 H15.01 M18 10 H18.01 M8 14 H16",
            IconKind.Theme => "M12 3.5 A8.5 8.5 0 1 1 11.99 3.5 Z M12 3.5 V20.5 A8.5 8.5 0 0 0 12 3.5",
            IconKind.Startup => "M12 3 V11 M7.5 6.5 A7 7 0 1 0 16.5 6.5",
            IconKind.Bell => "M6 16 V11 A6 6 0 0 1 18 11 V16 L19.5 18 H4.5 Z M10 20.5 H14",
            IconKind.Info => "M12 3.5 A8.5 8.5 0 1 1 11.99 3.5 Z M12 11 V16.5 M12 7.5 V8",
            IconKind.Warn => "M12 3.5 L21.5 20 H2.5 Z M12 9.5 V14 M12 17 V17.5",
            IconKind.Sliders => "M4 7 H11 M15 7 H20 M13 4.5 V9.5 M4 17 H8 M12 17 H20 M10 14.5 V19.5",
            IconKind.Speaker => "M4 9.5 H8 L13 5 V19 L8 14.5 H4 Z M16.5 9 A4 4 0 0 1 16.5 15 M19 6.5 A7.5 7.5 0 0 1 19 17.5",
            IconKind.Mute => "M4 9.5 H8 L13 5 V19 L8 14.5 H4 Z M16.5 9.5 L21 14 M21 9.5 L16.5 14",
            IconKind.Image => "M4 5 H20 V19 H4 Z M4 16 L9 11 L13 15 L16 12 L20 16 M15.5 8.5 A1 1 0 1 1 15.49 8.5 Z",
            _ => "M9 5 L16 12 L9 19"
        });
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }
    // Lets a selected sidebar item tint its icon without rebuilding it.
    public IBrush? Stroke { get => _brush; set { _brush = value; InvalidateVisual(); } }
    public override void Render(DrawingContext context)
    {
        var colors = DesignTokens.Colors(ActualThemeVariant == ThemeVariant.Dark);
        var brush = _brush ?? Brush.Parse(_accent ? colors.Accent : colors.SecondaryText);
        using (context.PushTransform(Matrix.CreateScale(Bounds.Width / 24, Bounds.Height / 24)))
            context.DrawGeometry(_filled ? brush : null, new Pen(brush, 1.7, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), _geometry);
    }
}
