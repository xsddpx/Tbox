using Tbox.Core.ModelServices;
using Xunit;

namespace Tbox.Core.Tests;

public sealed class ModelPolicyTests
{
    private static ModelConnection Connection(string url) => new(Guid.NewGuid(), "示例", url, ["text-model"]);
    [Theory]
    [InlineData("https://api.example.com/v1", "https://api.example.com/v1/chat/completions")]
    [InlineData("https://api.example.com/custom/", "https://api.example.com/custom/chat/completions")]
    [InlineData("http://127.0.0.1:11434/v1", "http://127.0.0.1:11434/v1/chat/completions")]
    [InlineData("http://localhost:8000", "http://localhost:8000/chat/completions")]
    [InlineData("http://[::1]:8000/v1", "http://[::1]:8000/v1/chat/completions")]
    public void BuildsAnExplicitEndpointWithoutDuplicatingV1(string url, string expected) =>
        Assert.Equal(expected, ModelPolicy.Endpoint(Connection(url), "chat/completions").AbsoluteUri);

    [Theory]
    [InlineData("http://api.example.com/v1")]
    [InlineData("https://user:secret@api.example.com/v1")]
    [InlineData("https://api.example.com/v1?key=secret")]
    [InlineData("https://api.example.com/v1#fragment")]
    [InlineData("https://api.example.com/v1/chat/completions")]
    [InlineData("file:///etc/passwd")]
    [InlineData("http://127.0.0.2:8000/v1")]
    public void RejectsUnsafeOrAmbiguousAddresses(string url) => Assert.Throws<ModelServiceException>(() => ModelPolicy.Endpoint(Connection(url), "models"));

    [Fact]
    public void ResolvesExplicitModelIndependentlyOfDefault()
    {
        var first = Connection("https://first.example/v1"); var second = Connection("https://second.example/v1");
        var config = new ModelConfiguration { Connections = [first, second], DefaultModel = new(first.Id, "text-model") };
        Assert.Equal(first.Id, ModelPolicy.Resolve(config, null).Connection.Id);
        Assert.Equal(second.Id, ModelPolicy.Resolve(config, new(second.Id, "text-model")).Connection.Id);
        Assert.Throws<ModelServiceException>(() => ModelPolicy.Resolve(config, new(first.Id, "unlisted")));
        Assert.Throws<ModelServiceException>(() => ModelPolicy.Resolve(config with { DefaultModel = null }, null));
    }

    [Fact]
    public void RejectsUnsupportedProtocolAndInvalidInputs()
    {
        Assert.Throws<ModelServiceException>(() => ModelPolicy.Validate(Connection("https://api.example/v1") with { Protocol = "anthropic" }));
        Assert.Throws<ModelServiceException>(() => ModelPolicy.Validate(Connection("https://api.example/v1") with { Models = ["a\nb"] }));
        Assert.Throws<ModelServiceException>(() => ModelPolicy.ValidateRequest(new([])));
        Assert.Throws<ModelServiceException>(() => ModelPolicy.ValidateRequest(new([new("tool", "run")] )));
        Assert.Throws<ModelServiceException>(() => ModelPolicy.ValidateRequest(new([new("user", new string('a', 100001))])));
        Assert.Throws<ModelServiceException>(() => ModelPolicy.ValidateRequest(new([new("user", "ok")]) { MaxOutputTokens = 0 }));
    }
}
