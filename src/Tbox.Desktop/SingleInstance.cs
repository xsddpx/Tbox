using System.IO.Pipes;

namespace Tbox.Desktop;

internal sealed class SingleInstance : IDisposable
{
    private readonly FileStream? _lock;
    private readonly CancellationTokenSource _stop = new();
    private static string PipeName => "tbox-desktop-" + Environment.UserName;

    private SingleInstance(FileStream? file) => _lock = file;

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

    public async Task ListenAsync(Action<byte> command)
    {
        if (_lock is null) return;
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_stop.Token);
                var buffer = new byte[1];
                if (await pipe.ReadAsync(buffer, _stop.Token) == 1) command(buffer[0]);
            }
            catch (OperationCanceledException) { break; }
            catch (IOException) { await Task.Delay(200, _stop.Token); }
        }
    }

    public void Dispose() { _stop.Cancel(); _lock?.Dispose(); }
}
