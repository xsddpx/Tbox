using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Tbox.Desktop.Views;

// Confirmation dialog (spec ④): the title asks the question, the text states the consequence.
// "返回" on the left, the action itself on the right; destructive actions are red. Esc = 返回.
internal sealed class ConfirmWindow : Window
{
    public ConfirmWindow(string title, string confirm, string? detail = null, bool destructive = false)
    {
        Ui.Configure(this, "Tbox", 400, 160);
        CanResize = false; WindowStartupLocation = WindowStartupLocation.CenterOwner; SizeToContent = SizeToContent.Height;
        ShowInTaskbar = false; Background = Brushes.White;
        var action = Ui.Primary(confirm, () => Close(true));
        if (destructive)
        {
            action.Classes.Remove("accent");
            foreach (var (state, color) in new[] { ("", "#D9363E"), ("PointerOver", "#C42F37"), ("Pressed", "#AE2830") })
            {
                action.Resources["ButtonBackground" + state] = Brush.Parse(color);
                action.Resources["ButtonForeground" + state] = Brushes.White;
                action.Resources["ButtonBorderBrush" + state] = Brush.Parse(color);
            }
        }
        var actions = Ui.Row(Ui.Button("返回", () => Close(false)), action);
        actions.HorizontalAlignment = HorizontalAlignment.Right; actions.Margin = new Thickness(0, 18, 0, 0);
        var body = new StackPanel { Margin = new Thickness(20, 20, 20, 16), Children =
        {
            new TextBlock { Text = title, FontSize = DesignTokens.SectionTitle, LineHeight = 22, FontWeight = FontWeight.SemiBold,
                TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#1A1B22") }
        } };
        if (detail is not null)
        {
            var text = Ui.Description(detail); text.FontSize = 13; text.LineHeight = 20; text.Margin = new Thickness(0, 6, 0, 0);
            body.Children.Add(text);
        }
        body.Children.Add(actions);
        Content = body;
        Opened += (_, _) => action.Focus();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(false); e.Handled = true; } };
    }
}
