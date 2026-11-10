using System.Text.Json;
using Tbox.Core.ModelServices;

namespace Tbox.Desktop.ModelServices;

internal sealed class ModelConfigurationStore(string path)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    public async Task<ModelConfiguration> LoadAsync(CancellationToken ct)
    {
        if (!File.Exists(path)) return new();
        try
        {
            if (new FileInfo(path).Length > 1_000_000) throw new JsonException();
            await using var stream = File.OpenRead(path);
            var configuration = await JsonSerializer.DeserializeAsync<ModelConfiguration>(stream, Options, ct) ?? throw new JsonException();
            if (configuration.Connections is null || configuration.Connections.Length > 20
                || configuration.Connections.Select(c => c.Id).Distinct().Count() != configuration.Connections.Length) throw new JsonException();
            foreach (var connection in configuration.Connections) ModelPolicy.Validate(connection);
            if (configuration.DefaultModel is not null) ModelPolicy.Resolve(configuration, configuration.DefaultModel);
            return configuration;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or NullReferenceException or ModelServiceException)
        { throw new ModelServiceException(ModelFailure.Configuration, "模型配置无法读取，请检查配置文件；原文件未被覆盖。"); }
    }

    public async Task SaveAsync(ModelConfiguration configuration, CancellationToken ct)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, configuration, Options, ct);
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
