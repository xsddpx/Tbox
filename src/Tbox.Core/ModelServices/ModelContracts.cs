namespace Tbox.Core.ModelServices;

// Public contracts contain no provider HTTP types or credentials. Tools depend only on these contracts.
public sealed record ModelReference(Guid ConnectionId, string ModelId);
public sealed record ModelConnection(Guid Id, string Name, string BaseUrl, string[] Models)
{
    public string Protocol { get; init; } = "openai-chat";
    public bool RequiresKey { get; init; } = true;
    public Guid? CredentialId { get; init; }
}
public sealed record ModelConfiguration
{
    public ModelConnection[] Connections { get; init; } = [];
    public ModelReference? DefaultModel { get; init; }
}
public sealed record ModelMessage(string Role, string Content);
public sealed record TextGenerationRequest(IReadOnlyList<ModelMessage> Messages)
{
    public ModelReference? Model { get; init; }
    public int MaxOutputTokens { get; init; } = 1024;
}
public sealed record ModelUsage(int? InputTokens, int? OutputTokens);
public sealed record TextGenerationEvent(string Text = "", string? FinishReason = null, ModelUsage? Usage = null)
{
    public bool IsComplete => FinishReason is not null;
}
public interface ITextModelService
{
    IAsyncEnumerable<TextGenerationEvent> GenerateAsync(TextGenerationRequest request, CancellationToken cancellationToken = default);
}

public enum ModelFailure { Configuration, Credentials, Authentication, RateLimited, Timeout, Network, Response, Incomplete }
public sealed class ModelServiceException(ModelFailure failure, string message) : Exception(message)
{
    public ModelFailure Failure { get; } = failure;
}

public static class ModelPolicy
{
    public static Uri Endpoint(ModelConnection connection, string resource)
    {
        if (connection.Protocol != "openai-chat") throw Invalid("当前仅支持 OpenAI 兼容 Chat Completions 协议。");
        if (!Uri.TryCreate(connection.BaseUrl, UriKind.Absolute, out var uri)
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)
            || uri.HostNameType == UriHostNameType.Unknown)
            throw Invalid("API 地址必须是完整地址，不能包含账号、查询参数或片段。");
        var local = uri.Host is "localhost" or "127.0.0.1" or "[::1]";
        if (uri.Scheme != "https" && !(uri.Scheme == "http" && local))
            throw Invalid("远程 API 必须使用 HTTPS；仅本机回环地址允许 HTTP。");
        if (uri.AbsolutePath.TrimEnd('/').EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            throw Invalid("请填写 API 基础地址（例如以 /v1 结尾），不要填写 /chat/completions。");
        return new Uri(uri.AbsoluteUri.TrimEnd('/') + "/" + resource);
    }

    public static ModelConnection Validate(ModelConnection connection)
    {
        if (connection.Id == Guid.Empty || string.IsNullOrWhiteSpace(connection.Name) || connection.Name.Length > 80)
            throw Invalid("请填写不超过 80 字的连接名称。");
        Endpoint(connection, "models");
        if (connection.Models.Length > 100 || connection.Models.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 200 || id.Any(char.IsControl)))
            throw Invalid("模型 ID 不能为空、包含控制字符或超过 200 字；每个连接最多 100 个模型。");
        return connection with { Name = connection.Name.Trim(), BaseUrl = connection.BaseUrl.Trim().TrimEnd('/'), Models = connection.Models.Distinct(StringComparer.Ordinal).ToArray() };
    }

    public static (ModelConnection Connection, ModelReference Model) Resolve(ModelConfiguration configuration, ModelReference? requested)
    {
        var model = requested ?? configuration.DefaultModel ?? throw Invalid("请先在模型服务中选择默认模型。");
        var connection = configuration.Connections.FirstOrDefault(c => c.Id == model.ConnectionId)
            ?? throw Invalid("指定的模型连接已不存在，请重新选择。");
        Validate(connection);
        if (!connection.Models.Contains(model.ModelId, StringComparer.Ordinal)) throw Invalid("指定模型不在此连接中，请重新选择。");
        return (connection, model);
    }

    public static void ValidateRequest(TextGenerationRequest request)
    {
        if (request.Messages.Count is < 1 or > 100 || request.Messages.Any(m => m.Role is not ("system" or "user" or "assistant") || m.Content is null)
            || request.Messages.Sum(m => (long)m.Content.Length) is < 1 or > 100_000 || request.MaxOutputTokens is < 1 or > 16_384)
            throw Invalid("文本请求无效：最多 100 条消息、10 万字符，输出上限为 1–16384 token。");
    }

    private static ModelServiceException Invalid(string message) => new(ModelFailure.Configuration, message);
}
