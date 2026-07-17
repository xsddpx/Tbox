using System.Diagnostics;
using System.Globalization;
using Tbox.Core;

namespace Tbox.App;

internal static class RecordingExporter
{
    internal static string FfmpegPath => Path.Combine(
        AppContext.BaseDirectory,
        "ThirdParty",
        "FFmpeg",
        "bin",
        "ffmpeg.exe");

    internal static bool IsImageExportAvailable => File.Exists(FfmpegPath);

    internal static async Task<string> ExportAsync(
        string sourceMp4,
        string outputPath,
        RecordingExportFormat format,
        TimeSpan duration,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(sourceMp4)) throw new FileNotFoundException("录屏母版不存在。", sourceMp4);
        if (format == RecordingExportFormat.Mp4)
        {
            await CopyFileAsync(sourceMp4, outputPath, cancellationToken);
            progress?.Report(1);
            return outputPath;
        }

        if (!IsImageExportAvailable)
        {
            throw new FileNotFoundException("录屏图像导出组件缺失。", FfmpegPath);
        }

        if (File.Exists(outputPath)) File.Delete(outputPath);
        var startInfo = new ProcessStartInfo
        {
            FileName = FfmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(FfmpegPath)!
        };
        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-loglevel");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-y");
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(sourceMp4);
        startInfo.ArgumentList.Add("-an");

        if (format == RecordingExportFormat.Gif)
        {
            startInfo.ArgumentList.Add("-filter_complex");
            startInfo.ArgumentList.Add(
                "fps=12,scale='if(gt(iw,ih),min(1280,iw),-2)':'if(gt(iw,ih),-2,min(1280,ih))':flags=lanczos," +
                "split[base][palette_source];[palette_source]palettegen=stats_mode=diff[palette];" +
                "[base][palette]paletteuse=dither=sierra2_4a");
            startInfo.ArgumentList.Add("-loop");
            startInfo.ArgumentList.Add("0");
        }
        else
        {
            startInfo.ArgumentList.Add("-vf");
            startInfo.ArgumentList.Add(
                "fps=24,scale='if(gt(iw,ih),min(1920,iw),-2)':'if(gt(iw,ih),-2,min(1920,ih))':flags=lanczos");
            startInfo.ArgumentList.Add("-c:v");
            startInfo.ArgumentList.Add("libwebp_anim");
            startInfo.ArgumentList.Add("-quality");
            startInfo.ArgumentList.Add("80");
            startInfo.ArgumentList.Add("-loop");
            startInfo.ArgumentList.Add("0");
        }

        startInfo.ArgumentList.Add("-progress");
        startInfo.ArgumentList.Add("pipe:1");
        startInfo.ArgumentList.Add("-nostats");
        startInfo.ArgumentList.Add(outputPath);

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start()) throw new InvalidOperationException("无法启动录屏导出组件。");
        using var cancellationRegistration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch
            {
                // The process may have exited between the checks.
            }
        });

        var standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
        {
            if (!line.StartsWith("out_time_us=", StringComparison.Ordinal) || duration <= TimeSpan.Zero) continue;
            if (long.TryParse(line.AsSpan("out_time_us=".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var microseconds))
            {
                progress?.Report(Math.Clamp(microseconds / (duration.TotalMilliseconds * 1000), 0, 0.99));
            }
        }

        await process.WaitForExitAsync(cancellationToken);
        var error = await standardErrorTask;
        if (process.ExitCode != 0 || !File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
        {
            if (File.Exists(outputPath)) File.Delete(outputPath);
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                ? $"导出失败，FFmpeg={process.ExitCode}。"
                : error.Trim());
        }

        progress?.Report(1);
        return outputPath;
    }

    internal static async Task CopyFileAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await source.CopyToAsync(destination, cancellationToken);
        await destination.FlushAsync(cancellationToken);
    }
}
