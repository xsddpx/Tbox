using System.Runtime.CompilerServices;
using System.Text.Json;
using Tbox.Core.ModelServices;
using Tbox.Desktop.Platform;

namespace Tbox.Desktop.ModelServices;

public sealed class ModelService : ITextModelService, IDisposable
{
    private readonly ModelConfigurationStore _store;
    private readonly IModelCredentialStore _credentials;
    private readonly OpenAiChatProvider _provider;
    private readonly SemaphoreSlim _changes = new(1, 1), _requests = new(3, 3);
    private readonly CancellationTokenSource _shutdown = new();
    private ModelConfiguration _configuration = new();
    private bool _loaded;
    internal TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(120);
    public ModelConfiguration Configuration => Clone(_configuration);
    public ModelService(string path, IModelCredentialStore credentials, HttpMessageHandler? handler = null)
    { _store = new(path); _credentials = credentials; _provider = new(handler); }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _changes.WaitAsync(ct);
        try { if (!_loaded) { _configuration = await _store.LoadAsync(ct); _loaded = true; } }
        finally { _changes.Release(); }
    }

    public Task<bool> HasCredentialAsync(ModelConnection connection, CancellationToken ct = default) => connection.CredentialId is { } id
        ? _credentials.ContainsAsync(id, ct) : Task.FromResult(false);

    // A replacement gets a fresh credential reference. If the JSON write fails the old connection/key remains intact.
    public async Task SaveConnectionAsync(ModelConnection draft, string? replacement, bool removeKey, bool makeDefault, string? defaultModel, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await _changes.WaitAsync(ct);
        Guid? addedKey = null;
        try
        {
            var connection = ModelPolicy.Validate(draft);
            var previous = _configuration.Connections.FirstOrDefault(c => c.Id == connection.Id);
            if (previous is null && _configuration.Connections.Length >= 20) throw new ModelServiceException(ModelFailure.Configuration, "最多保存 20 个模型连接。");
            if (replacement is not null && (string.IsNullOrWhiteSpace(replacement) || replacement.Length > 1280 || replacement.Any(char.IsControl)))
                throw new ModelServiceException(ModelFailure.Credentials, "密钥不能为空、包含控制字符或超过 1280 字符。");
            if (previous?.CredentialId is not null && replacement is null && !removeKey
                && new Uri(previous.BaseUrl).GetLeftPart(UriPartial.Authority) != new Uri(connection.BaseUrl).GetLeftPart(UriPartial.Authority))
                throw new ModelServiceException(ModelFailure.Credentials, "API 服务地址已更换，请替换或删除旧密钥，避免发送给其他服务。");
            connection = connection with { CredentialId = removeKey ? null : previous?.CredentialId };
            if (replacement is not null)
            {
                addedKey = Guid.NewGuid();
                await _credentials.WriteAsync(addedKey.Value, replacement, ct);
                connection = connection with { CredentialId = addedKey };
            }
            if (connection.RequiresKey && connection.CredentialId is null) throw new ModelServiceException(ModelFailure.Credentials, "请填写 API 密钥，或明确选择此连接无需密钥。");
            var connections = _configuration.Connections.Where(c => c.Id != connection.Id).Append(connection).ToArray();
            var selected = _configuration.DefaultModel;
            if (makeDefault) selected = new(connection.Id, defaultModel ?? "");
            else if (selected?.ConnectionId == connection.Id) selected = null;
            var next = new ModelConfiguration { Connections = connections, DefaultModel = selected };
            if (next.DefaultModel is not null) ModelPolicy.Resolve(next, next.DefaultModel);
            await _store.SaveAsync(next, ct);
            _configuration = Clone(next);
            addedKey = null;
            if (previous?.CredentialId is { } oldKey && oldKey != connection.CredentialId) await CleanupKeyAsync(oldKey);
        }
        finally
        {
            if (addedKey is { } uncommitted) await CleanupKeyAsync(uncommitted);
            _changes.Release();
        }
    }

    public async Task DeleteConnectionAsync(Guid id, CancellationToken ct = default)
    {
        await InitializeAsync(ct); await _changes.WaitAsync(ct);
        try
        {
            var previous = _configuration.Connections.FirstOrDefault(c => c.Id == id);
            // Delete the credential first. A failure leaves the connection visible, rather than silently orphaning a secret.
            if (previous?.CredentialId is { } key) await _credentials.DeleteAsync(key, ct);
            var next = _configuration with { Connections = _configuration.Connections.Where(c => c.Id != id).ToArray(), DefaultModel = _configuration.DefaultModel?.ConnectionId == id ? null : _configuration.DefaultModel };
            await _store.SaveAsync(next, ct); _configuration = Clone(next);
        }
        finally { _changes.Release(); }
    }

    public async Task DeleteCredentialAsync(Guid id, CancellationToken ct = default)
    {
        await InitializeAsync(ct); await _changes.WaitAsync(ct);
        try
        {
            var previous = _configuration.Connections.FirstOrDefault(c => c.Id == id) ?? throw new ModelServiceException(ModelFailure.Configuration, "连接不存在。");
            if (previous.CredentialId is { } key) await _credentials.DeleteAsync(key, ct);
            var next = _configuration with { Connections = _configuration.Connections.Select(c => c.Id == id ? c with { CredentialId = null } : c).ToArray() };
            await _store.SaveAsync(next, ct); _configuration = Clone(next);
        }
        finally { _changes.Release(); }
    }

    public async Task<string[]> GetModelsAsync(Guid connectionId, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var connection = _configuration.Connections.FirstOrDefault(c => c.Id == connectionId) ?? throw new ModelServiceException(ModelFailure.Configuration, "请先保存连接。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token); timeout.CancelAfter(RequestTimeout);
        try { return await _provider.GetModelsAsync(connection, await KeyAsync(connection, timeout.Token), timeout.Token); }
        catch (Exception e) { throw Sanitize(e, ct); }
    }

    public async IAsyncEnumerable<TextGenerationEvent> GenerateAsync(TextGenerationRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        ModelPolicy.ValidateRequest(request);
        var (connection, model) = ModelPolicy.Resolve(_configuration, request.Model);
        // Snapshot mutable caller-owned collections before the first network await.
        request = request with { Messages = request.Messages.Select(m => m with { }).ToArray() };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token); timeout.CancelAfter(RequestTimeout);
        var acquired = false;
        try
        {
            string? key;
            try { await _requests.WaitAsync(timeout.Token); acquired = true; key = await KeyAsync(connection, timeout.Token); }
            catch (Exception e) { throw Sanitize(e, cancellationToken); }
            await using var events = _provider.GenerateAsync(connection, model.ModelId, key, request, timeout.Token).GetAsyncEnumerator(timeout.Token);
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

    private async Task<string?> KeyAsync(ModelConnection connection, CancellationToken ct)
    {
        if (!connection.RequiresKey) return null;
        var key = connection.CredentialId is { } id ? await _credentials.ReadAsync(id, ct) : null;
        if (connection.RequiresKey && string.IsNullOrEmpty(key)) throw new ModelServiceException(ModelFailure.Credentials, "未找到此连接的密钥，请在模型服务中重新填写。");
        return key;
    }
    private static Exception Sanitize(Exception error, CancellationToken caller) => error switch
    {
        ModelServiceException => error,
        OperationCanceledException when caller.IsCancellationRequested => new OperationCanceledException(caller),
        OperationCanceledException => new ModelServiceException(ModelFailure.Timeout, "模型请求超时或应用正在退出；已收到的内容可能不完整。"),
        HttpRequestException or IOException => new ModelServiceException(ModelFailure.Network, "无法连接模型服务，请检查网络、代理和 API 地址。"),
        JsonException or InvalidOperationException or KeyNotFoundException or FormatException or IndexOutOfRangeException => new ModelServiceException(ModelFailure.Response, "服务响应与接口协议不兼容，请检查 API 配置。"),
        _ => new ModelServiceException(ModelFailure.Response, "模型请求未完成，请检查连接配置后重试。")
    };
    private async Task CleanupKeyAsync(Guid key)
    {
        try { await _credentials.DeleteAsync(key); }
        catch { AppLog.Write("Retired model credential cleanup failed; no credential content recorded."); }
    }
    private static ModelConfiguration Clone(ModelConfiguration c) => c with { Connections = c.Connections.Select(connection => connection with { Models = connection.Models.ToArray() }).ToArray() };
    public void Dispose() { _shutdown.Cancel(); _provider.Dispose(); }
}
