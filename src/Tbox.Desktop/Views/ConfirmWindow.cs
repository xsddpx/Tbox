using Avalonia;
using Avalonia.Controls;

namespace Tbox.Desktop.Views;

internal sealed class ConfirmWindow : Window
{
    public ConfirmWindow(string message, string confirm)
    {
        Ui.Configure(this, "Tbox", 400, 160);
        CanResize = false; WindowStartupLocation = WindowStartupLocation.CenterOwner; SizeToContent = SizeToContent.Height;
        var actions = Ui.Row(Ui.Button("返回", () => Close(false)), Ui.Primary(confirm, () => Close(true)));
        actions.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right;
        Content = new StackPanel { Margin = new Thickness(20, 18, 20, 16), Spacing = 18, Children = { Ui.Text(message), actions } };
    }
}
