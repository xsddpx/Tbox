using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Automation;

// This fixture controls only the disposable runner's actual, already-installed
// application. It intentionally has no reference to Tbox production assemblies.
internal static class Program
{
    private static readonly Dictionary<string, object?> Evidence = new();
    private static string _output = "";
    private static int _appPid;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    [STAThread]
    private static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" ||
            Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") != "github-hosted")
            throw new InvalidOperationException("Run only on a disposable GitHub Windows runner.");
        if (args.Length != 5) throw new ArgumentException("phase output-directory baseline-version target-version package-family");
        _output = Path.GetFullPath(args[1]); Directory.CreateDirectory(_output);
        Evidence["phase"] = args[0]; Evidence["baselineVersion"] = args[2]; Evidence["targetVersion"] = args[3];
        try
        {
            switch (args[0])
            {
                case "download": DownloadAndOpenInstaller(args[2], args[3], args[4]); break;
                case "install": ConfirmSystemInstaller(); break;
                case "relaunch": VerifyRelaunch(args[3]); break;
                default: throw new ArgumentException("Unknown phase.");
            }
            Evidence["success"] = true; return 0;
        }
        catch (Exception ex)
        {
            Evidence["success"] = false; Evidence["error"] = ex.ToString();
            try { Dump("failure"); } catch (Exception dumpError) { Evidence["dumpError"] = dumpError.Message; }
            Console.Error.WriteLine(ex); return 1;
        }
        finally { File.WriteAllText(Path.Combine(_output, args[0] + "-result.json"), JsonSerializer.Serialize(Evidence, Json)); }
    }

    private static void DownloadAndOpenInstaller(string baseline, string target, string packageFamily)
    {
        var main = AppWindow("Tbox"); _appPid = main.Current.ProcessId;
        General(main);
        WaitText(main, "Tbox " + baseline); Evidence["baselineUi"] = true;
        var toggle = (TogglePattern)WaitElement(main, "功能失败时通知").GetCurrentPattern(TogglePattern.Pattern);
        if (toggle.Current.ToggleState != ToggleState.On) throw new InvalidOperationException("Expected fresh default notification setting.");
        toggle.Toggle();
        Wait(() => toggle.Current.ToggleState == ToggleState.Off, "notification toggle off");
        Evidence["customSettingSaved"] = true;
        Invoke(main, "检查更新");
        var update = AppWindow("Tbox · 软件更新");
        Invoke(update, "检查更新");
        WaitText(update, "发现新版本 " + target, 45); Evidence["discoveredPublicUpdate"] = true;
        Dump("discovered");

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roots = new[] { Path.Combine(local, "Tbox", "updates"),
            Path.Combine(local, "Packages", packageFamily, "LocalCache", "Local", "Tbox", "updates") };
        IEnumerable<string> Files(string pattern) => roots.Where(Directory.Exists)
            .SelectMany(root => Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories));
        Invoke(update, "下载更新");
        string? partial = null;
        Wait(() => (partial = Files("*.partial").FirstOrDefault(f => new FileInfo(f).Length > 0)) is not null,
            "nonempty partial download", 60, 20);
        Evidence["partialBytesBeforeCancel"] = new FileInfo(partial!).Length;
        Invoke(update, "取消");
        WaitText(update, "已取消，可以重新尝试。");
        Wait(() => !Files("*.partial").Any(), "partial cleanup");
        Evidence["cancelledAndCleaned"] = true; Dump("cancelled");
        Invoke(update, "下载更新");
        WaitText(update, "下载完成，完整性校验通过。", 180);
        var file = Files("*.msix").Single();
        Evidence["downloadPath"] = file; Evidence["downloadBytes"] = new FileInfo(file).Length;
        using (var input = File.OpenRead(file)) Evidence["downloadSha256"] = Convert.ToHexString(SHA256.HashData(input));
        Evidence["retriedDownload"] = true; Dump("downloaded");

        // Alter only the disposable downloaded copy, prove the real app refuses
        // it, then restore the exact byte length and re-check the recorded hash.
        var originalLength = new FileInfo(file).Length;
        using (var altered = new FileStream(file, FileMode.Append, FileAccess.Write)) altered.WriteByte(0);
        ConfirmAppInstall(update);
        WaitText(update, "更新包已发生变化，请重新下载。");
        Evidence["tamperedDownloadRejected"] = true; Dump("tampered-rejected");
        using (var restored = new FileStream(file, FileMode.Open, FileAccess.Write)) restored.SetLength(originalLength);
        using (var input = File.OpenRead(file))
            if (Convert.ToHexString(SHA256.HashData(input)) != (string)Evidence["downloadSha256"]!)
                throw new InvalidDataException("Restored download differs from original.");
        ConfirmAppInstall(update);
        WaitText(update, "安装程序已打开。完成升级后，请重新打开 Tbox。", 45);
        Evidence["systemInstallerLaunched"] = true; Dump("installer-launched");
    }

    private static void ConfirmAppInstall(AutomationElement update)
    {
        Invoke(update, "打开安装程序");
        var confirm = WaitWindow(w => w.Current.ProcessId == _appPid && Find(w, "继续安装") is not null, "Tbox installation confirmation");
        Invoke(confirm, "继续安装");
    }

    private static void ConfirmSystemInstaller()
    {
        var installer = WaitWindow(w =>
        {
            using var process = Process.GetProcessById(w.Current.ProcessId);
            return process.ProcessName.Equals("AppInstaller", StringComparison.OrdinalIgnoreCase);
        }, "Windows App Installer", 45);
        Dump("system-confirmation");
        // Only invoke the explicit update action inside the observed installer.
        var update = Find(installer, "Update", ControlType.Button) ?? Find(installer, "更新", ControlType.Button);
        if (update is null) throw new InvalidOperationException("App Installer has no recognized update action; inspect system-confirmation.json.");
        ((InvokePattern)update.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
        Evidence["systemUpdateConfirmed"] = true;
        Thread.Sleep(1500); Dump("system-updating");
    }

    private static void VerifyRelaunch(string target)
    {
        var main = AppWindow("Tbox"); _appPid = main.Current.ProcessId;
        General(main); WaitText(main, "Tbox " + target);
        var toggle = (TogglePattern)WaitElement(main, "功能失败时通知").GetCurrentPattern(TogglePattern.Pattern);
        if (toggle.Current.ToggleState != ToggleState.Off) throw new InvalidOperationException("Custom setting was lost across update.");
        Evidence["newVersionUi"] = true; Evidence["customSettingRetained"] = true;
        Dump("restarted");
    }

    private static void General(AutomationElement main)
    {
        var menu = Find(main, "打开或关闭导航", ControlType.Button);
        if (menu is not null && !menu.Current.IsOffscreen) ((InvokePattern)menu.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
        var nav = WaitElement(main, "页面导航");
        var item = WaitElement(nav, "常规", ControlType.ListItem);
        ((SelectionItemPattern)item.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
        WaitElement(main, "检查更新", ControlType.Button);
    }

    private static AutomationElement AppWindow(string title) => WaitWindow(w =>
    {
        if (w.Current.Name != title) return false;
        using var process = Process.GetProcessById(w.Current.ProcessId);
        return process.ProcessName == "Tbox.Desktop";
    }, title);

    private static AutomationElement WaitWindow(Func<AutomationElement, bool> predicate, string label, int seconds = 30)
    {
        AutomationElement? found = null;
        Wait(() =>
        {
            foreach (AutomationElement window in AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition))
                try { if (predicate(window)) { found = window; return true; } } catch (ElementNotAvailableException) { } catch (ArgumentException) { }
            return false;
        }, label, seconds);
        return found!;
    }

    private static AutomationElement? Find(AutomationElement root, string name, ControlType? type = null)
    {
        Condition condition = new PropertyCondition(AutomationElement.NameProperty, name);
        if (type is not null) condition = new AndCondition(condition, new PropertyCondition(AutomationElement.ControlTypeProperty, type));
        return root.FindFirst(TreeScope.Descendants, condition);
    }

    private static AutomationElement WaitElement(AutomationElement root, string name, ControlType? type = null)
    {
        AutomationElement? found = null;
        Wait(() => (found = Find(root, name, type)) is not null, name);
        return found!;
    }

    private static void Invoke(AutomationElement root, string name)
    {
        var button = WaitElement(root, name, ControlType.Button);
        Wait(() => button.Current.IsEnabled, name + " enabled");
        ((InvokePattern)button.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
    }

    private static void WaitText(AutomationElement root, string text, int seconds = 30) => Wait(() =>
        root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text))
            .Cast<AutomationElement>().Any(e => e.Current.Name.Contains(text, StringComparison.Ordinal)), text, seconds);

    private static void Wait(Func<bool> condition, string label, int seconds = 30, int pollMs = 100)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed.TotalSeconds < seconds)
        {
            try { if (condition()) return; } catch (ElementNotAvailableException) { }
            Thread.Sleep(pollMs);
        }
        throw new TimeoutException("Timed out waiting for " + label);
    }

    private static void Dump(string stage)
    {
        var entries = new List<object>();
        foreach (AutomationElement window in AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition))
        {
            var current = window.Current;
            string processName;
            try { using var process = Process.GetProcessById(current.ProcessId); processName = process.ProcessName; }
            catch (ArgumentException) { continue; }
            if (processName is not ("Tbox.Desktop" or "AppInstaller" or "ApplicationFrameHost")) continue;
            foreach (AutomationElement element in window.FindAll(TreeScope.Subtree, Condition.TrueCondition))
            {
                var info = element.Current;
                entries.Add(new { window = current.Name, processName, info.Name, info.AutomationId,
                    type = info.ControlType.ProgrammaticName, info.IsEnabled, info.IsOffscreen,
                    patterns = element.GetSupportedPatterns().Select(p => p.ProgrammaticName).ToArray() });
            }
        }
        File.WriteAllText(Path.Combine(_output, stage + ".json"), JsonSerializer.Serialize(entries, Json));
    }
}
