using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Tbox.Core.ModelServices;
using Tbox.Desktop.ModelServices;

namespace Tbox.Desktop.Views;

// Settings for the shared capability, not a chat/translation tool. The only generation here is a fixed smoke request.
internal sealed class ModelServicesPage : UserControl, IDisposable
{
    private readonly ModelService _service;
    private readonly Func<Window> _owner;
    private readonly ComboBox _connections = new() { HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 0 };
    internal ComboBox Provider { get; } = new() { ItemsSource = new[] { "DeepSeek", "自定义服务（OpenAI 兼容）" }, HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 0 };
    internal Expander MoreSettings { get; } = new() { Header = "更多设置", IsExpanded = false, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    internal TextBox ConnectionName { get; } = Field("连接名称");
    internal TextBox BaseUrl { get; } = Field("API 基础地址", "例如 https://api.example.com/v1");
    internal TextBox Secret { get; } = new() { PasswordChar = '●', Watermark = "输入新的 API 密钥", MinWidth = 0, MaxLength = 1280 };
    internal TextBox Models { get; } = new() { AcceptsReturn = true, MinHeight = 72, MaxHeight = 140, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Watermark = "每行一个模型 ID，可手动填写", MinWidth = 0 };
    internal CheckBox NoKey { get; } = new() { Content = "此连接无需 API 密钥" };
    internal CheckBox UseAsDefault { get; } = new() { Content = "设为全局默认模型" };
    private readonly ComboBox _defaultModel = new() { HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 0 };
    private readonly TextBlock _savedKey = Ui.Description("尚未保存密钥");
    private readonly TextBlock _status = Ui.Description("未测试。保存配置不会发送模型请求。");
    private readonly TextBlock _endpoint = Ui.Description("");
    private readonly TextBlock _testResult = Ui.Description("");
    private readonly TextBlock _global = Ui.Description("");
    private readonly StackPanel _form = new() { Spacing = 10 };
    private readonly Control _secretEditor;
    private readonly Button _replace, _deleteKey, _save, _fetch, _test, _stop, _add, _delete, _reset;
    private CancellationTokenSource? _operation;
    private Guid _id = Guid.NewGuid();
    private bool _loading, _busy, _dirty, _editingKey = true, _opened, _disposed;
    internal string Status => _status.Text ?? "";
    internal bool IsBusy => _busy;
    internal bool IsEditingKey => _editingKey;
    internal Guid ConnectionId => _id;

    public ModelServicesPage(ModelService service, Func<Window> owner)
    {
        _service = service; _owner = owner;
        AutomationProperties.SetName(Secret, "新的 API 密钥");
        AutomationProperties.SetName(Models, "模型 ID 列表");
        AutomationProperties.SetName(_connections, "选择模型连接");
        AutomationProperties.SetName(_defaultModel, "全局默认模型 ID");
        AutomationProperties.SetName(Provider, "模型服务商");
        _replace = Ui.Link("替换", BeginReplaceKey);
        _deleteKey = Ui.Link("删除密钥", () => Run(DeleteKeyAsync), accent: false);
        _secretEditor = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8, Children =
        { Secret, Ui.Column(Ui.Link("取消替换", CancelReplaceKey), 1) } };
        var keyState = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 6,
            Children = { _savedKey, Ui.Column(_replace, 1), Ui.Column(_deleteKey, 2) } };
        _savedKey.VerticalAlignment = VerticalAlignment.Center;
        _save = Ui.Primary("保存配置", () => Run(SaveAsync));
        _fetch = Ui.Button("获取模型列表", () => Run(FetchModelsAsync));
        _test = Ui.Button("测试调用", () => Run(TestAsync));
        _stop = Ui.Button("停止", () => _operation?.Cancel()); _stop.IsVisible = false;
        _add = Ui.Button("添加连接", () => Run(() => LoadDraftAsync(null)));
        _delete = Ui.Link("删除连接", () => Run(DeleteConnectionAsync), accent: false);
        _reset = Ui.Link("放弃更改", () => Run(async () => { _dirty = false; await LoadDraftAsync(_service.Configuration.Connections.FirstOrDefault(c => c.Id == _id)); }));
        _connections.SelectionChanged += (_, _) =>
        {
            if (_loading || _connections.SelectedItem is not ConnectionOption option || option.Id == _id) return;
            if (_dirty)
            {
                _loading = true; _connections.SelectedItem = (_connections.ItemsSource as ConnectionOption[])?.FirstOrDefault(c => c.Id == _id); _loading = false;
                SetStatus("有未保存的更改，请先保存或点击“放弃更改”。"); return;
            }
            Run(() => LoadDraftAsync(_service.Configuration.Connections.FirstOrDefault(c => c.Id == option.Id)));
        };
        // TextChanged is deferred by Avalonia; use synchronous property changes so loading never looks like a user edit.
        foreach (var box in new[] { ConnectionName, BaseUrl, Secret, Models })
            box.PropertyChanged += (_, args) => { if (args.Property == TextBox.TextProperty) Changed(); };
        NoKey.IsCheckedChanged += (_, _) => Changed();
        UseAsDefault.IsCheckedChanged += (_, _) => Changed();
        _defaultModel.SelectionChanged += (_, _) => Changed();
        Provider.SelectionChanged += (_, _) => ApplyPreset();
        _form.Children.Add(Labelled("服务商", Provider));
        _form.Children.Add(Labelled("API 密钥", keyState));
        _form.Children.Add(_secretEditor);
        _form.Children.Add(Labelled("模型", _defaultModel));
        var advanced = new StackPanel { Spacing = 10, Margin = new Thickness(0, 8, 0, 8), Children =
        {
            new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8, Children = { _connections, Ui.Column(_add, 1) } },
            Labelled("连接名称", ConnectionName), Labelled("API 地址", BaseUrl), _endpoint,
            Ui.Description("接口：OpenAI 兼容 · Chat Completions"), NoKey,
            Labelled("模型 ID", Models), Ui.Row(_fetch, Ui.Description("每行一个，可手动填写")), UseAsDefault, _global, _delete
        } };
        MoreSettings.Content = advanced;
        _form.Children.Add(MoreSettings);
        _form.Children.Add(new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 6,
            Children = { _reset, Ui.Column(_save, 2) } });
        _delete.HorizontalAlignment = HorizontalAlignment.Left;
        var testbar = new StackPanel { Spacing = 10, Children =
        {
            Ui.GroupHeader("连接测试"), Ui.Description("保存不会发起调用。点击测试才发送少量文字，可能产生 API 费用。"),
            Ui.Row(_test, _stop), _status, _testResult,
            Ui.Description("密钥由系统安全保存，不回填显示；不会自动发送截图或剪贴板。")
        } };
        Content = new StackPanel { Spacing = 8, Margin = new Thickness(0, 16, 0, 0), Children = { _form, testbar } };
        RefreshControls();
    }

    public async Task OpenAsync()
    {
        if (_opened || _disposed) return;
        await _service.InitializeAsync();
        await LoadDraftAsync(_service.Configuration.Connections.FirstOrDefault());
        _opened = true;
    }

    private async Task LoadDraftAsync(ModelConnection? connection)
    {
        if (_busy) return;
        if (connection is null && _dirty) { SetStatus("请先保存或放弃当前更改，再添加连接。"); return; }
        _loading = true;
        try
        {
            _id = connection?.Id ?? Guid.NewGuid();
            ConnectionName.Text = connection?.Name ?? "DeepSeek";
            BaseUrl.Text = connection?.BaseUrl ?? "https://api.deepseek.com";
            Models.Text = string.Join('\n', connection?.Models ?? ["deepseek-chat"]);
            Provider.SelectedIndex = IsDeepSeek(BaseUrl.Text) ? 0 : 1;
            NoKey.IsChecked = connection is not null && !connection.RequiresKey;
            UseAsDefault.IsChecked = connection is null ? _service.Configuration.DefaultModel is null : _service.Configuration.DefaultModel?.ConnectionId == _id;
            Secret.Text = ""; _editingKey = connection?.CredentialId is null;
            _dirty = false;
            RefreshModelChoices(_service.Configuration.DefaultModel?.ConnectionId == _id ? _service.Configuration.DefaultModel.ModelId : null);
            var options = _service.Configuration.Connections.Select(c => new ConnectionOption(c.Id, c.Name)).ToArray();
            _connections.ItemsSource = options; _connections.SelectedItem = options.FirstOrDefault(c => c.Id == _id);
            _connections.PlaceholderText = "新连接";
            _savedKey.Text = connection?.CredentialId is not null ? "••••••••••••  · 检查保存状态…" : "尚未保存密钥";
            _testResult.Text = "";
            SetStatus("未测试。保存配置不会发送模型请求。");
            RefreshControls();
            if (connection?.CredentialId is not null)
                _savedKey.Text = await _service.HasCredentialAsync(connection) ? "••••••••••••  · 已保存" : "密钥缺失，请替换";
        }
        finally { _loading = false; RefreshControls(); }
    }

    internal async Task SaveAsync()
    {
        if (_busy) return;
        var draft = Draft();
        var replacement = _editingKey && !string.IsNullOrEmpty(Secret.Text) ? Secret.Text : null;
        await Busy(async ct =>
        {
            await _service.SaveConnectionAsync(draft, replacement, false, UseAsDefault.IsChecked == true, _defaultModel.SelectedItem as string, ct);
        });
        Secret.Text = ""; _dirty = false;
        await LoadDraftAsync(_service.Configuration.Connections.Single(c => c.Id == _id));
        SetStatus("已保存 · 尚未测试。可以直接在工具中使用。");
    }

    internal void BeginReplaceKey()
    { _editingKey = true; Secret.Text = ""; RefreshControls(); Secret.Focus(); }
    internal void CancelReplaceKey()
    { Secret.Text = ""; _editingKey = false; RefreshControls(); }

    private async Task DeleteKeyAsync()
    {
        if (!await Confirm("删除此连接的 API 密钥？", "删除密钥", "删除后需要重新填写才能调用需要认证的服务。连接和模型列表保留。")) return;
        await Busy(ct => _service.DeleteCredentialAsync(_id, ct));
        _savedKey.Text = "尚未保存密钥"; Secret.Text = ""; _editingKey = true;
        SetStatus("密钥已删除。连接测试状态已清除。"); RefreshControls();
    }
    private async Task DeleteConnectionAsync()
    {
        if (!await Confirm("删除此模型连接？", "删除连接", "将同时删除此连接保存的密钥；若它是默认模型，会清除默认选择。")) return;
        await Busy(ct => _service.DeleteConnectionAsync(_id, ct));
        _dirty = false; await LoadDraftAsync(_service.Configuration.Connections.FirstOrDefault());
        SetStatus("连接已删除。");
    }
    private Task<bool> Confirm(string title, string action, string detail) => new ConfirmWindow(title, action, detail, destructive: true).ShowDialog<bool>(_owner());

    private async Task FetchModelsAsync()
    {
        string[] models = [];
        await Busy(async ct => models = await _service.GetModelsAsync(_id, ct));
        Models.Text = string.Join('\n', models);
        SetStatus("模型列表已获取，保存后生效；这不代表模型调用已通过测试。");
    }

    internal async Task TestAsync()
    {
        if (_dirty) throw new ModelServiceException(ModelFailure.Configuration, "请先保存配置再测试。");
        var model = _defaultModel.SelectedItem as string ?? ModelIds().FirstOrDefault();
        if (model is null) throw new ModelServiceException(ModelFailure.Configuration, "请先添加并保存至少一个模型 ID。");
        _testResult.Text = "";
        await Busy(async ct =>
        {
            var input = new TextGenerationRequest([new("user", "Reply with OK.")]) { Model = new(_id, model), MaxOutputTokens = 32 };
            var completed = false;
            await foreach (var item in _service.GenerateAsync(input, ct))
            {
                if (item.Text.Length > 0) _testResult.Text = ((_testResult.Text ?? "") + item.Text)[..Math.Min(200, (_testResult.Text?.Length ?? 0) + item.Text.Length)];
                if (item.IsComplete)
                {
                    if (item.FinishReason != "stop") throw new ModelServiceException(ModelFailure.Incomplete, "模型已结束，但未正常完成（可能达到输出上限或内容受限）。");
                    completed = true;
                    SetStatus("测试通过 · 仅证明此模型本次文本调用可用。" + (item.Usage is { } usage ? $" 用量：输入 {usage.InputTokens?.ToString() ?? "未知"} / 输出 {usage.OutputTokens?.ToString() ?? "未知"} token。" : " 用量：服务未返回。"));
                }
            }
            if (!completed) throw new ModelServiceException(ModelFailure.Incomplete, "测试未完整结束，不能标记为通过。");
        }, testing: true);
    }

    private ModelConnection Draft()
    {
        var ids = ModelIds();
        // The simple selector also remembers a connection's preferred model without stealing the global default.
        if (_defaultModel.SelectedItem is string selected && ids.Contains(selected, StringComparer.Ordinal))
            ids = new[] { selected }.Concat(ids.Where(id => id != selected)).ToArray();
        return new(_id, ConnectionName.Text ?? "", BaseUrl.Text?.Trim() ?? "", ids) { RequiresKey = NoKey.IsChecked != true };
    }
    private static bool IsDeepSeek(string? url) => url?.Trim().TrimEnd('/') is "https://api.deepseek.com" or "https://api.deepseek.com/v1";
    private void ApplyPreset()
    {
        if (_loading || Provider.SelectedIndex < 0) return;
        _loading = true;
        var deepseek = Provider.SelectedIndex == 0;
        ConnectionName.Text = deepseek ? "DeepSeek" : "自定义服务";
        BaseUrl.Text = deepseek ? "https://api.deepseek.com" : "";
        Models.Text = deepseek ? "deepseek-chat" : "";
        Secret.Text = ""; _editingKey = true; NoKey.IsChecked = false;
        MoreSettings.IsExpanded = !deepseek;
        _loading = false;
        Changed();
        SetStatus(deepseek ? "已填好官方地址和默认模型，填写密钥后保存即可。" : "请在更多设置中填写 API 地址和模型 ID；不会沿用旧服务的密钥。");
    }
    private string[] ModelIds() => (Models.Text ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToArray();
    private void Changed()
    {
        if (_loading) return;
        _dirty = true; _testResult.Text = ""; SetStatus("配置已更改 · 未保存，需保存后重新测试。");
        RefreshModelChoices(_defaultModel.SelectedItem as string); RefreshControls();
    }
    private void RefreshModelChoices(string? selected)
    {
        var loading = _loading; _loading = true;
        var models = ModelIds();
        _defaultModel.ItemsSource = models; _defaultModel.SelectedItem = models.Contains(selected, StringComparer.Ordinal) ? selected : models.FirstOrDefault();
        _loading = loading;
    }
    private void RefreshControls()
    {
        var saved = _service.Configuration.Connections.FirstOrDefault(c => c.Id == _id);
        _form.IsEnabled = !_busy && !_loading && !_disposed;
        _secretEditor.IsVisible = _editingKey;
        _replace.IsVisible = !_editingKey;
        _deleteKey.IsVisible = saved?.CredentialId is not null;
        _delete.IsVisible = saved is not null;
        _fetch.IsEnabled = saved is not null && !_dirty;
        _test.IsEnabled = saved is not null && !_dirty && !_busy && !_disposed;
        _stop.IsVisible = _busy;
        _reset.IsVisible = _dirty;
        _defaultModel.IsEnabled = ModelIds().Length > 0;
        var selected = _service.Configuration.DefaultModel;
        _global.Text = selected is null ? "当前尚未选择全局默认模型" : "当前全局默认：" + _service.Configuration.Connections.FirstOrDefault(c => c.Id == selected.ConnectionId)?.Name + " / " + selected.ModelId;
        try { _endpoint.Text = "请求至 " + ModelPolicy.Endpoint(Draft(), "chat/completions").GetLeftPart(UriPartial.Path); }
        catch (ModelServiceException) { _endpoint.Text = "填写基础地址后显示实际请求地址"; }
    }
    private async Task Busy(Func<CancellationToken, Task> work, bool testing = false)
    {
        if (_busy) return;
        _operation = new(); _busy = true; RefreshControls();
        if (testing) SetStatus("正在测试 · 只发送固定测试文字，可点击停止。");
        try { await work(_operation.Token); }
        finally { _busy = false; _operation.Dispose(); _operation = null; RefreshControls(); }
    }
    private async void Run(Func<Task> operation)
    {
        try { await operation(); }
        catch (OperationCanceledException) { SetStatus("操作已停止；已收到的内容可能不完整。不会自动重试。"); }
        catch (ModelServiceException e) { SetStatus(e.Message + " 不会自动重试。"); }
        catch { SetStatus("操作未完成，请检查配置或系统凭据访问权限后重试。"); }
    }
    private void SetStatus(string message) => _status.Text = message;
    internal void Leave() { _operation?.Cancel(); Secret.Text = ""; _testResult.Text = ""; }
    public void Dispose() { _disposed = true; Leave(); }
    private static TextBox Field(string name, string? placeholder = null)
    {
        var field = new TextBox { Watermark = placeholder, MinWidth = 0, MaxLength = 2048 };
        AutomationProperties.SetName(field, name); return field;
    }
    private static Grid Labelled(string label, Control field)
    {
        var title = Ui.Text(label, 13); title.VerticalAlignment = VerticalAlignment.Center;
        return new Grid { ColumnDefinitions = new ColumnDefinitions("76,*"), ColumnSpacing = 10, Children = { title, Ui.Column(field, 1) } };
    }
    private sealed record ConnectionOption(Guid Id, string Name) { public override string ToString() => Name; }
}
