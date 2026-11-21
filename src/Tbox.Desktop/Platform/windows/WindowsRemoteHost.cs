using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using Tbox.Core.Accounts;
using Tbox.Desktop.Accounts;

namespace Tbox.Desktop.Platform;

[SupportedOSPlatform("windows")]
internal static class WindowsRemoteHost
{
    internal const string HelperArgument = "--tbox-host-admin";
    private const string MarkerKey = @"SYSTEM\CurrentControlSet\Control\TboxRemote";
    private const string MarkerValue = "DeviceId";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static bool Available => File.Exists(PolicyPath);
    private static string PolicyPath => Path.Combine(AppContext.BaseDirectory, "Remote", "tbox-host-policy.json");

    internal static Guid? ConfiguredDevice
    {
        get
        {
            using var key = Registry.LocalMachine.OpenSubKey(MarkerKey);
            return Guid.TryParse(key?.GetValue(MarkerValue) as string, out var device) ? device : null;
        }
    }

    internal static async Task ConfigureAsync(Guid device, string? credential,
        Func<RemoteTransportIdentity, Task>? register, CancellationToken ct)
    {
        if (!Available) throw new InvalidOperationException("未安装 Windows 远控服务。");
        var attempt = Guid.NewGuid();
        var executable = Environment.ProcessPath ?? throw new IOException("无法找到 Tbox 安装位置。");
        using var owner = WindowsIdentity.GetCurrent();
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(owner.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(Administrators, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        await using var pipe = NamedPipeServerStreamAcl.Create(PipeName(attempt), PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, security);
        var start = new ProcessStartInfo(executable) { UseShellExecute = true, Verb = "runas" };
        start.ArgumentList.Add(HelperArgument);
        start.ArgumentList.Add(attempt.ToString("D"));
        start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Process? elevated;
        try { elevated = await Task.Run(() => Process.Start(start), ct); }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        {
            throw new InvalidOperationException("已取消 Windows 管理员授权。");
        }
        using var child = elevated ?? throw new IOException("无法启动 Windows 远控授权程序。");
        using var pipeDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        pipeDeadline.CancelAfter(TimeSpan.FromSeconds(20));
        await pipe.WaitForConnectionAsync(pipeDeadline.Token);
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var client) || client != child.Id)
            throw new IOException("Windows 远控授权程序身份不匹配。");

        await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, leaveOpen: true);
        await WriteAsync(writer, new Request(device, credential), ct);
        if (register is not null)
        {
            var prepared = await ReadAsync<Reply>(reader, ct);
            if (prepared.Transport is not { } transport)
                throw new InvalidOperationException("Windows 远控服务未能准备本机身份。");
            await register(transport);
            await WriteAsync(writer, true, ct);
        }
        if (!(await ReadAsync<Reply>(reader, ct)).Succeeded)
            throw new InvalidOperationException("无法更新 Windows 的本机远控授权。");
    }

    internal static int RunHelper(string[] args)
    {
        if (args.Length != 3 || !Guid.TryParseExact(args[1], "D", out var attempt) ||
            !int.TryParse(args[2], out var parent) || parent <= 0) return 1;
        using var identity = WindowsIdentity.GetCurrent();
        if (!Available || !new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return 1;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(95));
            ApplyAsync(attempt, parent, timeout.Token).GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception error) when (error is IOException or JsonException or InvalidOperationException or
            OperationCanceledException or UnauthorizedAccessException or Win32Exception)
        {
            return 1;
        }
    }

    private static async Task ApplyAsync(Guid attempt, int parent, CancellationToken ct)
    {
        await using var pipe = new NamedPipeClientStream(".", PipeName(attempt), PipeDirection.InOut,
            PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        await pipe.ConnectAsync(5000, ct);
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var server) || server != parent)
            throw new IOException("Windows 远控授权进程身份不匹配。");
        using var caller = Process.GetProcessById(parent);
        if (!string.Equals(caller.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Windows 远控授权只能由已安装的 Tbox 发起。");

        await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, leaveOpen: true);
        var request = await ReadAsync<Request>(reader, ct);
        if (request.DeviceId == Guid.Empty || request.Credential is not null && !CloudProtocol.IsSecret(request.Credential))
            throw new IOException("Windows 远控授权请求无效。");
        VerifyServiceLocation();

        // The system profile keeps host credentials out of ordinary user profiles and across MSIX updates.
        var systemProfile = Path.Combine(Environment.SystemDirectory, "config", "systemprofile");
        var directory = Path.Combine(systemProfile, "AppData", "Local", "TboxRemoteHost");
        Directory.CreateDirectory(directory);
        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
        {
            if (current.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("Windows 远控服务存储路径无效。");
            if (current.FullName.Equals(systemProfile, StringComparison.OrdinalIgnoreCase)) break;
        }
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var principal in new[] { Administrators, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
            security.AddAccessRule(new FileSystemAccessRule(principal, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(directory).SetAccessControl(security);
        using var gate = new FileStream(Path.Combine(directory, "configuration.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        var pending = Path.Combine(directory, "pending.json");
        var active = Path.Combine(directory, "active.json");
        using var marker = Registry.LocalMachine.CreateSubKey(MarkerKey);
        if (request.Credential is null)
        {
            File.Delete(pending);
            File.Delete(active);
            marker.DeleteValue(MarkerValue, throwOnMissingValue: false);
            await WriteAsync(writer, new Reply(true), ct);
            return;
        }

        if (File.Exists(active))
        {
            await WriteAsync(writer, new Reply(false), ct);
            return;
        }
        var identityPath = Path.Combine(directory, $"identity-{attempt:D}.json");
        var draft = pending + ".new";
        try
        {
            var policy = JsonSerializer.Deserialize<Policy>(await File.ReadAllTextAsync(PolicyPath, ct), Json)
                ?? throw new IOException("Windows 远控服务配置无效。");
            var consent = new Consent(attempt, request.DeviceId, request.Credential,
                DateTimeOffset.UtcNow.AddSeconds(100).ToUnixTimeSeconds(), policy);
            await File.WriteAllTextAsync(draft, JsonSerializer.Serialize(consent, Json), ct);
            File.Move(draft, pending, overwrite: true);
            StartInstalledService();
            while (!File.Exists(identityPath)) await Task.Delay(250, ct);
            var transport = JsonSerializer.Deserialize<RemoteTransportIdentity>(await File.ReadAllTextAsync(identityPath, ct), Json);
            if (!RemoteAccessProtocol.IsTransportIdentity(transport)) throw new IOException("Windows 远控服务返回无效身份。");
            await WriteAsync(writer, new Reply(false, transport), ct);
            if (!await ReadAsync<bool>(reader, ct)) return;
            marker.SetValue(MarkerValue, consent.DeviceId.ToString("D"));
            try { File.Move(pending, active, overwrite: false); }
            catch { marker.DeleteValue(MarkerValue, throwOnMissingValue: false); throw; }
            await WriteAsync(writer, new Reply(true), ct);
        }
        finally
        {
            File.Delete(draft);
            File.Delete(pending);
            File.Delete(identityPath);
        }
    }

    private static string PipeName(Guid attempt) => $"TboxRemoteHost-{attempt:N}";

    private static SecurityIdentifier Administrators => new(WellKnownSidType.BuiltinAdministratorsSid, null);

    private static void VerifyServiceLocation()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\TboxRemote");
        var image = key?.GetValue("ImagePath") as string;
        var expected = Path.Combine(AppContext.BaseDirectory, "Remote", "TboxRemote.exe");
        if (!string.Equals(image, $"\"{expected}\" --tbox-service", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(image, $"{expected} --tbox-service", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Tbox Windows 远控服务未正确注册。");
    }

    private static void StartInstalledService()
    {
        var manager = OpenSCManager(null, null, 1); // SC_MANAGER_CONNECT
        if (manager == IntPtr.Zero) throw new Win32Exception();
        try
        {
            var service = OpenService(manager, "TboxRemote", 0x10); // SERVICE_START
            if (service == IntPtr.Zero) throw new Win32Exception();
            try
            {
                if (!StartService(service, 0, IntPtr.Zero) && Marshal.GetLastWin32Error() != 1056)
                    throw new Win32Exception(); // ERROR_SERVICE_ALREADY_RUNNING
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }

    private static async Task<T> ReadAsync<T>(StreamReader reader, CancellationToken ct)
    {
        var line = await reader.ReadLineAsync(ct);
        if (line is null || line.Length > 4096) throw new IOException("Windows 远控授权程序已退出或返回无效消息。");
        return JsonSerializer.Deserialize<T>(line, Json) ?? throw new IOException("Windows 远控授权程序返回空消息。");
    }

    private static Task WriteAsync<T>(StreamWriter writer, T value, CancellationToken ct) =>
        writer.WriteLineAsync(JsonSerializer.Serialize(value, Json).AsMemory(), ct);

    private sealed record Request(Guid DeviceId, string? Credential);
    private sealed record Reply(bool Succeeded, RemoteTransportIdentity? Transport = null);
    private sealed record Policy(string Origin, RemoteNetwork Network);
    private sealed record Consent(Guid Attempt, Guid DeviceId, string Credential, long ExpiresAt, Policy Policy);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManager(string? machine, string? database, uint desiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenService(IntPtr manager, string name, uint desiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartService(IntPtr service, uint argumentCount, IntPtr arguments);

    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);
}
