using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Tbox.Core.ModelServices;
using Tbox.Desktop.ModelServices;

namespace Tbox.Desktop.Views;

// Settings for the shared capability, not a chat/translation tool. The only generation here is a fixed smoke request.
internal sealed class ModelServicesPage : UserControl, IDisposable
{
    internal const string HelpText = "密钥保存在系统凭据库。保存不发送请求；测试会发送少量文字，可能产生费用。API 地址填写基础路径，当前仅支持 OpenAI 兼容 Chat Completions。";
    private static readonly IBrush Strong = Brush.Parse("#1A1B22");
    private static readonly IBrush Secondary = Brush.Parse("#5F6270");
    private static readonly IBrush Hint = Brush.Parse("#8C8F9B");
    private static readonly IBrush Hairline = Brush.Parse("#F1F0F5");
    private static readonly IBrush Error = Brush.Parse("#D9363E");
    private static readonly IBrush Ready = Brush.Parse("#1F9D61");

    // Only OpenAI-compatible presets may be offered until a native protocol is implemented.
    private sealed record Preset(string Name, string BaseUrl, string? DefaultModel, string Letter, string Background, string Foreground);
    private static readonly Preset[] Presets =
    [
        new("OpenAI", "https://api.openai.com/v1", null, "O", "#F0EFF5", "#1A1B22"),
        new("DeepSeek", "https://api.deepseek.com", "deepseek-chat", "D", "#E7F0FF", "#2F6BDB"),
        new("ZenMux", "https://zenmux.ai/api/v1", null, "Z", "#F0EFF5", "#1A1B22")
    ];
    private sealed record DefaultOption(Guid ConnectionId, string ConnectionName, string ModelId)
    {
        public override string ToString() => ConnectionName + " · " + ModelId;
    }

    private readonly ModelService _service;
    private readonly Func<Window> _owner;
    private readonly StackPanel _listView = new();
    private readonly StackPanel _editView = new();
    private readonly StackPanel _connectionRows = new();
    private readonly StackPanel _defaultGroup;
    private readonly TextBlock _connectionMeta = new() { FontSize = DesignTokens.Caption, Foreground = Hint, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _emptyConnections;
    private readonly Button _addButton;
    private readonly Button _helpButton;
    private readonly ComboBox _defaultModelBox = new() { MinWidth = 0, Width = 232, FontSize = 12.5, MinHeight = 32 };
    private readonly TextBlock _editStatus = StatusText();
    private bool _refreshingDefault;

    // Edit form
    internal TextBox ConnectionName { get; } = Field("连接名称");
    internal TextBox BaseUrl { get; } = Field("API 基础地址", "例如 https://api.example.com/v1");
    internal TextBox Secret { get; } = new() { PasswordChar = '●', Watermark = "输入新的 API 密钥", MinWidth = 0, MaxLength = 1280, Height = 32, FontSize = 13 };
    internal CheckBox NoKey { get; } = new() { Content = "此服务无需密钥", FontSize = 12.5 };
    internal TextBox ModelInput { get; } = Field("模型 ID", "输入模型 ID，例如 deepseek-chat");
    private readonly ComboBox _protocol = new() { ItemsSource = new[] { "OpenAI Chat Completions" }, SelectedIndex = 0, Height = 32, FontSize = 13, MinWidth = 0 };
    private readonly TextBlock _savedKey = new() { FontSize = 12.5, Foreground = Secondary, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _editTitle = new() { FontSize = DesignTokens.PageTitle, LineHeight = 26, FontWeight = FontWeight.SemiBold, Foreground = Strong };
    private readonly Border _tabs;
    private readonly Grid _presetGrid = new() { ColumnDefinitions = new ColumnDefinitions("*,*,*"), ColumnSpacing = 10, Margin = new Thickness(0, 12, 0, 0) };
    private readonly StackPanel _modelsHost = new();
    private readonly Border _modelsEmpty;
    private readonly Border _modelAddRow;
    private readonly Button _addModel;
    private readonly Button _fetch;
    private readonly Button _test;
    private readonly Button _save;
    private readonly Button _cancel;
    private readonly Button _delete;
    private readonly Button _stop;
    private readonly Button _replace;
    private readonly Button _deleteKey;
    private readonly Button _cancelReplace;
    private readonly StackPanel _keyEditor;
    private readonly Grid _savedKeyRow;
    private StackPanel _nameBlock = new();
    private StackPanel _addressBlock = new();
    private StackPanel _protocolBlock = new();
    private StackPanel _keyBlock = new();
    private StackPanel _modelSection = new();
    private Grid _fieldRow = new();
    private readonly Border _presetNote;
    private readonly List<string> _models = [];
    private readonly Dictionary<Guid, bool> _testStates = [];
    private CancellationTokenSource? _operation;
    private Guid _id = Guid.NewGuid();
    private ModelConnection? _saved;
    private bool _loading, _busy, _dirty, _editingKey = true, _customTab, _addingModel;
    private int _selectedPreset = -1;
    private string _statusMessage = "";

    internal string Status => _statusMessage;
    internal bool IsBusy => _busy;
    internal bool IsEditing => _editView.IsVisible;
    internal bool IsEditingKey => _editView.IsVisible && _editingKey;
    internal Guid ConnectionId => _id;
    internal IReadOnlyList<string> Models => _models;

    public ModelServicesPage(ModelService service, Func<Window> owner)
    {
        _service = service; _owner = owner;
        AutomationProperties.SetName(Secret, "新的 API 密钥");
        AutomationProperties.SetName(ModelInput, "模型 ID");
        AutomationProperties.SetName(_protocol, "API 协议");
        AutomationProperties.SetName(_defaultModelBox, "全局默认模型");
        _replace = Ui.Link("替换", () => Run(ReplaceKeyAsync));
        _deleteKey = Ui.Link("删除", () => Run(DeleteKeyAsync), "删除密钥", accent: false);
        _cancelReplace = Ui.Link("取消替换", CancelReplaceKey);
        _keyEditor = new StackPanel { Spacing = 6, Children =
        {
            new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8, Children = { Secret, Ui.Column(_cancelReplace, 1) } }
        } };
        _savedKeyRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 6, Children =
        {
            _savedKey, Ui.Column(_replace, 1), Ui.Column(_deleteKey, 2)
        } };
        _savedKey.VerticalAlignment = VerticalAlignment.Center;

        _addButton = Ui.Primary("添加连接", BeginAdd);
        _helpButton = Ui.IconButton(IconKind.Info, "模型服务说明", ShowHelp);
        ToolTip.SetTip(_helpButton, HelpText);
        _emptyConnections = BuildEmpty();
        _tabs = Ui.Segmented(["预设服务商", "自定义接口"], 0, OnTabChanged, out _, out _);
        _modelsEmpty = BuildModelsEmpty();
        _modelAddRow = BuildModelAddRow();
        _addModel = Ui.Button("添加模型", BeginAddModel);
        _addModel.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children =
        {
            new LineIcon(IconKind.Plus, brush: Secondary) { Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Center },
            new TextBlock { Text = "添加模型", VerticalAlignment = VerticalAlignment.Center }
        } };
        _fetch = Ui.Link("获取可用模型", () => Run(FetchModelsAsync));
        _test = Ui.Link("测试连接", () => Run(TestAsync));
        _save = Ui.Primary("保存连接", () => Run(SaveAsync));
        _cancel = Ui.Button("取消", () => Run(CancelAsync));
        _delete = Ui.Link("删除连接", () => Run(DeleteConnectionAsync), accent: false);
        _stop = Ui.Button("停止", () => _operation?.Cancel());
        _stop.IsVisible = false;
        _presetNote = new Border
        {
            IsVisible = false, Margin = new Thickness(0, 14, 0, 0), Padding = new Thickness(12, 10),
            CornerRadius = new CornerRadius(12), Background = Brush.Parse("#F7F6FB"),
            Child = new TextBlock { Text = "保存后可获取该服务商的模型；也可以手动添加。", FontSize = DesignTokens.Caption, Foreground = Secondary, TextWrapping = TextWrapping.Wrap }
        };

        _defaultModelBox.SelectionChanged += (_, _) => { if (!_refreshingDefault && !_loading) Run(SetDefaultAsync); };
        foreach (var box in new[] { ConnectionName, BaseUrl, Secret, ModelInput })
            box.PropertyChanged += (_, args) => { if (args.Property == TextBox.TextProperty) Changed(); };
        NoKey.IsCheckedChanged += (_, _) => { Changed(); RefreshControls(); };
        _protocol.SelectionChanged += (_, _) => Changed();
        ModelInput.KeyDown += (_, args) => { if (args.Key == Avalonia.Input.Key.Enter) { CommitModel(); args.Handled = true; } };

        _listView.Children.Add(BuildListHeader());
        _listView.Children.Add(new Border { Margin = new Thickness(0, 18, 0, 0), Child = Ui.Note("支持中转站、自部署服务，以及兼容 OpenAI Chat Completions 的接口。") });
        _listView.Children.Add(Ui.GroupHeader("连接", _connectionMeta));
        _listView.Children.Add(_connectionRows);
        _listView.Children.Add(_emptyConnections);
        _defaultGroup = BuildDefaultGroup();
        _listView.Children.Add(_defaultGroup);
        _listView.Children.Add(new TextBlock
        {
            Text = "API 密钥只保存在这台电脑上。删除连接后，引用它的功能会要求重新选择。",
            FontSize = 11.5, LineHeight = 16, Foreground = Hint, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 18, 0, 0)
        });

        _editView.Children.Add(BuildEditHeader());
        _editView.Children.Add(_tabs);
        _editView.Children.Add(_presetGrid);
        _editView.Children.Add(BuildFields());
        _editView.Children.Add(_presetNote);
        _editView.Children.Add(BuildModelSection());
        _editView.Children.Add(_editStatus);
        _editView.Children.Add(BuildFooter());

        Content = new Grid { Children = { _listView, _editView } };
        _editView.IsVisible = false;
        RefreshAll();
    }

    public async Task OpenAsync()
    {
        await _service.InitializeAsync();
        ShowList();
    }

    internal void BeginAdd()
    {
        if (_busy) return;
        Run(() => LoadAsync(null));
    }

    internal Task BeginAddAsync() => LoadAsync(null);

    internal void BeginEdit(Guid id) => Run(() => LoadAsync(id));

    internal Task BeginEditAsync(Guid id) => LoadAsync(id);

    internal Task FetchAsync() => FetchModelsAsync();

    internal void BeginReplaceKey() => ReplaceKeyAsync();

    internal void CancelReplace() => CancelReplaceKey();

    internal void SelectPreset(string name)
    {
        var index = Array.FindIndex(Presets, p => p.Name == name);
        if (index >= 0) SelectPreset(index);
    }

    internal void AddModel(string id)
    {
        id = id.Trim();
        if (id.Length == 0) return;
        if (!_models.Contains(id, StringComparer.Ordinal)) _models.Add(id);
        _dirty = true; RefreshModels(); RefreshControls();
    }

    internal async Task SaveAsync()
    {
        if (_busy) return;
        var draft = new ModelConnection(_id, ConnectionName.Text?.Trim() ?? "", BaseUrl.Text?.Trim().TrimEnd('/') ?? "", _models.ToArray())
        { Protocol = "openai-chat", RequiresKey = NoKey.IsChecked != true };
        var replacement = _editingKey && !string.IsNullOrEmpty(Secret.Text) ? Secret.Text : null;
        var configuration = _service.Configuration;
        var currentDefault = configuration.DefaultModel;
        var keepDefault = currentDefault?.ConnectionId == _id;
        var makeDefault = _models.Count > 0 && (keepDefault || currentDefault is null);
        var defaultModel = makeDefault
            ? keepDefault && _models.Contains(currentDefault!.ModelId, StringComparer.Ordinal) ? currentDefault.ModelId : _models[0]
            : null;
        await Busy(ct => _service.SaveConnectionAsync(draft, replacement, false, makeDefault, defaultModel, ct));
        _saved = _service.Configuration.Connections.FirstOrDefault(c => c.Id == _id);
        Secret.Text = ""; _editingKey = false; _dirty = false;
        if (_saved?.CredentialId is not null) _savedKey.Text = "••••••••  · 已保存";
        SetStatus("已保存");
        RefreshAll();
    }

    internal async Task TestAsync()
    {
        if (_dirty) throw new ModelServiceException(ModelFailure.Configuration, "请先保存连接再测试。");
        var connection = _editView.IsVisible ? _service.Configuration.Connections.FirstOrDefault(c => c.Id == _id) : _service.Configuration.Connections.FirstOrDefault();
        if (connection is null) throw new ModelServiceException(ModelFailure.Configuration, "请先添加并保存一个连接。");
        var model = connection.Models.FirstOrDefault();
        if (model is null) throw new ModelServiceException(ModelFailure.Configuration, "请先添加并保存至少一个模型 ID。");
        SetStatus("测试中…");
        try
        {
            await Busy(async ct =>
            {
                var input = new TextGenerationRequest([new("user", "Reply with OK.")]) { Model = new(connection.Id, model), MaxOutputTokens = 32 };
                var completed = false; var text = "";
                await foreach (var item in _service.GenerateAsync(input, ct))
                {
                    if (item.Text.Length > 0) text = (text + item.Text)[..Math.Min(200, text.Length + item.Text.Length)];
                    if (!item.IsComplete) continue;
                    if (item.FinishReason != "stop") throw new ModelServiceException(ModelFailure.Incomplete, "模型已结束，但未正常完成（可能达到输出上限或内容受限）。");
                    completed = true;
                    ToolTip.SetTip(_editStatus, item.Usage is { } usage ? $"输入 {usage.InputTokens?.ToString() ?? "未知"} / 输出 {usage.OutputTokens?.ToString() ?? "未知"} token" : "用量未返回");
                }
                if (!completed || string.IsNullOrWhiteSpace(text)) throw new ModelServiceException(ModelFailure.Incomplete, "未收到完整测试结果。");
                _testStates[connection.Id] = true;
                SetStatus("连接正常");
            }, testing: true);
        }
        catch (ModelServiceException e)
        {
            _testStates[connection.Id] = false;
            SetStatus(e.Message, error: true);
            throw;
        }
        finally { RefreshList(); }
    }

    private async Task LoadAsync(Guid? id)
    {
        if (_busy) return;
        await _service.InitializeAsync();
        var connection = id is { } wanted ? _service.Configuration.Connections.FirstOrDefault(c => c.Id == wanted) : null;
        if (id is not null && connection is null)
        {
            SetStatus("连接已不存在。", error: true); ShowList(); return;
        }
        await LoadDraftAsync(connection);
        _listView.IsVisible = false;
        _editView.IsVisible = true;
    }

    private async Task LoadDraftAsync(ModelConnection? connection)
    {
        _loading = true;
        try
        {
            _saved = connection;
            _id = connection?.Id ?? Guid.NewGuid();
            _models.Clear();
            _editingKey = connection?.CredentialId is null;
            Secret.Text = "";
            NoKey.IsChecked = connection is not null && !connection.RequiresKey;
            _customTab = false;
            _selectedPreset = -1;
            _addingModel = false;
            _dirty = false;
            ToolTip.SetTip(_editStatus, null);
            if (connection is null)
            {
                var preset = Presets.FirstOrDefault(p => p.Name == "DeepSeek") ?? Presets[0];
                _selectedPreset = Array.IndexOf(Presets, preset);
                ConnectionName.Text = preset.Name;
                BaseUrl.Text = preset.BaseUrl;
                if (preset.DefaultModel is not null) _models.Add(preset.DefaultModel);
                _savedKey.Text = "尚未保存密钥";
            }
            else
            {
                ConnectionName.Text = connection.Name;
                BaseUrl.Text = connection.BaseUrl;
                _models.AddRange(connection.Models);
                _selectedPreset = Array.FindIndex(Presets, p => SameAddress(p.BaseUrl, connection.BaseUrl));
                _savedKey.Text = connection.CredentialId is null ? "尚未保存密钥" : "••••••••  · 检查保存状态…";
            }
            _protocol.SelectedIndex = 0;
            RefreshAll();
            if (connection?.CredentialId is not null)
            {
                var id = _id;
                var present = await _service.HasCredentialAsync(connection);
                if (id == _id && _saved == connection) _savedKey.Text = present ? "••••••••  · 已保存" : "密钥缺失，请替换";
            }
        }
        finally
        {
            _loading = false;
            RefreshAll();
        }
    }

    private Control BuildListHeader()
    {
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children =
        {
            new TextBlock { Text = "模型服务", FontSize = DesignTokens.PageTitle, LineHeight = 26, FontWeight = FontWeight.SemiBold, Foreground = Strong },
            new TextBlock { Text = "统一管理连接与模型，供翻译等工具复用", FontSize = DesignTokens.Description, LineHeight = 18, Foreground = Secondary }
        } };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center,
            Children = { _helpButton, _addButton } };
        return new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 14, Children =
        {
            Ui.IconChip(IconKind.Model, ToolTone.Violet, 40, 20), Ui.Column(titles, 1), Ui.Column(actions, 2)
        } };
    }

    private StackPanel BuildDefaultGroup()
    {
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children =
        {
            new TextBlock { Text = "功能默认使用", FontSize = DesignTokens.Body, Foreground = Strong },
            new TextBlock { Text = "未单独指定时，翻译等工具使用这个模型", FontSize = DesignTokens.Caption, Foreground = Hint, Margin = new Thickness(0, 1, 0, 0) }
        } };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 14, MinHeight = 52, Children =
        {
            text, Ui.Column(_defaultModelBox, 1)
        } };
        return new StackPanel { Children = { Ui.GroupHeader("默认模型"), row } };
    }

    private Border BuildEmpty()
    {
        var add = Ui.Primary("添加连接", BeginAdd);
        var content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Children =
        {
            Ui.IconChip(IconKind.Model, ToolTone.Violet, 44, 22),
            new TextBlock { Text = "还没有添加连接", FontSize = 15, LineHeight = 22, FontWeight = FontWeight.SemiBold, Foreground = Strong,
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 0) },
            new TextBlock { Text = "添加后，翻译等工具就可以选用模型。", FontSize = DesignTokens.Description, Foreground = Hint,
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) },
            new Border { Margin = new Thickness(0, 16, 0, 0), HorizontalAlignment = HorizontalAlignment.Center, Child = add }
        } };
        return new Border { IsVisible = false, Margin = new Thickness(0, 14, 0, 0), Padding = new Thickness(28, 20), CornerRadius = new CornerRadius(14),
            BorderBrush = Brush.Parse("#EEEDF3"), BorderThickness = new Thickness(1), Background = Brush.Parse("#FEFEFF"), Child = content };
    }

    private Border BuildModelsEmpty() => new()
    {
        Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(12, 10), CornerRadius = new CornerRadius(12),
        BorderBrush = Brush.Parse("#E3E1EC"), BorderThickness = new Thickness(1), Background = Brush.Parse("#FEFEFF"),
        Child = new StackPanel { Children =
        {
            new TextBlock { Text = "还没有模型", FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = Secondary, HorizontalAlignment = HorizontalAlignment.Center },
            new TextBlock { Text = "保存连接后可获取模型，也可以手动添加", FontSize = DesignTokens.Caption, Foreground = Hint,
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0) }
        } }
    };

    private Border BuildModelAddRow()
    {
        var add = Ui.Button("添加", CommitModel);
        var cancel = Ui.Link("取消", () => { _addingModel = false; ModelInput.Text = ""; RefreshControls(); }, accent: false);
        return new Border { IsVisible = false, Margin = new Thickness(0, 8, 0, 0), Child = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 8,
            Children = { ModelInput, Ui.Column(add, 1), Ui.Column(cancel, 2) }
        } };
    }

    private Control BuildEditHeader()
    {
        var back = Ui.IconButton(IconKind.Back, "返回连接列表", () => Run(CancelAsync));
        back.Background = Brushes.White; back.BorderBrush = Brush.Parse("#0F101220"); back.BorderThickness = new Thickness(1);
        back.CornerRadius = new CornerRadius(DesignTokens.ControlRadius);
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children =
        {
            _editTitle,
            new TextBlock { Text = "连接中转站、自部署服务或其他兼容接口。", FontSize = DesignTokens.Description, LineHeight = 18, Foreground = Secondary }
        } };
        return new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 12, Children =
        {
            back, Ui.Column(titles, 1)
        } };
    }

    private Control BuildFields()
    {
        _nameBlock = FieldBlock("名称", ConnectionName);
        var addressLabel = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8, Children =
        {
            new TextBlock { Text = "API 地址", FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = Secondary },
            Ui.Column(_test, 1)
        } };
        _addressBlock = new StackPanel { Spacing = 6, Children = { addressLabel, BaseUrl } };
        _protocolBlock = FieldBlock("API 协议", _protocol);
        _keyBlock = FieldBlock("API 密钥", new StackPanel { Spacing = 6, Children = { _keyEditor, _savedKeyRow, NoKey } }, hint: "仅保存在本机");
        _keyEditor.IsVisible = false; _savedKeyRow.IsVisible = false;
        _fieldRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 14, Children = { _protocolBlock, Ui.Column(_keyBlock, 1) } };
        return new StackPanel { Spacing = 12, Margin = new Thickness(0, 14, 0, 0), Children = { _nameBlock, _addressBlock, _fieldRow } };
    }

    private Control BuildModelSection()
    {
        _modelSection = new StackPanel { Children =
        {
            Ui.GroupHeader("模型", _fetch),
            _modelsEmpty,
            new Border { Margin = new Thickness(0, 8, 0, 0), Child = _modelsHost },
            new Border { Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, Child = _addModel },
            _modelAddRow
        } };
        return _modelSection;
    }

    private Control BuildFooter()
    {
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right,
            Children = { _stop, _cancel, _save } };
        return new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 8, Margin = new Thickness(0, 14, 0, 0), Children =
        {
            _delete, Ui.Column(buttons, 1)
        } };
    }

    private static StackPanel FieldBlock(string label, Control input, string? hint = null)
    {
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8, Children =
        {
            new TextBlock { Text = label, FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = Secondary }
        } };
        if (hint is not null)
            head.Children.Add(Ui.Column(new TextBlock { Text = hint, FontSize = 11, Foreground = Hint, VerticalAlignment = VerticalAlignment.Center }, 1));
        return new StackPanel { Spacing = 6, Children = { head, input } };
    }

    private Control ConnectionRow(ModelConnection connection)
    {
        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children =
        {
            new TextBlock { Text = connection.Name, FontSize = DesignTokens.Body, Foreground = Strong, TextTrimming = TextTrimming.CharacterEllipsis },
            new TextBlock { Text = ProtocolLabel(connection) + " · " + HostOf(connection.BaseUrl), FontSize = DesignTokens.Caption, Foreground = Hint,
                TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) }
        } };
        var (statusText, statusColor) = StatusOf(connection);
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, VerticalAlignment = VerticalAlignment.Center, Children =
        {
            Ui.Status(statusText, statusColor),
            new TextBlock { Text = connection.Models.Length == 0 ? "未添加模型" : connection.Models.Length + " 个模型", FontSize = DesignTokens.Caption, Foreground = Hint, VerticalAlignment = VerticalAlignment.Center },
            new LineIcon(IconKind.Chevron, brush: Hint) { Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center }
        } };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 12, Margin = new Thickness(0, 10), Children =
        {
            ProviderChip(connection), Ui.Column(names, 1), Ui.Column(right, 2)
        } };
        var button = new SurfaceButton(connection.Name, () => BeginEdit(connection.Id)) { Content = grid, MinHeight = 60 };
        button.PointerEntered += (_, _) => button.Background = Brush.Parse("#F7F6FB");
        button.PointerExited += (_, _) => button.Background = Brushes.Transparent;
        return button;
    }

    private static Border ProviderChip(ModelConnection connection)
    {
        var preset = Presets.FirstOrDefault(p => SameAddress(p.BaseUrl, connection.BaseUrl));
        var letter = preset?.Letter ?? (connection.Name.Length > 0 ? connection.Name[..1].ToUpperInvariant() : "?");
        return new Border
        {
            Width = 32, Height = 32, CornerRadius = new CornerRadius(9), VerticalAlignment = VerticalAlignment.Center,
            Background = Brush.Parse(preset?.Background ?? "#F2EDFF"),
            Child = new TextBlock { Text = letter, FontSize = 13, FontWeight = FontWeight.SemiBold,
                Foreground = Brush.Parse(preset?.Foreground ?? "#6D4AE8"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        };
    }

    private (string Text, string Color) StatusOf(ModelConnection connection)
    {
        if (_testStates.TryGetValue(connection.Id, out var ready)) return ready ? ("可用", DesignTokens.Ready) : ("失败", DesignTokens.Error);
        if (connection.RequiresKey && connection.CredentialId is null) return ("缺少密钥", DesignTokens.Attention);
        return ("未验证", DesignTokens.Attention);
    }

    private void ShowList()
    {
        _editView.IsVisible = false;
        _listView.IsVisible = true;
        _selectedPreset = -1;
        RefreshList();
    }

    private void RefreshAll()
    {
        _editTitle.Text = _saved is null ? "添加连接" : "编辑连接";
        _tabs.IsVisible = _saved is null;
        RefreshPresets();
        RefreshModels();
        RefreshKeyState();
        RefreshControls();
    }

    private void RefreshList()
    {
        var configuration = _service.Configuration;
        _connectionRows.Children.Clear();
        for (var i = 0; i < configuration.Connections.Length; i++)
        {
            if (i > 0) _connectionRows.Children.Add(new Border { Height = 1, Background = Hairline });
            _connectionRows.Children.Add(ConnectionRow(configuration.Connections[i]));
        }
        _emptyConnections.IsVisible = configuration.Connections.Length == 0;
        _connectionRows.IsVisible = configuration.Connections.Length > 0;
        _defaultGroup.IsVisible = configuration.Connections.Length > 0;
        _addButton.IsVisible = configuration.Connections.Length > 0;
        _connectionMeta.Text = configuration.Connections.Length + " 个连接 · " + configuration.Connections.Sum(c => c.Models.Length) + " 个模型";
        RefreshDefault(configuration);
    }

    private void RefreshDefault(ModelConfiguration configuration)
    {
        _refreshingDefault = true;
        var options = configuration.Connections.SelectMany(c => c.Models.Select(m => new DefaultOption(c.Id, c.Name, m))).ToArray();
        _defaultModelBox.ItemsSource = options;
        _defaultModelBox.SelectedItem = options.FirstOrDefault(o => configuration.DefaultModel is { } d && d.ConnectionId == o.ConnectionId && d.ModelId == o.ModelId);
        _defaultModelBox.IsEnabled = options.Length > 0;
        _defaultModelBox.PlaceholderText = options.Length == 0 ? "先添加连接和模型" : "选择默认模型";
        _refreshingDefault = false;
    }

    private async Task SetDefaultAsync()
    {
        if (_refreshingDefault || _busy || _defaultModelBox.SelectedItem is not DefaultOption option) return;
        await Busy(ct => _service.SetDefaultModelAsync(new ModelReference(option.ConnectionId, option.ModelId), ct));
        SetStatus("已更新默认模型");
        RefreshList();
    }

    private void RefreshPresets()
    {
        _presetGrid.Children.Clear();
        for (var i = 0; i < Presets.Length; i++)
        {
            var index = i;
            var preset = Presets[i];
            var name = new TextBlock { Text = preset.Name, FontSize = 13, FontWeight = FontWeight.Medium, Foreground = Strong, VerticalAlignment = VerticalAlignment.Center };
            var chip = new Border
            {
                Width = 28, Height = 28, CornerRadius = new CornerRadius(8), Background = Brush.Parse(preset.Background),
                Child = new TextBlock { Text = preset.Letter, FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = Brush.Parse(preset.Foreground),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            };
            var content = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 10, Children = { chip, Ui.Column(name, 1) } };
            if (index == _selectedPreset)
            {
                var check = new Border
                {
                    Width = 16, Height = 16, CornerRadius = new CornerRadius(8), Background = Brush.Parse(DesignTokens.Accent),
                    HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 7, 7, 0),
                    Child = new LineIcon(IconKind.Check, brush: Brushes.White) { Width = 9, Height = 9, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
                };
                Grid.SetColumn(check, 1);
                content.Children.Add(check);
            }
            var tile = new Border
            {
                Height = 64, Padding = new Thickness(12, 0), CornerRadius = new CornerRadius(12), Background = Brushes.White,
                BorderBrush = index == _selectedPreset ? Brush.Parse(DesignTokens.Accent) : Brush.Parse("#EEEDF3"),
                BorderThickness = new Thickness(1), Child = content
            };
            var button = new SurfaceButton(preset.Name, () => SelectPreset(index)) { Content = tile };
            _presetGrid.Children.Add(Ui.Column(button, index));
        }
        _presetGrid.IsVisible = _saved is null && !_customTab;
    }

    private void SelectPreset(int index)
    {
        if (_loading || index < 0 || index >= Presets.Length) return;
        _selectedPreset = index;
        var preset = Presets[index];
        _loading = true;
        ConnectionName.Text = preset.Name;
        BaseUrl.Text = preset.BaseUrl;
        _models.Clear();
        if (preset.DefaultModel is not null) _models.Add(preset.DefaultModel);
        Secret.Text = ""; _editingKey = true; NoKey.IsChecked = false;
        _loading = false;
        RefreshPresets(); RefreshModels(); RefreshKeyState(); Changed();
    }

    private void OnTabChanged(int index)
    {
        _customTab = index == 1;
        if (_customTab && _saved is null)
        {
            _selectedPreset = -1;
            _loading = true;
            ConnectionName.Text = "自定义服务";
            BaseUrl.Text = "";
            _models.Clear();
            Secret.Text = ""; _editingKey = true;
            _loading = false;
            RefreshModels(); RefreshKeyState(); Changed();
        }
        RefreshPresets(); RefreshControls();
    }

    private void RefreshModels()
    {
        _modelsHost.Children.Clear();
        _modelsEmpty.IsVisible = _models.Count == 0;
        for (var i = 0; i < _models.Count; i++)
        {
            var id = _models[i];
            var row = new Grid { MinHeight = 36, ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
            row.Children.Add(new TextBlock { Text = id, FontSize = 13, Foreground = Strong, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            var remove = Ui.Link("移除", () => { _models.Remove(id); _dirty = true; RefreshModels(); RefreshControls(); }, accent: false);
            remove.FontSize = 12;
            row.Children.Add(Ui.Column(remove, 1));
            _modelsHost.Children.Add(row);
            if (i < _models.Count - 1) _modelsHost.Children.Add(new Border { Height = 1, Background = Hairline });
        }
    }

    private void RefreshKeyState()
    {
        var noKey = NoKey.IsChecked == true;
        _keyEditor.IsVisible = !noKey && _editingKey;
        _savedKeyRow.IsVisible = !noKey && !_editingKey;
        _replace.IsVisible = !_editingKey;
        _cancelReplace.IsVisible = _editingKey && _saved?.CredentialId is not null;
        _deleteKey.IsVisible = _saved?.CredentialId is not null;
    }

    private void RefreshMode()
    {
        var preset = _saved is null && !_customTab;
        _presetGrid.IsVisible = preset;
        _presetNote.IsVisible = preset;
        _nameBlock.IsVisible = !preset;
        _protocolBlock.IsVisible = !preset;
        _modelSection.IsVisible = !preset;
        NoKey.IsVisible = !preset;
        _test.IsVisible = !preset;
        BaseUrl.IsReadOnly = preset;
        BaseUrl.Background = preset ? Brush.Parse("#F7F6FB") : Brushes.White;
        Grid.SetColumn(_keyBlock, preset ? 0 : 1);
        Grid.SetColumnSpan(_keyBlock, preset ? 2 : 1);
    }

    private void RefreshControls()
    {
        RefreshMode();
        _tabs.IsVisible = _saved is null;
        _modelAddRow.IsVisible = _addingModel;
        _fetch.IsEnabled = _saved is not null && !_dirty && !_busy;
        _test.IsEnabled = _saved is not null && !_dirty && _models.Count > 0 && !_busy;
        _save.IsEnabled = !_busy && !_loading;
        _delete.IsVisible = _saved is not null;
        _stop.IsVisible = _busy;
        _addModel.IsVisible = !_addingModel;
        _modelsEmpty.IsVisible = _models.Count == 0;
    }

    private void Changed()
    {
        if (_loading) return;
        _dirty = true;
        SetStatus("未保存");
        RefreshControls();
    }

    private void BeginAddModel()
    {
        _addingModel = true; RefreshControls(); ModelInput.Focus();
    }

    private void CommitModel()
    {
        var id = ModelInput.Text?.Trim();
        if (string.IsNullOrEmpty(id)) return;
        if (!_models.Contains(id, StringComparer.Ordinal)) _models.Add(id);
        ModelInput.Text = ""; _addingModel = false; _dirty = true;
        RefreshModels(); RefreshControls(); SetStatus("未保存");
    }

    internal async Task FetchModelsAsync()
    {
        if (_saved is null || _dirty) throw new ModelServiceException(ModelFailure.Configuration, "请先保存连接再获取模型。");
        string[] models = [];
        await Busy(async ct => models = await _service.GetModelsAsync(_id, ct));
        _models.Clear(); _models.AddRange(models); _dirty = true;
        RefreshModels(); RefreshControls(); SetStatus("已获取，待保存");
    }

    private async Task CancelAsync()
    {
        if (_dirty)
        {
            var discard = await new ConfirmWindow("放弃未保存的更改？", "放弃", "填写的内容不会保存。", destructive: true).ShowDialog<bool>(_owner());
            if (!discard) return;
        }
        Secret.Text = ""; _dirty = false;
        ShowList();
    }

    private Task ReplaceKeyAsync()
    {
        _editingKey = true; Secret.Text = ""; RefreshKeyState(); RefreshControls(); Secret.Focus();
        return Task.CompletedTask;
    }

    private void CancelReplaceKey()
    {
        Secret.Text = ""; _editingKey = _saved?.CredentialId is null;
        RefreshKeyState(); RefreshControls();
    }

    private async Task DeleteKeyAsync()
    {
        if (!await Confirm("删除此连接的 API 密钥？", "删除密钥", "删除后需要重新填写才能调用需要认证的服务。连接和模型列表保留。")) return;
        await Busy(ct => _service.DeleteCredentialAsync(_id, ct));
        _saved = _service.Configuration.Connections.FirstOrDefault(c => c.Id == _id);
        _editingKey = true; _savedKey.Text = "尚未保存密钥"; Secret.Text = "";
        RefreshKeyState(); RefreshControls(); SetStatus("密钥已删除");
    }

    private async Task DeleteConnectionAsync()
    {
        if (!await Confirm("删除此模型连接？", "删除连接", "将同时删除此连接保存的密钥；若它是默认模型，会清除默认选择。")) return;
        await Busy(ct => _service.DeleteConnectionAsync(_id, ct));
        _dirty = false; Secret.Text = "";
        ShowList(); SetStatus("连接已删除");
    }

    private Task<bool> Confirm(string title, string action, string detail) => new ConfirmWindow(title, action, detail, destructive: true).ShowDialog<bool>(_owner());

    private void ShowHelp()
    {
        var flyout = new Flyout { Content = new TextBlock { Text = HelpText, MaxWidth = 280, TextWrapping = TextWrapping.Wrap, FontSize = 12.5 } };
        flyout.ShowAt(_helpButton);
    }

    private async Task Busy(Func<CancellationToken, Task> work, bool testing = false)
    {
        if (_busy) return;
        _operation = new(); _busy = true; RefreshControls();
        if (testing) SetStatus("测试中…");
        try { await work(_operation.Token); }
        finally { _busy = false; _operation.Dispose(); _operation = null; RefreshControls(); }
    }

    private async void Run(Func<Task> operation)
    {
        try { await operation(); }
        catch (OperationCanceledException) { SetStatus("已停止"); }
        catch (ModelServiceException e) { SetStatus(e.Message, error: true); }
        catch { SetStatus("操作未完成，请检查配置或系统凭据访问权限后重试。", error: true); }
    }

    private void SetStatus(string message, bool error = false)
    {
        _statusMessage = message;
        _editStatus.Text = message;
        _editStatus.Foreground = error ? Error : message is "已保存" or "连接正常" ? Ready : Secondary;
    }

    internal void Leave()
    {
        _operation?.Cancel();
        Secret.Text = ""; _dirty = false;
        ShowList();
    }

    public void Dispose() { Leave(); }

    private static TextBlock StatusText() => new() { FontSize = DesignTokens.Caption, Foreground = Secondary, Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap };

    private static TextBox Field(string name, string? placeholder = null)
    {
        var field = new TextBox { Watermark = placeholder, MinWidth = 0, MaxLength = 2048, Height = 32, FontSize = 13, VerticalContentAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(field, name);
        return field;
    }

    private static bool SameAddress(string left, string right) => string.Equals(left.Trim().TrimEnd('/'), right.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
    private static string ProtocolLabel(ModelConnection connection) => connection.Protocol == "openai-chat" ? "OpenAI 兼容" : connection.Protocol;
    private static string HostOf(string address) => Uri.TryCreate(address, UriKind.Absolute, out var uri) ? uri.Host : address;
}
