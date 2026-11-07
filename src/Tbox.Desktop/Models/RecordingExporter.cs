using System.Diagnostics;
using System.Globalization;
using Tbox.Core;

namespace Tbox.Desktop.Models;

public static class RecordingExporter
{
    public static string FfmpegPath => Environment.GetEnvironmentVariable("TBOX_FFMPEG") ?? Path.Combine(
        AppContext.BaseDirectory, "ThirdParty", "FFmpeg", "bin", OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");

    public static IReadOnlyList<string> Arguments(string source, string destination, RecordingExportFormat format)
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-i", source, "-an" };
        if (format == RecordingExportFormat.Gif)
            args.AddRange(["-filter_complex", "fps=12,scale='if(gt(iw,ih),min(1280,iw),-2)':'if(gt(iw,ih),-2,min(1280,ih))':flags=lanczos," +
                "split[base][palette_source];[palette_source]palettegen=stats_mode=diff[palette];[base][palette]paletteuse=dither=sierra2_4a", "-loop", "0"]);
        else if (format == RecordingExportFormat.WebP)
            args.AddRange(["-vf", "fps=24,scale='if(gt(iw,ih),min(1920,iw),-2)':'if(gt(iw,ih),-2,min(1920,ih))':flags=lanczos",
                "-c:v", "libwebp_anim", "-quality", "80", "-loop", "0"]);
        else throw new ArgumentOutOfRangeException(nameof(format), "MP4 应直接复制母版。");
        args.AddRange(["-progress", "pipe:1", "-nostats", destination]); return args;
    }

    // destination belongs to the session workspace, never directly to the user's selected file.
    public static async Task<string> ExportAsync(string source, string destination, RecordingExportFormat format,
        TimeSpan duration, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (format == RecordingExportFormat.Mp4) return source;
        if (!File.Exists(FfmpegPath)) throw new FileNotFoundException("图像导出组件缺失，仍可保存 MP4。", FfmpegPath);
        var info = new ProcessStartInfo(FfmpegPath) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in Arguments(source, destination, format)) info.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = info };
        var success = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!process.Start()) throw new InvalidOperationException("无法启动图像导出。");
            using var registration = cancellationToken.Register(() => Kill(process));
            var errors = process.StandardError.ReadToEndAsync();
            while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
                if (line.StartsWith("out_time_us=", StringComparison.Ordinal) && duration > TimeSpan.Zero &&
                    long.TryParse(line.AsSpan(12), NumberStyles.Integer, CultureInfo.InvariantCulture, out var micros))
                    progress?.Report(Math.Clamp(micros / (duration.TotalMilliseconds * 1000), 0, .99));
            await process.WaitForExitAsync(cancellationToken);
            var error = await errors;
            cancellationToken.ThrowIfCancellationRequested();
            if (process.ExitCode != 0 || !File.Exists(destination) || new FileInfo(destination).Length == 0)
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "图像导出失败，母版仍保留。" : error.Trim());
            success = true; progress?.Report(1); return destination;
        }
        finally
        {
            if (!success)
            {
                Kill(process);
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (Exception error) when (error is InvalidOperationException or TimeoutException) { }
                try { File.Delete(destination); } catch (IOException) { }
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
