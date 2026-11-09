using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Tbox.Desktop.Views;

// Corner notice (spec ④), shown only while the main window is closed. Success disappears after 4 s;
// a failure stays until closed and offers "打开 Tbox". The icon colour matches the tool; failures are red.
internal sealed class FailureNotificationWindow : Window
{
    private static FailureNotificationWindow? _current;
    internal static bool IsShowing => _current?.IsVisible == true;
    public static void Dismiss() => _current?.Close();
    public static void ShowFailure(DesktopController controller, string message) => Show(controller, "操作没有完成", message, failure: true);
    public static void ShowInfo(DesktopController controller, string message) => Show(controller, message.TrimEnd('。'), null, failure: false);
    private static void Show(DesktopController controller, string title, string? message, bool failure)
    {
        _current?.Close();
        var window = new FailureNotificationWindow(controller, title, message, failure);
        _current = window;
        window.Closed += (_, _) => { if (ReferenceEquals(_current, window)) _current = null; };
        window.Show();
    }
    private FailureNotificationWindow(DesktopController controller, string title, string? message, bool failure)
    {
        Ui.Configure(this, "Tbox 提示", 384, 120);
        ShowActivated = false; Topmost = true; ShowInTaskbar = false; CanResize = false;
        SystemDecorations = SystemDecorations.None;
        SizeToContent = SizeToContent.Height;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;

        var text = new StackPanel { Children =
        {
            new TextBlock { Text = title, FontSize = DesignTokens.Body, LineHeight = 20, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap,
                Foreground = Brush.Parse("#1A1B22") }
        } };
        if (message is not null)
        {
            var detail = Ui.Description(message); detail.FontSize = 13; detail.LineHeight = 19;
            text.Children.Add(new ScrollViewer { Content = detail, MaxHeight = 76 });
        }
        if (failure)
        {
            var open = Ui.Link("打开 Tbox", () => { Close(); controller.ShowMain(); });
            open.HorizontalAlignment = HorizontalAlignment.Right; open.Margin = new Thickness(0, 6, 0, 0);
            text.Children.Add(open);
        }
        var close = Ui.IconButton(IconKind.Close, "关闭", Close);
        close.MinWidth = close.MinHeight = 24; close.Width = close.Height = 24; close.Padding = new Thickness(4);
        close.VerticalAlignment = VerticalAlignment.Top;
        var layout = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 12, Children =
        {
            Ui.IconChip(failure ? IconKind.Warn : IconKind.Check, failure ? ToolTone.Red : ToolTone.Violet),
            Ui.Column(text, 1), Ui.Column(close, 2)
        } };
        ((Control)layout.Children[0]).VerticalAlignment = VerticalAlignment.Top;
        // A transparent margin leaves room for the card's shadow inside the borderless window.
        Content = new Border
        {
            Margin = new Thickness(12), Padding = new Thickness(14, 14, 14, 12), CornerRadius = new CornerRadius(DesignTokens.PanelRadius),
            Background = Brushes.White, BoxShadow = BoxShadows.Parse("0 0 0 1 #0F101220, 0 8 24 0 #24101220"), Child = layout
        };
        Opened += (_, _) =>
        {
            if ((Screens.ScreenFromWindow(this) ?? Screens.Primary) is { } screen)
            {
                Width = Math.Min(384, screen.WorkingArea.Width / screen.Scaling - 8);
                UpdateLayout();
                Position = new PixelPoint(screen.WorkingArea.Right - (int)((Width + 4) * screen.Scaling),
                    screen.WorkingArea.Bottom - (int)((Bounds.Height + 4) * screen.Scaling));
            }
            if (!failure) DispatcherTimer.RunOnce(Close, TimeSpan.FromSeconds(4));
        };
    }
}
