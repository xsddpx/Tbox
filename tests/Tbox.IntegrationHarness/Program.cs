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
    private const ushort VkTab = 0x09;
    private const ushort VkT = 0x54;

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
        if (args.Length > 0 && string.Equals(args[0], "--recording-only", StringComparison.OrdinalIgnoreCase))
        {
            var outcome = ExerciseRecordingAndExports().GetAwaiter().GetResult();
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                recordingSucceeded = outcome.Success,
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
        var multiTabTrackerSucceeded = false;
        var annotationRendererSucceeded = false;
        var clipboardTextRendererSucceeded = false;
        var screenCaptureSucceeded = false;
        var captureTargetDetectionSucceeded = false;
        var recordingSucceeded = false;
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

            var clipboardTextOutcome = ExerciseClipboardTextRenderer();
            clipboardTextRendererSucceeded = clipboardTextOutcome.Success;
            if (!clipboardTextOutcome.Success)
            {
                failures.Add($"剪贴板文字贴图渲染失败：{clipboardTextOutcome.Error}");
            }

            var captureOutcome = ExerciseScreenCapture();
            screenCaptureSucceeded = captureOutcome.Success;
            if (!captureOutcome.Success) failures.Add($"多显示器截图捕获失败：{captureOutcome.Error}");

            var targetDetectionOutcome = ExerciseCaptureTargetDetection();
            captureTargetDetectionSucceeded = targetDetectionOutcome.Success;
            if (!targetDetectionOutcome.Success) failures.Add($"界面元素检测失败：{targetDetectionOutcome.Error}");

            var recordingOutcome = ExerciseRecordingAndExports().GetAwaiter().GetResult();
            recordingSucceeded = recordingOutcome.Success;
            if (!recordingOutcome.Success) failures.Add($"区域录屏与导出失败：{recordingOutcome.Error}");

            var trackerOutcome = ExerciseExplorerTracker(target, attempts);
            trackerSucceeded = trackerOutcome.Success;
            if (!trackerSucceeded) failures.Add($"资源管理器目录追踪失败；捕获值={trackerOutcome.Value}。");

            var multiTabTrackerOutcome = ExerciseExplorerMultiTabTracker(target);
            multiTabTrackerSucceeded = multiTabTrackerOutcome.Success;
            if (!multiTabTrackerSucceeded)
            {
                failures.Add($"资源管理器多标签目录追踪失败；{multiTabTrackerOutcome.Value}。");
            }
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
                multiTabTrackerSucceeded,
                annotationRendererSucceeded,
                clipboardTextRendererSucceeded,
                screenCaptureSucceeded,
                captureTargetDetectionSucceeded,
                recordingSucceeded,
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

    private static async Task<Outcome> ExerciseRecordingAndExports()
    {
        RecordingWorkspace? workspace = null;
        ScreenRegionRecorder? recorder = null;
        try
        {
            var screen = ScreenCaptureService.CaptureAllMonitors()
                .OrderByDescending(candidate => candidate.Left <= 0 && candidate.Top <= 0 &&
                                                candidate.Left + candidate.Width > 0 &&
                                                candidate.Top + candidate.Height > 0)
                .First();
            var width = Math.Min(320, screen.Width - 2);
            var height = Math.Min(240, screen.Height - 2);
            var normalized = RecordingRegionPolicy.NormalizeForVideo(new PixelRegion(
                screen.Left + 1,
                screen.Top + 1,
                width,
                height));
            var region = new Windows.Graphics.RectInt32(
                normalized.Left,
                normalized.Top,
                normalized.Width,
                normalized.Height);

            workspace = new RecordingWorkspace();
            recorder = new ScreenRegionRecorder(
                region,
                includeSystemAudio: false,
                RecordingFrameRatePolicy.DefaultFrameRate);
            await recorder.StartAsync(workspace.MasterFilePath);
            await Task.Delay(900);
            recorder.Pause();
            await Task.Delay(250);
            recorder.Resume();
            await Task.Delay(450);
            var path = await recorder.StopAsync();
            recorder.Dispose();
            recorder = null;
            if (!File.Exists(path) || new FileInfo(path).Length < 1024)
            {
                return new Outcome(false, 0, "MP4 母版未生成或文件过小。");
            }

            var duration = TimeSpan.FromSeconds(1.35);
            var gif = await RecordingExporter.ExportAsync(
                path,
                workspace.GetExportPath(RecordingExportFormat.Gif),
                RecordingExportFormat.Gif,
                duration);
            var webp = await RecordingExporter.ExportAsync(
                path,
                workspace.GetExportPath(RecordingExportFormat.WebP),
                RecordingExportFormat.WebP,
                duration);
            var gifBytes = File.ReadAllBytes(gif);
            var webpBytes = File.ReadAllBytes(webp);
            var gifHeader = System.Text.Encoding.ASCII.GetString(gifBytes, 0, 6);
            var webpContainer = System.Text.Encoding.ASCII.GetString(webpBytes, 0, 4);
            var webpKind = System.Text.Encoding.ASCII.GetString(webpBytes, 8, 4);
            var gifValid = gifHeader is "GIF89a" or "GIF87a";
            var webpValid = webpContainer == "RIFF" && webpKind == "WEBP";
            return gifValid && webpValid
                ? new Outcome(true, 0, null)
                : new Outcome(false, 0, "GIF 或 WebP 文件头无效。");
        }
        catch (Exception exception)
        {
            return new Outcome(false, 0, exception.ToString());
        }
        finally
        {
            recorder?.Dispose();
            workspace?.Dispose();
        }
    }

    private static Outcome ExerciseClipboardTextRenderer()
    {
        var path = Path.Combine(Path.GetTempPath(), $"Tbox.ClipboardText.{Guid.NewGuid():N}.png");
        try
        {
            var pngBytes = ClipboardTextCardRenderer.RenderPngAsync(
                    "第一行\r\nSecond line\r第三行包含足够长的中英文内容，用于确认剪贴板文字卡片能够保留换行并自动折行。" +
                    string.Concat(Enumerable.Repeat(" Tbox clipboard text wrapping", 18)))
                .GetAwaiter()
                .GetResult();
            File.WriteAllBytes(path, pngBytes);
            var rendered = CapturedScreen.LoadAsync(path).GetAwaiter().GetResult();
            var nonBackgroundPixels = 0;
            var alphaIsOpaque = true;
            for (var index = 0; index < rendered.Pixels.Length; index += 4)
            {
                if (rendered.Pixels[index + 3] != 255) alphaIsOpaque = false;
                if (rendered.Pixels[index] != 251 ||
                    rendered.Pixels[index + 1] != 250 ||
                    rendered.Pixels[index + 2] != 249)
                {
                    nonBackgroundPixels++;
                }
            }

            return pngBytes.Length > 0 &&
                   rendered.Width is >= 280 and <= 760 &&
                   rendered.Height is > 112 and <= 4096 &&
                   alphaIsOpaque &&
                   nonBackgroundPixels >= 100
                ? Outcome.Pass(0)
                : Outcome.Fail(
                    $"png={pngBytes.Length}; size={rendered.Width}x{rendered.Height}; " +
                    $"opaque={alphaIsOpaque}; textPixels={nonBackgroundPixels}");
        }
        catch (Exception exception)
        {
            return Outcome.Fail($"{exception.GetType().Name}: {exception.Message}");
        }
        finally
        {
            File.Delete(path);
        }
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

    private static Outcome ExerciseCaptureTargetDetection()
    {
        using var form = new System.Windows.Forms.Form
        {
            Text = "Tbox Capture Target Integration",
            StartPosition = FormStartPosition.Manual,
            Location = new System.Drawing.Point(120, 120),
            ClientSize = new System.Drawing.Size(360, 220),
            ShowInTaskbar = false
        };
        using var button = new System.Windows.Forms.Button
        {
            Text = "Detected control",
            Location = new System.Drawing.Point(70, 65),
            Size = new System.Drawing.Size(180, 54)
        };
        form.Controls.Add(button);
        form.Show();
        System.Windows.Forms.Application.DoEvents();
        try
        {
            var center = button.PointToScreen(new System.Drawing.Point(button.Width / 2, button.Height / 2));
            var detected = CaptureTargetDetector.FindAccessibleElementForTesting(
                form.Handle,
                new Windows.Graphics.PointInt32(center.X, center.Y));
            return detected is { } bounds &&
                   bounds.Contains(center.X, center.Y) &&
                   bounds.Width <= form.Width &&
                   bounds.Height <= form.Height
                ? Outcome.Pass(0)
                : Outcome.Fail($"point={center.X},{center.Y}; detected={detected?.ToString() ?? "<null>"}");
        }
        finally
        {
            form.Close();
        }
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

    private static (bool Success, string Value) ExerciseExplorerMultiTabTracker(string target)
    {
        var firstTabPath = Path.Combine(target, "multi-tab-a");
        var secondTabPath = Path.Combine(target, "multi-tab-b");
        Directory.CreateDirectory(firstTabPath);
        Directory.CreateDirectory(secondTabPath);
        var originalForeground = GetForegroundWindow();
        Process.Start(new ProcessStartInfo("explorer.exe", $"/n,/e,\"{firstTabPath}\"") { UseShellExecute = true });

        long explorerWindow = 0;
        var deadline = Environment.TickCount64 + 8000;
        do
        {
            TryFindExplorerWindow(firstTabPath, out explorerWindow);
            if (explorerWindow == 0) Thread.Sleep(50);
        }
        while (explorerWindow == 0 && Environment.TickCount64 < deadline);
        if (explorerWindow == 0) return (false, "测试窗口未出现");

        var window = new IntPtr(explorerWindow);
        try
        {
            if (!TryInvokeExplorerButton(window, "AddButton") &&
                !SendShortcut(window, [NativeMethods.VkControl], VkT))
            {
                return (false, "无法在测试窗口创建第二个标签");
            }

            deadline = Environment.TickCount64 + 5000;
            IReadOnlyList<string?> paths;
            do
            {
                paths = GetExplorerPaths(explorerWindow);
                if (paths.Count >= 2) break;
                Thread.Sleep(50);
            }
            while (Environment.TickCount64 < deadline);
            if (paths.Count < 2) return (false, $"Shell 只暴露了 {paths.Count} 个标签");

            if (!TryNavigateActiveExplorerTab(window, secondTabPath, out var navigationError))
            {
                return (false, navigationError);
            }

            using var tracker = new ExplorerFolderTracker();
            tracker.Start();
            tracker.CaptureWindowForTesting(window);
            var activeSecond = WaitForTrackedFolder(tracker, secondTabPath, TimeSpan.FromSeconds(5));
            if (!PathsEqual(activeSecond, secondTabPath))
            {
                return (false, $"选中第二个标签时捕获到 {activeSecond ?? "<null>"}");
            }

            if (!TrySelectExplorerTab(window, 0) &&
                !SendShortcut(window, [NativeMethods.VkControl, NativeMethods.VkShift], VkTab))
            {
                return (false, "无法切换回第一个标签");
            }
            var activeFirst = WaitForTrackedFolder(tracker, firstTabPath, TimeSpan.FromSeconds(5));
            return PathsEqual(activeFirst, firstTabPath)
                ? (true, $"{activeSecond} -> {activeFirst}")
                : (false, $"切回第一个标签时捕获到 {activeFirst ?? "<null>"}");
        }
        finally
        {
            PostMessage(window, WmClose, IntPtr.Zero, IntPtr.Zero);
            Thread.Sleep(750);
            if (originalForeground != IntPtr.Zero)
            {
                ForceForegroundWindow(originalForeground);
            }
        }
    }

    private static string? WaitForTrackedFolder(
        ExplorerFolderTracker tracker,
        string expected,
        TimeSpan timeout)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        string? captured;
        do
        {
            captured = tracker.RefreshCurrentFolder();
            if (PathsEqual(captured, expected)) return captured;
            Thread.Sleep(50);
        }
        while (Environment.TickCount64 < deadline);
        return captured;
    }

    private static bool TryNavigateActiveExplorerTab(
        IntPtr explorerWindow,
        string path,
        out string error)
    {
        error = string.Empty;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            if (TryNavigateNewestExplorerTab(explorerWindow.ToInt64(), path, out error))
            {
                var deadline = Environment.TickCount64 + 5000;
                do
                {
                    if (GetExplorerPaths(explorerWindow.ToInt64()).Any(candidate => PathsEqual(candidate, path)))
                    {
                        return true;
                    }
                    Thread.Sleep(50);
                }
                while (Environment.TickCount64 < deadline);
                error = "第二个标签未导航到目标目录";
            }

            if (TryNavigateActiveExplorerTabOnce(explorerWindow, path, out error))
            {
                return true;
            }
            Thread.Sleep(100);
        }
        error = $"三次尝试后仍失败：{error}";
        return false;
    }

    private static bool TryNavigateActiveExplorerTabOnce(
        IntPtr explorerWindow,
        string path,
        out string error)
    {
        error = string.Empty;
        var threadId = GetWindowThreadProcessId(explorerWindow, out _);
        var deadline = Environment.TickCount64 + 1500;
        IntPtr addressEditor = IntPtr.Zero;
        AutomationElement? addressElement = null;
        do
        {
            try
            {
                var root = AutomationElement.FromHandle(explorerWindow);
                var editCondition = new PropertyCondition(
                    AutomationElement.ControlTypeProperty,
                    ControlType.Edit);
                var edits = root.FindAll(TreeScope.Descendants, editCondition);
                var leftmost = double.PositiveInfinity;
                for (var index = 0; index < edits.Count; index++)
                {
                    var candidate = edits[index];
                    var current = candidate.Current;
                    if (current.ClassName != "TextBox" ||
                        current.AutomationId != "TextBox" ||
                        !candidate.TryGetCurrentPattern(ValuePattern.Pattern, out _))
                    {
                        continue;
                    }

                    if (current.BoundingRectangle.Left < leftmost)
                    {
                        leftmost = current.BoundingRectangle.Left;
                        addressElement = candidate;
                    }
                }
                if (addressElement is not null) break;
            }
            catch (ElementNotAvailableException)
            {
                // Explorer can replace the address element while Ctrl+L settles.
            }

            var info = new NativeMethods.GuiThreadInfo
            {
                size = Marshal.SizeOf<NativeMethods.GuiThreadInfo>()
            };
            if (NativeMethods.GetGUIThreadInfo(threadId, ref info) &&
                info.focusWindow != IntPtr.Zero &&
                NativeMethods.IsChild(explorerWindow, info.focusWindow))
            {
                var className = new char[64];
                var length = NativeMethods.GetClassName(info.focusWindow, className, className.Length);
                if (length > 0 && new string(className, 0, length) == "Edit")
                {
                    addressEditor = info.focusWindow;
                    break;
                }
            }
            Thread.Sleep(20);
        }
        while (Environment.TickCount64 < deadline);

        if (addressElement is not null)
        {
            try
            {
                addressElement.SetFocus();
                var valuePattern = (ValuePattern)addressElement.GetCurrentPattern(ValuePattern.Pattern);
                valuePattern.SetValue(path);
                Thread.Sleep(75);
            }
            catch (Exception exception) when (
                exception is ElementNotAvailableException or InvalidOperationException)
            {
                error = "无法通过自动化设置资源管理器地址栏文本";
                return false;
            }
        }
        else if (addressEditor != IntPtr.Zero)
        {
            NativeMethods.SetFocus(addressEditor);
            if (NativeMethods.SendMessageTimeout(
                    addressEditor,
                    NativeMethods.WmSetText,
                    IntPtr.Zero,
                    path,
                    NativeMethods.SmtoAbortIfHung,
                    750,
                    out _) == IntPtr.Zero)
            {
                error = "无法设置资源管理器地址栏文本";
                return false;
            }
        }
        else
        {
            error = "资源管理器地址栏编辑器未出现";
            return false;
        }

        if (!SendShortcut(explorerWindow, [], NativeMethods.VkReturn))
        {
            error = "无法提交资源管理器地址栏导航";
            return false;
        }

        deadline = Environment.TickCount64 + 5000;
        do
        {
            if (GetExplorerPaths(explorerWindow.ToInt64()).Any(candidate => PathsEqual(candidate, path)))
            {
                return true;
            }
            Thread.Sleep(50);
        }
        while (Environment.TickCount64 < deadline);
        error = "第二个标签未导航到目标目录";
        return false;
    }

    private static bool TryInvokeExplorerButton(IntPtr explorerWindow, string automationId)
    {
        try
        {
            var root = AutomationElement.FromHandle(explorerWindow);
            var condition = new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                new PropertyCondition(AutomationElement.AutomationIdProperty, automationId));
            var button = root.FindFirst(TreeScope.Descendants, condition);
            if (button is null ||
                !button.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern))
            {
                return false;
            }

            ((InvokePattern)pattern).Invoke();
            return true;
        }
        catch (Exception exception) when (
            exception is ElementNotAvailableException or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool TrySelectExplorerTab(IntPtr explorerWindow, int index)
    {
        try
        {
            var root = AutomationElement.FromHandle(explorerWindow);
            var condition = new PropertyCondition(
                AutomationElement.ControlTypeProperty,
                ControlType.TabItem);
            var tabs = root.FindAll(TreeScope.Descendants, condition);
            if (index < 0 || index >= tabs.Count ||
                !tabs[index].TryGetCurrentPattern(SelectionItemPattern.Pattern, out var pattern))
            {
                return false;
            }

            ((SelectionItemPattern)pattern).Select();
            return true;
        }
        catch (Exception exception) when (
            exception is ElementNotAvailableException or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool SendShortcut(IntPtr window, IReadOnlyList<ushort> modifiers, ushort key)
    {
        var deadline = Environment.TickCount64 + 2000;
        do
        {
            ForceForegroundWindow(window);
            if (GetForegroundWindow() == window) break;

            // Test runners launched from a terminal can be subject to the
            // foreground-lock timeout. SwitchToThisWindow gives the harness a
            // deterministic fallback without changing application behavior.
            SwitchToThisWindow(window, true);
            if (GetForegroundWindow() == window) break;
            Thread.Sleep(50);
        }
        while (Environment.TickCount64 < deadline);
        if (GetForegroundWindow() != window) return false;

        var inputs = new List<NativeMethods.INPUT>();
        inputs.AddRange(modifiers.Select(modifier => KeyInput(modifier)));
        inputs.Add(KeyInput(key));
        inputs.Add(KeyInput(key, NativeMethods.KeyeventfKeyup));
        for (var index = modifiers.Count - 1; index >= 0; index--)
        {
            inputs.Add(KeyInput(modifiers[index], NativeMethods.KeyeventfKeyup));
        }

        return NativeMethods.SendInput(
            (uint)inputs.Count,
            [.. inputs],
            Marshal.SizeOf<NativeMethods.INPUT>()) == inputs.Count;
    }

    private static NativeMethods.INPUT KeyInput(ushort key, uint flags = 0)
    {
        return new NativeMethods.INPUT
        {
            type = NativeMethods.InputKeyboard,
            union = new NativeMethods.InputUnion
            {
                keyboard = new NativeMethods.KEYBDINPUT
                {
                    virtualKey = key,
                    flags = flags
                }
            }
        };
    }

    private static IReadOnlyList<string?> GetExplorerPaths(long windowHandle)
    {
        var paths = new List<string?>();
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null) return paths;
            dynamic shell = Activator.CreateInstance(shellType)!;
            foreach (dynamic item in shell.Windows())
            {
                try
                {
                    if (Convert.ToInt64(item.HWND) != windowHandle) continue;
                    paths.Add(item.Document?.Folder?.Self?.Path);
                }
                catch
                {
                    // A tab may close while Shell windows are enumerated.
                }
            }
        }
        catch
        {
            // The caller retries until its deadline.
        }
        return paths;
    }

    private static bool TryNavigateNewestExplorerTab(long windowHandle, string targetPath, out string error)
    {
        error = string.Empty;
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null)
            {
                error = "Shell.Application 不可用";
                return false;
            }

            dynamic shell = Activator.CreateInstance(shellType)!;
            object? newestTab = null;
            foreach (dynamic item in shell.Windows())
            {
                try
                {
                    if (Convert.ToInt64(item.HWND) == windowHandle) newestTab = item;
                }
                catch
                {
                    // Explorer can mutate while tabs are enumerated.
                }
            }

            if (newestTab is null)
            {
                error = "测试窗口已关闭";
                return false;
            }

            ((dynamic)newestTab).Navigate2(targetPath);
            return true;
        }
        catch (Exception exception)
        {
            error = $"Shell 导航失败：{exception.Message}";
            return false;
        }
    }

    private static bool PathsEqual(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second)) return false;
        return string.Equals(
            Path.GetFullPath(first),
            Path.GetFullPath(second),
            StringComparison.OrdinalIgnoreCase);
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

        // Cold shell enumeration can take more than 1.5 seconds even after the
        // address bar has reached the target. UI Automation can also briefly
        // expose neither the file list nor the address toolbar, so corroborate
        // it with the native address-bar text used by the production path.
        var reachedTarget = WaitForDialogNavigationEvidence(
            dialog,
            target,
            marker,
            TimeSpan.FromSeconds(5),
            out var address);
        NavigationResult? retryResult = null;
        if (!reachedTarget)
        {
            ForceForegroundWindow(dialog);
            retryResult = FileDialogNavigator.Navigate(dialog, target);
            if (retryResult == NavigationResult.Success)
            {
                reachedTarget = WaitForDialogNavigationEvidence(
                    dialog,
                    target,
                    marker,
                    TimeSpan.FromSeconds(5),
                    out address);
            }
        }
        stopwatch.Stop();
        PostMessage(dialog, WmClose, IntPtr.Zero, IntPtr.Zero);
        return !reachedTarget
            ? Outcome.Fail(
                $"导航后没有看到目标目录标记文件；地址栏={address ?? "<missing>"}；" +
                $"重试结果={retryResult?.ToString() ?? "未重试"}。")
            : Outcome.Pass(stopwatch.ElapsedMilliseconds);
    }

    private static bool WaitForDialogNavigationEvidence(
        IntPtr dialog,
        string target,
        string marker,
        TimeSpan timeout,
        out string? address)
    {
        address = null;
        var markerCondition = new PropertyCondition(AutomationElement.NameProperty, marker);
        var addressCondition = new PropertyCondition(AutomationElement.AutomationIdProperty, "1001");
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        do
        {
            Thread.Sleep(20);
            try
            {
                var root = AutomationElement.FromHandle(dialog);
                if (root.FindFirst(TreeScope.Descendants, markerCondition) is not null)
                {
                    return true;
                }

                var automationAddress = root.FindFirst(TreeScope.Descendants, addressCondition)?.Current.Name;
                if (!string.IsNullOrWhiteSpace(automationAddress)) address = automationAddress;
            }
            catch (ElementNotAvailableException)
            {
                // The shell is rebuilding the dialog's automation tree.
            }
            catch (InvalidOperationException)
            {
                // The shell is rebuilding the dialog's automation tree.
            }
            catch (COMException)
            {
                // The shell is rebuilding the dialog's automation tree.
            }

            if (FileDialogNavigator.TryGetCurrentAddress(dialog, out var nativeAddress))
            {
                address = nativeAddress;
            }

            if (FileDialogNavigator.AddressTextMatches(
                    target,
                    address ?? string.Empty,
                    Path.GetFileName(target)))
            {
                return true;
            }
        }
        while (Environment.TickCount64 < deadline);
        return false;
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

    [LibraryImport("user32.dll")]
    private static partial void SwitchToThisWindow(IntPtr window, [MarshalAs(UnmanagedType.Bool)] bool altTab);
}
