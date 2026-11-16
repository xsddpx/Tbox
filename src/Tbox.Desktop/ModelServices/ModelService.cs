using System.Runtime.CompilerServices;
using System.Text.Json;
using Tbox.Core.ModelServices;
using Tbox.Desktop.Platform;

namespace Tbox.Desktop.ModelServices;

public sealed class ModelService : ITextModelService, IDisposable
{
    internal const int MaximumRecognitionImageBytes = 8 * 1024 * 1024;
    private readonly ModelConfigurationStore _store;
    private readonly IModelCredentialStore? _legacyCredentials;
    private readonly OpenAiChatProvider _provider;
    private readonly SemaphoreSlim _changes = new(1, 1), _requests = new(3, 3);
    private readonly CancellationTokenSource _shutdown = new();
    private ModelConfiguration _configuration = new();
    // API keys stay out of ModelConfiguration so tools never receive them.
    private Dictionary<Guid, string> _keys = [];
    private Dictionary<Guid, Guid> _legacy = [];
    private bool _loaded;
    internal TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(120);
    public ModelConfiguration Configuration => Clone(_configuration);
    public ModelService(string path, IModelCredentialStore? legacyCredentials = null, HttpMessageHandler? handler = null)
    { _store = new(path); _legacyCredentials = legacyCredentials; _provider = new(handler); }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _changes.WaitAsync(ct);
        try
        {
            if (_loaded) return;
            var snapshot = await _store.LoadAsync(ct);
            _configuration = snapshot.Configuration; _keys = new(snapshot.Keys); _legacy = new(snapshot.Legacy);
            _loaded = true;
            await MigrateLegacyKeysAsync(ct);
        }
        finally { _changes.Release(); }
    }

    // Copy keys saved by earlier versions into the configuration file; delete the old credential only after the file is committed.
    private async Task MigrateLegacyKeysAsync(CancellationToken ct)
    {
        if (_legacy.Count == 0 || _legacyCredentials is null) return;
        var migrated = new List<Guid>();
        var keys = new Dictionary<Guid, string>(_keys);
        var legacy = new Dictionary<Guid, Guid>(_legacy);
        foreach (var (connection, credential) in _legacy)
        {
            string? key;
            try { key = await _legacyCredentials.ReadAsync(credential, ct); }
            catch (ModelServiceException) { AppLog.Write("Legacy model credential could not be read; migration will retry on next start."); continue; }
            if (!string.IsNullOrWhiteSpace(key)) keys[connection] = key.Trim();
            legacy.Remove(connection); migrated.Add(credential);
        }
        if (migrated.Count == 0) return;
        try { await _store.SaveAsync(_configuration, keys, legacy, ct); }
        catch (ModelServiceException) { AppLog.Write("Model key migration could not save the configuration; legacy credentials kept."); return; }
        _keys = keys; _legacy = legacy;
        foreach (var credential in migrated) await CleanupLegacyAsync(credential);
    }

    public bool HasKey(Guid connectionId) => _keys.ContainsKey(connectionId);

    // The settings page shows the saved key masked and may reveal it on request; tools use GenerateAsync instead.
    internal string? KeyOf(Guid connectionId) => _keys.GetValueOrDefault(connectionId);

    public async Task SaveConnectionAsync(ModelConnection draft, string key, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await _changes.WaitAsync(ct);
        try
        {
            var connection = ModelPolicy.Validate(draft);
            var previous = _configuration.Connections.FirstOrDefault(c => c.Id == connection.Id);
            if (previous is null && _configuration.Connections.Length >= 20) throw new ModelServiceException(ModelFailure.Configuration, "最多保存 20 个模型连接。");
            key = ModelKey.Validate(key);
            var connections = previous is null
                ? _configuration.Connections.Append(connection).ToArray()
                : _configuration.Connections.Select(c => c.Id == connection.Id ? connection : c).ToArray();
            var next = _configuration with { Connections = connections };
            next = next with { ModelOrder = ModelPolicy.OrderedModels(next) };
            var keys = new Dictionary<Guid, string>(_keys) { [connection.Id] = key };
            var legacy = new Dictionary<Guid, Guid>(_legacy);
            var retired = legacy.Remove(connection.Id, out var old) ? old : (Guid?)null;
            await _store.SaveAsync(next, keys, legacy, ct);
            _configuration = Clone(next); _keys = keys; _legacy = legacy;
            if (retired is { } credential) await CleanupLegacyAsync(credential);
        }
        finally { _changes.Release(); }
    }

    public async Task DeleteConnectionAsync(Guid id, CancellationToken ct = default)
    {
        await InitializeAsync(ct); await _changes.WaitAsync(ct);
        try
        {
            var next = _configuration with
            {
                Connections = _configuration.Connections.Where(c => c.Id != id).ToArray(),
                ModelOrder = _configuration.ModelOrder.Where(model => model.ConnectionId != id).ToArray()
            };
            var keys = new Dictionary<Guid, string>(_keys); keys.Remove(id);
            var legacy = new Dictionary<Guid, Guid>(_legacy);
            var retired = legacy.Remove(id, out var old) ? old : (Guid?)null;
            await _store.SaveAsync(next, keys, legacy, ct);
            _configuration = Clone(next); _keys = keys; _legacy = legacy;
            if (retired is { } credential) await CleanupLegacyAsync(credential);
        }
        finally { _changes.Release(); }
    }

    public async Task<string[]> GetModelsAsync(Guid connectionId, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var connection = _configuration.Connections.FirstOrDefault(c => c.Id == connectionId) ?? throw new ModelServiceException(ModelFailure.Configuration, "请先保存连接。");
        return await GetModelsAsync(connection, Key(connection), ct);
    }

    internal async Task<string[]> GetModelsAsync(ModelConnection draft, string key, CancellationToken ct)
    {
        key = ModelKey.Validate(key);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token); timeout.CancelAfter(RequestTimeout);
        try { return await _provider.GetModelsAsync(draft, key, timeout.Token); }
        catch (Exception e) { throw Sanitize(e, ct); }
    }

    public async Task MoveModelAsync(ModelReference model, ModelReference target, bool after, CancellationToken ct = default)
    {
        await InitializeAsync(ct); await _changes.WaitAsync(ct);
        try
        {
            if (model == target) return;
            ModelPolicy.Resolve(_configuration, model);
            ModelPolicy.Resolve(_configuration, target);
            var order = _configuration.ModelOrder.ToList();
            order.Remove(model);
            order.Insert(order.IndexOf(target) + (after ? 1 : 0), model);
            if (order.SequenceEqual(_configuration.ModelOrder)) return;
            var next = _configuration with { ModelOrder = order.ToArray() };
            await _store.SaveAsync(next, _keys, _legacy, ct);
            _configuration = Clone(next);
        }
        finally { _changes.Release(); }
    }

    public async IAsyncEnumerable<TextGenerationEvent> GenerateAsync(TextGenerationRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        ModelPolicy.ValidateRequest(request);
        var (connection, model) = ModelPolicy.Resolve(_configuration, request.Model);
        var key = Key(connection);
        // Snapshot mutable caller-owned collections before the first network await.
        request = request with { Messages = request.Messages.Select(m => m with { }).ToArray() };
        await foreach (var item in GenerateAsync(connection, model.ModelId, key, request, cancellationToken))
            yield return item;
    }

    internal async Task<ModelUsage?> TestConnectionAsync(ModelConnection draft, string key, CancellationToken ct)
    {
        var connection = ModelPolicy.Validate(draft);
        var model = connection.Models.FirstOrDefault()
            ?? throw new ModelServiceException(ModelFailure.Configuration, "请先添加至少一个模型 ID。");
        key = ModelKey.Validate(key);
        var request = new TextGenerationRequest([new("user", "Reply with OK.")]) { MaxOutputTokens = 32 };
        var receivedText = false;
        var completed = false;
        ModelUsage? usage = null;
        await foreach (var item in GenerateAsync(connection, model, key, request, ct))
        {
            receivedText |= !string.IsNullOrWhiteSpace(item.Text);
            if (!item.IsComplete) continue;
            if (item.FinishReason != "stop")
                throw new ModelServiceException(ModelFailure.Incomplete, "模型已结束，但未正常完成（可能达到输出上限或内容受限）。");
            completed = true;
            usage = item.Usage;
        }
        ct.ThrowIfCancellationRequested();
        if (!completed || !receivedText)
            throw new ModelServiceException(ModelFailure.Incomplete, "未收到完整测试结果。");
        return usage;
    }

    private async IAsyncEnumerable<TextGenerationEvent> GenerateAsync(ModelConnection connection, string model, string key,
        TextGenerationRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token); timeout.CancelAfter(RequestTimeout);
        var acquired = false;
        try
        {
            try { await _requests.WaitAsync(timeout.Token); acquired = true; }
            catch (Exception e) { throw Sanitize(e, cancellationToken); }
            await using var events = _provider.GenerateAsync(connection, model, key, request, timeout.Token).GetAsyncEnumerator(timeout.Token);
            while (true)
            {
                TextGenerationEvent next;
                try { if (!await events.MoveNextAsync()) break; next = events.Current; }
                catch (Exception e) { throw Sanitize(e, cancellationToken); }
                yield return next;
            }
        }
        finally { if (acquired) _requests.Release(); }
    }

    internal async Task<string> RecognizeImageAsync(ModelReference model, Uri confirmedEndpoint, ReadOnlyMemory<byte> png, CancellationToken ct)
    {
        if (png.Length is 0 or > MaximumRecognitionImageBytes)
            throw new ModelServiceException(ModelFailure.Configuration, "AI 识别支持不超过 8 MiB 的图片，请截取更小的区域。");
        await InitializeAsync(ct);
        var (connection, reference) = ModelPolicy.Resolve(_configuration, model);
        var endpoint = ModelPolicy.Endpoint(connection, "chat/completions");
        if (!string.Equals(endpoint.AbsoluteUri, confirmedEndpoint.AbsoluteUri, StringComparison.Ordinal))
            throw new ModelServiceException(ModelFailure.Configuration, "模型服务地址已改变，请重新确认发送。");
        var key = Key(connection);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token);
        timeout.CancelAfter(RequestTimeout);
        var acquired = false;
        try
        {
            await _requests.WaitAsync(timeout.Token).ConfigureAwait(false);
            acquired = true;
            return await _provider.RecognizeImageAsync(connection, reference.ModelId, key, png, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception error) { throw Sanitize(error, ct); }
        finally { if (acquired) _requests.Release(); }
    }

    private string Key(ModelConnection connection) => _keys.TryGetValue(connection.Id, out var key)
        ? key : throw new ModelServiceException(ModelFailure.Credentials, "此连接还没有 API 密钥，请在模型服务中填写。");
    private static Exception Sanitize(Exception error, CancellationToken caller) => error switch
    {
        ModelServiceException => error,
        OperationCanceledException when caller.IsCancellationRequested => new OperationCanceledException(caller),
        OperationCanceledException => new ModelServiceException(ModelFailure.Timeout, "模型请求超时或应用正在退出；已收到的内容可能不完整。"),
        HttpRequestException or IOException => new ModelServiceException(ModelFailure.Network, "无法连接模型服务，请检查网络、代理和 API 地址。"),
        JsonException or InvalidOperationException or KeyNotFoundException or FormatException or IndexOutOfRangeException => new ModelServiceException(ModelFailure.Response, "服务响应与接口协议不兼容，请检查 API 配置。"),
        _ => new ModelServiceException(ModelFailure.Response, "模型请求未完成，请检查连接配置后重试。")
    };
    private async Task CleanupLegacyAsync(Guid credential)
    {
        if (_legacyCredentials is null) return;
        try { await _legacyCredentials.DeleteAsync(credential); }
        catch { AppLog.Write("Legacy model credential cleanup failed; no credential content recorded."); }
    }
    private static ModelConfiguration Clone(ModelConfiguration c) => c with
    {
        Connections = c.Connections.Select(connection => connection with { Models = connection.Models.ToArray() }).ToArray(),
        ModelOrder = c.ModelOrder.ToArray()
    };
    public void Dispose() { _shutdown.Cancel(); _provider.Dispose(); }
}
