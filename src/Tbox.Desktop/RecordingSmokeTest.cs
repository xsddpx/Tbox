using System.Diagnostics;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Avalonia.Controls;
using Avalonia.Media;
using LibVLCSharp.Shared;
using Tbox.Core;
using Tbox.Desktop.Models;
using Tbox.Desktop.Platform;

namespace Tbox.Desktop;

// Explicit installed-package diagnostic. The Mac fixture runs separately so
// ScreenCaptureKit includes it while excluding Tbox.
internal static class RecordingSmokeTest
{
    internal sealed record DecodedRecording(long DurationMs, int Frames, int RedFrames, int GreenFrames, int BlueFrames);

    internal static void Validate(DecodedRecording result)
    {
        if (result.Frames < 20 || result.RedFrames < 5 || result.BlueFrames < 5)
            throw new InvalidOperationException("真实录屏没有包含暂停前后的测试画面。");
        if (result.GreenFrames != 0)
            throw new InvalidOperationException("暂停期间的绿色测试画面进入了录屏。");
        if (result.DurationMs is < 3000 or > 5250)
            throw new InvalidOperationException($"录屏时长不符合两段有效录制时间：{result.DurationMs} ms。");
    }

    public static async Task<object> RunAsync(IPlatformServices platform, Window main, string directory)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(70));
        var cancellation = timeout.Token;
        if (OperatingSystem.IsMacOS()) return await RunMacAsync(platform, directory, cancellation);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("当前系统不支持录屏验收。");
        var fixture = new Window
        {
            Title = "Tbox recording verification", Width = 320, Height = 240,
            SystemDecorations = SystemDecorations.None, CanResize = false,
            ShowInTaskbar = false, Topmost = true, Background = Brush(224, 48, 48)
        };
        try
        {
            fixture.Show();
            var handle = fixture.TryGetPlatformHandle()!.Handle;
            var work = platform.GetWorkArea(main.TryGetPlatformHandle()!.Handle);
            if (work.Width < 360 || work.Height < 280) throw new InvalidOperationException("显示区域不足以验证录屏。");
            var area = new DesktopPlacement(work.Left + 20, work.Top + 20, 320, 240);
            platform.PlaceWindow(handle, area, overlay: false);
            await Task.Delay(500, cancellation);
            var source = Path.Combine(directory, "recording.mp4");
            await using (var recorder = platform.CreateRecorder(new RecordingArea(area, 320, 240), false, false, RecordingFrameRatePolicy.RecordingFrameRate))
            {
                await recorder.StartAsync(source, cancellation);
                await Task.Delay(2000, cancellation);
                recorder.Pause();
                await Task.Delay(300, cancellation);
                fixture.Background = Brush(48, 224, 48);
                await Task.Delay(2000, cancellation);
                fixture.Background = Brush(48, 48, 224);
                await Task.Delay(300, cancellation);
                recorder.Resume();
                await Task.Delay(2000, cancellation);
                var output = await recorder.StopAsync(cancellation);
                if (Path.GetFullPath(output.FilePath) != source || output.HasSystemAudio)
                    throw new InvalidOperationException("录屏输出与本次静音验收请求不一致。");
            }
            fixture.Close();
            return await VerifyOutputAsync(source, directory, false, null, cancellation);
        }
        finally { fixture.Close(); }
    }

    private static async Task<object> RunMacAsync(IPlatformServices platform, string directory, CancellationToken cancellation)
    {
        if (!platform.HasScreenPermission) throw new InvalidOperationException("实采验收需要已授权的屏幕录制权限。");
        var bounds = JsonSerializer.Deserialize<DesktopPlacement>(
            await File.ReadAllTextAsync(Path.Combine(directory, "fixture-bounds.json"), cancellation));
        if (bounds is not { Width: 320, Height: 240 }) throw new InvalidOperationException("录屏验收色块尺寸错误。");
        var phasePath = Path.Combine(directory, "fixture-phase.txt");
        var acknowledgment = Path.Combine(directory, "fixture-ready.txt");
        async Task SetPhaseAsync(string phase)
        {
            await File.WriteAllTextAsync(phasePath, phase, cancellation);
            while (!File.Exists(acknowledgment) || await File.ReadAllTextAsync(acknowledgment, cancellation) != phase)
                await Task.Delay(30, cancellation);
        }

        await SetPhaseAsync("red");
        var source = Path.Combine(directory, "recording.mp4");
        using var process = Process.GetCurrentProcess();
        var cpuBefore = process.TotalProcessorTime;
        var timer = Stopwatch.StartNew();
        await using (var recorder = platform.CreateRecorder(new RecordingArea(bounds, 320, 240), true, false, RecordingFrameRatePolicy.RecordingFrameRate))
        {
            await recorder.StartAsync(source, cancellation);
            await Task.Delay(2000, cancellation);
            recorder.Pause();
            await SetPhaseAsync("green");
            await Task.Delay(2000, cancellation);
            await SetPhaseAsync("blue");
            await Task.Delay(200, cancellation);
            recorder.Resume();
            await Task.Delay(2000, cancellation);
            var output = await recorder.StopAsync(cancellation);
            if (Path.GetFullPath(output.FilePath) != source || !output.HasSystemAudio)
                throw new InvalidOperationException("实采源文件缺少系统音频。");
        }
        var performance = new { cpuMs = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds, elapsedMs = timer.Elapsed.TotalMilliseconds };
        await SetPhaseAsync("stop");
        return await VerifyOutputAsync(source, directory, true, performance, cancellation);
    }

    private static async Task<object> VerifyOutputAsync(string source, string directory, bool systemAudioTested,
        object? performance, CancellationToken cancellation)
    {
        var decoded = await Task.Run(() => DecodeAsync(source, cancellation), cancellation);
        Validate(decoded);
        var duration = TimeSpan.FromMilliseconds(decoded.DurationMs);
        var gif = Path.Combine(directory, "recording.gif");
        var webp = Path.Combine(directory, "recording.webp");
        var sheet = Path.Combine(directory, "recording.jpg");
        var size = new VideoSize(320, 240); var full = new RecordingTimeRange(TimeSpan.Zero, duration);
        await RecordingExporter.ExportAsync(source, gif, new(RecordingExportFormat.Gif, 10, size, full), size, duration, null, cancellation);
        await RecordingExporter.ExportAsync(source, webp, new(RecordingExportFormat.WebP, 25, size, full), size, duration, null, cancellation);
        await RecordingExporter.ExportAsync(source, sheet, new(RecordingExportFormat.ContactSheet, 0, new(200, 150), full), size, duration, null, cancellation);
        var sheetBytes = await File.ReadAllBytesAsync(sheet, cancellation);
        if (sheetBytes.Length < 4 || sheetBytes[0] != 0xFF || sheetBytes[1] != 0xD8) throw new InvalidOperationException("实采六宫格不是 JPG。");
        var gifBytes = await File.ReadAllBytesAsync(gif, cancellation);
        var webpBytes = await File.ReadAllBytesAsync(webp, cancellation);
        if (gifBytes.Length < 13 || !gifBytes.AsSpan(0, 6).SequenceEqual("GIF89a"u8) ||
            BitConverter.ToUInt16(gifBytes, 6) != 320 || BitConverter.ToUInt16(gifBytes, 8) != 240)
            throw new InvalidOperationException("实采 GIF 格式或尺寸错误。");
        if (webpBytes.Length < 30 || !webpBytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) ||
            !webpBytes.AsSpan(8, 8).SequenceEqual("WEBPVP8X"u8) ||
            UInt24(webpBytes, 24) + 1 != 320 || UInt24(webpBytes, 27) + 1 != 240)
            throw new InvalidOperationException("实采 WebP 格式或尺寸错误。");
        var copy = Path.Combine(directory, "recording-copy.mp4");
        await RecordingExporter.SaveAtomicallyAsync(source, copy, cancellation);
        var sourceHash = SHA256.HashData(await File.ReadAllBytesAsync(source, cancellation));
        var copyHash = SHA256.HashData(await File.ReadAllBytesAsync(copy, cancellation));
        if (!sourceHash.SequenceEqual(copyHash))
            throw new InvalidOperationException("保存 MP4 改变了录屏母版。");
        return new { verified = true, systemAudioTested, performance, width = 320, height = 240, requestedFps = RecordingFrameRatePolicy.RecordingFrameRate,
            decoded, gifBytes = gifBytes.Length, webpBytes = webpBytes.Length, sheetBytes = sheetBytes.Length, mp4CopyIdentical = true };
    }

    private static SolidColorBrush Brush(byte red, byte green, byte blue) => new(Color.FromRgb(red, green, blue));
    private static int UInt24(byte[] bytes, int offset) => bytes[offset] | bytes[offset + 1] << 8 | bytes[offset + 2] << 16;

    private static async Task<DecodedRecording> DecodeAsync(string path, CancellationToken cancellation)
    {
        using var engine = MediaRuntime.Create();
        using var media = new Media(engine, path, FromType.FromPath);
        using var player = new MediaPlayer(engine);
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        player.EndReached += (_, _) => ended.TrySetResult();
        player.EncounteredError += (_, _) => ended.TrySetException(new InvalidOperationException("实采录屏无法回放。"));
        var frames = 0; var red = 0; var green = 0; var blue = 0;
        var buffer = Marshal.AllocHGlobal(320 * 240 * 4);
        try
        {
            player.SetVideoFormat("RV32", 320, 240, 320 * 4);
            player.SetVideoCallbacks((_, planes) => { Marshal.WriteIntPtr(planes, buffer); return IntPtr.Zero; }, null,
                (_, _) =>
                {
                    Interlocked.Increment(ref frames);
                    // Sample away from the pointer and edges. RV32 is BGRX on Windows.
                    var pixel = buffer + (60 * 320 + 80) * 4;
                    var b = Marshal.ReadByte(pixel); var g = Marshal.ReadByte(pixel, 1); var r = Marshal.ReadByte(pixel, 2);
                    if (r > 150 && g < 100 && b < 100) Interlocked.Increment(ref red);
                    if (g > 150 && r < 100 && b < 100) Interlocked.Increment(ref green);
                    if (b > 150 && r < 100 && g < 100) Interlocked.Increment(ref blue);
                });
            if (!player.Play(media)) throw new InvalidOperationException("实采录屏无法开始回放。");
            await ended.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellation);
            var duration = player.Length;
            await Task.Run(player.Stop);
            return new DecodedRecording(duration, frames, red, green, blue);
        }
        finally
        {
            await Task.Run(player.Stop);
            player.Media = null;
            Marshal.FreeHGlobal(buffer);
        }
    }
}
