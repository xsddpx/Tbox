using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Tbox.Core;
using Tbox.Desktop.Platform;

namespace Tbox.Desktop.Views;

internal sealed class FolderJumpBarWindow : Window
{
    internal const double CompactHeight = 112;
    private const double RowHeight = 42;
    private static readonly IBrush Selected = Brush.Parse("#F0EBFF");
    private static readonly IBrush Outline = Brush.Parse("#DCD1FF");
    private readonly DesktopController _controller;
    private readonly IFolderNavigation _navigation;
    private readonly Func<string, Shortcut?, Task> _jump;
    private readonly Func<Task<bool>> _returnToDialog;
    private readonly Action _layoutChanged;
    private readonly TextBox _search = new() { Watermark = "搜索文件夹名或路径", FontSize = 13, MinHeight = 30, MaxLength = 2048,
        Padding = new Thickness(6, 3), VerticalContentAlignment = VerticalAlignment.Center };
    private readonly Button _favorites;
    private readonly Button _recent;
    private readonly TextBlock _heading = Ui.Text("", 12);
    private readonly TextBlock _hint = Ui.Text("↑ ↓ 选择    Enter 跳转    Esc 返回", 11);
    private readonly TextBlock _failure = Ui.Text("", 12);
    private readonly StackPanel _rows = new();
    private readonly List<(Button Jump, Button Star, Border Row)> _buttons = [];
    private IReadOnlyList<string> _opened = [];
    private IReadOnlyList<string> _matches = [];
    private IReadOnlyList<string> _displayed = [];
    private CancellationTokenSource? _searchWork;
    private bool _loaded;
    private int _selected;
    private View _view;

    private enum View { Opened, Favorites, Recent, Search }

    internal double DesiredBarHeight => CompactHeight + Math.Max(0, Math.Min(_displayed.Count, 5) - 1) * RowHeight +
        (_hint.IsVisible ? 20 : 0) + (_failure.IsVisible ? 22 : 0);
    internal string? RecommendedPath => _opened.FirstOrDefault();

    internal FolderJumpBarWindow(DesktopController controller, IFolderNavigation navigation,
        Func<string, Shortcut?, Task> jump, Func<Task<bool>> returnToDialog, Action layoutChanged)
    {
        _controller = controller;
        _navigation = navigation;
        _jump = jump;
        _returnToDialog = returnToDialog;
        _layoutChanged = layoutChanged;
        Title = "Tbox 文件夹跳转";
        ShowActivated = false;
        ShowInTaskbar = false;
        Topmost = true;
        CanResize = false;
        SystemDecorations = SystemDecorations.None;
        Background = Brushes.White;
        var brandBitmap = Ui.Brand();
        var brand = new Image
        {
            Width = 22, Height = 22, Source = brandBitmap, IsHitTestVisible = false,
            VerticalAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetName(brand, "Tbox");
        AutomationProperties.SetName(_search, _search.Watermark);
        _favorites = ViewButton("☆ 收藏", View.Favorites);
        _recent = ViewButton("◷ 最近", View.Recent);
        var header = new Grid { ColumnDefinitions = new("Auto,*,Auto,Auto"), ColumnSpacing = 6,
            Children = { brand, Ui.Column(_search, 1), Ui.Column(_favorites, 2), Ui.Column(_recent, 3) } };
        var scroller = new ScrollViewer { Content = _rows, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _heading.Margin = new Thickness(6, 4, 0, 1);
        _heading.Foreground = Brush.Parse(DesignTokens.Colors(dark: false).SecondaryText);
        _hint.Margin = new Thickness(6, 2, 0, 0);
        _hint.Foreground = _heading.Foreground;
        _failure.Margin = new Thickness(6, 2, 0, 0);
        _failure.Foreground = Brush.Parse(DesignTokens.Error);
        _failure.IsVisible = false;
        Content = new Border
        {
            Padding = new Thickness(7), BorderBrush = Outline, BorderThickness = new Thickness(1),
            Child = new Grid { RowDefinitions = new("Auto,Auto,*,Auto,Auto"),
                Children = { header, Row(_heading, 1), Row(scroller, 2), Row(_hint, 3), Row(_failure, 4) } }
        };
        AddHandler(KeyDownEvent, HandleKeys, RoutingStrategies.Tunnel);
        _search.TextChanged += (_, _) => _ = UpdateSearchAsync();
        Closed += (_, _) => { CancelSearch(); brandBitmap.Dispose(); };
        Render();
    }

    private static Control Row(Control child, int index) { Grid.SetRow(child, index); return child; }

    private Button ViewButton(string label, View view)
    {
        var button = Ui.Link(label, () => SetView(_view == view ? View.Opened : view), accent: false);
        button.MinHeight = 30;
        return button;
    }

    internal void SetOpenFolders(IReadOnlyList<string> paths)
    {
        if (_loaded && _opened.SequenceEqual(paths, FolderJumpList.PathComparer)) return;
        _loaded = true;
        _opened = paths;
        if (_view == View.Opened) Render();
    }

    internal void Reset() => SetView(View.Opened);

    internal void ShowFailure(string? message)
    {
        _failure.Text = message ?? "文件夹跳转未完成。";
        _failure.IsVisible = true;
        _layoutChanged();
    }

    private void SetView(View view)
    {
        CancelSearch();
        _search.Text = "";
        _view = view;
        _selected = 0;
        _failure.IsVisible = false;
        Render();
    }

    private void CancelSearch()
    {
        var work = _searchWork;
        _searchWork = null;
        work?.Cancel();
    }

    private async Task UpdateSearchAsync()
    {
        CancelSearch();
        var query = _search.Text?.Trim() ?? "";
        if (query.Length == 0)
        {
            if (_view == View.Search) Reset();
            return;
        }
        _view = View.Search;
        _selected = 0;
        _failure.IsVisible = false;
        var known = _opened.Concat(_controller.Settings.FavoriteFolders ?? []).Concat(_controller.Settings.RecentFolderJumps ?? []);
        _matches = FolderJumpList.Search(known, query);
        using var work = new CancellationTokenSource();
        _searchWork = work;
        var token = work.Token;
        Render();
        try
        {
            await Task.Delay(180, token);
            var absolute = await Task.Run(() => Path.IsPathFullyQualified(query) && Directory.Exists(query), token);
            token.ThrowIfCancellationRequested();
            if (absolute) known = known.Prepend(query);
            _matches = FolderJumpList.Search(known, query);
            var indexed = await _navigation.SearchFoldersAsync(query, token);
            token.ThrowIfCancellationRequested();
            _matches = FolderJumpList.Search(known.Concat(indexed), query);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { AppLog.Write("Folder bar search: " + error.GetType().Name); }
        finally
        {
            if (_searchWork == work)
            {
                _searchWork = null;
                Render();
            }
        }
    }

    private void Render()
    {
        _displayed = _view switch
        {
            View.Favorites => _controller.Settings.FavoriteFolders ?? [],
            View.Recent => _controller.Settings.RecentFolderJumps ?? [],
            View.Search => _matches,
            _ => FolderJumpList.Recommend(_opened, [])
        };
        _heading.Text = _view switch
        {
            View.Favorites => "收藏文件夹",
            View.Recent => "最近跳转",
            View.Search => "搜索结果",
            _ => "推荐 · 已打开的文件夹"
        };
        _favorites.Classes.Set("accent", _view == View.Favorites);
        _recent.Classes.Set("accent", _view == View.Recent);
        _hint.IsVisible = _view != View.Opened || _displayed.Count > 1;
        _rows.Children.Clear();
        _buttons.Clear();
        for (var index = 0; index < _displayed.Count; index++)
            AddResult(_displayed[index], _view == View.Opened && index == 0);
        if (_displayed.Count == 0)
        {
            var empty = _view switch
            {
                View.Favorites => "暂无收藏文件夹",
                View.Recent => "暂无最近跳转",
                View.Search => _searchWork is null ? "没有匹配的文件夹" : "正在搜索文件夹…",
                _ => _loaded ? "暂无已打开的文件夹" : "正在读取已打开的文件夹…"
            };
            var message = Ui.Text(empty, 12);
            message.Margin = new Thickness(6, 7, 6, 0);
            _rows.Children.Add(message);
        }
        _selected = Math.Clamp(_selected, 0, Math.Max(0, _buttons.Count - 1));
        HighlightSelection();
        _layoutChanged();
    }

    private void AddResult(string path, bool showShortcut)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        var label = new TextBlock { Text = string.IsNullOrEmpty(name) ? path : name, FontSize = 13, FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis };
        var directory = new TextBlock { Text = path, FontSize = 11, Foreground = _heading.Foreground,
            TextTrimming = TextTrimming.CharacterEllipsis };
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { label, directory } };
        var content = new Grid { ColumnDefinitions = new("Auto,*,Auto"), ColumnSpacing = 8,
            Children = { new LineIcon(IconKind.Folder, brush: Ui.LongYellow), Ui.Column(stack, 1) } };
        if (showShortcut)
        {
            var key = Ui.Key(string.Join("+", ShortcutLabels.Caps(HotkeyAction.Folder)));
            content.Children.Add(Ui.Column(key, 2));
        }
        var jump = Ui.Link(path, () => _controller.Run(() => _jump(path, null)), path, accent: false);
        jump.Content = content;
        jump.HorizontalAlignment = HorizontalAlignment.Stretch;
        jump.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        jump.MinHeight = RowHeight - 2;
        var favorite = (_controller.Settings.FavoriteFolders ?? []).Contains(path, FolderJumpList.PathComparer);
        var index = _buttons.Count;
        var star = Ui.Link(favorite ? "★" : "☆", () => _controller.Run(async () =>
        {
            await _controller.ToggleFavoriteFolderAsync(path);
            if (!IsVisible) return;
            Render();
            if (_buttons.Count == 0) _favorites.Focus();
            else _buttons[Math.Min(index, _buttons.Count - 1)].Star.Focus();
        }), favorite ? "取消收藏 " + path : "收藏 " + path, accent: favorite);
        star.MinWidth = 29;
        star.MinHeight = 29;
        var row = new Border { CornerRadius = new CornerRadius(6), Height = RowHeight, BorderThickness = new Thickness(1),
            Child = new Grid { ColumnDefinitions = new("*,Auto"), Children = { jump, Ui.Column(star, 1) } } };
        _buttons.Add((jump, star, row));
        _rows.Children.Add(row);
    }

    private void HighlightSelection()
    {
        for (var index = 0; index < _buttons.Count; index++)
        {
            _buttons[index].Row.Background = index == _selected ? Selected : Brushes.Transparent;
            _buttons[index].Row.BorderBrush = index == _selected ? Outline : Brushes.Transparent;
        }
    }

    private void HandleKeys(object? sender, KeyEventArgs args)
    {
        if (args.KeyModifiers != KeyModifiers.None) return;
        if (args.Key == Key.Escape)
        {
            args.Handled = true;
            _controller.Run(async () => { Reset(); await _returnToDialog(); });
        }
        else if ((args.Key == Key.Up || args.Key == Key.Down) && _buttons.Count > 0)
        {
            args.Handled = true;
            var focus = _buttons.FindIndex(item => item.Jump.IsFocused || item.Star.IsFocused);
            var current = focus >= 0 ? focus : _selected;
            _selected = (current + (args.Key == Key.Down ? 1 : -1) + _buttons.Count) % _buttons.Count;
            HighlightSelection();
            _buttons[_selected].Row.BringIntoView();
            if (!_search.IsFocused) _buttons[_selected].Jump.Focus();
        }
        else if (args.Key == Key.Enter && _search.IsFocused && _displayed.Count > 0)
        {
            args.Handled = true;
            var path = _displayed[_selected];
            _controller.Run(() => _jump(path, null));
        }
    }
}
