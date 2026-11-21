using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Tbox.Core.Accounts;

namespace Tbox.Desktop.Accounts;

internal static class BrowserLogin
{
    internal static async Task<(string Code, string Verifier)> AuthenticateAsync(Uri origin, CancellationToken ct)
    {
        var verifier = CloudProtocol.RandomSecret();
        var state = CloudProtocol.RandomSecret();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var url = new Uri(origin, $"/v1/auth/start?port={port}&challenge={CloudProtocol.Challenge(verifier)}&state={state}");
        Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true })?.Dispose();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            requestTimeout.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                var stream = client.GetStream();
                var request = await ReadRequestLineAsync(stream, requestTimeout.Token);
                var parameters = ParseCallback(request, port, state);
                if (parameters is null)
                {
                    await RespondAsync(stream, 400, "请求无效。", requestTimeout.Token);
                    continue;
                }

                var accepted = CloudProtocol.IsSecret(parameters["code"]);
                await RespondAsync(stream, 200, accepted ? "请返回 Tbox 完成登录。" : "未完成授权，请返回 Tbox。", requestTimeout.Token);
                if (!accepted) throw new InvalidOperationException("GitHub 未授权此账号登录 Tbox。");
                return (parameters["code"]!, verifier);
            }
            catch (IOException) when (!timeout.IsCancellationRequested) { }
            catch (OperationCanceledException) when (!timeout.IsCancellationRequested) { }
        }
    }

    private static async Task<string> ReadRequestLineAsync(NetworkStream stream, CancellationToken ct)
    {
        var bytes = new byte[4096];
        var read = 0;
        while (read < bytes.Length)
        {
            var length = await stream.ReadAsync(bytes.AsMemory(read), ct);
            if (length == 0) break;
            read += length;
            var end = Array.IndexOf(bytes, (byte)'\n', 0, read);
            if (end >= 0) return Encoding.ASCII.GetString(bytes, 0, end).TrimEnd('\r');
        }
        return "";
    }

    private static System.Collections.Specialized.NameValueCollection? ParseCallback(string line, int port, string state)
    {
        const string prefix = "GET /tbox-callback?";
        if (!line.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var end = line.LastIndexOf(' ');
        if (end <= prefix.Length ||
            !Uri.TryCreate($"http://127.0.0.1:{port}" + line[4..end], UriKind.Absolute, out var uri)) return null;
        var parameters = System.Web.HttpUtility.ParseQueryString(uri.Query);
        return parameters["state"] == state ? parameters : null;
    }

    private static async Task RespondAsync(NetworkStream stream, int status, string message, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        var statusText = status == 200 ? "OK" : "Bad Request";
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {statusText}\r\n" +
            $"Content-Type: text/plain; charset=utf-8\r\nContent-Length: {bytes.Length}\r\n" +
            "Connection: close\r\nCache-Control: no-store\r\nReferrer-Policy: no-referrer\r\n\r\n"), ct);
        await stream.WriteAsync(bytes, ct);
    }
}
