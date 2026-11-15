using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Tbox.Core.ModelServices;

namespace Tbox.Desktop.Views;

internal sealed class ModelPickerWindow : Window
{
    private readonly TextBox _search = new() { Watermark = "搜索模型", MaxLength = 200 };
    private readonly StackPanel _list = new();
    private readonly ScrollViewer _scroll;
    private readonly TextBlock _empty = Ui.Caption("没有匹配的模型");
    private readonly TextBlock _status = Ui.Caption("");
    private readonly Button _toggleMatches;
    private readonly Button _apply;
    private readonly (string Id, CheckBox Check)[] _options;
    private bool _updatingSelection;

    public ModelPickerWindow(IEnumerable<string> catalog, IReadOnlyCollection<string> current)
    {
        Ui.Configure(this, "选择模型", 470, 510);
        MinWidth = 360;
        MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var checkedIds = current.ToHashSet(StringComparer.Ordinal);
        // Preserve the current order for the default and the connection test. Manual IDs remain selectable.
        _options = current.Concat(catalog).Distinct(StringComparer.Ordinal)
            .Select(id =>
            {
                var check = new CheckBox
                {
                    Content = new TextBlock { Text = id, TextTrimming = TextTrimming.CharacterEllipsis },
                    IsChecked = checkedIds.Contains(id),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    MinWidth = 0
                };
                AutomationProperties.SetName(check, id);
                ToolTip.SetTip(check, id);
                _list.Children.Add(check);
                return (id, check);
            }).ToArray();

        _toggleMatches = Ui.Link("全选结果", ToggleMatches);
        _apply = Ui.Primary("更新列表", () => Close(SelectedModelIds()));
        _scroll = new ScrollViewer { Content = _list, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        AutomationProperties.SetName(_search, "搜索模型");
        DockPanel.SetDock(_toggleMatches, Dock.Right);
        var searchRow = new DockPanel { LastChildFill = true, Children = { _toggleMatches, _search } };

        var header = new StackPanel
        {
            Spacing = 10,
            Margin = new Thickness(0, 0, 0, 10),
            Children = { Ui.Description("勾选要显示在此连接中的模型。"), searchRow }
        };
        var actions = Ui.Row(Ui.Button("取消", () => Close()), _apply);
        DockPanel.SetDock(actions, Dock.Right);
        _status.VerticalAlignment = VerticalAlignment.Center;
        _status.TextWrapping = TextWrapping.Wrap;
        var footer = new DockPanel { Margin = new Thickness(0, 12, 0, 0), Children = { actions, _status } };
        _empty.Margin = new Thickness(0, 12, 0, 0);
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        Content = new DockPanel
        {
            Margin = new Thickness(20),
            Children = { header, footer, new Grid { Children = { _scroll, _empty } } }
        };

        _search.TextChanged += (_, _) => Filter();
        foreach (var (_, check) in _options)
            check.IsCheckedChanged += (_, _) => { if (!_updatingSelection) RefreshActions(); };
        Opened += (_, _) => _search.Focus();
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Close();
        };
        Filter();
    }

    private string[] SelectedModelIds() => _options.Where(option => option.Check.IsChecked == true).Select(option => option.Id).ToArray();

    private void Filter()
    {
        var query = _search.Text?.Trim() ?? "";
        foreach (var (id, check) in _options)
            check.IsVisible = id.Contains(query, StringComparison.OrdinalIgnoreCase);
        _scroll.Offset = default;
        RefreshActions();
    }

    private void ToggleMatches()
    {
        var matches = _options.Where(option => option.Check.IsVisible).ToArray();
        var select = matches.Any(option => option.Check.IsChecked != true);
        _updatingSelection = true;
        foreach (var (_, check) in matches)
            check.IsChecked = select;
        _updatingSelection = false;
        RefreshActions();
    }

    private void RefreshActions()
    {
        var hasMatches = _options.Any(option => option.Check.IsVisible);
        var allMatchesChecked = hasMatches && _options.All(option => !option.Check.IsVisible || option.Check.IsChecked == true);
        _empty.IsVisible = !hasMatches;
        _toggleMatches.IsEnabled = hasMatches;
        _toggleMatches.Content = allMatchesChecked ? "取消全选" : "全选结果";
        AutomationProperties.SetName(_toggleMatches, (string)_toggleMatches.Content);

        var count = _options.Count(option => option.Check.IsChecked == true);
        _apply.IsEnabled = count <= ModelPolicy.MaxModelsPerConnection;
        _status.Text = _apply.IsEnabled ? $"已选 {count} 个" : $"已选 {count} 个，最多 {ModelPolicy.MaxModelsPerConnection} 个";
    }
}
