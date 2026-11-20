using System.Diagnostics;
using System.Globalization;
using SkiaSharp;
using Tbox.Core;

namespace Tbox.Desktop.Models;

public static class RecordingExporter
{
    public static string FfmpegPath => Environment.GetEnvironmentVariable("TBOX_FFMPEG") ?? Path.Combine(
        AppContext.BaseDirectory, "ThirdParty", "FFmpeg", "bin", OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");

    private static string Seconds(TimeSpan value) => value.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);

    public static IReadOnlyList<string> Arguments(string source, string destination, RecordingExportOptions options)
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-y" };
        // Input seeking keeps long recordings fast; -t then bounds the decoded length.
        if (options.Range.Start > TimeSpan.Zero) args.AddRange(["-ss", Seconds(options.Range.Start)]);
        args.AddRange(["-i", source, "-t", Seconds(options.Range.Length)]);
        var scale = $"scale={options.Size.Width}:{options.Size.Height}:flags=lanczos";
        var fps = "fps=" + options.FramesPerSecond.ToString(CultureInfo.InvariantCulture);
        switch (options.Format)
        {
            case RecordingExportFormat.Gif:
                args.AddRange(["-an", "-filter_complex", $"{fps},{scale},split[base][palette_source];[palette_source]palettegen=stats_mode=diff[palette];" +
                    "[base][palette]paletteuse=dither=sierra2_4a", "-loop", "0"]);
                break;
            case RecordingExportFormat.WebP:
                args.AddRange(["-an", "-vf", $"{fps},{scale}", "-c:v", "libwebp_anim", "-quality", "80", "-loop", "0"]);
                break;
            case RecordingExportFormat.Mp4:
                var bitrate = RecordingRegionPolicy.CalculateVideoBitrate(options.Size.Width, options.Size.Height, options.FramesPerSecond);
                args.AddRange(["-vf", $"{fps},{scale},format=nv12", "-c:v", OperatingSystem.IsMacOS() ? "h264_videotoolbox" : "h264_mf"]);
                // camera_record keeps Media Foundation at constant frame rate instead of dropping frames; VideoToolbox may fall back to software.
                args.AddRange(OperatingSystem.IsMacOS() ? ["-allow_sw", "1"] : ["-scenario", "camera_record", "-rate_control", "cbr"]);
                args.AddRange(["-b:v", bitrate.ToString(CultureInfo.InvariantCulture), "-g", (options.FramesPerSecond * 2).ToString(CultureInfo.InvariantCulture),
                    // The bundled runtime has no AAC encoder, so the recorded AAC track is copied as-is.
                    "-c:a", "copy", "-movflags", "+faststart"]);
                break;
            default: throw new ArgumentOutOfRangeException(nameof(options), "六宫格通过逐帧截取生成。");
        }
        args.AddRange(["-progress", "pipe:1", "-nostats", destination]); return args;
    }

    public static IReadOnlyList<string> FrameArguments(string source, TimeSpan time, VideoSize size) =>
    [
        "-hide_banner", "-loglevel", "error", "-nostdin", "-ss", Seconds(time), "-i", source, "-frames:v", "1", "-an",
        "-vf", $"scale={size.Width}:{size.Height}:flags=lanczos,format=bgra", "-f", "rawvideo", "-pix_fmt", "bgra", "pipe:1"
    ];

    // destination belongs to the session workspace, never directly to the user's selected file.
    public static async Task<string> ExportAsync(string source, string destination, RecordingExportOptions options, VideoSize sourceSize,
        TimeSpan duration, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (options.CopiesMaster(sourceSize, duration)) return source;
        if (!File.Exists(FfmpegPath)) throw new FileNotFoundException("导出组件缺失，仍可保存未修改的 MP4。", FfmpegPath);
        var success = false;
        try
        {
            if (options.Format == RecordingExportFormat.ContactSheet) await ExportSheetAsync(source, destination, options, progress, cancellationToken);
            else await TranscodeAsync(source, destination, options, progress, cancellationToken);
            if (!File.Exists(destination) || new FileInfo(destination).Length == 0) throw new InvalidOperationException("导出失败，母版仍保留。");
            success = true; progress?.Report(1); return destination;
        }
        finally { if (!success) try { File.Delete(destination); } catch (IOException) { } }
    }

    private static async Task TranscodeAsync(string source, string destination, RecordingExportOptions options, IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var length = options.Range.Length;
        using var process = Start(Arguments(source, destination, options));
        await RunAsync(process, async () =>
        {
            while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
                if (line.StartsWith("out_time_us=", StringComparison.Ordinal) && length > TimeSpan.Zero &&
                    long.TryParse(line.AsSpan(12), NumberStyles.Integer, CultureInfo.InvariantCulture, out var micros))
                    progress?.Report(Math.Clamp(micros / (length.TotalMilliseconds * 1000), 0, .99));
        }, cancellationToken);
    }

    private static async Task ExportSheetAsync(string source, string destination, RecordingExportOptions options, IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var cell = options.Size;
        var frames = new List<byte[]>();
        foreach (var time in ContactSheetPolicy.SampleTimes(options.Range, RecordingFrameRatePolicy.RecordingFrameRate))
        {
            frames.Add(await ReadFrameAsync(source, time, cell, cancellationToken));
            progress?.Report(frames.Count / (ContactSheetPolicy.Count + 1.0));
        }
        cancellationToken.ThrowIfCancellationRequested();
        await File.WriteAllBytesAsync(destination, ComposeSheet(frames, cell), cancellationToken);
    }

    internal static byte[] ComposeSheet(IReadOnlyList<byte[]> bgraFrames, VideoSize cell)
    {
        var sheet = ContactSheetPolicy.SheetSize(cell);
        using var surface = SKSurface.Create(new SKImageInfo(sheet.Width, sheet.Height, SKColorType.Bgra8888, SKAlphaType.Opaque))
            ?? throw new InvalidOperationException("无法创建六宫格画布。");
        var canvas = surface.Canvas; canvas.Clear(SKColors.Black);
        using var typeface = SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold) ?? SKTypeface.Default;
        using var paint = new SKPaint { Color = SKColors.White, IsAntialias = true, Typeface = typeface, TextSize = ContactSheetPolicy.LabelSize };
        var layout = ContactSheetPolicy.Layout(cell);
        for (var i = 0; i < layout.Count; i++)
        {
            var info = new SKImageInfo(cell.Width, cell.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
            using (var data = SKData.CreateCopy(bgraFrames[i]))
            using (var image = SKImage.FromPixels(info, data, info.RowBytes) ?? throw new InvalidOperationException("帧数据无效。"))
                canvas.DrawImage(image, layout[i].X, layout[i].Y);
            // Align the glyph box's top-left like the AE script's sourceRectAtTime placement.
            var bounds = new SKRect(); paint.MeasureText(layout[i].Label, ref bounds);
            canvas.DrawText(layout[i].Label, layout[i].LabelX - bounds.Left, layout[i].LabelY - bounds.Top, paint);
        }
        using var snapshot = surface.Snapshot();
        using var encoded = snapshot.Encode(SKEncodedImageFormat.Jpeg, RecordingExportProfiles.Get(RecordingExportFormat.ContactSheet).Quality!.Value);
        return encoded.ToArray();
    }

    internal static async Task<byte[]> ReadFrameAsync(string source, TimeSpan time, VideoSize size, CancellationToken cancellationToken)
    {
        // The writer's final timestamp can land a frame short of the measured duration; step back to the last decodable frame.
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var at = time - TimeSpan.FromSeconds(attempt / (double)RecordingFrameRatePolicy.RecordingFrameRate);
            if (await TryReadFrameAsync(source, at < TimeSpan.Zero ? TimeSpan.Zero : at, size, cancellationToken) is { } frame) return frame;
        }
        throw new InvalidOperationException("截取六宫格画面失败，母版仍保留。");
    }

    private static async Task<byte[]?> TryReadFrameAsync(string source, TimeSpan time, VideoSize size, CancellationToken cancellationToken)
    {
        var expected = checked(size.Width * size.Height * 4);
        var buffer = new byte[expected]; var read = 0;
        using var process = Start(FrameArguments(source, time, size));
        await RunAsync(process, async () =>
        {
            var stream = process.StandardOutput.BaseStream;
            while (read < expected && await stream.ReadAsync(buffer.AsMemory(read), cancellationToken) is var count and > 0) read += count;
            await stream.CopyToAsync(Stream.Null, cancellationToken);
        }, cancellationToken);
        return read == expected ? buffer : null;
    }

    private static Process Start(IReadOnlyList<string> arguments)
    {
        var info = new ProcessStartInfo(FfmpegPath) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in arguments) info.ArgumentList.Add(arg);
        var process = new Process { StartInfo = info };
        if (!process.Start()) { process.Dispose(); throw new InvalidOperationException("无法启动导出组件。"); }
        return process;
    }

    private static async Task RunAsync(Process process, Func<Task> readOutput, CancellationToken cancellationToken)
    {
        var success = false;
        try
        {
            using var registration = cancellationToken.Register(() => Kill(process));
            var errors = process.StandardError.ReadToEndAsync();
            await readOutput();
            await process.WaitForExitAsync(cancellationToken);
            var error = await errors;
            cancellationToken.ThrowIfCancellationRequested();
            if (process.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "导出失败，母版仍保留。" : error.Trim());
            success = true;
        }
        finally
        {
            if (!success)
            {
                Kill(process);
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (Exception error) when (error is InvalidOperationException or TimeoutException) { }
            }
        }
    }

    public static async Task SaveAtomicallyAsync(string source, string destination, CancellationToken cancellationToken)
    {
        if (Path.GetFullPath(source) == Path.GetFullPath(destination)) return;
        var temporary = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(destination))!, ".tbox-save-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var input = File.OpenRead(source))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                await input.CopyToAsync(output, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void Kill(Process process)
    { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { } }
}
