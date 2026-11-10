using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Tbox.Core.ModelServices;

namespace Tbox.Desktop.ModelServices;

// This adapter is the only place aware of Chat Completions wire types. No response bodies reach logs/errors.
internal sealed class OpenAiChatProvider : IDisposable
{
    private readonly HttpClient _http;
    public OpenAiChatProvider(HttpMessageHandler? handler = null)
    {
        _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private static HttpRequestMessage Request(ModelConnection connection, string resource, string? key, HttpMethod method)
    {
        var request = new HttpRequestMessage(method, ModelPolicy.Endpoint(connection, resource));
        request.Headers.UserAgent.ParseAdd("Tbox/1.0");
        if (!string.IsNullOrEmpty(key)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return request;
    }

    public async Task<string[]> GetModelsAsync(ModelConnection connection, string? key, CancellationToken ct)
    {
        using var request = Request(connection, "models", key, HttpMethod.Get);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        EnsureSuccess(response);
        using var json = JsonDocument.Parse(await ReadBoundedAsync(response.Content, ct));
        if (!json.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) throw Invalid();
        var models = data.EnumerateArray().Select(m => m.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null)
            .Where(id => !string.IsNullOrWhiteSpace(id) && id.Length <= 200 && !id.Any(char.IsControl)).Select(id => id!).Distinct(StringComparer.Ordinal).Take(100).ToArray();
        if (models.Length == 0) throw new ModelServiceException(ModelFailure.Response, "服务未返回可用模型，请手动添加模型 ID。");
        return models;
    }

    public async IAsyncEnumerable<TextGenerationEvent> GenerateAsync(ModelConnection connection, string modelId, string? key,
        TextGenerationRequest input, [EnumeratorCancellation] CancellationToken ct)
    {
        using var request = Request(connection, "chat/completions", key, HttpMethod.Post);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Content = new StringContent(JsonSerializer.Serialize(new
        {
            model = modelId, messages = input.Messages.Select(m => new { role = m.Role, content = m.Content }),
            stream = true, max_tokens = input.MaxOutputTokens
        }), Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        EnsureSuccess(response);
        if (response.Content.Headers.ContentType?.MediaType == "application/json")
        {
            using var json = JsonDocument.Parse(await ReadBoundedAsync(response.Content, ct));
            var root = json.RootElement;
            if (root.TryGetProperty("error", out _)) throw Invalid();
            var choice = root.GetProperty("choices")[0];
            if (choice.TryGetProperty("message", out var message) && message.TryGetProperty("tool_calls", out _)) throw UnsupportedTools();
            var content = choice.GetProperty("message").GetProperty("content").GetString() ?? "";
            var reason = choice.GetProperty("finish_reason").GetString() ?? throw Incomplete();
            if (reason == "tool_calls") throw UnsupportedTools();
            if (content.Length > 100_000) throw Invalid();
            if (content.Length > 0) yield return new(content);
            yield return new(FinishReason: reason, Usage: Usage(root));
            yield break;
        }
        if (response.Content.Headers.ContentType?.MediaType != "text/event-stream") throw Invalid();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var lines = new BoundedLineReader(reader);
        var data = new StringBuilder();
        string? finish = null;
        ModelUsage? usage = null;
        var total = 0;
        var output = 0;
        while (true)
        {
            var line = await lines.ReadAsync(ct);
            if (line is not null) { total += line.Length; if (total > 2_000_000) throw Invalid(); }
            if (line is { Length: > 0 })
            {
                if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    data.Append(line.AsSpan(5).TrimStart()); data.Append('\n');
                    if (data.Length > 65_536) throw Invalid();
                }
                continue;
            }
            if (data.Length > 0)
            {
                var payload = data.ToString().Trim(); data.Clear();
                if (payload == "[DONE]") { if (finish is null) throw Incomplete(); break; }
                using var json = JsonDocument.Parse(payload);
                var root = json.RootElement;
                if (root.TryGetProperty("error", out _)) throw Invalid();
                usage = Usage(root) ?? usage;
                if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array) throw Invalid();
                if (choices.GetArrayLength() > 0)
                {
                    var choice = choices[0];
                    if (choice.TryGetProperty("delta", out var delta))
                    {
                        if (delta.TryGetProperty("tool_calls", out _) || delta.TryGetProperty("function_call", out _)) throw UnsupportedTools();
                        if (delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String && content.GetString() is { Length: > 0 } text)
                        {
                            if (finish is not null || (output += text.Length) > 100_000) throw Invalid();
                            yield return new(text);
                        }
                    }
                    if (choice.TryGetProperty("finish_reason", out var reason) && reason.ValueKind == JsonValueKind.String)
                    {
                        finish = reason.GetString();
                        if (finish is "tool_calls" or "function_call") throw UnsupportedTools();
                    }
                }
            }
            if (line is null) break;
        }
        if (finish is null) throw Incomplete();
        yield return new(FinishReason: finish, Usage: usage);
    }

    private static ModelUsage? Usage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) return null;
        int? Token(string name) => usage.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) && number >= 0 ? number : null;
        return new(Token("prompt_tokens"), Token("completion_tokens"));
    }

    private sealed class BoundedLineReader(StreamReader reader)
    {
        private readonly char[] _buffer = new char[4096];
        private int _offset, _count;
        public async Task<string?> ReadAsync(CancellationToken ct)
        {
            // Bound before allocation without an async call for each character.
            var line = new StringBuilder();
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (_offset == _count)
                {
                    _count = await reader.ReadAsync(_buffer.AsMemory(), ct); _offset = 0;
                    if (_count == 0) return line.Length == 0 ? null : line.ToString();
                }
                var next = _buffer[_offset++];
                if (next == '\n') return line.ToString().TrimEnd('\r');
                line.Append(next);
                if (line.Length > 65_536) throw Invalid();
            }
        }
    }

    private static async Task<string> ReadBoundedAsync(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(chunk.AsMemory(), ct)) > 0)
        {
            if (buffer.Length + count > 1_000_000) throw Invalid();
            buffer.Write(chunk, 0, count);
        }
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }
    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        throw response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new ModelServiceException(ModelFailure.Authentication, "服务拒绝访问，请检查密钥和模型权限（401/403）。"),
            HttpStatusCode.TooManyRequests => new ModelServiceException(ModelFailure.RateLimited, "服务限流或额度不足（429），请稍后手动重试。"),
            >= HttpStatusCode.MultipleChoices and < HttpStatusCode.BadRequest => new ModelServiceException(ModelFailure.Network, "服务返回重定向，出于密钥安全已停止；请核对 API 地址。"),
            _ => new ModelServiceException(ModelFailure.Response, $"模型服务返回 HTTP {(int)response.StatusCode}，请检查地址、模型及接口兼容性。")
        };
    }
    private static ModelServiceException Invalid() => new(ModelFailure.Response, "服务响应格式无效或超过大小上限，请确认接口兼容性。");
    private static ModelServiceException Incomplete() => new(ModelFailure.Incomplete, "响应提前结束，已收到的内容仅为部分结果；请手动重试。");
    private static ModelServiceException UnsupportedTools() => new(ModelFailure.Response, "模型返回了工具调用；当前公共能力仅支持文本，不执行任何工具或命令。");
    public void Dispose() => _http.Dispose();
}
