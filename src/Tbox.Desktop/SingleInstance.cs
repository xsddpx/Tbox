using System.IO.Pipes;

namespace Tbox.Desktop;

internal sealed class SingleInstance : IDisposable
{
    private readonly FileStream? _lock;
    private readonly CancellationTokenSource _stop = new();
    private readonly string _pipeName;
    private static string PipeName => "tbox-desktop-" + Environment.UserName;

    internal SingleInstance(FileStream? file, string? pipeName = null)
    { _lock = file; _pipeName = pipeName ?? PipeName; }

    public static SingleInstance? Acquire(bool isolated)
    {
        if (isolated) return new SingleInstance(null);
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tbox");
        Directory.CreateDirectory(directory);
        try
        {
            return new SingleInstance(new FileStream(Path.Combine(directory, "desktop.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
        }
        catch (IOException)
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            try { pipe.Connect(2000); pipe.WriteByte(1); }
            catch (TimeoutException) { }
            return null;
        }
    }

    public static void RequestExit()
    {
        using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
        try { pipe.Connect(2000); pipe.WriteByte(2); }
        catch (TimeoutException) { return; }
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tbox", "desktop.lock");
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            try { using var probe = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); return; }
            catch (IOException) when (DateTime.UtcNow < deadline) { Thread.Sleep(100); }
        }
    }

    public static async Task<string> RequestPermissionProbeAsync(string? pipeName = null)
    {
        using var pipe = new NamedPipeClientStream(".", pipeName ?? PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await pipe.ConnectAsync(2000, timeout.Token);
        await pipe.WriteAsync(new byte[] { 3 }, timeout.Token);
        using var reader = new StreamReader(pipe);
        return await reader.ReadLineAsync(timeout.Token) ?? throw new IOException("Tbox 未返回权限验证结果。");
    }

    public async Task ListenAsync(Func<byte, Task<string?>> command)
    {
        if (_lock is null) return;
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_stop.Token);
                var buffer = new byte[1];
                if (await pipe.ReadAsync(buffer, _stop.Token) == 1 && await command(buffer[0]) is { } response)
                {
                    await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
                    await writer.WriteLineAsync(response.AsMemory(), _stop.Token);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (IOException) { if (_stop.IsCancellationRequested) break; await Task.Delay(200); }
        }
    }

    public void Dispose() { _stop.Cancel(); _lock?.Dispose(); }
}
