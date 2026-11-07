using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Automation;
using Tbox.Desktop.Platform;
using Forms = System.Windows.Forms;

namespace Tbox.FolderSmoke;

// Integration fixture for disposable Windows runners. It calls the same public
// platform factory as the shared application, without old WinUI test hooks.
internal static class Program
{
    private static int _exitCode = 1;

    [STAThread]
    private static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" ||
            Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") != "github-hosted")
            throw new InvalidOperationException("Run this interactive fixture only on a disposable GitHub Windows runner.");
        if (args.Length != 1) throw new ArgumentException("Pass a new result directory.");
        var directory = Path.GetFullPath(args[0]);
        if (Directory.Exists(directory)) throw new IOException("Use a new output directory.");
        Directory.CreateDirectory(directory);
        Forms.Application.EnableVisualStyles();
        using var host = new Forms.Form { Text = "Tbox shared folder verification", Width = 560, Height = 240 };
        var protectedText = new Forms.TextBox { Text = "UNCHANGED", Dock = Forms.DockStyle.Top };
        host.Controls.Add(protectedText);
        host.Shown += async (_, _) =>
        {
            var outcomes = new List<object>();
            dynamic? explorer = null;
            var fixtures = Path.Combine(directory, "fixtures");
            Directory.CreateDirectory(fixtures);
            try
            {
                using var platform = new WindowsPlatformServices();
                using var navigation = platform.CreateFolderNavigation();
                navigation.Start();
                var initial = Path.Combine(fixtures, "initial");
                Directory.CreateDirectory(initial);
                var targets = new[] { Path.Combine(fixtures, "目标 A", "同名目录"), Path.Combine(fixtures, "目标 B", "同名目录") };
                for (var i = 0; i < targets.Length; i++) Directory.CreateDirectory(Path.Combine(targets[i], $"TBOX_MARKER_{i}"));
                Process.Start(new ProcessStartInfo("explorer.exe", $"/n,/e,\"{targets[0]}\"") { UseShellExecute = true });
                await UntilAsync(() => (explorer = FindExplorer(targets[0])) is not null, "Explorer window did not appear.");
                var handle = new IntPtr(Convert.ToInt64(explorer!.HWND));
                for (var i = 0; i < targets.Length; i++)
                {
                    if (i > 0) explorer.Navigate(targets[i]);
                    await UntilAsync(() => SamePath(ReadPath(explorer), targets[i]), "Explorer did not reach the fixture directory.");
                    Focus(handle);
                    await UntilAsync(() => SamePath(navigation.CurrentDirectory, targets[i]),
                        $"Foreground folder tracking failed: {navigation.CurrentDirectory}");
                    foreach (var save in new[] { false, true })
                    {
                        host.Activate();
                        var title = "Tbox Folder " + Guid.NewGuid().ToString("N");
                        using Forms.FileDialog dialog = save ? new Forms.SaveFileDialog() : new Forms.OpenFileDialog();
                        dialog.Title = title; dialog.InitialDirectory = initial;
                        var marker = $"TBOX_MARKER_{i}";
                        var target = targets[i];
                        var worker = Task.Run(() => ExerciseDialogAsync(navigation, title, target, marker));
                        dialog.ShowDialog(host);
                        var result = await worker;
                        outcomes.Add(new { kind = save ? "save" : "open", target, result.elapsedMs, markerVerified = true });
                    }
                }
                host.Activate();
                Focus(host.Handle);
                var unsupported = await navigation.JumpAsync(CancellationToken.None);
                if (unsupported.Success || protectedText.Text != "UNCHANGED")
                    throw new InvalidOperationException("An unsupported window was changed or accepted.");
                navigation.Stop();
                explorer.Navigate(targets[0]);
                await UntilAsync(() => SamePath(ReadPath(explorer), targets[0]), "Explorer navigation after stop failed.");
                Focus(handle);
                await Task.Delay(500);
                if (!SamePath(navigation.CurrentDirectory, targets[1])) throw new InvalidOperationException("Stopped tracker still changed directory.");
                navigation.Start();
                await UntilAsync(() => SamePath(navigation.CurrentDirectory, targets[0]), "Tracker did not resume after restart.");
                await WriteAsync(directory, new { success = true, trackedDirectories = targets.Length,
                    dialogs = outcomes, unsupportedWindowUnchanged = true, stopAndRestartVerified = true });
                _exitCode = 0;
            }
            catch (Exception error) { await WriteAsync(directory, new { success = false, dialogs = outcomes, error = error.ToString() }); }
            finally
            {
                try { explorer?.Quit(); } catch (Exception) { }
                host.Close();
            }
        };
        Forms.Application.Run(host);
        return _exitCode;
    }

    private static async Task<(long elapsedMs, string marker)> ExerciseDialogAsync(
        IFolderNavigation navigation, string title, string target, string marker)
    {
        nint dialog = 0;
        try
        {
            await UntilAsync(() => (dialog = FindWindow(null, title)) != 0, "Standard dialog did not appear.");
            Focus(dialog);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var clock = Stopwatch.StartNew();
            var result = await navigation.JumpAsync(timeout.Token);
            if (!result.Success) throw new InvalidOperationException(result.Message);
            if (!SamePath(navigation.CurrentDirectory, target)) throw new InvalidOperationException("Tracker returned a different parent directory.");
            // Same basename in two different parents makes a loose address-bar
            // match insufficient. Require the unique directory marker in the UI.
            await UntilAsync(() => HasMarker(dialog, marker), "The dialog did not display the target's unique marker.");
            return (clock.ElapsedMilliseconds, marker);
        }
        finally { if (dialog != 0) PostMessage(dialog, 0x0010, 0, 0); }
    }

    private static bool HasMarker(nint dialog, string marker)
    {
        try
        {
            return AutomationElement.FromHandle(dialog).FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.NameProperty, marker)) is not null;
        }
        catch (Exception error) when (error is ElementNotAvailableException or InvalidOperationException or COMException) { return false; }
    }

    private static dynamic? FindExplorer(string path)
    {
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
        foreach (dynamic window in shell.Windows())
            try { if (SamePath(ReadPath(window), path)) return window; }
            catch (COMException) { }
        return null;
    }
    private static string? ReadPath(dynamic window)
    { try { return window.Document.Folder.Self.Path; } catch (Exception) { return null; } }
    private static bool SamePath(string? actual, string expected) => actual is not null &&
        string.Equals(Path.TrimEndingDirectorySeparator(actual), Path.TrimEndingDirectorySeparator(expected), StringComparison.OrdinalIgnoreCase);
    private static async Task UntilAsync(Func<bool> predicate, string message)
    {
        var clock = Stopwatch.StartNew();
        while (!predicate())
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(12)) throw new TimeoutException(message);
            await Task.Delay(50);
        }
    }
    private static Task WriteAsync(string directory, object result) => File.WriteAllTextAsync(Path.Combine(directory, "result.json"),
        JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    private static void Focus(nint window)
    {
        var current = GetCurrentThreadId();
        var target = GetWindowThreadProcessId(window, out _);
        var foreground = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        if (foreground != current) AttachThreadInput(current, foreground, true);
        if (target != current && target != foreground) AttachThreadInput(current, target, true);
        try { BringWindowToTop(window); SetForegroundWindow(window); }
        finally
        {
            if (target != current && target != foreground) AttachThreadInput(current, target, false);
            if (foreground != current) AttachThreadInput(current, foreground, false);
        }
        if (GetForegroundWindow() != window) throw new InvalidOperationException("Could not focus the disposable test window.");
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "FindWindowW")] private static extern nint FindWindow(string? className, string title);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(nint window);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint from, uint to, bool attach);
}
