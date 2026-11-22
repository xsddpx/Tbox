using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Tbox.Desktop.Views;

// Each tool leads with its current task or state, plus the control that acts on it.
internal sealed class ToolBanner : Border
{
    private readonly Grid _layout;
    private readonly Control? _action;
    internal TextBlock Title { get; }
    internal TextBlock Detail { get; }

    public ToolBanner(IconKind icon, string title, string detail, Control? action = null, Control? status = null)
    {
        Title = new TextBlock { Text = title, FontSize = DesignTokens.PageTitle, FontWeight = FontWeight.SemiBold,
            Foreground = Brush.Parse("#161B22"), TextWrapping = TextWrapping.Wrap };
        Detail = Ui.Description(detail);
        Detail.TextWrapping = TextWrapping.Wrap;
        Detail.MaxLines = 3; Detail.TextTrimming = TextTrimming.CharacterEllipsis;
        var copy = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        if (status is not null) copy.Children.Add(status);
        copy.Children.Add(Title); copy.Children.Add(Detail);
        var chip = Ui.IconChip(icon, ToolTone.Grey, 44, 24);
        chip.VerticalAlignment = VerticalAlignment.Center;
        chip.Background = Brush.Parse("#DFE6EF");
        _layout = new Grid { Margin = new Thickness(18, 16), ColumnDefinitions = new("Auto,*,Auto"),
            RowDefinitions = new("Auto,Auto"), ColumnSpacing = 12, VerticalAlignment = VerticalAlignment.Center,
            Children = { chip, Ui.Column(copy, 1) } };
        _action = action;
        if (action is not null)
        {
            action.VerticalAlignment = VerticalAlignment.Center;
            _layout.Children.Add(Ui.Column(action, 2));
        }
        CornerRadius = new(14); ClipToBounds = true; MinHeight = 112;
        Background = Brush.Parse("#F0F3F7");
        Child = new Panel { Children = { new Rings { IsHitTestVisible = false }, _layout } };
    }

    protected override Size MeasureOverride(Size available)
    {
        if (_action is not null)
        {
            _action.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var stacked = available.Width < _action.DesiredSize.Width + 264;
            Grid.SetColumn(_action, stacked ? 1 : 2); Grid.SetRow(_action, stacked ? 1 : 0);
            _action.HorizontalAlignment = stacked ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            _layout.RowSpacing = stacked ? 10 : 0;
        }
        return base.MeasureOverride(available);
    }

    private sealed class Rings : Control
    {
        private static readonly IBrush Tint = Brush.Parse("#5069A0B9");
        private static readonly IPen Edge = new Pen(Brush.Parse("#6091A5BB"), 1);
        public override void Render(DrawingContext context)
        {
            var center = new Point(Bounds.Width * .83, -24);
            using var opacity = context.PushOpacity(.17);
            context.DrawEllipse(Tint, null, center, 114, 114);
            context.DrawEllipse(null, Edge, center, 126, 126);
            context.DrawEllipse(null, Edge, center, 144, 144);
        }
    }
}
