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
    private static readonly IBrush Strong = Brush.Parse("#1A1B22");
    private static readonly IBrush Secondary = Brush.Parse("#5F6270");
    private static readonly IBrush Hint = Brush.Parse("#8C8F9B");
    private static readonly IBrush Hairline = Brush.Parse("#F1F0F5");
    private static readonly IBrush Error = Brush.Parse("#D9363E");
    private static readonly IBrush Ready = Brush.Parse("#1F9D61");

    // Only OpenAI-compatible presets may be offered until a native protocol is implemented.
    private sealed record Preset(string Name, string BaseUrl, string? DefaultModel, string Letter, string Background, string Foreground);
    private const int DefaultPresetIndex = 1;
    private static readonly Preset[] Presets =
    [
        new("OpenAI", "https://api.openai.com/v1", null, "O", "#F0EFF5", "#1A1B22"),
        new("DeepSeek", "https://api.deepseek.com", "deepseek-flash", "D", "#E7F0FF", "#2F6BDB"),
        new("ZenMux", "https://zenmux.ai/api/v1", null, "Z", "#F0EFF5", "#1A1B22")
    ];
    private readonly ModelService _service;
    private readonly Func<Window> _owner;
    private readonly StackPanel _listView = new();
    private readonly StackPanel _editView = new();
    private readonly StackPanel _connectionRows = new();
    private readonly ModelOrderList _modelOrder;
    private readonly TextBlock _connectionMeta = new() { FontSize = DesignTokens.Caption, Foreground = Hint, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _emptyConnections;
    private readonly Button _addButton;
    private readonly TextBlock _editStatus = StatusText();

    // Edit form
    internal TextBox ConnectionName { get; } = Field("连接名称");
    internal TextBox BaseUrl { get; } = Field("API 基础地址", "例如 https://api.example.com/v1");
    internal TextBox Secret { get; } = new() { PasswordChar = '●', Watermark = "输入 API 密钥", MinWidth = 0, MaxLength = 1280, Height = 32, FontSize = 13, VerticalContentAlignment = VerticalAlignment.Center };
    internal TextBox ModelInput { get; } = Field("模型 ID", "输入模型 ID，例如 deepseek-flash");
    // Only one protocol exists, so it is shown as read-only text rather than a one-item dropdown.
    private readonly TextBox _protocol = new() { Text = "OpenAI Chat Completions", IsReadOnly = true, Focusable = false, Height = 32, FontSize = 13, MinWidth = 0,
        VerticalContentAlignment = VerticalAlignment.Center, Background = Brush.Parse("#F7F6FB"), Foreground = Secondary };
    private readonly TextBlock _editTitle = new() { FontSize = DesignTokens.PageTitle, LineHeight = 26, FontWeight = FontWeight.SemiBold, Foreground = Strong };
    private readonly Border _tabs = new() { HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 16, 0, 0) };
    private readonly Grid _presetGrid = new() { ColumnDefinitions = new ColumnDefinitions("*,*,*"), ColumnSpacing = 10, Margin = new Thickness(0, 12, 0, 0) };
    private readonly StackPanel _modelsHost = new() { Spacing = 4 };
    private readonly Border _modelsEmpty;
    private readonly Border _modelAddRow;
    private readonly Button _addModel;
    private readonly Button _fetch;
    private readonly Button _test;
    private readonly Button _save;
    private readonly Button _cancel;
    private readonly Button _delete;
    private readonly Button _stop;
    private readonly Button _reveal;
    private readonly Grid _testResult;
    private readonly LineIcon _testCheck = new(IconKind.Check, brush: Brush.Parse("#17784A"))
    { Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _testResultText = new() { FontSize = DesignTokens.Description, LineHeight = 18, TextWrapping = TextWrapping.Wrap };
    private StackPanel _nameBlock = new();
    private StackPanel _addressBlock = new();
    private StackPanel _protocolBlock = new();
    private Grid _fieldRow = new();
    private readonly List<string> _models = [];
    // A saved success follows the configuration. Keep failure messages only for this session.
    private readonly Dictionary<Guid, string> _testFailures = [];
    private (bool Ready, string Reason)? _draftTest;
    private CancellationTokenSource? _operation;
    private Guid _id = Guid.NewGuid();
    private ModelConnection? _saved;
    private bool _loading, _busy, _dirty, _customTab, _addingModel;
    private int _selectedPreset = -1;
    private string _statusMessage = "";

    internal string Status => _statusMessage;
    internal bool IsBusy => _busy;
    internal bool IsEditing => _editView.IsVisible;
    internal bool IsKeyRevealed => Secret.RevealPassword;
    internal Guid ConnectionId => _id;
    internal IReadOnlyList<string> Models => _models;

    public ModelServicesPage(ModelService service, Func<Window> owner)
    {
        _service = service; _owner = owner;
        AutomationProperties.SetName(Secret, "API 密钥");
        AutomationProperties.SetName(ModelInput, "模型 ID");
        AutomationProperties.SetName(_protocol, "API 协议");
        _reveal = Ui.IconButton(IconKind.Eye, "显示密钥", ToggleReveal);
        _reveal.MinWidth = 28;
        _reveal.MinHeight = 24;
        _reveal.Padding = new Thickness(5, 2);
        _reveal.Margin = new Thickness(0, 0, 4, 0);
        _reveal.HorizontalContentAlignment = HorizontalAlignment.Center;
        Secret.InnerRightContent = _reveal;

        _addButton = Ui.Primary("添加连接", BeginAdd);
        _emptyConnections = BuildEmpty();
        ResetTabs();
        _modelsEmpty = BuildModelsEmpty();
        _modelAddRow = BuildModelAddRow();
        _addModel = Ui.Link("手动添加", BeginAddModel);
        _fetch = Ui.Link("获取模型", () => Run(FetchModelsAsync));
        _test = Ui.Link("测试连接", () => Run(TestAsync));
        ToolTip.SetTip(_test, "测试将向当前地址发送一条简短的模型请求");
        _save = Ui.Primary("保存", () => Run(SaveAsync));
        _cancel = Ui.Button("取消", CancelEdit);
        _delete = Ui.Link("删除连接", () => Run(DeleteConnectionAsync), accent: false);
        _stop = Ui.Link("停止", () => _operation?.Cancel(), accent: false);
        _stop.IsVisible = false;
        _testResult = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 4,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0),
            IsVisible = false, Children = { _testCheck, Ui.Column(_testResultText, 1) }
        };
        AutomationProperties.SetLiveSetting(_testResultText, AutomationLiveSetting.Polite);
        _editStatus.HorizontalAlignment = HorizontalAlignment.Right;

        foreach (var box in new[] { ConnectionName, BaseUrl, Secret })
            box.PropertyChanged += (_, args) => { if (args.Property == TextBox.TextProperty) Changed(); };
        ModelInput.KeyDown += (_, args) => { if (args.Key == Avalonia.Input.Key.Enter) { CommitModel(); args.Handled = true; } };

        _listView.Children.Add(BuildListHeader());
        _listView.Children.Add(Ui.GroupHeader("连接", _connectionMeta));
        _listView.Children.Add(_connectionRows);
        _listView.Children.Add(_emptyConnections);
        _modelOrder = new ModelOrderList(MoveModelAsync);
        _listView.Children.Add(_modelOrder);
        _listView.Children.Add(new TextBlock
        {
            Text = "API 密钥以明文保存在本机配置文件 model-services.json 中，不要共享该文件。删除连接后，引用它的功能会要求重新选择。",
            FontSize = 11.5, LineHeight = 16, Foreground = Hint, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 18, 0, 0)
        });

        _editView.Children.Add(BuildEditHeader());
        _editView.Children.Add(_tabs);
        _editView.Children.Add(_presetGrid);
        _editView.Children.Add(BuildFields());
        _editView.Children.Add(BuildModelSection());
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

    internal void SelectPreset(string name)
    {
        var index = Array.FindIndex(Presets, p => p.Name == name);
        if (index >= 0) SelectPreset(index);
    }

    internal void AddModel(string id)
    {
        id = id.Trim();
        if (id.Length == 0 || _models.Contains(id, StringComparer.Ordinal)) return;
        _models.Add(id);
        RefreshModels();
        Changed();
    }

    private ModelConnection Draft() => new(_id, ConnectionName.Text?.Trim() ?? "", BaseUrl.Text?.Trim().TrimEnd('/') ?? "", _models.ToArray());

    internal async Task SaveAsync()
    {
        if (_busy || !IsEditing) return;
        var draft = Draft();
        var key = Secret.Text ?? "";
        var test = _draftTest;
        var changed = _dirty;
        await Busy(ct => _service.SaveConnectionAsync(draft, key, test?.Ready, ct));
        if (test is { Ready: false } failure) _testFailures[draft.Id] = failure.Reason;
        else if (test.HasValue || changed) _testFailures.Remove(draft.Id);
        if (!IsEditing || _id != draft.Id) return;
        _saved = _service.Configuration.Connections.FirstOrDefault(c => c.Id == draft.Id);
        _dirty = false;
        SetRevealed(false);
        ClearTestResult();
        SetStatus("已保存");
        RefreshAll();
    }

    internal async Task TestAsync()
    {
        if (_busy || !IsEditing) return;
        var draft = Draft();
        var key = Secret.Text ?? "";
        var testingSavedVersion = _saved is not null && !_dirty;
        ClearTestResult();
        SetStatus("测试中…");
        try
        {
            ModelUsage? usage = null;
            await Busy(async ct =>
            {
                try { usage = await _service.TestConnectionAsync(draft, key, ct); }
                catch (ModelServiceException) when (testingSavedVersion)
                {
                    await _service.RecordSavedTestAsync(draft, key, false, ct);
                    throw;
                }
                if (testingSavedVersion) await _service.RecordSavedTestAsync(draft, key, true, ct);
                ct.ThrowIfCancellationRequested();
            });
            if (!IsEditing) return;
            _draftTest = (true, "");
            if (testingSavedVersion) _testFailures.Remove(draft.Id);
            SetStatus("连接正常");
            ShowTestResult(true, "连接正常");
            ToolTip.SetTip(_testResultText, usage is null ? "测试模型：" + draft.Models[0]
                : $"测试模型：{draft.Models[0]} · 输入 {usage.InputTokens?.ToString() ?? "未知"} / 输出 {usage.OutputTokens?.ToString() ?? "未知"} token");
        }
        catch (ModelServiceException e)
        {
            if (IsEditing)
            {
                _draftTest = (false, ShortReason(e.Failure));
                if (testingSavedVersion) _testFailures[draft.Id] = _draftTest.Value.Reason;
                SetStatus(e.Message, error: true);
                ShowTestResult(false, "测试失败：" + e.Message);
            }
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
        LoadDraft(connection);
        _listView.IsVisible = false;
        _editView.IsVisible = true;
    }

    private void LoadDraft(ModelConnection? connection)
    {
        _loading = true;
        try
        {
            _saved = connection;
            _id = connection?.Id ?? Guid.NewGuid();
            _models.Clear();
            Secret.Text = connection is null ? "" : _service.KeyOf(connection.Id) ?? "";
            SetRevealed(false);
            ClearTestResult();
            SetStatus("");
            _customTab = false;
            ResetTabs();
            _selectedPreset = -1;
            _addingModel = false;
            _dirty = false;
            ModelInput.Text = "";
            if (connection is null)
            {
                var preset = Presets[DefaultPresetIndex];
                _selectedPreset = DefaultPresetIndex;
                ConnectionName.Text = preset.Name;
                BaseUrl.Text = preset.BaseUrl;
                if (preset.DefaultModel is not null) _models.Add(preset.DefaultModel);
            }
            else
            {
                ConnectionName.Text = connection.Name;
                BaseUrl.Text = connection.BaseUrl;
                _models.AddRange(connection.Models);
                _selectedPreset = Array.FindIndex(Presets, p => SameAddress(p.BaseUrl, connection.BaseUrl));
                if (connection.IsVerified) ShowTestResult(true, "连接正常");
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
            Ui.GroupTitle("模型服务"),
            new TextBlock { Text = "管理 OpenAI 兼容接口的连接与模型，供翻译等工具复用", FontSize = DesignTokens.Description, LineHeight = 18, Foreground = Secondary }
        } };
        _addButton.VerticalAlignment = VerticalAlignment.Center;
        return new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 14, Children =
        {
            titles, Ui.Column(_addButton, 1)
        } };
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
            BorderBrush = Brush.Parse("#E0E5EA"), BorderThickness = new Thickness(1), Background = Brush.Parse("#FEFEFF"), Child = content };
    }

    private Border BuildModelsEmpty() => new()
    {
        Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(12, 10), CornerRadius = new CornerRadius(12),
        BorderBrush = Brush.Parse("#E3E1EC"), BorderThickness = new Thickness(1), Background = Brush.Parse("#FEFEFF"),
        Child = new StackPanel { Children =
        {
            new TextBlock { Text = "还没有模型", FontSize = 13, Foreground = Secondary, HorizontalAlignment = HorizontalAlignment.Center }
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
        var back = Ui.IconButton(IconKind.Back, "返回连接列表", CancelEdit);
        _editTitle.VerticalAlignment = VerticalAlignment.Center;
        return new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 12, Children = { back, Ui.Column(_editTitle, 1) } };
    }

    private Control BuildFields()
    {
        _nameBlock = FieldBlock("名称", ConnectionName);
        _addressBlock = FieldBlock("API 地址", BaseUrl);
        _protocolBlock = FieldBlock("API 协议", _protocol);
        _fieldRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 14, Children = { _addressBlock, Ui.Column(_protocolBlock, 1) } };
        return new StackPanel { Spacing = 12, Margin = new Thickness(0, 14, 0, 0), Children =
        {
            _nameBlock, _fieldRow, FieldBlock("API 密钥", Secret, hint: "明文保存在本机")
        } };
    }

    private Control BuildModelSection()
    {
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Children = { _fetch, _addModel, _test, _stop } };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 4, MinHeight = 26, Children =
        {
            new TextBlock { Text = "模型", FontWeight = FontWeight.SemiBold, Foreground = Strong, VerticalAlignment = VerticalAlignment.Center },
            Ui.Column(actions, 1)
        } };
        return new StackPanel { Margin = new Thickness(0, 18, 0, 0), Children =
        {
            header, _modelsEmpty,
            new Border { Margin = new Thickness(0, 6, 0, 0), Child = _modelsHost },
            _modelAddRow, _testResult, _editStatus
        } };
    }

    private Control BuildFooter()
    {
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right,
            Children = { _cancel, _save } };
        return new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 8, Margin = new Thickness(0, 18, 0, 0), Children =
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
            new TextBlock { Text = HostOf(connection.BaseUrl) + " · " + (connection.Models.Length == 0 ? "未添加模型" : connection.Models.Length + " 个模型"), FontSize = DesignTokens.Caption, Foreground = Hint,
                TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) }
        } };
        var (statusText, statusColor) = StatusOf(connection);
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, VerticalAlignment = VerticalAlignment.Center, Children =
        {
            Ui.Status(statusText, statusColor),
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
            Background = Brush.Parse(preset?.Background ?? "#E7EBEF"),
            Child = new TextBlock { Text = letter, FontSize = 13, FontWeight = FontWeight.SemiBold,
                Foreground = Brush.Parse(preset?.Foreground ?? DesignTokens.Accent), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        };
    }

    private (string Text, string Color) StatusOf(ModelConnection connection)
    {
        if (connection.IsVerified) return ("可用", DesignTokens.Ready);
        if (_testFailures.TryGetValue(connection.Id, out var reason)) return (reason, DesignTokens.Error);
        if (!_service.HasKey(connection.Id)) return ("缺少密钥", DesignTokens.Attention);
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
        _addButton.IsVisible = configuration.Connections.Length > 0;
        _connectionMeta.Text = configuration.Connections.Length + " 个连接 · " + configuration.Connections.Sum(c => c.Models.Length) + " 个模型";
        _modelOrder.Show(configuration);
    }

    private async Task<ModelConfiguration> MoveModelAsync(ModelReference model, ModelReference target, bool after)
    {
        if (!_busy && !IsEditing)
            await Busy(_ => _service.MoveModelAsync(model, target, after));
        return _service.Configuration;
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
                Width = 24, Height = 24, CornerRadius = new CornerRadius(6), Background = Brush.Parse(preset.Background),
                Child = new TextBlock { Text = preset.Letter, FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = Brush.Parse(preset.Foreground),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            };
            var content = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 6, Children = { chip, Ui.Column(name, 1) }
            };
            var selected = index == _selectedPreset;
            if (selected)
            {
                content.Children.Add(Ui.Column(new LineIcon(IconKind.Check, accent: true)
                { Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Center }, 2));
            }
            var tile = new Border
            {
                Height = 42, Padding = new Thickness(8, 0), CornerRadius = new CornerRadius(9),
                Background = selected ? Brush.Parse("#FAF7FF") : Brushes.White,
                BorderBrush = Brush.Parse(selected ? DesignTokens.Accent : "#E0E5EA"),
                BorderThickness = new Thickness(1), Child = content
            };
            var button = new SurfaceButton(preset.Name, () => SelectPreset(index)) { Content = tile };
            _presetGrid.Children.Add(Ui.Column(button, index));
        }
        _presetGrid.IsVisible = _saved is null && !_customTab;
    }

    private void SelectPreset(int index)
    {
        if (_loading || _busy || index < 0 || index >= Presets.Length || index == _selectedPreset) return;
        _selectedPreset = index;
        var preset = Presets[index];
        _loading = true;
        ConnectionName.Text = preset.Name;
        BaseUrl.Text = preset.BaseUrl;
        _models.Clear();
        if (preset.DefaultModel is not null) _models.Add(preset.DefaultModel);
        Secret.Text = "";
        ModelInput.Text = "";
        _addingModel = false;
        SetRevealed(false);
        _loading = false;
        RefreshPresets(); RefreshModels(); Changed();
    }

    private void ResetTabs() => _tabs.Child = Ui.Segmented(["预设服务商", "自定义接口"], 0, OnTabChanged, out _, out _);

    private void OnTabChanged(int index)
    {
        _customTab = index == 1;
        if (_customTab)
        {
            _selectedPreset = -1;
            _loading = true;
            ConnectionName.Text = "自定义服务";
            BaseUrl.Text = "";
            _models.Clear();
            Secret.Text = "";
            ModelInput.Text = "";
            _addingModel = false;
            SetRevealed(false);
            _loading = false;
            RefreshPresets(); RefreshModels(); Changed();
        }
        else
        {
            SelectPreset(DefaultPresetIndex);
        }
    }

    private void RefreshModels()
    {
        _modelsHost.Children.Clear();
        foreach (var id in _models)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 8 };
            row.Children.Add(new LineIcon(IconKind.Model, brush: Secondary)
            { Width = 15, Height = 15, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(Ui.Column(new TextBlock
            {
                Text = id, FontSize = 13, Foreground = Strong, VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            }, 1));
            var remove = Ui.IconButton(IconKind.Close, "移除模型 " + id, () =>
            {
                _models.Remove(id);
                RefreshModels();
                Changed();
            });
            remove.Content = new Border
            {
                Width = 23, Height = 23, CornerRadius = new CornerRadius(11.5), Background = Brush.Parse("#E9E6EF"),
                Child = new LineIcon(IconKind.Close, brush: Secondary)
                { Width = 15, Height = 15, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            };
            remove.MinWidth = remove.MinHeight = 28;
            remove.Padding = new Thickness(2);
            remove.CornerRadius = new CornerRadius(14);
            remove.HorizontalContentAlignment = HorizontalAlignment.Center;
            row.Children.Add(Ui.Column(remove, 2));
            _modelsHost.Children.Add(new Border
            {
                MinHeight = 36, Padding = new Thickness(10, 4), CornerRadius = new CornerRadius(7),
                Background = Brush.Parse("#F7F6FB"), Child = row
            });
        }
    }

    private void RefreshControls()
    {
        var preset = _saved is null && !_customTab;
        _presetGrid.IsVisible = preset;
        _nameBlock.IsVisible = !preset;
        _protocolBlock.IsVisible = !preset;
        BaseUrl.IsReadOnly = preset;
        BaseUrl.Background = preset ? Brush.Parse("#F7F6FB") : Brushes.White;
        Grid.SetColumnSpan(_addressBlock, preset ? 2 : 1);

        var canEdit = !_busy && !_loading;
        var canConnect = canEdit && !string.IsNullOrWhiteSpace(BaseUrl.Text) && !string.IsNullOrWhiteSpace(Secret.Text);
        _tabs.IsVisible = _saved is null;
        _tabs.IsEnabled = _presetGrid.IsEnabled = _modelsHost.IsEnabled = _modelAddRow.IsEnabled = _fieldRow.IsEnabled = canEdit;
        ConnectionName.IsEnabled = Secret.IsEnabled = canEdit;
        _modelAddRow.IsVisible = _addingModel;
        _fetch.IsEnabled = canConnect;
        _test.IsEnabled = canConnect && _models.Count > 0;
        _save.IsEnabled = _cancel.IsEnabled = _delete.IsEnabled = canEdit;
        _delete.IsVisible = _saved is not null;
        _stop.IsVisible = _busy;
        _addModel.IsEnabled = canEdit && !_addingModel;
        _modelsEmpty.IsVisible = _models.Count == 0;
    }

    private void Changed()
    {
        if (_loading) return;
        _dirty = true;
        ClearTestResult();
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
        ModelInput.Text = "";
        _addingModel = false;
        AddModel(id);
        RefreshControls();
    }

    internal async Task FetchModelsAsync()
    {
        if (_busy || !IsEditing) return;
        var draft = Draft();
        var key = Secret.Text ?? "";
        await Busy(async ct =>
        {
            var catalog = await _service.GetModelsAsync(draft, key, ct);
            ct.ThrowIfCancellationRequested();
            if (!IsEditing || _id != draft.Id) return;

            var picker = new ModelPickerWindow(catalog, draft.Models);
            using var cancelPicker = ct.Register(() => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (picker.IsVisible) picker.Close();
            }));
            var selected = await picker.ShowDialog<string[]?>(_owner());
            if (selected is null || ct.IsCancellationRequested || _models.SequenceEqual(selected, StringComparer.Ordinal)) return;

            _models.Clear();
            _models.AddRange(selected);
            RefreshModels();
            Changed();
            SetStatus("模型列表待保存");
        });
    }

    private void CancelEdit()
    {
        if (!_busy) Leave();
    }

    internal void ToggleReveal()
    {
        SetRevealed(!Secret.RevealPassword);
    }

    private void SetRevealed(bool revealed)
    {
        Secret.RevealPassword = revealed;
        _reveal.Content = new LineIcon(revealed ? IconKind.EyeOff : IconKind.Eye, brush: Secondary) { Width = 17, Height = 17 };
        var label = revealed ? "隐藏密钥" : "显示密钥";
        AutomationProperties.SetName(_reveal, label);
        ToolTip.SetTip(_reveal, label);
    }

    private void ClearTestResult()
    {
        _draftTest = null;
        _testResult.IsVisible = false;
        ToolTip.SetTip(_testResultText, null);
    }

    private void ShowTestResult(bool ready, string text)
    {
        _testResult.IsVisible = true;
        _editStatus.IsVisible = false;
        _testCheck.IsVisible = ready;
        _testResultText.Text = text;
        _testResultText.Foreground = Brush.Parse(ready ? "#17784A" : "#B42318");
    }

    // A few words for the list row; the full message stays on the edit page.
    private static string ShortReason(ModelFailure failure) => failure switch
    {
        ModelFailure.Authentication => "密钥无效或无权限",
        ModelFailure.Credentials => "缺少密钥",
        ModelFailure.RateLimited => "限流或额度不足",
        ModelFailure.Timeout => "请求超时",
        ModelFailure.Network => "无法连接",
        ModelFailure.Configuration => "配置有误",
        _ => "接口或模型不兼容"
    };

    private async Task DeleteConnectionAsync()
    {
        if (!await Confirm("删除此模型连接？", "删除连接", "将同时移除这个连接的模型和密钥。")) return;
        _testFailures.Remove(_id);
        await Busy(ct => _service.DeleteConnectionAsync(_id, ct));
        _dirty = false; Secret.Text = "";
        ShowList(); SetStatus("连接已删除");
    }

    private Task<bool> Confirm(string title, string action, string detail) => new ConfirmWindow(title, action, detail, destructive: true).ShowDialog<bool>(_owner());

    private async Task Busy(Func<CancellationToken, Task> work)
    {
        if (_busy) return;
        _operation = new(); _busy = true; RefreshControls();
        try { await work(_operation.Token); }
        finally { _busy = false; _operation.Dispose(); _operation = null; RefreshControls(); }
    }

    private async void Run(Func<Task> operation)
    {
        try { await operation(); }
        catch (OperationCanceledException) { SetStatus("已停止"); }
        catch (ModelServiceException e) { if (!_testResult.IsVisible || e.Message != Status) SetStatus(e.Message, error: true); }
        catch { SetStatus("操作未完成，请检查配置后重试。", error: true); }
    }

    private void SetStatus(string message, bool error = false)
    {
        _statusMessage = message;
        _editStatus.Text = message;
        _editStatus.IsVisible = message is not ("" or "未保存");
        _editStatus.Foreground = error ? Error : message is "已保存" or "连接正常" ? Ready : Secondary;
    }

    internal void Leave()
    {
        _operation?.Cancel();
        Secret.Text = ""; SetRevealed(false); ClearTestResult(); _dirty = false;
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
    private static string HostOf(string address) => Uri.TryCreate(address, UriKind.Absolute, out var uri) ? uri.Host : address;
}
