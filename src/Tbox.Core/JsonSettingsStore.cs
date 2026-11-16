using System.Text.Json;

namespace Tbox.Core;

public sealed class JsonSettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly string _path;
    private readonly SemaphoreSlim _accessLock = new(1, 1);

    public JsonSettingsStore(string path) => _path = path;
    public string? LoadWarning { get; private set; }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _accessLock.WaitAsync(cancellationToken);
        try
        {
            await using var stream = File.OpenRead(_path);
            var settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, Options, cancellationToken)
                ?? throw new JsonException("Settings must contain an object.");
            LoadWarning = null;
            return settings;
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            LoadWarning = null;
            return new AppSettings();
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        {
            LoadWarning = $"无法读取 {_path}。本次使用默认设置，暂停保存。请检查文件后重启 Tbox。";
            return new AppSettings();
        }
        finally
        {
            _accessLock.Release();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await _accessLock.WaitAsync(cancellationToken);
        string? temporaryPath = null;
        try
        {
            if (LoadWarning is { } warning) throw new IOException(warning);
            temporaryPath = _path + ".tmp";
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, settings, Options, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, _path, true);
        }
        finally
        {
            try
            {
                if (temporaryPath is not null && File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            finally
            {
                _accessLock.Release();
            }
        }
    }
}
