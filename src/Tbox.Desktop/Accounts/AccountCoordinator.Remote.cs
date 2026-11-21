using System.Net;
using Tbox.Core.Accounts;

namespace Tbox.Desktop.Accounts;

internal sealed partial class AccountCoordinator
{
    private RemoteEngineProcess? _remoteHost;
    private readonly Dictionary<Guid, RemoteEngineProcess> _controllers = [];
    internal IReadOnlyList<RemoteHost> RemoteHosts { get; private set; } = [];
    internal bool RemoteAvailable => RemoteEngine.Installed is not null && _account is { } account &&
        CloudClient.TryOrigin(_packagedOrigin, out var origin) && account.Origin == origin.AbsoluteUri;
    internal bool RemoteHostEnabled => _remoteHost is not null;

    internal Task EnableRemoteHostAsync() => RunAsync(async ct =>
    {
        if (_remoteHost is not null || !SignedIn || !RemoteAvailable ||
            _account is not { } account || RemoteEngine.Installed is not { } engine) return;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        var credential = CloudProtocol.RandomSecret();
        var process = engine.StartHost();
        try
        {
            await process.SendAsync(new
            {
                engine.Network, account.Origin, account.DeviceId, Credential = credential
            }, token);
            var transport = await process.ReceiveAsync<RemoteTransportIdentity>(token);
            if (!RemoteAccessProtocol.IsTransportIdentity(transport))
                throw new InvalidOperationException("远程桌面组件返回的设备身份无效。");
            var signature = account.Sign(RemoteAccessProtocol.HostRegistrationProof(account.DeviceId, credential, transport));
            await RequestAsync<object>(HttpMethod.Put, "/v1/remote/hosts/current",
                new RemoteHostRegistration(credential, transport, signature), token);
            await process.SendAsync(new { Ready = true }, token);
            if (_disposed) throw new OperationCanceledException(token);
            _remoteHost = process;
            _ = ObserveRemoteProcessAsync(process, null, ct);
            Status = "已允许此设备接受远程控制。";
        }
        finally
        {
            if (_remoteHost != process) process.Dispose();
        }
    });

    internal Task DisableRemoteHostAsync() => RunAsync(async ct =>
    {
        StopRemoteHost();
        await RequestAsync<object>(HttpMethod.Delete, "/v1/remote/hosts/current", null, ct);
        Status = "此设备的远程控制已关闭。";
    });

    internal Task ConnectRemoteHostAsync(Guid hostId) => RunAsync(async ct =>
    {
        if (!SignedIn || !RemoteAvailable || _account is not { } account ||
            RemoteEngine.Installed is not { } engine) return;
        if (_controllers.ContainsKey(hostId)) return;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(RemoteAccessProtocol.LeaseSeconds));
        var token = timeout.Token;
        RemoteInvitation invitation;
        try
        {
            invitation = await RequestAsync<RemoteInvitation>(HttpMethod.Post, "/v1/remote/sessions",
                new RemoteConnectionRequest(hostId), token);
        }
        catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.Conflict)
        {
            RemoteHosts = await RequestAsync<List<RemoteHost>>(HttpMethod.Get, "/v1/remote/hosts", null, ct);
            Status = "对方暂时无法接受远程连接。";
            return;
        }

        if (invitation.HostId != hostId || invitation.SessionId == Guid.Empty ||
            !CloudProtocol.IsSecret(invitation.Ticket) || !RemoteAccessProtocol.IsTransportIdentity(invitation.Transport))
            throw new InvalidOperationException("远程连接邀请无效。");

        var process = engine.StartController();
        try
        {
            await process.SendAsync(new { engine.Network, account.DeviceId, Invitation = invitation }, token);
            var challenge = await process.ReceiveAsync<RemoteEngineChallenge>(token);
            if (!CloudProtocol.IsSecret(challenge.Challenge))
                throw new InvalidOperationException("远程设备的连接挑战无效。");
            var signature = account.Sign(RemoteAccessProtocol.ClaimProof(account.DeviceId, hostId, invitation.Ticket, challenge.Challenge));
            await process.SendAsync(new { Signature = signature }, token);
            if (!(await process.ReceiveAsync<RemoteEngineConnected>(token)).Connected)
                throw new InvalidOperationException("远程连接未获授权。");
            if (_disposed) throw new OperationCanceledException(token);
            _controllers.Add(hostId, process);
            _ = ObserveRemoteProcessAsync(process, hostId, ct);
            Status = "已打开远程桌面窗口。";
        }
        finally
        {
            if (!_controllers.TryGetValue(hostId, out var current) || current != process) process.Dispose();
        }
    });

    private async Task ObserveRemoteProcessAsync(RemoteEngineProcess process, Guid? hostId, CancellationToken ct)
    {
        try { await process.WaitForExitAsync(ct); }
        catch (Exception error) when (error is IOException or InvalidOperationException or OperationCanceledException) { }
        finally
        {
            if (hostId is { } id)
            {
                if (_controllers.TryGetValue(id, out var current) && current == process)
                    _controllers.Remove(id);
            }
            else if (_remoteHost == process)
            {
                _remoteHost = null;
                if (!_disposed)
                {
                    Status = "本机远程桌面组件已退出。";
                    Changed?.Invoke();
                }
            }
            process.Dispose();
        }
    }

    private void StopRemoteHost()
    {
        var host = _remoteHost;
        _remoteHost = null;
        host?.Dispose();
    }

    private void StopRemoteEngine()
    {
        StopRemoteHost();
        var controllers = _controllers.Values.ToArray();
        _controllers.Clear();
        foreach (var controller in controllers) controller.Dispose();
    }
}
