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
    private readonly Action _configure;
    private readonly Func<Task<string?>> _readClipboard;
    private readonly Func<string, Task> _writeClipboard;
    internal TranslationSession Session { get; }
    internal TextBox Source { get; } = Editor("翻译原文", "输入或主动粘贴需要翻译的文字");
    internal TextBox Result { get; } = Editor("翻译译文", "译文将在这里逐步显示");
    internal ComboBox Target { get; } = new() { MinWidth = 100, FontSize = 13 };
    internal ComboBox Model { get; } = new() { HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 0, FontSize = 12 };
    private readonly Button _translate, _copy, _paste, _clear;
    private readonly TextBlock _status = Ui.Description("");
    private readonly TextBlock _destination = Ui.Description("尚未选择模型，不会发送文字。");
    private readonly TextBlock _count = Ui.Description("0 / 20000");
    private readonly TextBlock _resultLabel = Ui.Description("待翻译");
    private readonly Grid _editors = new() { ColumnDefinitions = new("*,*"), RowDefinitions = new("Auto,Auto"), ColumnSpacing = 12, RowSpacing = 12 };
    private readonly Control _sourcePane, _resultPane;
    private readonly Control _unconfigured;
    private ModelConfiguration _snapshot = new();
    private ModelReference? _resolved;
    private bool _loading, _disposed;
    private long _openRevision;

    public TranslationPage(ITextModelService capability, Func<Task<ModelConfiguration>> configuration, Action configure,
        Func<Task<string?>>? readClipboard = null, Func<string, Task>? writeClipboard = null)
    {
        _configuration = configuration; _configure = configure;
        _readClipboard = readClipboard ?? (() => (TopLevel.GetTopLevel(this)?.Clipboard ?? throw new InvalidOperationException()).TryGetTextAsync());
        _writeClipboard = writeClipboard ?? (text => (TopLevel.GetTopLevel(this)?.Clipboard ?? throw new InvalidOperationException()).SetTextAsync(text));
        Session = new(capability); Session.Changed += Refresh;
        Source.MaxLength = TranslationPolicy.MaximumInputLength; Result.IsReadOnly = true;
        Target.ItemsSource = TranslationPolicy.Languages; Target.SelectedItem = TranslationPolicy.Languages[0];
        Target.ItemTemplate = new FuncDataTemplate<TranslationLanguage>((item, _) => Ui.Text(item?.Name ?? "", 13));
        Model.ItemTemplate = new FuncDataTemplate<ModelOption>((item, _) => new TextBlock { Text = item?.Label, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis });
        AutomationProperties.SetName(Target, "翻译目标语言"); AutomationProperties.SetName(Model, "翻译使用的模型");
        _translate = Ui.Primary("翻译", () => Run(TranslateOrStopAsync));
        _copy = Ui.Button("复制译文", () => Run(CopyAsync));
        _paste = Ui.Link("粘贴文字", () => Run(PasteAsync));
        _clear = Ui.Link("清空", () => Source.Text = "", accent: false);
        _sourcePane = Pane("原文", Source, _count);
        _resultPane = Pane("译文", Result, _resultLabel);
        _editors.Children.Add(_sourcePane); _editors.Children.Add(Ui.Column(_resultPane, 1));
        var settings = Ui.Button("配置模型", _configure);
        _unconfigured = Ui.Note("请先在模型服务中配置连接并选择默认模型，或在下拉框中指定已有模型。不会自动上传任何内容。");
        var languages = new Grid { ColumnDefinitions = new("Auto,Auto,*,Auto,Auto"), ColumnSpacing = 8, Children =
        {
            Ui.Row(Ui.Description("自动识别"), new LineIcon(IconKind.Arrow) { Width = 14, Height = 14 }), Ui.Column(Target, 1),
            Ui.Column(_paste, 3), Ui.Column(_clear, 4)
        } };
        var models = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8, Children = { Model, Ui.Column(settings, 1) } };
        Content = new StackPanel { Spacing = 10, Margin = new Thickness(0, 16, 0, 0), Children =
        {
            models, _unconfigured, languages, _editors, _destination, _status,
            new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 8, Children = { Ui.Column(_copy, 1), Ui.Column(_translate, 2) } }
        } };
        Source.PropertyChanged += (_, args) => { if (args.Property == TextBox.TextProperty && !_loading) Session.Invalidate(); };
        Target.SelectionChanged += (_, _) => { if (!_loading) Session.Invalidate(); };
        Model.SelectionChanged += (_, _) => { if (!_loading) { ResolveSelection(); Session.Invalidate(); } };
        SizeChanged += (_, _) => Reflow();
        Refresh();
    }

    internal async Task OpenAsync()
    {
        var revision = ++_openRevision;
        try
        {
            var configuration = await _configuration();
            if (_disposed || revision != _openRevision) return;
            var chosen = (Model.SelectedItem as ModelOption)?.Reference;
            _snapshot = configuration;
            var options = new List<ModelOption> { new(null, "跟随全局默认" + (configuration.DefaultModel is { } d ? " · " + d.ModelId : " · 未配置")) };
            options.AddRange(configuration.Connections.SelectMany(c => c.Models.Select(id => new ModelOption(new(c.Id, id), c.Name + " / " + id))));
            if (chosen is not null && options.All(o => o.Reference != chosen)) options.Add(new(chosen, "已失效 · " + chosen.ModelId));
            var old = _resolved;
            _loading = true;
            Model.ItemsSource = options; Model.SelectedItem = options.First(o => o.Reference == chosen);
            _loading = false;
            ResolveSelection();
            if (old != _resolved) Session.Invalidate();
            Refresh();
        }
        catch (ModelServiceException error) { if (!_disposed && revision == _openRevision) { _resolved = null; Refresh(); _status.Text = error.Message; } }
    }

    internal async Task TranslateOrStopAsync()
    {
        if (Session.IsRunning) { Session.Stop(); return; }
        // Re-read configuration before a request, but never silently replace an explicitly chosen model.
        await OpenAsync();
        if (_resolved is null) { _status.Text = "请选择可用模型，或点击“配置模型”完成设置。"; return; }
        var source = Source.Text ?? "";
        var language = (Target.SelectedItem as TranslationLanguage)?.Id ?? "zh-Hans";
        await Session.TranslateAsync(source, language, _resolved);
    }
    internal async Task PasteAsync()
    {
        var before = Source.Text;
        var text = await _readClipboard();
        if (_disposed || Session.IsRunning || before != Source.Text) return;
        if (string.IsNullOrEmpty(text)) { _status.Text = "剪贴板中没有文字。"; return; }
        if (text.Length > TranslationPolicy.MaximumInputLength) { _status.Text = "剪贴板文字超过 20000 字符，请分段粘贴。"; return; }
        Source.Text = text; Source.Focus();
    }
    internal async Task CopyAsync()
    {
        if (Session.IsRunning || Session.Text.Length == 0) return;
        var revision = Session.Revision;
        var complete = Session.State == TranslationState.Completed;
        await _writeClipboard(Session.Text);
        if (!_disposed && Session.Revision == revision) _status.Text = complete ? "已复制译文。" : "已复制部分译文，内容尚未完整完成。";
    }

    private void ResolveSelection()
    {
        try
        {
            var (connection, model) = ModelPolicy.Resolve(_snapshot, (Model.SelectedItem as ModelOption)?.Reference);
            _resolved = model;
            _destination.Text = $"仅发送原文至 {new Uri(connection.BaseUrl).Authority} · 可能产生 API 费用，不发送截图";
            _unconfigured.IsVisible = false;
        }
        catch (ModelServiceException) { _resolved = null; _destination.Text = "尚未选择可用模型，不会发送文字。"; _unconfigured.IsVisible = true; }
    }
    private void Refresh()
    {
        if (_disposed) return;
        Result.Text = Session.Text; _status.Text = Session.Status;
        _count.Text = $"{Source.Text?.Length ?? 0} / 20000"; _count.FontSize = 10.5;
        _resultLabel.Text = Session.State switch
        { TranslationState.Running => "生成中", TranslationState.Completed => "已完成", TranslationState.Partial or TranslationState.Stopped when Session.Text.Length > 0 => "未完成", _ => "待翻译" };
        _resultLabel.FontSize = 11;
        Source.IsReadOnly = Session.IsRunning;
        Target.IsEnabled = Model.IsEnabled = _paste.IsEnabled = _clear.IsEnabled = !Session.IsRunning;
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
    }
    private async void Run(Func<Task> operation)
    {
        try { await operation(); }
        catch (ArgumentException error) { _status.Text = error.Message; }
        catch { _status.Text = "操作未完成，请检查模型配置或系统剪贴板后重试。"; }
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
    private sealed record ModelOption(ModelReference? Reference, string Label) { public override string ToString() => Label; }
}
