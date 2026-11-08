using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Tbox.Desktop.Views;

// Short notice for actions started from the tray panel or a hotkey while no Tbox window is open.
internal sealed class FailureNotificationWindow : Window
{
    private static FailureNotificationWindow? _current;
    internal static bool IsShowing => _current?.IsVisible == true;
    public static void Dismiss() => _current?.Close();
    public static void ShowFailure(DesktopController controller, string message) => Show(controller, "操作未完成", message, 7);
    public static void ShowInfo(DesktopController controller, string message) => Show(controller, "Tbox", message, 4);
    private static void Show(DesktopController controller, string title, string message, int seconds)
    {
        _current?.Close();
        var window = new FailureNotificationWindow(controller, title, message, seconds);
        _current = window;
        window.Closed += (_, _) => { if (ReferenceEquals(_current, window)) _current = null; };
        window.Show();
    }
    private FailureNotificationWindow(DesktopController controller, string title, string message, int seconds)
    {
        Ui.Configure(this, "Tbox 提示", 360, 180);
        ShowActivated = false; Topmost = true; ShowInTaskbar = false; CanResize = false;
        SystemDecorations = SystemDecorations.None;
        SizeToContent = SizeToContent.Height;
        Content = Ui.Card(new StackPanel { Spacing = 12, Children =
        {
            Ui.Text(title, 16),
            new ScrollViewer { Content = Ui.Text(message), MaxHeight = 72 },
            Ui.Row(Ui.Button("打开 Tbox", () => { Close(); controller.ShowPanel(); }), Ui.Button("关闭", Close))
        }});
        Opened += (_, _) =>
        {
            if ((Screens.ScreenFromWindow(this) ?? Screens.Primary) is { } screen)
            {
                Width = Math.Min(360, screen.WorkingArea.Width / screen.Scaling - 24);
                Position = new PixelPoint(screen.WorkingArea.Right - (int)((Width + 12) * screen.Scaling),
                    screen.WorkingArea.Bottom - (int)((Bounds.Height + 12) * screen.Scaling));
            }
            DispatcherTimer.RunOnce(Close, TimeSpan.FromSeconds(seconds));
        };
    }
}
