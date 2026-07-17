using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Automation;
using Tbox.App;
using Tbox.Core;

namespace Tbox.IntegrationHarness;

internal static partial class Program
{
    private const uint WmClose = 0x0010;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 && string.Equals(args[0], "--capture-only", StringComparison.OrdinalIgnoreCase))
        {
            var outcome = ExerciseScreenCapture();
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                screenCaptureSucceeded = outcome.Success,
                error = outcome.Error
            }));
            return outcome.Success ? 0 : 1;
        }

        var attempts = args.Length > 0 && int.TryParse(args[0], out var value) ? value : 10;
        var target = Path.Combine(Path.GetTempPath(), $"Tbox.Integration.{Guid.NewGuid():N}");
        var marker = "TBOX_NAVIGATION_SUCCESS";
        Directory.CreateDirectory(target);
        // A directory name is stable in Explorer UI Automation regardless of
        // the user's "hide known file extensions" setting.
        Directory.CreateDirectory(Path.Combine(target, marker));

        var durations = new List<long>();
        var failures = new List<string>();
        var trackerSucceeded = false;
        var annotationRendererSucceeded = false;
        var screenCaptureSucceeded = false;
        try
        {
            if (!FileDialogNavigator.AddressTextMatches(
                    @"C:\Users\tester\Downloads",
                    "地址: 此电脑 > 下载",
                    "下载") ||
                FileDialogNavigator.AddressTextMatches(
                    @"C:\Users\tester\Downloads",
                    "地址: 此电脑 > 音乐",
                    "下载") ||
                FileDialogNavigator.AddressTextMatches(
                    @"C:\Users\tester\Downloads",
                    "地址: 此电脑 > 下载备份",
                    "下载"))
            {
                failures.Add("本地化地址栏名称匹配策略失败。");
            }

            var annotationOutcome = ExerciseAnnotationRenderer();
            annotationRendererSucceeded = annotationOutcome.Success;
            if (!annotationOutcome.Success) failures.Add($"截图标注渲染失败：{annotationOutcome.Error}");

            var captureOutcome = ExerciseScreenCapture();
            screenCaptureSucceeded = captureOutcome.Success;
            if (!captureOutcome.Success) failures.Add($"多显示器截图捕获失败：{captureOutcome.Error}");

            var trackerOutcome = ExerciseExplorerTracker(target, attempts);
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
                annotationRendererSucceeded,
                screenCaptureSucceeded,
                failures
            }));
            return failures.Count == 0 ? 0 : 1;
        }
        finally
        {
            Directory.Delete(target, true);
        }
    }

    private static Outcome ExerciseAnnotationRenderer()
    {
        const int width = 140;
        const int height = 150;
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = ((y * width) + x) * 4;
                pixels[offset] = (byte)((x * 13 + y * 3) % 256);
                pixels[offset + 1] = (byte)((x * 5 + y * 11) % 256);
                pixels[offset + 2] = (byte)((x * 7 + y * 17) % 256);
                pixels[offset + 3] = 255;
            }
        }
        var screen = new CapturedScreen(100, 200, width, height, pixels);
        var style = new AnnotationStyle(0xFFFF0000, 3, 14);
        AnnotationCommand[] commands =
        [
            new RectangleAnnotationCommand(new AnnotationPoint(10, 10), new AnnotationPoint(35, 30), style),
            new EllipseAnnotationCommand(new AnnotationPoint(45, 10), new AnnotationPoint(75, 30), style),
            new MosaicAnnotationCommand(
                [new AnnotationPoint(92, 14), new AnnotationPoint(118, 32)],
                new AnnotationStyle(0xFF000000, 14, 14),
                6),
            new LineAnnotationCommand(new AnnotationPoint(10, 48), new AnnotationPoint(35, 65), style),
            new ArrowAnnotationCommand(new AnnotationPoint(45, 48), new AnnotationPoint(75, 65), style),
            new TextAnnotationCommand(
                "Tbox",
                new AnnotationBounds(90, 44, 130, 72),
                style),
            new PolylineAnnotationCommand(
                [new AnnotationPoint(10, 102), new AnnotationPoint(22, 82), new AnnotationPoint(35, 104)],
                style),
            new FreePathAnnotationCommand(
                [new AnnotationPoint(45, 104), new AnnotationPoint(58, 82), new AnnotationPoint(75, 104)],
                style),
            new HighlighterAnnotationCommand(
                [new AnnotationPoint(90, 94), new AnnotationPoint(128, 94)],
                new AnnotationStyle(0x66FFFF00, 10, 14)),
            new NumberAnnotationCommand(
                12,
                new AnnotationPoint(70, 130),
                24,
                new AnnotationStyle(0xFF007AFF, 2, 14))
        ];

        var region = new PixelRegion(4, 4, 132, 142);
        var baseline = screen.Crop(region.Left, region.Top, region.Width, region.Height);
        var plain = CaptureAnnotationRenderer.RenderAsync(
            screen,
            region,
            Array.Empty<AnnotationCommand>()).GetAwaiter().GetResult();
        var rendered = CaptureAnnotationRenderer.RenderAsync(screen, region, commands).GetAwaiter().GetResult();

        int CountChanged(AnnotationBounds sourceBounds)
        {
            var left = Math.Clamp((int)Math.Floor(sourceBounds.Left) - region.Left, 0, rendered.Width);
            var top = Math.Clamp((int)Math.Floor(sourceBounds.Top) - region.Top, 0, rendered.Height);
            var right = Math.Clamp((int)Math.Ceiling(sourceBounds.Right) - region.Left, 0, rendered.Width);
            var bottom = Math.Clamp((int)Math.Ceiling(sourceBounds.Bottom) - region.Top, 0, rendered.Height);
            var changed = 0;
            for (var y = top; y < bottom; y++)
            {
                for (var x = left; x < right; x++)
                {
                    var offset = ((y * rendered.Width) + x) * 4;
                    if (!rendered.Pixels.AsSpan(offset, 4).SequenceEqual(baseline.Pixels.AsSpan(offset, 4)))
                    {
                        changed++;
                    }
                }
            }
            return changed;
        }

        var changedByTool = new Dictionary<string, int>
        {
            ["rectangle"] = CountChanged(new AnnotationBounds(6, 6, 39, 34)),
            ["ellipse"] = CountChanged(new AnnotationBounds(41, 6, 79, 34)),
            ["mosaic"] = CountChanged(new AnnotationBounds(84, 5, 126, 40)),
            ["line"] = CountChanged(new AnnotationBounds(6, 40, 39, 70)),
            ["arrow"] = CountChanged(new AnnotationBounds(41, 40, 80, 70)),
            ["text"] = CountChanged(new AnnotationBounds(84, 40, 132, 75)),
            ["polyline"] = CountChanged(new AnnotationBounds(6, 77, 39, 109)),
            ["pen"] = CountChanged(new AnnotationBounds(41, 77, 79, 109)),
            ["highlighter"] = CountChanged(new AnnotationBounds(84, 84, 132, 101)),
            ["number"] = CountChanged(new AnnotationBounds(54, 114, 86, 146))
        };
        var alphaIsOpaque = rendered.Pixels.Where((_, index) => index % 4 == 3).All(alpha => alpha == 255);
        var allToolsRendered = changedByTool.All(pair => pair.Value >= 4);

        return plain.Pixels.SequenceEqual(baseline.Pixels) &&
               rendered.Left == 104 && rendered.Top == 204 &&
               rendered.Width == 132 && rendered.Height == 142 &&
               alphaIsOpaque && allToolsRendered
            ? Outcome.Pass(0)
            : Outcome.Fail(
                $"bounds={rendered.Left},{rendered.Top},{rendered.Width},{rendered.Height}; " +
                $"plainExact={plain.Pixels.SequenceEqual(baseline.Pixels)}; alphaOpaque={alphaIsOpaque}; " +
                $"toolPixels={JsonSerializer.Serialize(changedByTool)}");
    }

    private static Outcome ExerciseScreenCapture()
    {
        var expected = Screen.AllScreens
            .Select(screen => screen.Bounds)
            .OrderBy(bounds => bounds.Left)
            .ThenBy(bounds => bounds.Top)
            .ToArray();
        var captured = ScreenCaptureService.CaptureAllMonitors();
        var boundsMatch = expected.Length == captured.Count &&
                          expected.Zip(captured).All(pair =>
                              pair.First.Left == pair.Second.Left &&
                              pair.First.Top == pair.Second.Top &&
                              pair.First.Width == pair.Second.Width &&
                              pair.First.Height == pair.Second.Height);
        var buffersMatch = captured.All(screen =>
            screen.Pixels.Length == checked(screen.Width * screen.Height * 4));

        return boundsMatch && buffersMatch
            ? Outcome.Pass(0)
            : Outcome.Fail(
                $"expected={JsonSerializer.Serialize(expected.Select(bounds => new { bounds.Left, bounds.Top, bounds.Width, bounds.Height }))}; " +
                $"captured={JsonSerializer.Serialize(captured.Select(screen => new { screen.Left, screen.Top, screen.Width, screen.Height, bytes = screen.Pixels.Length }))}");
    }

    private static (bool Success, string Value) ExerciseExplorerTracker(string target, int attempts)
    {
        var changedTargets = new[]
        {
            Path.Combine(target, "changed-directory-a"),
            Path.Combine(target, "changed-directory-b")
        };
        foreach (var changedTarget in changedTargets) Directory.CreateDirectory(changedTarget);
        Process.Start(new ProcessStartInfo("explorer.exe", $"/n,/e,\"{target}\"") { UseShellExecute = true });
        long matchedWindowHandle = 0;
        var deadline = Environment.TickCount64 + 5000;
        do
        {
            TryFindExplorerWindow(target, out matchedWindowHandle);
            if (matchedWindowHandle == 0) Thread.Sleep(50);
        }
        while (matchedWindowHandle == 0 && Environment.TickCount64 < deadline);
        if (matchedWindowHandle == 0) return (false, "<explorer window missing>");

        try
        {
            using var tracker = new ExplorerFolderTracker();
            tracker.Start();
            tracker.CaptureWindowForTesting(new IntPtr(matchedWindowHandle));
            deadline = Environment.TickCount64 + 8000;
            do
            {
                if (string.Equals(Path.GetFullPath(tracker.CurrentFolder), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
                Thread.Sleep(20);
            }
            while (Environment.TickCount64 < deadline);
            if (!string.Equals(Path.GetFullPath(tracker.CurrentFolder), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            {
                return (false, $"initial current={tracker.CurrentFolder}; raw={tracker.LastShellPathForTesting ?? "<null>"}");
            }

            var refreshDurations = new List<long>();
            var refreshAttempts = Math.Clamp(attempts, 3, 10);
            for (var attempt = 0; attempt < refreshAttempts; attempt++)
            {
                var changedTarget = changedTargets[attempt % changedTargets.Length];
                Exception? navigationException = null;
                for (var navigationAttempt = 1; navigationAttempt <= 20; navigationAttempt++)
                {
                    if (TryNavigateExplorerWindow(
                            matchedWindowHandle,
                            changedTarget,
                            out navigationException))
                    {
                        break;
                    }
                    Thread.Sleep(50);
                }
                if (navigationException is not null)
                {
                    return (false,
                        $"Explorer rejected navigation #{attempt + 1}; " +
                        $"type={navigationException.GetType().Name}; HRESULT=0x{navigationException.HResult:X8}");
                }
                deadline = Environment.TickCount64 + 5000;
                string? navigatedPath = null;
                do
                {
                    TryGetExplorerWindowPath(matchedWindowHandle, out navigatedPath);
                    if (string.Equals(
                            Path.GetFullPath(navigatedPath ?? string.Empty),
                            Path.GetFullPath(changedTarget),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }
                    Thread.Sleep(20);
                }
                while (Environment.TickCount64 < deadline);
                if (!string.Equals(
                        Path.GetFullPath(navigatedPath ?? string.Empty),
                        Path.GetFullPath(changedTarget),
                        StringComparison.OrdinalIgnoreCase))
                {
                    return (false, $"Explorer did not navigate to changed directory #{attempt + 1}; shell={navigatedPath ?? "<null>"}");
                }

                var stopwatch = Stopwatch.StartNew();
                var refreshed = tracker.RefreshCurrentFolder();
                stopwatch.Stop();
                refreshDurations.Add(stopwatch.ElapsedMilliseconds);
                if (!string.Equals(
                        Path.GetFullPath(refreshed),
                        Path.GetFullPath(changedTarget),
                        StringComparison.OrdinalIgnoreCase))
                {
                    return (false, $"refresh #{attempt + 1} current={refreshed}; raw={tracker.LastShellPathForTesting ?? "<null>"}");
                }
            }

            const long maximumAllowedRefreshMilliseconds = 1000;
            var maximumRefreshMilliseconds = refreshDurations.Max();
            return maximumRefreshMilliseconds <= maximumAllowedRefreshMilliseconds
                ? (true, $"{refreshAttempts} refreshes; max={maximumRefreshMilliseconds}ms")
                : (false, $"refresh exceeded {maximumAllowedRefreshMilliseconds}ms; max={maximumRefreshMilliseconds}ms");
        }
        finally
        {
            TryQuitExplorerWindow(matchedWindowHandle);
        }
    }

    private static bool TryFindExplorerWindow(string targetPath, out long windowHandle)
    {
        windowHandle = 0;
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null) return false;
            dynamic shell = Activator.CreateInstance(shellType)!;
            foreach (dynamic item in shell.Windows())
            {
                try
                {
                    string? path = item.Document?.Folder?.Self?.Path;
                    if (!string.Equals(
                            Path.GetFullPath(path ?? string.Empty),
                            Path.GetFullPath(targetPath),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    windowHandle = Convert.ToInt64(item.HWND);
                    return windowHandle != 0;
                }
                catch
                {
                    // Explorer can mutate while enumerating.
                }
            }
        }
        catch
        {
            // Shell.Application can invalidate its collection between calls.
        }
        return false;
    }

    private static bool TryNavigateExplorerWindow(long windowHandle, string targetPath, out Exception? error)
    {
        error = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null)
            {
                error = new InvalidOperationException("Shell.Application 不可用。");
                return false;
            }
            dynamic shell = Activator.CreateInstance(shellType)!;
            foreach (dynamic item in shell.Windows())
            {
                try
                {
                    if (Convert.ToInt64(item.HWND) != windowHandle) continue;
                    item.Navigate(targetPath);
                    return true;
                }
                catch (Exception exception)
                {
                    error = exception;
                    return false;
                }
            }
            error = new InvalidOperationException("目标 Explorer 窗口已关闭。");
        }
        catch (Exception exception)
        {
            error = exception;
        }
        return false;
    }

    private static bool TryGetExplorerWindowPath(long windowHandle, out string? path)
    {
        path = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null) return false;
            dynamic shell = Activator.CreateInstance(shellType)!;
            foreach (dynamic item in shell.Windows())
            {
                try
                {
                    if (Convert.ToInt64(item.HWND) != windowHandle) continue;
                    path = item.Document?.Folder?.Self?.Path;
                    return true;
                }
                catch
                {
                    return false;
                }
            }
        }
        catch
        {
            // The caller retries until its deadline.
        }
        return false;
    }

    private static void TryQuitExplorerWindow(long windowHandle)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null) return;
            dynamic shell = Activator.CreateInstance(shellType)!;
            foreach (dynamic item in shell.Windows())
            {
                try
                {
                    if (Convert.ToInt64(item.HWND) != windowHandle) continue;
                    item.Quit();
                    return;
                }
                catch
                {
                    return;
                }
            }
        }
        catch
        {
            // Cleanup must not hide the test result.
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
        // Cold shell enumeration can take more than 1.5 seconds even after the
        // address bar has reached the target. Keep the gate strict but avoid a
        // false failure while the file list is still materializing.
        var deadline = Environment.TickCount64 + 5000;
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
        var addressMatches = FileDialogNavigator.AddressTextMatches(
            target,
            address ?? string.Empty,
            Path.GetFileName(target));
        PostMessage(dialog, WmClose, IntPtr.Zero, IntPtr.Zero);
        return found is null && !addressMatches
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
