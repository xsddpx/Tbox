using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Automation;
using Tbox.App;

namespace Tbox.IntegrationHarness;

internal static partial class Program
{
    private const uint WmClose = 0x0010;

    [STAThread]
    private static int Main(string[] args)
    {
        var attempts = args.Length > 0 && int.TryParse(args[0], out var value) ? value : 10;
        var target = Path.Combine(Path.GetTempPath(), $"Tbox.Integration.{Guid.NewGuid():N}");
        var marker = "TBOX_NAVIGATION_SUCCESS.txt";
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, marker), "ok");

        var durations = new List<long>();
        var failures = new List<string>();
        var trackerSucceeded = false;
        try
        {
            var trackerOutcome = ExerciseExplorerTracker(target);
            trackerSucceeded = trackerOutcome.Success;
            if (!trackerSucceeded) failures.Add($"资源管理器目录追踪失败；捕获值={trackerOutcome.Value}。");
            for (var index = 0; index < attempts; index++)
            {
                var title = $"Tbox Integration {Guid.NewGuid():N}";
                using var dialog = new OpenFileDialog
                {
                    Title = title,
                    InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
                };
                var worker = Task.Run(() => ExerciseDialog(title, target, marker));
                dialog.ShowDialog();
                var outcome = worker.GetAwaiter().GetResult();
                if (outcome.Success)
                {
                    durations.Add(outcome.ElapsedMilliseconds);
                }
                else
                {
                    failures.Add($"#{index + 1}: {outcome.Error}");
                }
            }

            Console.WriteLine(JsonSerializer.Serialize(new
            {
                attempts,
                succeeded = durations.Count,
                failed = failures.Count,
                minimumMs = durations.Count == 0 ? 0 : durations.Min(),
                maximumMs = durations.Count == 0 ? 0 : durations.Max(),
                averageMs = durations.Count == 0 ? 0 : Math.Round(durations.Average(), 1),
                trackerSucceeded,
                failures
            }));
            return failures.Count == 0 ? 0 : 1;
        }
        finally
        {
            Directory.Delete(target, true);
        }
    }

    private static (bool Success, string Value) ExerciseExplorerTracker(string target)
    {
        Process.Start(new ProcessStartInfo("explorer.exe", $"/n,/e,\"{target}\"") { UseShellExecute = true });
        dynamic? matchedWindow = null;
        var shellType = Type.GetTypeFromProgID("Shell.Application");
        if (shellType is null) return (false, "<shell unavailable>");
        dynamic shell = Activator.CreateInstance(shellType)!;
        var deadline = Environment.TickCount64 + 5000;
        do
        {
            foreach (dynamic item in shell.Windows())
            {
                try
                {
                    string? path = item.Document?.Folder?.Self?.Path;
                    if (string.Equals(Path.GetFullPath(path ?? string.Empty), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                    {
                        matchedWindow = item;
                        break;
                    }
                }
                catch
                {
                    // Explorer can mutate while enumerating.
                }
            }
            if (matchedWindow is null) Thread.Sleep(50);
        }
        while (matchedWindow is null && Environment.TickCount64 < deadline);
        if (matchedWindow is null) return (false, "<explorer window missing>");

        try
        {
            using var tracker = new ExplorerFolderTracker();
            tracker.Start();
            tracker.CaptureWindowForTesting(new IntPtr(Convert.ToInt64(matchedWindow.HWND)));
            deadline = Environment.TickCount64 + 8000;
            do
            {
                if (string.Equals(Path.GetFullPath(tracker.CurrentFolder), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                {
                    return (true, tracker.CurrentFolder);
                }
                Thread.Sleep(20);
            }
            while (Environment.TickCount64 < deadline);
            return (false, $"current={tracker.CurrentFolder}; raw={tracker.LastShellPathForTesting ?? "<null>"}");
        }
        finally
        {
            try { matchedWindow.Quit(); } catch { }
        }
    }

    private static Outcome ExerciseDialog(string title, string target, string marker)
    {
        var dialog = WaitForWindow(title, TimeSpan.FromSeconds(3));
        if (dialog == IntPtr.Zero) return Outcome.Fail("没有找到标准文件对话框。");

        ForceForegroundWindow(dialog);
        Thread.Sleep(50);
        var stopwatch = Stopwatch.StartNew();
        var result = FileDialogNavigator.Navigate(dialog, target);
        if (result != NavigationResult.Success)
        {
            PostMessage(dialog, WmClose, IntPtr.Zero, IntPtr.Zero);
            return Outcome.Fail($"导航返回 {result}。");
        }

        var markerCondition = new PropertyCondition(AutomationElement.NameProperty, marker);
        var deadline = Environment.TickCount64 + 1500;
        AutomationElement? found = null;
        do
        {
            Thread.Sleep(20);
            found = AutomationElement.FromHandle(dialog).FindFirst(TreeScope.Descendants, markerCondition);
        }
        while (found is null && Environment.TickCount64 < deadline);
        stopwatch.Stop();
        var addressCondition = new PropertyCondition(AutomationElement.AutomationIdProperty, "1001");
        var address = AutomationElement.FromHandle(dialog).FindFirst(TreeScope.Descendants, addressCondition)?.Current.Name;
        PostMessage(dialog, WmClose, IntPtr.Zero, IntPtr.Zero);
        return found is null
            ? Outcome.Fail($"导航后没有看到目标目录标记文件；地址栏={address ?? "<missing>"}。")
            : Outcome.Pass(stopwatch.ElapsedMilliseconds);
    }

    private static IntPtr WaitForWindow(string title, TimeSpan timeout)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        IntPtr window;
        do
        {
            window = FindWindow(null, title);
            if (window != IntPtr.Zero) return window;
            Thread.Sleep(20);
        }
        while (Environment.TickCount64 < deadline);
        return IntPtr.Zero;
    }

    private static void ForceForegroundWindow(IntPtr window)
    {
        var currentThread = GetCurrentThreadId();
        var targetThread = GetWindowThreadProcessId(window, out _);
        var foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        if (foregroundThread != currentThread) AttachThreadInput(currentThread, foregroundThread, true);
        if (targetThread != currentThread) AttachThreadInput(currentThread, targetThread, true);
        BringWindowToTop(window);
        SetForegroundWindow(window);
        if (targetThread != currentThread) AttachThreadInput(currentThread, targetThread, false);
        if (foregroundThread != currentThread) AttachThreadInput(currentThread, foregroundThread, false);
    }

    private sealed record Outcome(bool Success, long ElapsedMilliseconds, string? Error)
    {
        internal static Outcome Pass(long elapsedMilliseconds) => new(true, elapsedMilliseconds, null);
        internal static Outcome Fail(string error) => new(false, 0, error);
    }

    [LibraryImport("user32.dll", EntryPoint = "FindWindowW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr FindWindow(string? className, string windowName);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr window);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachThreadInput(uint attach, uint attachTo, [MarshalAs(UnmanagedType.Bool)] bool value);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool BringWindowToTop(IntPtr window);
}
