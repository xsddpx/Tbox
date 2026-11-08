using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace Tbox.Desktop.Views;

internal enum IconKind { Back, Settings, Folder, Capture, Pin, Ocr, Chevron }

internal sealed class LineIcon : Control
{
    private readonly Geometry _geometry;
    private readonly bool _accent;
    public LineIcon(IconKind kind, bool accent = false)
    {
        Width = Height = 20; _accent = accent;
        _geometry = Geometry.Parse(kind switch
        {
            IconKind.Back => "M19 12 H5 M11 6 L5 12 L11 18",
            IconKind.Settings => "M4 7 L20 7 M4 17 L20 17 M8 4 L8 10 M16 14 L16 20",
            IconKind.Folder => "M3 6 L9 6 L11 8 L21 8 L21 20 L3 20 Z M3 11 L21 11",
            IconKind.Capture => "M3 8 L3 3 L8 3 M16 3 L21 3 L21 8 M21 16 L21 21 L16 21 M8 21 L3 21 L3 16 M8 8 L16 8 L16 16 L8 16 Z",
            IconKind.Pin => "M8 3 L16 3 L15 10 L19 14 L13 14 L12 21 L11 14 L5 14 L9 10 Z",
            IconKind.Ocr => "M3 8 V3 H8 M16 3 H21 V8 M21 16 V21 H16 M8 21 H3 V16 M7 9 H17 M7 13 H17 M7 17 H13",
            _ => "M9 5 L16 12 L9 19"
        });
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }
    public override void Render(DrawingContext context)
    {
        var colors = DesignTokens.Colors(ActualThemeVariant == ThemeVariant.Dark);
        using (context.PushTransform(Matrix.CreateScale(Bounds.Width / 24, Bounds.Height / 24)))
            context.DrawGeometry(null, new Pen(Brush.Parse(_accent ? colors.Accent : colors.SecondaryText), 1.7,
                lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), _geometry);
    }
}
