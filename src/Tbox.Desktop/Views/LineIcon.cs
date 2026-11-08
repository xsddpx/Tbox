using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace Tbox.Desktop.Views;

internal enum IconKind { Back, Settings, Folder, Capture, Pin, Ocr, Chevron, Record, Long, Eye, EyeOff, Jump, Gear, Copy }

internal sealed class LineIcon : Control
{
    private readonly Geometry _geometry;
    private readonly bool _accent;
    private readonly IBrush? _brush;
    public LineIcon(IconKind kind, bool accent = false, IBrush? brush = null)
    {
        Width = Height = 20; _accent = accent; _brush = brush;
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
            _ => "M9 5 L16 12 L9 19"
        });
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }
    public override void Render(DrawingContext context)
    {
        var colors = DesignTokens.Colors(ActualThemeVariant == ThemeVariant.Dark);
        var brush = _brush ?? Brush.Parse(_accent ? colors.Accent : colors.SecondaryText);
        using (context.PushTransform(Matrix.CreateScale(Bounds.Width / 24, Bounds.Height / 24)))
            context.DrawGeometry(null, new Pen(brush, 1.7, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), _geometry);
    }
}
