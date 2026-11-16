using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Tbox.Core.ModelServices;

namespace Tbox.Desktop.Views;

internal sealed class OcrModelWindow : Window
{
    internal sealed record Destination(ModelReference Model, Uri Endpoint, string Label);

    public OcrModelWindow(ModelConfiguration configuration)
    {
        Ui.Configure(this, "AI 识别", 480, 220);
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SizeToContent = SizeToContent.Height;

        var models = new ComboBox
        {
            PlaceholderText = "选择支持图片输入的模型",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = configuration.Connections.SelectMany(c => c.Models.Select(id => new Destination(new(c.Id, id),
                ModelPolicy.Endpoint(c, "chat/completions"), c.Name + " / " + id))).ToArray(),
            ItemTemplate = new FuncDataTemplate<Destination>((item, _) =>
                new TextBlock { Text = item?.Label, TextTrimming = TextTrimming.CharacterEllipsis })
        };
        AutomationProperties.SetName(models, "AI 识别模型");
        var endpoint = Ui.Description("");
        var send = Ui.Primary("发送并识别", () =>
        {
            if (models.SelectedItem is Destination selected) Close(selected);
        });
        send.IsEnabled = false;
        models.SelectionChanged += (_, _) =>
        {
            var selected = models.SelectedItem as Destination;
            send.IsEnabled = selected is not null;
            endpoint.Text = selected is null ? "" : $"发送至 {selected.Endpoint.AbsoluteUri}";
        };
        var actions = Ui.Row(Ui.Button("取消", () => Close()), send);
        actions.HorizontalAlignment = HorizontalAlignment.Right;
        Content = new StackPanel
        {
            Margin = new Thickness(20), Spacing = 10,
            Children =
            {
                Ui.Description("将发送标注前的原图。Tbox 中添加的马赛克或涂抹不会遮挡发送内容。可能产生 API 费用，识别成功后替换当前文字。"),
                models, endpoint, actions
            }
        };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
    }
}
