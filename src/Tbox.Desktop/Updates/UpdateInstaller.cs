using System.Diagnostics;
using System.Reflection;
using System.Runtime.Versioning;

namespace Tbox.Desktop.Updates;

internal sealed class PreparedUpdate : IDisposable
{
    private readonly Action _launch;
    private readonly Action _cleanup;
    private bool _launched;
    public PreparedUpdate(Action launch, Action cleanup)
    { _launch = launch; _cleanup = cleanup; }
    public void Launch() { _launch(); _launched = true; }
    public void Dispose() { if (!_launched) _cleanup(); }
}

internal static class UpdateInstaller
{
    public static string Runtime => OperatingSystem.IsMacOS() && System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64
        ? "osx-arm64" : OperatingSystem.IsWindows() && System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.X64
        ? "win-x64" : throw new NotSupportedException("当前平台尚无更新包。");

    public static async Task<PreparedUpdate> PrepareAsync(string file, UpdateRelease release, CancellationToken token)
    {
        await UpdatePackagePolicy.VerifyDigestAsync(file, release, token);
        if (OperatingSystem.IsMacOS()) return await PrepareMacAsync(file, release, token);
#if WINDOWS
        if (OperatingSystem.IsWindows())
        {
            Windows.ApplicationModel.Package package;
            try { package = Windows.ApplicationModel.Package.Current; }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
            { throw new InvalidOperationException("请先安装正式 MSIX 版本，再使用在线升级。"); }
            UpdatePackagePolicy.ValidateWindowsManifest(file, release.Version, package.Id.Name, package.Id.Publisher);
            // App Installer verifies the MSIX signature and trust before showing the
            // system confirmation. Windows owns replacement, rollback and app identity.
            return new(() => Process.Start(new ProcessStartInfo(file) { UseShellExecute = true }), () => { });
        }
#endif
        throw new NotSupportedException("当前系统不支持在线安装。");
    }

    [SupportedOSPlatform("macos")]
    private static async Task<PreparedUpdate> PrepareMacAsync(string file, UpdateRelease release, CancellationToken token)
    {
        var installed = new FileInfo(Environment.ProcessPath ?? "").Directory?.Parent?.Parent?.FullName;
        var userPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications", "Tbox.app");
        if (installed != userPath && installed != "/Applications/Tbox.app")
            throw new InvalidOperationException("请先将 Tbox 安装到“应用程序”或用户的 Applications 文件夹，再使用在线升级。");
        if (new DirectoryInfo(installed).LinkTarget is not null) throw new InvalidOperationException("不支持通过应用替身执行在线升级。");
        var installedVersion = (await ExecuteAsync("/usr/libexec/PlistBuddy", ["-c", "Print CFBundleVersion", Path.Combine(installed, "Contents", "Info.plist")], token)).Trim();
        if (!Version.TryParse(installedVersion, out var current) || release.Version <= current)
            throw new InvalidOperationException("安装位置的版本已变化，请重新检查更新。");
        await Task.Run(() => UpdatePackagePolicy.ValidateMacArchive(file), token);
        var stage = Path.Combine(Path.GetDirectoryName(installed)!, ".tbox-update-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(stage); // Requires a writable destination before quitting.
            File.SetUnixFileMode(stage, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            await ExecuteAsync("/usr/bin/ditto", ["-x", "-k", file, stage], token);
            var app = Path.Combine(stage, "Tbox.app");
            var requirementOutput = await ExecuteAsync("/usr/bin/codesign", ["-d", "-r-", installed], token);
            var requirement = requirementOutput.Split('\n').Select(s => s.TrimStart('#', ' '))
                .FirstOrDefault(s => s.StartsWith("designated => ", StringComparison.Ordinal))?[14..];
            if (string.IsNullOrWhiteSpace(requirement) || !requirement.Contains("certificate", StringComparison.Ordinal) || requirement.Contains("cdhash", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("当前应用没有可持久复用的签名身份，无法自动升级。");
            await ExecuteAsync("/usr/bin/codesign", ["--verify", "--deep", "--strict", "-R", "=" + requirement, app], token);
            var plist = Path.Combine(app, "Contents", "Info.plist");
            if ((await ExecuteAsync("/usr/libexec/PlistBuddy", ["-c", "Print CFBundleIdentifier", plist], token)).Trim() != "com.xsddpx.tbox" ||
                (await ExecuteAsync("/usr/libexec/PlistBuddy", ["-c", "Print CFBundleVersion", plist], token)).Trim() != release.Version.ToString(4))
                throw new InvalidDataException("更新包的应用标识或版本不匹配。");
            var script = Path.Combine(stage, "install.sh");
            await using (var input = Assembly.GetExecutingAssembly().GetManifestResourceStream("Tbox.Desktop.Updates.install-macos-update.sh")!)
            await using (var output = File.Create(script)) await input.CopyToAsync(output, token);
            await File.WriteAllTextAsync(Path.Combine(stage, "requirement.txt"), requirement, token);
            var info = new ProcessStartInfo("/bin/bash") { UseShellExecute = false, WorkingDirectory = stage };
            foreach (var arg in new[] { script, installed, stage, Environment.ProcessId.ToString(), release.Version.ToString(4), installedVersion }) info.ArgumentList.Add(arg);
            return new(() => { using var process = Process.Start(info) ?? throw new IOException("无法启动安装程序。"); }, () => Directory.Delete(stage, true));
        }
        catch { if (Directory.Exists(stage)) Directory.Delete(stage, true); throw; }
    }

    private static async Task<string> ExecuteAsync(string executable, string[] arguments, CancellationToken token)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new IOException("无法验证更新包。");
        var stdout = process.StandardOutput.ReadToEndAsync(token); var stderr = process.StandardError.ReadToEndAsync(token);
        try { await process.WaitForExitAsync(token); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        var output = await stdout + "\n" + await stderr;
        if (process.ExitCode != 0) throw new InvalidDataException("更新包签名或安装路径验证失败，原版本保持可用。");
        return output;
    }
}
