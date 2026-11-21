using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia.Threading;
using Tbox.Core;
using Tbox.Core.Accounts;
using Tbox.Desktop.Platform;

namespace Tbox.Desktop.Accounts;

internal sealed partial class AccountCoordinator(
    Func<AppSettings> settings, Func<Func<AppSettings, AppSettings>, Task> save,
    IAccountCredentialVault vault, string dataDirectory, CancellationToken lifetime, HttpClient? http = null,
    string? packagedOrigin = null, Action<Uri>? openBrowser = null) : IDisposable
{
    private static readonly string? PackagedOrigin = typeof(AccountCoordinator).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .SingleOrDefault(attribute => attribute.Key == "TboxAccountOrigin")?.Value;
    private readonly HttpClient _http = http ?? CloudClient.CreateHttpClient();
    private readonly string? _packagedOrigin = packagedOrigin ?? PackagedOrigin;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _outboxGate = new(1, 1);
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(20) };
    private readonly CaptureOutbox _outbox = new(Path.Combine(dataDirectory, "cloud-capture-outbox"));
    private LocalAccount? _account;
    private CloudSession? _session;
    private CancellationTokenSource? _captureCancellation;

    private CancellationTokenSource? _loginCancellation;
    private CancellationTokenSource? _watchCancellation;
    private Task? _watch;
    private bool _loaded, _disposed, _capturePaused;
    private string? _conflict;
    private CloudClient Client => new(_http);
    internal event Action? Changed;
    internal string? Login => SignedIn ? _account!.Login : null;
    internal Guid? LocalDevice => _account?.DeviceId;
    internal bool SignedIn => _account is { RefreshToken: not null, SignOutPending: false };
    internal bool CanSignIn => LoginOrigin is not null;
    internal bool CanQueueCapture => !_disposed && SignedIn && settings().Cloud.SyncCaptures && _captureCancellation is not null;
    internal bool Busy { get; private set; }
    internal bool SigningIn => _loginCancellation is not null;
    internal string Status { get; private set; } = "";
    internal IReadOnlyList<CloudDevice> Devices { get; private set; } = [];
    internal IReadOnlyList<CaptureListing> Captures { get; private set; } = [];

    internal void Start()
    {
        _timer.Tick += OnTick;
        _timer.Start();
        _ = RefreshAsync();
    }

    private void OnTick(object? sender, EventArgs args) => _ = RefreshAsync();

    private Uri? LoginOrigin => CloudClient.TryOrigin(settings().Cloud.Origin, out var saved)
        ? saved
        : CloudClient.TryOrigin(_packagedOrigin, out var configured) ? configured : null;

    internal Task SignInAsync() => RunAsync(async ct =>
    {
        if (LoginOrigin is not { } origin)
            throw new InvalidOperationException("账号服务尚未配置。");
        await LoadAsync(ct);
        if (_account is { SignOutPending: true }) await FinishSignOutAsync(ct);
        if (_account?.Origin != origin.AbsoluteUri)
        {
            var identity = LocalAccount.Create(origin);
            await WriteAsync(identity, ct);
        }
        if (settings().Cloud.Origin != origin.AbsoluteUri)
            await save(current => current with { Cloud = new LocalCloudPreferences { Origin = origin.AbsoluteUri } });

        Status = "请在浏览器完成 GitHub 授权。";
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loginCancellation = cancel;
        Changed?.Invoke();
        try
        {
            var (code, verifier) = await BrowserLogin.AuthenticateAsync(origin, cancel.Token, openBrowser);
            var answer = await Client.ExchangeAsync(_account!, code, verifier, cancel.Token);
            await WriteAsync(_account! with { RefreshToken = answer.RefreshToken, Login = answer.Login }, ct);
            _session = answer;
            _capturePaused = false;
            await SynchronizeAsync(ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Status = "登录已取消或超时。";
        }
        finally { _loginCancellation = null; }
    });

    internal void CancelSignIn() => _loginCancellation?.Cancel();

    internal Task SignOutAsync()
    {
        var captureWasPaused = _capturePaused;
        PauseCaptureSync();
        StopRemoteEngine();
        return RunAsync(async ct =>
        {
            if (_account is not { RefreshToken: not null } account) return;
            try { await DisableWindowsRemoteHostAsync(account, signingOut: true, ct); }
            catch
            {
                _capturePaused = captureWasPaused;
                if (!_capturePaused && settings().Cloud.SyncCaptures) StartCaptureSync();
                throw;
            }
            await WriteAsync(account with { SignOutPending = true, Login = null }, ct);
            ResetView();
            await save(current => current with { Cloud = new LocalCloudPreferences { Origin = account.Origin } });
            await ClearOutboxAsync(account, ct);
            await FinishSignOutAsync(ct);
            Status = "已退出账号。";
        });
    }

    private async Task FinishSignOutAsync(CancellationToken ct)
    {
        if (_account is not { SignOutPending: true } account) return;
        await ClearOutboxAsync(account, ct);
        await Client.SignOutAsync(account, ct);
        await WriteAsync(account with { RefreshToken = null, Login = null, SignOutPending = false }, ct);
    }

    internal Task RefreshAsync() => RunAsync(async ct =>
    {
        if (settings().Cloud.Origin is null) return;
        await LoadAsync(ct);
        if (_account is { SignOutPending: true }) await FinishSignOutAsync(ct);
        else if (SignedIn) await SynchronizeAsync(ct);
    }, reportBusy: false, skipIfBusy: true);

    internal Task SetPreferenceSyncAsync(bool enabled) => RunAsync(async ct =>
    {
        await save(current => current with
        {
            Cloud = current.Cloud with { SyncPreferences = enabled, Pending = [], Revisions = [] }
        });
        if (enabled && SignedIn) await SynchronizeAsync(ct);
    });

    internal async Task SetCaptureSyncAsync(bool enabled)
    {
        if (!enabled) PauseCaptureSync();
        await RunAsync(async ct =>
        {
            var account = _account;
            if (!SignedIn || account is null) return;
            await save(current => current with { Cloud = current.Cloud with { SyncCaptures = enabled } });
            if (enabled)
            {
                _capturePaused = false;
                StartCaptureSync();
            }
            else await ClearOutboxAsync(account, ct);
        }, skipIfBusy: false);
    }

    internal async Task QueueCaptureAsync(byte[] png)
    {
        if (!CanQueueCapture || _account is not { } account || _captureCancellation is not { } cancellation) return;
        var ct = cancellation.Token;
        try
        {
            await _outboxGate.WaitAsync(ct);
            try { await _outbox.EnqueueAsync(account, png, ct); }
            finally { _outboxGate.Release(); }
            _ = RefreshAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Status = png.Length > CloudProtocol.MaximumCaptureBytes ? "这张截图超过同步大小上限。" : "无法保存待同步截图。";
            Changed?.Invoke();
        }
    }

    internal Task RemoveDeviceAsync(Guid id) => RunAsync(async ct =>
    {
        if (id == LocalDevice) return;
        await RequestAsync<object>(HttpMethod.Delete, $"/v1/devices/{id:D}", null, ct);
        await LoadListsAsync(ct);
    });

    internal Task DeleteCaptureAsync(Guid id) => RunAsync(async ct =>
    {
        await RequestAsync<object>(HttpMethod.Delete, $"/v1/captures/{id:D}", null, ct);
        Captures = await RequestAsync<List<CaptureListing>>(HttpMethod.Get, "/v1/captures", null, ct);
    });

    internal Task OpenCaptureAsync(Guid id, Action<byte[]> show) => RunAsync(async ct =>
        show(await RequestAsync<byte[]>(HttpMethod.Get, $"/v1/captures/{id:D}", null, ct)));

    private async Task SynchronizeAsync(CancellationToken ct)
    {
        await LoadListsAsync(ct);
        if (settings().Cloud.SyncPreferences)
        {
            var remote = await RequestAsync<List<CloudPreference>>(HttpMethod.Get, "/v1/preferences", null, ct);
            if (remote.Count == 0)
                remote = await RequestAsync<List<CloudPreference>>(HttpMethod.Post, "/v1/preferences/initial", CloudPreferences.Snapshot(settings()), ct);

            foreach (var pending in settings().Cloud.Pending.Values.ToArray())
            {
                var answer = await RequestAsync<PreferenceResult>(HttpMethod.Patch, "/v1/preferences", pending, ct);
                await save(current => CloudPreferences.Apply(current, answer.Preference, pending, answer.Conflict));
                if (answer.Conflict)
                {
                    var name = pending.Key switch
                    {
                        CloudProtocol.Theme => "外观主题",
                        CloudProtocol.TranslationLanguage => "翻译目标语言",
                        _ => "失败提示"
                    };
                    _conflict = $"「{name}」未同步，已采用服务器上的较新设置。";
                }
            }

            await save(current =>
            {
                foreach (var preference in remote) current = CloudPreferences.Apply(current, preference);
                return current;
            });
        }

        if (!_capturePaused && settings().Cloud.SyncCaptures && _account is { } account)
        {
            StartCaptureSync();
            var upload = _captureCancellation!.Token;
            try
            {
                FileInfo[] pending;
                await _outboxGate.WaitAsync(upload);
                try { pending = _outbox.Pending(account); }
                finally { _outboxGate.Release(); }
                foreach (var file in pending)
                {
                    upload.ThrowIfCancellationRequested();
                    var id = Guid.ParseExact(Path.GetFileNameWithoutExtension(file.Name), "N");
                    byte[] png;
                    try { png = await File.ReadAllBytesAsync(file.FullName, upload); }
                    catch (FileNotFoundException) { continue; }
                    try
                    {
                        await RequestAsync<object>(HttpMethod.Put, $"/v1/captures/{id:D}",
                            new CaptureUpload(file.LastWriteTimeUtc, png), upload);
                    }
                    catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.BadRequest)
                    {
                        _conflict = "一张截图超出云端同步的尺寸或保留范围。";
                    }
                    file.Delete();
                }
                Captures = await RequestAsync<List<CaptureListing>>(HttpMethod.Get, "/v1/captures", null, ct);
            }
            catch (OperationCanceledException) when (upload.IsCancellationRequested) { }
        }
        Status = _conflict ?? "已连接账号服务。";
    }

    private async Task LoadListsAsync(CancellationToken ct)
    {
        Devices = await RequestAsync<List<CloudDevice>>(HttpMethod.Get, "/v1/devices", null, ct);
        Captures = await RequestAsync<List<CaptureListing>>(HttpMethod.Get, "/v1/captures", null, ct);
        if (RemoteAvailable)
            RemoteHosts = await RequestAsync<List<RemoteHost>>(HttpMethod.Get, "/v1/remote/hosts", null, ct);
    }

    private async Task<T> RequestAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        if (!SignedIn || _account is not { } account) throw new InvalidOperationException("请先登录 Tbox。");
        if (_session is null || _session.AccessExpires <= DateTimeOffset.UtcNow.AddMinutes(1))
        {
            StopWatching();
            _session = await Client.RefreshAsync(account, ct);
        }
        if (_watch is not { IsCompleted: false } && method == HttpMethod.Get && path == "/v1/devices")
        {
            StopWatching();
            _watchCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            _watch = ObserveAsync(account, _session.AccessToken, _watchCancellation.Token);
        }
        return await Client.RequestAsync<T>(account.Origin, method, path, _session.AccessToken, body, ct);
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        if (_loaded) return;
        _account = await vault.ReadAsync(ct) is { } json ? JsonSerializer.Deserialize<LocalAccount>(json) : null;
        if (_account is { } restored && !CloudClient.TryOrigin(restored.Origin, out _))
            throw new IOException("Invalid account service origin.");
        if (_account is { RefreshToken: not null, SignOutPending: false } active &&
            !string.Equals(settings().Cloud.Origin, active.Origin, StringComparison.Ordinal))
            _account = null;
        _loaded = true;
        if (SignedIn && settings().Cloud.SyncCaptures) StartCaptureSync();
    }

    private async Task WriteAsync(LocalAccount account, CancellationToken ct)
    {
        await vault.WriteAsync(JsonSerializer.Serialize(account), ct);
        _account = account;
    }

    private void ResetView()
    {
        StopWatching();
        StopCaptureSync();
        StopRemoteEngine();
        _session = null;
        Devices = [];
        Captures = [];
        RemoteHosts = [];
        _conflict = null;
    }

    private async Task ObserveAsync(LocalAccount account, string token, CancellationToken ct)
    {
        try
        {
            if (!await Client.WatchAsync(account, token, ct) || _session?.AccessToken != token || !SignedIn) return;
            PauseCaptureSync();
            await RunAsync(async lifetimeToken =>
            {
                if (_session?.AccessToken == token && SignedIn)
                    await InvalidateAsync(account, lifetimeToken);
            }, reportBusy: false);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException or ObjectDisposedException) { }
    }

    private void StopWatching()
    {
        _watchCancellation?.Cancel();
        _watchCancellation?.Dispose();
        _watchCancellation = null;
        _watch = null;
    }

    private async Task InvalidateAsync(LocalAccount account, CancellationToken ct)
    {
        ResetView();
        await WriteAsync(account with { RefreshToken = null, Login = null }, ct);
        await ClearOutboxAsync(account, ct);
        await save(current => current with { Cloud = new LocalCloudPreferences { Origin = account.Origin } });
        Status = "登录已失效或设备已移除，请重新登录。";
    }

    private void StartCaptureSync() =>
        _captureCancellation ??= CancellationTokenSource.CreateLinkedTokenSource(lifetime);

    private void PauseCaptureSync()
    {
        _capturePaused = true;
        StopCaptureSync();
    }

    private void StopCaptureSync()
    {
        _captureCancellation?.Cancel();
        _captureCancellation?.Dispose();
        _captureCancellation = null;
    }

    private async Task ClearOutboxAsync(LocalAccount account, CancellationToken ct)
    {
        await _outboxGate.WaitAsync(ct);
        try { _outbox.Clear(account); }
        finally { _outboxGate.Release(); }
    }

    private async Task RunAsync(Func<CancellationToken, Task> action, bool reportBusy = true, bool skipIfBusy = false)
    {
        if (_disposed) return;
        if (skipIfBusy)
        {
            if (!_gate.Wait(0)) return;
        }
        else
        {
            try { await _gate.WaitAsync(lifetime); }
            catch (OperationCanceledException) { return; }
        }
        Busy = reportBusy;
        if (reportBusy) Changed?.Invoke();
        try { await action(lifetime); }
        catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.Unauthorized)
        {
            if (SignedIn && _account is { } account)
            {
                try { await InvalidateAsync(account, lifetime); }
                catch (Exception) when (!lifetime.IsCancellationRequested) { _loaded = false; }
            }
            Status = "登录已失效或设备已移除，请重新登录。";
        }
        catch (HttpRequestException)
        {
            Status = _account is { SignOutPending: true } ? "本机已退出，联网后会撤销服务器会话。"
                : "账号服务暂不可用，稍后重试。";
        }
        catch (OperationCanceledException) { if (!_disposed) Status = "账号服务响应超时。"; }
        catch (Exception error) when (error is IOException or JsonException or CryptographicException or UnauthorizedAccessException
            or DllNotFoundException or EntryPointNotFoundException or System.ComponentModel.Win32Exception
            or Tbox.Core.ModelServices.ModelServiceException)
        {
            Status = "无法读取或保存账号数据，请检查系统凭据库和本机存储。";
        }
        catch (InvalidOperationException error) { Status = error.Message; }
        finally
        {
            Busy = false;
            _gate.Release();
            if (!_disposed) Changed?.Invoke();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTick;
        StopWatching();
        StopCaptureSync();
        StopRemoteEngine();
        _http.Dispose();
    }
}
