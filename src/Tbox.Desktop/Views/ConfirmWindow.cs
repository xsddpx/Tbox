using Avalonia;
using Avalonia.Controls;

namespace Tbox.Desktop.Views;

internal sealed class ConfirmWindow : Window
{
    public ConfirmWindow(string message, string confirm)
    {
        Ui.Configure(this, "Tbox", 400, 160);
        CanResize = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = new StackPanel { Margin = new Thickness(24), Spacing = 20, Children =
        {
            Ui.Text(message), Ui.Row(Ui.Button("返回", () => Close(false)), Ui.Button(confirm, () => Close(true)))
        } };
    }
}
