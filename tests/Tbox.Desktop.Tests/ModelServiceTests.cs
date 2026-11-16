using System.Net;
using System.Text;
using System.Text.Json;
using Tbox.Core.ModelServices;
using Tbox.Desktop.ModelServices;
using Tbox.Desktop.Platform;
using Xunit;

namespace Tbox.Desktop.Tests;

public sealed class ModelServiceTests
{
    private const string CompleteStream = "data: {\"choices\":[{\"delta\":{\"content\":\"你好\"}}]}\n\ndata: {\"choices\":[{\"delta\":{\"content\":\"世界\"}}]}\n\ndata: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":3,\"completion_tokens\":2}}\n\ndata: [DONE]\n\n";

    [Fact]
    public async Task ImageIsNotSentIfTheConfirmedEndpointChanges()
    {
        var calls = 0;
        using var scope = new Scope((_, _) => { calls++; return Task.FromResult(Response("{}")); });
        var original = scope.Connection();
        var confirmedEndpoint = ModelPolicy.Endpoint(original, "chat/completions");
        await scope.Service.SaveConnectionAsync(original, "fixture-key");
        await scope.Service.SaveConnectionAsync(original with { BaseUrl = "https://changed.example/v1" }, "fixture-key");
        var error = await Assert.ThrowsAsync<ModelServiceException>(() => scope.Service.RecognizeImageAsync(
            new ModelReference(original.Id, "text-one"), confirmedEndpoint, new byte[] { 1 }, CancellationToken.None));
        Assert.Equal(ModelFailure.Configuration, error.Failure);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(200, "{\"choices\":[{\"message\":{\"content\":\"部分结果\"},\"finish_reason\":\"length\"}]}", ModelFailure.Incomplete)]
    [InlineData(400, "private image echoed by server", ModelFailure.Response)]
    public async Task ImageRequestRejectsIncompleteAndUnsupportedResponsesWithoutRetry(int status, string body, ModelFailure failure)
    {
        var calls = 0;
        using var scope = new Scope((_, _) => { calls++; return Task.FromResult(Response(body, "application/json", (HttpStatusCode)status)); });
        var connection = scope.Connection();
        await scope.Service.SaveConnectionAsync(connection, "fixture-key");
        var error = await Assert.ThrowsAsync<ModelServiceException>(() => scope.Service.RecognizeImageAsync(
            new ModelReference(connection.Id, "text-one"), ModelPolicy.Endpoint(connection, "chat/completions"), new byte[] { 1 }, CancellationToken.None));
        Assert.Equal(failure, error.Failure);
        Assert.DoesNotContain(body, error.ToString());
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task SavesTheKeyInPlaintextAndReloadsItWithoutTheCredentialStore()
    {
        using var scope = new Scope();
        var connection = scope.Connection();
        await scope.Service.SaveConnectionAsync(connection, "  test-only-secret ");
        var json = await File.ReadAllTextAsync(scope.Path);
        Assert.Contains("\"ApiKey\": \"test-only-secret\"", json);
        Assert.DoesNotContain("CredentialId", json);
        using var reloaded = new ModelService(scope.Path, scope.Credentials);
        await reloaded.InitializeAsync();
        Assert.Equal("text-one", reloaded.Configuration.ModelOrder[0].ModelId);
        Assert.Equal("test-only-secret", reloaded.KeyOf(connection.Id));
        Assert.Equal(0, scope.Credentials.ReadCount);
        // Caller-owned arrays and returned snapshots cannot mutate live configuration.
        connection.Models[0] = "mutated";
        var snapshot = scope.Service.Configuration; snapshot.Connections[0].Models[0] = "also-mutated";
        Assert.Equal("text-one", scope.Service.Configuration.Connections[0].Models[0]);
    }

    [Fact]
    public async Task StreamsUnicodeUsageAndCompletionThroughTheSharedInterface()
    {
        var calls = 0;
        using var scope = new Scope(async (r, ct) =>
        {
            calls++;
            Assert.Equal("https://api.example.com/v1/chat/completions", r.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", r.Headers.Authorization!.Scheme);
            Assert.Equal("test-only-secret", r.Headers.Authorization.Parameter);
            var body = await r.Content!.ReadAsStringAsync(ct);
            Assert.Contains("text-one", body); Assert.DoesNotContain("test-only-secret", body);
            Assert.DoesNotContain("thinking", body); // Only DeepSeek's own endpoint receives its thinking switch.
            return Response(CompleteStream);
        });
        await scope.Save();
        ITextModelService capability = scope.Service;
        var events = await Collect(capability.GenerateAsync(Input()));
        Assert.Equal("你好世界", string.Concat(events.Select(e => e.Text)));
        Assert.Equal("stop", events.Last().FinishReason);
        Assert.Equal(new ModelUsage(3, 2), events.Last().Usage);
        Assert.Equal(1, calls); // Never automatically retry.
    }

    [Fact]
    public async Task DeepSeekRequestsTurnThinkingOff()
    {
        string? body = null;
        using var scope = new Scope(async (r, ct) => { body = await r.Content!.ReadAsStringAsync(ct); return Response(CompleteStream); });
        var connection = new ModelConnection(Guid.NewGuid(), "DeepSeek", "https://api.deepseek.com", ["deepseek-flash"]);
        await scope.Service.SaveConnectionAsync(connection, "test-only-secret");
        await Collect(scope.Service.GenerateAsync(Input()));
        using var json = JsonDocument.Parse(body!);
        Assert.Equal("disabled", json.RootElement.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal("deepseek-flash", json.RootElement.GetProperty("model").GetString());
    }

    [Theory]
    [InlineData(401, ModelFailure.Authentication)]
    [InlineData(403, ModelFailure.Authentication)]
    [InlineData(429, ModelFailure.RateLimited)]
    [InlineData(500, ModelFailure.Response)]
    [InlineData(302, ModelFailure.Network)]
    public async Task SanitizesProviderErrorsWithoutRetryingOrFollowingRedirects(int status, ModelFailure expected)
    {
        var calls = 0;
        using var scope = new Scope((_, _) =>
        {
            calls++;
            var r = Response("provider echoed test-only-secret and private prompt", "application/json", (HttpStatusCode)status);
            r.Headers.Location = new Uri("https://other.example/steal");
            return Task.FromResult(r);
        });
        await scope.Save();
        var error = await Assert.ThrowsAsync<ModelServiceException>(() => Collect(scope.Service.GenerateAsync(Input())));
        Assert.Equal(expected, error.Failure);
        Assert.DoesNotContain("test-only-secret", error.ToString()); Assert.DoesNotContain("private prompt", error.ToString());
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("data: {\"choices\":[{\"delta\":{\"content\":\"partial\"}}]}\n\n", ModelFailure.Incomplete)]
    [InlineData("data: [DONE]\n\n", ModelFailure.Incomplete)]
    [InlineData("data: {bad json}\n\n", ModelFailure.Response)]
    [InlineData("data: {\"choices\":[{\"delta\":{\"tool_calls\":[{}]}}]}\n\n", ModelFailure.Response)]
    [InlineData("data: {\"error\":{\"message\":\"private text\"}}\n\n", ModelFailure.Response)]
    public async Task RejectsTruncatedMalformedAndToolCallResponses(string stream, ModelFailure expected)
    {
        using var scope = new Scope((_, _) => Task.FromResult(Response(stream)));
        await scope.Save();
        var error = await Assert.ThrowsAsync<ModelServiceException>(() => Collect(scope.Service.GenerateAsync(Input())));
        Assert.Equal(expected, error.Failure); Assert.DoesNotContain("private text", error.ToString());
    }

    [Fact]
    public async Task DistinguishesLengthLimitFromNormalCompletionAndDoesNotInventUsage()
    {
        using var scope = new Scope((_, _) => Task.FromResult(Response("data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"length\"}]}\n\ndata: [DONE]\n\n")));
        await scope.Save();
        var result = Assert.Single(await Collect(scope.Service.GenerateAsync(Input())));
        Assert.Equal("length", result.FinishReason); Assert.Null(result.Usage);
    }

    [Fact]
    public async Task AcceptsNonStreamingJsonFromCompatibleProviders()
    {
        using var scope = new Scope((_, _) => Task.FromResult(Response("{\"choices\":[{\"message\":{\"content\":\"OK\"},\"finish_reason\":\"stop\"}]}", "application/json")));
        await scope.Save();
        var result = await Collect(scope.Service.GenerateAsync(Input()));
        Assert.Equal("OK", result[0].Text); Assert.Equal("stop", result[1].FinishReason);
    }

    [Fact]
    public async Task DeliversTheFirstFragmentBeforeTheProviderFinishesTheResponse()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var scope = new Scope((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new GatedStream(gate.Task)) };
            response.Content.Headers.ContentType = new("text/event-stream");
            return Task.FromResult(response);
        });
        await scope.Save();
        await using var enumerator = scope.Service.GenerateAsync(Input()).GetAsyncEnumerator();
        try
        {
            Assert.True(await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal("first", enumerator.Current.Text);
            Assert.False(gate.Task.IsCompleted);
            gate.SetResult();
            Assert.True(await enumerator.MoveNextAsync()); Assert.Equal("stop", enumerator.Current.FinishReason);
        }
        finally { gate.TrySetResult(); }
    }

    [Fact]
    public async Task BoundsSseLineLengthBeforeAllocatingAnUnboundedLine()
    {
        using var scope = new Scope((_, _) => Task.FromResult(Response("data: " + new string('x', 70000))));
        await scope.Save();
        Assert.Equal(ModelFailure.Response, (await Assert.ThrowsAsync<ModelServiceException>(() => Collect(scope.Service.GenerateAsync(Input())))).Failure);
    }

    [Fact]
    public async Task PreservesCallerCancellationAndClassifiesTimeout()
    {
        using var scope = new Scope(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return Response(CompleteStream); }, TimeSpan.FromMilliseconds(60));
        await scope.Save();
        var timeout = await Assert.ThrowsAsync<ModelServiceException>(() => Collect(scope.Service.GenerateAsync(Input())));
        Assert.Equal(ModelFailure.Timeout, timeout.Failure);
        using var cancel = new CancellationTokenSource();
        var running = Collect(scope.Service.GenerateAsync(Input(), cancel.Token));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }

    [Fact]
    public async Task ConnectionWithoutAKeyNeverSendsAnUnauthenticatedRequest()
    {
        var calls = 0;
        using var scope = new Scope((_, _) => { calls++; return Task.FromResult(Response(CompleteStream)); });
        var id = Guid.NewGuid();
        await File.WriteAllTextAsync(scope.Path, $$$"""{"Connections":[{"Id":"{{{id}}}","Name":"无密钥","BaseUrl":"https://api.example.com/v1","Models":["text-one"]}],"DefaultModel":{"ConnectionId":"{{{id}}}","ModelId":"text-one"}}""");
        Assert.Equal(ModelFailure.Credentials, (await Assert.ThrowsAsync<ModelServiceException>(() => Collect(scope.Service.GenerateAsync(Input())))).Failure);
        Assert.Equal(0, calls);
        Assert.False(scope.Service.HasKey(id));
    }

    [Fact]
    public async Task SavingRequiresAKey()
    {
        using var scope = new Scope();
        Assert.Equal(ModelFailure.Credentials, (await Assert.ThrowsAsync<ModelServiceException>(() => scope.Service.SaveConnectionAsync(scope.Connection(), "  "))).Failure);
        Assert.False(File.Exists(scope.Path));
    }

    [Fact]
    public async Task LegacyCredentialIsMigratedIntoTheFileThenDeleted()
    {
        using var scope = new Scope();
        var id = Guid.NewGuid(); var credential = Guid.NewGuid();
        scope.Credentials.Values[credential] = "legacy-test-secret";
        await File.WriteAllTextAsync(scope.Path, $$$"""{"Connections":[{"Id":"{{{id}}}","Name":"旧连接","BaseUrl":"https://api.example.com/v1","Models":["text-one"],"Protocol":"openai-chat","RequiresKey":true,"CredentialId":"{{{credential}}}"}],"DefaultModel":{"ConnectionId":"{{{id}}}","ModelId":"text-one"}}""");
        await scope.Service.InitializeAsync();
        Assert.Equal("legacy-test-secret", scope.Service.KeyOf(id));
        var json = await File.ReadAllTextAsync(scope.Path);
        Assert.Contains("legacy-test-secret", json);
        Assert.DoesNotContain("CredentialId", json); Assert.DoesNotContain("RequiresKey", json);
        Assert.Empty(scope.Credentials.Values);
        Assert.Equal("text-one", scope.Service.Configuration.ModelOrder[0].ModelId);
    }

    [Fact]
    public async Task UnreadableLegacyCredentialIsKeptForTheNextStart()
    {
        using var scope = new Scope();
        var id = Guid.NewGuid(); var credential = Guid.NewGuid();
        scope.Credentials.Values[credential] = "legacy-test-secret"; scope.Credentials.FailRead = true;
        var original = $$$"""{"Connections":[{"Id":"{{{id}}}","Name":"旧连接","BaseUrl":"https://api.example.com/v1","Models":["text-one"],"CredentialId":"{{{credential}}}"}]}""";
        await File.WriteAllTextAsync(scope.Path, original);
        await scope.Service.InitializeAsync();
        Assert.False(scope.Service.HasKey(id));
        Assert.Equal(original, await File.ReadAllTextAsync(scope.Path));
        Assert.Single(scope.Credentials.Values);
    }

    [Fact]
    public async Task FailedConfigurationCommitKeepsThePreviousKey()
    {
        using var scope = new Scope(); await scope.Save();
        var previous = scope.Service.Configuration.Connections.Single();
        File.Delete(scope.Path); Directory.CreateDirectory(scope.Path); // Force the atomic replace to fail.
        await Assert.ThrowsAsync<ModelServiceException>(() => scope.Service.SaveConnectionAsync(previous, "replacement-test-secret"));
        Assert.Equal("test-only-secret", scope.Service.KeyOf(previous.Id));
    }

    [Fact]
    public async Task DeletingAConnectionRemovesItsKeyAndModelsFromTheOrder()
    {
        using var scope = new Scope(); await scope.Save();
        var c = scope.Service.Configuration.Connections.Single();
        await scope.Service.DeleteConnectionAsync(c.Id);
        Assert.Empty(scope.Service.Configuration.Connections); Assert.Empty(scope.Service.Configuration.ModelOrder);
        Assert.False(scope.Service.HasKey(c.Id));
        Assert.DoesNotContain("test-only-secret", await File.ReadAllTextAsync(scope.Path));
    }

    [Fact]
    public async Task DamagedConfigurationIsNotOverwrittenWithAnEmptyConfiguration()
    {
        using var scope = new Scope(); await File.WriteAllTextAsync(scope.Path, "{broken");
        await Assert.ThrowsAsync<ModelServiceException>(() => scope.Save());
        Assert.Equal("{broken", await File.ReadAllTextAsync(scope.Path));
    }

    [Fact]
    public async Task ModelListIsIndependentOfGenerationAndCanBeOverriddenManually()
    {
        using var scope = new Scope((r, _) =>
        {
            Assert.Equal("GET", r.Method.Method); Assert.EndsWith("/v1/models", r.RequestUri!.AbsoluteUri);
            return Task.FromResult(Response("{\"data\":[{\"id\":\"first\"},{\"id\":\"first\"},{\"id\":\"second\"}]}", "application/json"));
        });
        await scope.Save(); var c = scope.Service.Configuration.Connections.Single();
        Assert.Equal(["first", "second"], await scope.Service.GetModelsAsync(c.Id));
        Assert.Equal(["text-one", "text-two"], scope.Service.Configuration.Connections.Single().Models); // Fetch isn't a save or test.
        await scope.Service.SaveConnectionAsync(c with { Models = ["manually-added"] }, "test-only-secret");
        Assert.Equal("manually-added", scope.Service.Configuration.ModelOrder[0].ModelId);
    }

    [Fact]
    public async Task CrossConnectionOrderSurvivesEditsAndReloadAndSkipsRemovedModels()
    {
        using var scope = new Scope(); await scope.Save();
        var first = scope.Service.Configuration.Connections.Single();
        var second = scope.Connection() with { Name = "第二个连接", Models = ["text-one"] };
        await scope.Service.SaveConnectionAsync(second, "second-key");
        var promoted = new ModelReference(second.Id, "text-one");
        var original = new ModelReference(first.Id, "text-one");
        await scope.Service.MoveModelAsync(promoted, original, after: false);
        await scope.Service.SaveConnectionAsync(first with { Models = ["text-one", "text-two", "new-model"] }, "test-only-secret");
        using var reloaded = new ModelService(scope.Path);
        await reloaded.InitializeAsync();
        Assert.Equal([promoted, original, new(first.Id, "text-two"), new(first.Id, "new-model")], reloaded.Configuration.ModelOrder);
        Assert.Equal(promoted, ModelPolicy.Resolve(reloaded.Configuration, null).Model);
        Assert.Equal("test-only-secret", reloaded.KeyOf(first.Id));
        Assert.Equal("second-key", reloaded.KeyOf(second.Id));
        await Assert.ThrowsAsync<ModelServiceException>(() => reloaded.MoveModelAsync(new(first.Id, "missing"), original, after: true));
        Assert.Equal(promoted, reloaded.Configuration.ModelOrder[0]);
        await reloaded.DeleteConnectionAsync(second.Id);
        Assert.Equal(original, reloaded.Configuration.ModelOrder[0]);
    }

    [Fact]
    public async Task LegacyDefaultBecomesTheFirstModelWithoutRewritingUntilSave()
    {
        using var scope = new Scope();
        var id = Guid.NewGuid();
        var legacy = $$$"""{"Connections":[{"Id":"{{{id}}}","Name":"旧连接","BaseUrl":"https://api.example.com/v1","Models":["first","second"],"ApiKey":"fixture-key"}],"DefaultModel":{"ConnectionId":"{{{id}}}","ModelId":"second"}}""";
        await File.WriteAllTextAsync(scope.Path, legacy);
        await scope.Service.InitializeAsync();
        Assert.Equal(["second", "first"], scope.Service.Configuration.ModelOrder.Select(model => model.ModelId));
        Assert.Equal(legacy, await File.ReadAllTextAsync(scope.Path));
        await scope.Service.MoveModelAsync(new(id, "first"), new(id, "second"), after: false);
        var saved = await File.ReadAllTextAsync(scope.Path);
        Assert.Contains("ModelOrder", saved);
        Assert.DoesNotContain("DefaultModel", saved);
    }

    [Fact]
    public async Task FailedOrderCommitKeepsThePreviousSelection()
    {
        using var scope = new Scope(); await scope.Save();
        var order = scope.Service.Configuration.ModelOrder;
        File.Delete(scope.Path);
        Directory.CreateDirectory(scope.Path);
        await Assert.ThrowsAsync<ModelServiceException>(() => scope.Service.MoveModelAsync(order[1], order[0], after: false));
        Assert.Equal(order, scope.Service.Configuration.ModelOrder);
    }

    private static TextGenerationRequest Input() => new([new("user", "private prompt")]);
    internal static HttpResponseMessage Response(string body, string type = "text/event-stream", HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(body, Encoding.UTF8, type) };
    internal static async Task<List<TextGenerationEvent>> Collect(IAsyncEnumerable<TextGenerationEvent> stream)
    { var events = new List<TextGenerationEvent>(); await foreach (var item in stream) events.Add(item); return events; }

    // Legacy credential store holding keys saved before plaintext storage.
    internal sealed class Credentials : IModelCredentialStore
    {
        public Dictionary<Guid, string> Values { get; } = [];
        public int ReadCount { get; private set; }
        public bool FailRead { get; set; }
        public Task<string?> ReadAsync(Guid id, CancellationToken ct = default)
        { ReadCount++; if (FailRead) throw new ModelServiceException(ModelFailure.Credentials, "模拟凭据读取失败"); return Task.FromResult(Values.GetValueOrDefault(id)); }
        public Task DeleteAsync(Guid id, CancellationToken ct = default) { Values.Remove(id); return Task.CompletedTask; }
    }
    internal sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct); }
    private sealed class GatedStream(Task gate) : Stream
    {
        private int _part;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_part >= 2) return 0;
            if (_part == 1) await gate.WaitAsync(ct);
            var text = _part++ == 0 ? "data: {\"choices\":[{\"delta\":{\"content\":\"first\"}}]}\n\n"
                : "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";
            return Encoding.UTF8.GetBytes(text, buffer.Span);
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private sealed class Scope : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tbox-model-tests-" + Guid.NewGuid().ToString("N"));
        public string Path { get; }
        public Credentials Credentials { get; } = new();
        public ModelService Service { get; }
        public Scope(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? send = null, TimeSpan? timeout = null)
        {
            Directory.CreateDirectory(_directory); Path = System.IO.Path.Combine(_directory, "models.json");
            Service = new(Path, Credentials, new Handler(send ?? ((_, _) => Task.FromResult(Response(CompleteStream))))) { RequestTimeout = timeout ?? TimeSpan.FromSeconds(5) };
        }
        public ModelConnection Connection() => new(Guid.NewGuid(), "测试连接", "https://api.example.com/v1", ["text-one", "text-two"]);
        public Task Save() => Service.SaveConnectionAsync(Connection(), "test-only-secret");
        public void Dispose() { Service.Dispose(); Directory.Delete(_directory, true); }
    }
}
