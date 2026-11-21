using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Tbox.Core.ModelServices;
using Tbox.Core.Translation;
using Tbox.Desktop.Translation;

namespace Tbox.Desktop.Views;

internal sealed class TranslationPage : UserControl, IDisposable
{
    private readonly Func<Task<ModelConfiguration>> _configuration;
    private readonly Func<Task<string?>> _readClipboard;
    private readonly Func<string, Task> _writeClipboard;
    internal TranslationSession Session { get; }
    internal event Action<string>? LanguageChanged;
    internal TextBox Source { get; } = Editor("翻译原文", "输入或主动粘贴需要翻译的文字");
    internal TextBox Result { get; } = Editor("翻译译文", "译文将在这里逐步显示");
    internal ComboBox Target { get; } = new() { MinWidth = 100, FontSize = 13 };
    internal ComboBox Model { get; } = new() { Width = 176, MinWidth = 0, PlaceholderText = "选择模型", FontSize = 13 };
    private readonly Button _translate, _copy, _paste, _clear;
    private readonly TextBlock _status = Ui.Description("");
    private readonly TextBlock _destination = Ui.Description("尚未选择模型，不会发送文字。");
    private readonly TextBlock _count = Ui.Description("0 / 20000");
    private readonly TextBlock _resultLabel = Ui.Description("待翻译");
    private readonly Grid _editors = new() { ColumnDefinitions = new("*,*"), RowDefinitions = new("Auto,Auto"), ColumnSpacing = 12, RowSpacing = 12 };
    private readonly Control _sourcePane, _resultPane;
    private readonly Control _unconfigured;
    private readonly Grid _footer;
    private ModelConfiguration _snapshot = new();
    private ModelReference? _resolved;
    private ModelReference? _chosen;
    private bool _loading, _disposed;
    private long _openRevision;

    internal void ApplyLanguage(string id)
    {
        var language = TranslationPolicy.Languages.FirstOrDefault(item => item.Id == id) ?? TranslationPolicy.Languages[0];
        if (Equals(language, Target.SelectedItem)) return;
        var wasLoading = _loading;
        _loading = true;
        try { Target.SelectedItem = language; }
        finally { _loading = wasLoading; }
        Session.Invalidate();
    }

    public TranslationPage(ITextModelService capability, Func<Task<ModelConfiguration>> configuration, Action configure,
        Func<Task<string?>>? readClipboard = null, Func<string, Task>? writeClipboard = null)
    {
        _configuration = configuration;
        _readClipboard = readClipboard ?? (() => (TopLevel.GetTopLevel(this)?.Clipboard ?? throw new InvalidOperationException()).TryGetTextAsync());
        _writeClipboard = writeClipboard ?? (text => (TopLevel.GetTopLevel(this)?.Clipboard ?? throw new InvalidOperationException()).SetTextAsync(text));
        Session = new(capability); Session.Changed += Refresh;
        Source.MaxLength = TranslationPolicy.MaximumInputLength; Result.IsReadOnly = true;
        Target.ItemsSource = TranslationPolicy.Languages; Target.SelectedItem = TranslationPolicy.Languages[0];
        Target.ItemTemplate = new FuncDataTemplate<TranslationLanguage>((item, _) => Ui.Text(item?.Name ?? "", 13));
        Model.ItemTemplate = new FuncDataTemplate<ModelOption>((item, _) => ModelLabel(item?.MenuLabel));
        Model.SelectionBoxItemTemplate = new FuncDataTemplate<ModelOption>((item, _) => ModelLabel(item?.Reference.ModelId));
        AutomationProperties.SetName(Target, "翻译目标语言"); AutomationProperties.SetName(Model, "翻译使用的模型");
        _translate = Ui.Primary("翻译", () => Run(TranslateOrStopAsync));
        _copy = Ui.Button("复制译文", () => Run(CopyAsync));
        _paste = Ui.Link("粘贴文字", () => Run(PasteAsync));
        _clear = Ui.Link("清空", () => Source.Text = "", accent: false);
        _sourcePane = Pane("原文", Source, _count);
        _resultPane = Pane("译文", Result, _resultLabel);
        _editors.Children.Add(_sourcePane); _editors.Children.Add(Ui.Column(_resultPane, 1));
        _unconfigured = Ui.Row(Ui.Description("请先在模型服务中添加连接和模型。"), Ui.Link("配置模型", configure));
        var heading = new Grid
        {
            ColumnDefinitions = new("Auto,*,Auto"), ColumnSpacing = 12,
            Children =
            {
                Ui.IconChip(IconKind.Translate, ToolTone.Violet, 40, 20),
                Ui.Column(new TextBlock
                {
                    Text = "翻译", FontSize = DesignTokens.PageTitle, FontWeight = FontWeight.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center
                }, 1),
                Ui.Column(Model, 2)
            }
        };
        Model.VerticalAlignment = VerticalAlignment.Center;
        var languages = new Grid { ColumnDefinitions = new("Auto,Auto,*,Auto,Auto"), ColumnSpacing = 8, Children =
        {
            Ui.Row(Ui.Description("自动识别"), new LineIcon(IconKind.Arrow) { Width = 14, Height = 14 }), Ui.Column(Target, 1),
            Ui.Column(_paste, 3), Ui.Column(_clear, 4)
        } };
        _destination.VerticalAlignment = VerticalAlignment.Center;
        _destination.FontSize = DesignTokens.Caption;
        var actions = Ui.Row(_copy, _translate);
        actions.HorizontalAlignment = HorizontalAlignment.Right;
        _footer = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8, RowDefinitions = new("Auto,Auto"), RowSpacing = 5,
            Children = { _destination, Ui.Column(actions, 1) } };
        Content = new StackPanel { Spacing = 10, Children =
        {
            heading, _unconfigured, languages, _editors, _footer, _status
        } };
        Source.PropertyChanged += (_, args) => { if (args.Property == TextBox.TextProperty && !_loading) Session.Invalidate(); };
        Target.SelectionChanged += (_, _) =>
        {
            if (_loading) return;
            Session.Invalidate();
            if (Target.SelectedItem is TranslationLanguage language) LanguageChanged?.Invoke(language.Id);
        };
        Model.SelectionChanged += (_, _) =>
        {
            if (_loading) return;
            _chosen = (Model.SelectedItem as ModelOption)?.Reference;
            ResolveSelection();
            Session.Invalidate();
        };
        SizeChanged += (_, _) => Reflow();
        Refresh();
    }

    internal Task OpenAsync() => RefreshModelsAsync();

    private async Task<bool> RefreshModelsAsync()
    {
        var revision = ++_openRevision;
        try
        {
            var configuration = await _configuration();
            if (_disposed || revision != _openRevision) return false;
            _snapshot = configuration;
            var order = ModelPolicy.OrderedModels(configuration);
            var repeated = order.GroupBy(model => model.ModelId).Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet();
            var connections = configuration.Connections.ToDictionary(connection => connection.Id);
            var options = order.Select(model => new ModelOption(model, repeated.Contains(model.ModelId)
                ? $"{model.ModelId} · {connections[model.ConnectionId].Name}" : model.ModelId)).ToArray();
            var old = _resolved;
            _loading = true;
            Model.ItemsSource = options;
            Model.SelectedItem = _chosen is null ? options.FirstOrDefault() : options.FirstOrDefault(o => o.Reference == _chosen);
            Model.PlaceholderText = _chosen is not null && Model.SelectedItem is null ? "重新选择模型" : "选择模型";
            _loading = false;
            ResolveSelection();
            if (old != _resolved) Session.Invalidate();
            Refresh();
            return true;
        }
        catch (ModelServiceException error)
        {
            if (!_disposed && revision == _openRevision) { _resolved = null; Refresh(); ShowStatus(error.Message); }
            return false;
        }
    }

    internal async Task TranslateOrStopAsync()
    {
        if (Session.IsRunning) { Session.Stop(); return; }
        // Re-read configuration before a request, but never silently replace an explicitly chosen model.
        if (!await RefreshModelsAsync() || _resolved is null) return;
        var source = Source.Text ?? "";
        var language = (Target.SelectedItem as TranslationLanguage)?.Id ?? "zh-Hans";
        await Session.TranslateAsync(source, language, _resolved);
    }
    internal async Task PasteAsync()
    {
        var before = Source.Text;
        var text = await _readClipboard();
        if (_disposed || Session.IsRunning || before != Source.Text) return;
        if (string.IsNullOrEmpty(text)) { ShowStatus("剪贴板中没有文字。"); return; }
        if (text.Length > TranslationPolicy.MaximumInputLength) { ShowStatus("剪贴板文字超过 20000 字符，请分段粘贴。"); return; }
        Source.Text = text; Source.Focus();
    }
    internal async Task CopyAsync()
    {
        if (Session.IsRunning || Session.Text.Length == 0) return;
        var revision = Session.Revision;
        var complete = Session.State == TranslationState.Completed;
        await _writeClipboard(Session.Text);
        if (!_disposed && Session.Revision == revision) ShowStatus(complete ? "已复制译文。" : "已复制部分译文，内容尚未完整完成。");
    }

    private void ResolveSelection()
    {
        _unconfigured.IsVisible = Model.ItemCount == 0;
        if (Model.SelectedItem is not ModelOption option)
        {
            _resolved = null;
            _destination.Text = _chosen is not null && Model.ItemCount > 0 ? "所选模型已移除，请重新选择。" : "";
            return;
        }
        try
        {
            var (connection, model) = ModelPolicy.Resolve(_snapshot, option.Reference);
            _resolved = model;
            _destination.Text = $"原文将发送至 {new Uri(connection.BaseUrl).Authority}，可能产生 API 费用";
        }
        catch (ModelServiceException error) { _resolved = null; _destination.Text = error.Message; }
    }
    private void Refresh()
    {
        if (_disposed) return;
        Result.Text = Session.Text;
        ShowStatus(Session.State == TranslationState.Idle ? "" : Session.Status);
        _count.Text = $"{Source.Text?.Length ?? 0} / 20000"; _count.FontSize = 10.5;
        _resultLabel.Text = Session.State switch
        { TranslationState.Running => "生成中", TranslationState.Completed => "已完成", TranslationState.Partial or TranslationState.Stopped when Session.Text.Length > 0 => "未完成", _ => "待翻译" };
        _resultLabel.FontSize = 11;
        Source.IsReadOnly = Session.IsRunning;
        Target.IsEnabled = _paste.IsEnabled = _clear.IsEnabled = !Session.IsRunning;
        Model.IsEnabled = !Session.IsRunning && Model.ItemCount > 0;
        _copy.IsEnabled = !Session.IsRunning && Session.Text.Length > 0;
        _translate.IsEnabled = Session.IsRunning || (_resolved is not null && !string.IsNullOrWhiteSpace(Source.Text));
        _translate.Content = Session.IsRunning ? "停止生成" : Session.Text.Length > 0 ? "重新翻译" : "翻译";
        AutomationProperties.SetName(_translate, Session.IsRunning ? "停止翻译" : "开始翻译");
    }
    private void Reflow()
    {
        var narrow = Bounds.Width < 480;
        _editors.ColumnDefinitions[1].Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(_resultPane, narrow ? 0 : 1); Grid.SetRow(_resultPane, narrow ? 1 : 0);
        Source.Height = Result.Height = narrow ? 140 : 190;
        var actions = _footer.Children[1];
        Grid.SetColumn(actions, narrow ? 0 : 1); Grid.SetRow(actions, narrow ? 1 : 0);
    }
    private void ShowStatus(string message) { _status.Text = message; _status.IsVisible = message.Length > 0; }
    private async void Run(Func<Task> operation)
    {
        try { await operation(); }
        catch (ArgumentException error) { ShowStatus(error.Message); }
        catch { ShowStatus("操作未完成，请检查模型配置或系统剪贴板后重试。"); }
    }
    internal void Leave() { ++_openRevision; Session.Stop(); }
    public void Dispose() { Leave(); _disposed = true; Session.Dispose(); Source.Text = ""; Result.Text = ""; }
    private static TextBox Editor(string name, string placeholder)
    {
        var editor = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontSize = 13, LineHeight = 23, Padding = new Thickness(10), MinWidth = 0, Height = 190, BorderThickness = new Thickness(0), Watermark = placeholder };
        AutomationProperties.SetName(editor, name); return editor;
    }
    private static Border Pane(string label, Control editor, Control trailing)
    {
        var title = new Grid { ColumnDefinitions = new("*,Auto"), Margin = new Thickness(10, 8, 10, 0), Children = { Ui.Description(label), Ui.Column(trailing, 1) } };
        return new Border { BorderBrush = Brush.Parse("#E7E3EF"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), ClipToBounds = true,
            Child = new StackPanel { Children = { title, editor } } };
    }
    private static TextBlock ModelLabel(string? text) => new() { Text = text, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis };
    private sealed record ModelOption(ModelReference Reference, string MenuLabel) { public override string ToString() => Reference.ModelId; }
}
