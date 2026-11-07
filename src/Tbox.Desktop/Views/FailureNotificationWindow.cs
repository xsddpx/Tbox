using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Tbox.Desktop.Views;

internal sealed class FailureNotificationWindow : Window
{
    private static FailureNotificationWindow? _current;
    internal static bool IsShowing => _current?.IsVisible == true;
    public static void Dismiss() => _current?.Close();
    public static void ShowFailure(DesktopController controller, string message)
    {
        _current?.Close();
        var window = new FailureNotificationWindow(controller, message);
        _current = window;
        window.Closed += (_, _) => { if (ReferenceEquals(_current, window)) _current = null; };
        window.Show();
    }
    private FailureNotificationWindow(DesktopController controller, string message)
    {
        Ui.Configure(this, "Tbox 提示", 360, 180);
        ShowActivated = false; Topmost = true; ShowInTaskbar = false; CanResize = false;
        SystemDecorations = SystemDecorations.None;
        Content = Ui.Card(new StackPanel { Spacing = 12, Children =
        {
            Ui.Text("操作未完成", 16),
            new ScrollViewer { Content = Ui.Text(message), MaxHeight = 72 },
            Ui.Row(Ui.Button("打开 Tbox", () => { Close(); controller.ShowMain(); }), Ui.Button("关闭", Close))
        }});
        Opened += (_, _) =>
        {
            if (Screens.ScreenFromWindow(controller.Main) is { } screen)
            {
                Width = Math.Min(360, screen.WorkingArea.Width / screen.Scaling - 24);
                Position = new PixelPoint(screen.WorkingArea.Right - (int)((Width + 12) * screen.Scaling),
                    screen.WorkingArea.Bottom - (int)((Height + 12) * screen.Scaling));
            }
            DispatcherTimer.RunOnce(Close, TimeSpan.FromSeconds(7));
        };
    }
}
