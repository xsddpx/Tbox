using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Tbox.Core.Accounts;

namespace Tbox.Desktop.Accounts;

internal sealed record RemoteNetwork(string Rendezvous, string RendezvousKey);
internal sealed record RemoteEngine(string Executable, RemoteNetwork Network)
{
    private static readonly Dictionary<string, string?> Metadata = typeof(RemoteEngine).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().ToDictionary(attribute => attribute.Key, attribute => attribute.Value);

    internal static RemoteEngine? Installed { get; } = FindInstalled();

    private static RemoteEngine? FindInstalled()
    {
        if (!Metadata.TryGetValue("TboxRemoteRendezvous", out var server) || string.IsNullOrWhiteSpace(server) ||
            !Metadata.TryGetValue("TboxRemoteRendezvousKey", out var key) || key is not { Length: 44 })
            return null;
        var relativePath = OperatingSystem.IsWindows()
            ? Path.Combine("Remote", "TboxRemote.exe")
            : Path.Combine("..", "Applications", "TboxRemote.app", "Contents", "MacOS", "TboxRemote");
        var executable = Path.GetFullPath(relativePath, AppContext.BaseDirectory);
        return File.Exists(executable) ? new(executable, new(server, key)) : null;
    }

    internal RemoteEngineProcess StartHost() => Start("--tbox-host");
    internal RemoteEngineProcess StartController() => Start("--tbox-connect");

    private RemoteEngineProcess Start(string command)
    {
        var start = new ProcessStartInfo(Executable)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            WorkingDirectory = Path.GetDirectoryName(Executable)!,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(command);
        return new(Process.Start(start) ?? throw new IOException("无法启动 Tbox 远程桌面。"));
    }
}

internal sealed class RemoteEngineProcess(Process process) : IDisposable
{
    private const string Prefix = "TBOX_REMOTE ";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private int _disposed;

    internal async Task SendAsync<T>(T message, CancellationToken ct)
    {
        var packet = JsonSerializer.Serialize(message, Json);
        await process.StandardInput.WriteLineAsync(packet.AsMemory(), ct);
        await process.StandardInput.FlushAsync(ct);
    }

    internal async Task<T> ReceiveAsync<T>(CancellationToken ct)
    {
        var packet = await ReadControlAsync(process.StandardOutput, ct);
        return JsonSerializer.Deserialize<T>(packet, Json) ?? throw new IOException("远程桌面组件返回空消息。");
    }

    internal async Task WaitForExitAsync(CancellationToken ct)
    {
        // Drain unrelated engine output so a full pipe cannot block a running session.
        while (await process.StandardOutput.ReadLineAsync(ct) is not null) { }
        await process.WaitForExitAsync(ct);
    }

    internal static async Task<string> ReadControlAsync(TextReader reader, CancellationToken ct)
    {
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!line.StartsWith(Prefix, StringComparison.Ordinal)) continue;
            if (line.Length > 4096) throw new IOException("远程桌面组件返回的消息过长。");
            return line[Prefix.Length..];
        }
        throw new IOException("远程桌面组件已退出。");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            process.StandardInput.Close();
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or Win32Exception) { }
        finally { process.Dispose(); }
    }
}

internal sealed record RemoteEngineChallenge(string Challenge);
internal sealed record RemoteEngineConnected(bool Connected);
