using System.Text.Json;
using System.Text.Json.Serialization;
using Tbox.Core.ModelServices;

namespace Tbox.Desktop.ModelServices;

// The file holds each connection's API key in plaintext; the public ModelConfiguration never carries keys.
// Legacy maps a connection to its pre-1.1.75 system-credential reference until that key is migrated.
internal sealed record ModelFileSnapshot(ModelConfiguration Configuration, IReadOnlyDictionary<Guid, string> Keys, IReadOnlyDictionary<Guid, Guid> Legacy);

internal sealed class ModelConfigurationStore(string path)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private sealed record StoredConnection(Guid Id, string Name, string BaseUrl, string[] Models)
    {
        public string Protocol { get; init; } = "openai-chat";
        public string? ApiKey { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public Guid? CredentialId { get; init; }
    }
    private sealed record StoredFile
    {
        public StoredConnection[] Connections { get; init; } = [];
        public ModelReference? DefaultModel { get; init; }
    }

    public async Task<ModelFileSnapshot> LoadAsync(CancellationToken ct)
    {
        if (!File.Exists(path)) return new(new(), new Dictionary<Guid, string>(), new Dictionary<Guid, Guid>());
        try
        {
            if (new FileInfo(path).Length > 1_000_000) throw new JsonException();
            await using var stream = File.OpenRead(path);
            var file = await JsonSerializer.DeserializeAsync<StoredFile>(stream, Options, ct) ?? throw new JsonException();
            if (file.Connections is null || file.Connections.Length > 20
                || file.Connections.Select(c => c.Id).Distinct().Count() != file.Connections.Length) throw new JsonException();
            var configuration = new ModelConfiguration
            {
                Connections = file.Connections.Select(c => new ModelConnection(c.Id, c.Name, c.BaseUrl, c.Models) { Protocol = c.Protocol }).ToArray(),
                DefaultModel = file.DefaultModel
            };
            foreach (var connection in configuration.Connections) ModelPolicy.Validate(connection);
            if (configuration.DefaultModel is not null) ModelPolicy.Resolve(configuration, configuration.DefaultModel);
            var keys = new Dictionary<Guid, string>();
            var legacy = new Dictionary<Guid, Guid>();
            foreach (var connection in file.Connections)
            {
                if (!string.IsNullOrEmpty(connection.ApiKey)) keys[connection.Id] = ModelKey.Validate(connection.ApiKey);
                else if (connection.CredentialId is { } credential) legacy[connection.Id] = credential;
            }
            return new(configuration, keys, legacy);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or NullReferenceException or ModelServiceException)
        { throw new ModelServiceException(ModelFailure.Configuration, "模型配置无法读取，请检查配置文件；原文件未被覆盖。"); }
    }

    public async Task SaveAsync(ModelConfiguration configuration, IReadOnlyDictionary<Guid, string> keys, IReadOnlyDictionary<Guid, Guid> legacy, CancellationToken ct)
    {
        var file = new StoredFile
        {
            Connections = configuration.Connections.Select(c => new StoredConnection(c.Id, c.Name, c.BaseUrl, c.Models)
            {
                Protocol = c.Protocol, ApiKey = keys.GetValueOrDefault(c.Id), CredentialId = legacy.TryGetValue(c.Id, out var old) ? old : null
            }).ToArray(),
            DefaultModel = configuration.DefaultModel
        };
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, file, Options, ct);
                await stream.FlushAsync(ct);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, path, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { throw new ModelServiceException(ModelFailure.Configuration, "模型配置保存失败，请检查目录写入权限。"); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

internal static class ModelKey
{
    public static string Validate(string? key)
    {
        key = key?.Trim();
        if (string.IsNullOrEmpty(key)) throw new ModelServiceException(ModelFailure.Credentials, "请填写 API 密钥。");
        if (key.Length > 1280 || key.Any(char.IsControl)) throw new ModelServiceException(ModelFailure.Credentials, "密钥不能包含控制字符或超过 1280 字符。");
        return key;
    }
}
